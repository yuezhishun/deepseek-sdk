using System.ClientModel.Primitives;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using DeepSeek.Chat;

namespace DeepSeek.Decisions;

public sealed class DecisionsClient
{
    private readonly ChatClient _chat;
    private readonly object _capabilityLock = new();
    private int _absences;
    private bool _logprobsUnavailable;

    // RequestOptions 没有公开复制接口；与 Core 一样保留 header 和 policy 配置。
    private static readonly FieldInfo[] RequestOptionFields = new[] { "_headersUpdates", "_perCallPolicies", "_perTryPolicies", "_beforeTransportPolicies" }
        .Select(name => typeof(RequestOptions).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!)
        .Where(field => field is not null).ToArray();

    public DecisionsClient(ChatClient client) => _chat = client ?? throw new ArgumentNullException(nameof(client));

    public string Model => _chat.Model;

    public Decision CreateDecision(DecisionRequest request, DecisionOptions? decisionOptions = null, RequestOptions? options = null)
        => CreateDecisionAsync(request, decisionOptions, options).GetAwaiter().GetResult();

    public async Task<Decision> CreateDecisionAsync(DecisionRequest request, DecisionOptions? decisionOptions = null, RequestOptions? options = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        CheckModel(request.Model);
        var prepared = PreparedQuestion.ForDecisions(request);
        var response = await RunAsync(prepared, decisionOptions, options).ConfigureAwait(false);
        var answers = prepared.Select(question =>
        {
            var answer = response.Answers[question.Id];
            return new DecisionAnswer
            {
                Type = question.Kind == "noul" ? "predicate" : question.Kind,
                Name = question.Name,
                Probability = answer.Noul,
                Choice = answer.Choice is null ? null : question.Values[Array.IndexOf(question.Keys, answer.Choice)],
                Score = answer.Score,
                Confidence = answer.Confidence,
                Probabilities = answer.Probabilities is null ? null : question.Keys.Select((key, index) => new DecisionProbability
                {
                    Value = question.Values[index],
                    Label = question.LevelLabels?[index],
                    Probability = answer.Probabilities[key],
                }).ToList(),
            };
        }).ToList();
        return new Decision { Model = Model, Answers = answers, Usage = response.Usage, Diagnostics = response.Diagnostics };
    }

    public SystemOneResponse SystemOne(SystemOneRequest request, DecisionOptions? decisionOptions = null, RequestOptions? options = null)
        => SystemOneAsync(request, decisionOptions, options).GetAwaiter().GetResult();

    public Task<SystemOneResponse> SystemOneAsync(SystemOneRequest request, DecisionOptions? decisionOptions = null, RequestOptions? options = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        CheckModel(request.Model);
        return RunAsync(PreparedQuestion.ForSystemOne(request), decisionOptions, options);
    }

    private void CheckModel(string? requested)
    {
        if (requested is not null && !string.Equals(Model, requested, StringComparison.Ordinal))
            throw new ArgumentException("请求模型必须与绑定的 ChatClient.Model 一致；切换模型请创建新的客户端。");
    }

    private async Task<SystemOneResponse> RunAsync(PreparedQuestion[] questions, DecisionOptions? decisionOptions, RequestOptions? options)
    {
        var settings = (decisionOptions ?? new DecisionOptions()).Snapshot();
        if (options?.BufferResponse is false) throw new InvalidOperationException("决策调用要求 RequestOptions.BufferResponse 为 true。");
        if (settings.Method == "logprobs" && questions.Any(question => question.Keys.Length > 26))
            throw new ArgumentException("单 token logprobs 最多支持 26 个选项；请使用 auto、structured 或 discrete。");
        var requestOptions = CopyOptions(options, options?.CancellationToken ?? CancellationToken.None);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(requestOptions.CancellationToken);
        cancellation.Token.ThrowIfCancellationRequested();
        var diagnostics = questions.Select(question => new DecisionDiagnostic { QuestionId = question.Id }).ToArray();
        var answers = new SystemOneAnswer[questions.Length];
        var next = -1;
        Exception? failure = null;

        async Task Worker()
        {
            while (!cancellation.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= questions.Length) return;
                try
                {
                    answers[index] = await AnswerAsync(questions[index], settings, requestOptions, diagnostics[index], cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    exception.Data["QuestionId"] = questions[index].Id;
                    if (Interlocked.CompareExchange(ref failure, exception, null) is null) cancellation.Cancel();
                    return;
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Min(settings.MaxConcurrency, questions.Length)).Select(_ => Worker())).ConfigureAwait(false);
        var usage = DecisionUsage.Sum(diagnostics.Where(item => item.CallCount > 0).Select(item => item.Usage));
        if (failure is not null)
        {
            failure.Data["DecisionDiagnostics"] = diagnostics;
            failure.Data["DecisionUsage"] = usage;
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        cancellation.Token.ThrowIfCancellationRequested();
        return new SystemOneResponse
        {
            Model = Model,
            Answers = questions.Select((question, index) => new { question.Id, Answer = answers[index] }).ToDictionary(item => item.Id, item => item.Answer, StringComparer.Ordinal),
            Usage = usage,
            Diagnostics = diagnostics,
        };
    }

    private async Task<SystemOneAnswer> AnswerAsync(PreparedQuestion question, DecisionOptions settings, RequestOptions? options, DecisionDiagnostic diagnostic, CancellationToken cancellationToken)
    {
        var method = settings.Method;
        if (method == "auto")
        {
            lock (_capabilityLock) method = question.Keys.Length > 26 || _logprobsUnavailable ? "structured" : "logprobs";
            if (method == "structured") diagnostic.FallbackReason = question.Keys.Length > 26 ? "选项数量超过单 token 标签上限。" : "此客户端已观察到 logprobs 不可用。";
        }
        var usages = new List<DecisionUsage>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostic.Method = method;
            var request = new ChatCompletionRequest
            {
                Messages = question.Messages(method, diagnostic.MalformedRetries > 0),
                Thinking = settings.Thinking,
                ReasoningEffort = settings.ReasoningEffort,
                MaxTokens = settings.MaxTokens,
                Temperature = settings.Temperature,
                Logprobs = method == "logprobs" ? true : null,
                TopLogprobs = method == "logprobs" ? settings.TopLogprobs : null,
                ResponseFormat = method == "logprobs" ? null : new ResponseFormat { Type = "json_object" },
            };
            diagnostic.CallCount++;
            var usage = new DecisionUsage();
            var recorded = false;
            try
            {
                var result = await _chat.CompleteChatResponseAsync(request, CopyOptions(options, cancellationToken)).ConfigureAwait(false);
                using var raw = result.GetRawResponse();
                using var document = JsonDocument.Parse(raw.Content?.ToString() ?? string.Empty);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("completion 响应必须是对象。");
                usage = DecisionUsage.Read(document.RootElement);
                usages.Add(usage);
                recorded = true;
                var choice = DecisionReadout.Choice(document.RootElement);
                var text = DecisionReadout.Text(choice);
                double[]? distribution;
                if (method == "logprobs")
                {
                    distribution = DecisionReadout.Logprobs(choice, question, diagnostic, out var reason);
                    if (distribution is null)
                    {
                        if (settings.Method != "auto") throw new InvalidOperationException(reason);
                        lock (_capabilityLock)
                        {
                            if (++_absences >= 2) _logprobsUnavailable = true;
                        }
                        diagnostic.FallbackReason = reason;
                        method = "structured";
                        continue;
                    }
                    lock (_capabilityLock)
                    {
                        _absences = 0;
                        _logprobsUnavailable = false;
                    }
                }
                else distribution = DecisionReadout.Json(text, question, method, settings, diagnostic);
                diagnostic.ProbabilitySource = method == "logprobs" ? "token_logprobs" : method == "structured" ? "model_reported" : "one_hot";
                return DecisionReadout.Answer(question, distribution);
            }
            catch (DeepSeekException exception) when (settings.Method == "auto" && method == "logprobs" && (IsLogprobsRefusal(exception) || exception.Status >= 500 && exception.Status <= 599))
            {
                if (IsLogprobsRefusal(exception))
                {
                    lock (_capabilityLock) _logprobsUnavailable = true;
                    diagnostic.FallbackReason = "服务端明确拒绝 logprobs 字段。";
                }
                else diagnostic.FallbackReason = "pipeline 重试后服务端仍返回 5xx；仅为本题回退。";
                method = "structured";
            }
            catch (JsonException exception)
            {
                if (diagnostic.MalformedRetries >= settings.MaxMalformedRetries)
                    throw new InvalidOperationException("决策答案格式无效；纠正次数已耗尽。", exception);
                diagnostic.MalformedRetries++;
            }
            finally
            {
                if (!recorded) usages.Add(usage);
                diagnostic.Usage = DecisionUsage.Sum(usages);
            }
        }
    }

    private static bool IsLogprobsRefusal(DeepSeekException exception)
    {
        if (exception.Status is not (400 or 403 or 422)) return false;
        var message = exception.Message.ToLowerInvariant();
        if (new[] { "api key", "api_key", "api-key", "auth", "credential", "permission", "denied", "forbidden", "unknown model", "invalid model", "model_not_found", "no such model" }.Any(message.Contains))
            return false;
        return message.Contains("logprobs") &&
            (message.Contains("not supported") || message.Contains("unsupported") || message.Contains("unknown") || message.Contains("unrecognized"));
    }

    private static RequestOptions CopyOptions(RequestOptions? source, CancellationToken cancellationToken)
    {
        var copy = new RequestOptions { CancellationToken = cancellationToken, BufferResponse = source?.BufferResponse ?? true };
        if (source is null) return copy;
        copy.ErrorOptions = source.ErrorOptions;
        foreach (var field in RequestOptionFields)
        {
            var value = field.GetValue(source);
            field.SetValue(copy, value switch
            {
                Array array => array.Clone(),
                System.Collections.IList list => Activator.CreateInstance(field.FieldType, list),
                _ => value,
            });
        }
        return copy;
    }
}
