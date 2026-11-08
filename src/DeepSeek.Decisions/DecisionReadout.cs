using System.Text.Json;

namespace DeepSeek.Decisions;

internal static class DecisionReadout
{
    internal static JsonElement Choice(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
            throw new JsonException("模型必须返回一个 completion choice。");
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object) throw new JsonException("completion choice 必须是对象。");
        var stop = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String ? finish.GetString() : null;
        if (stop is "length" or "max_tokens" or "max_output_tokens" or "model_context_window_exceeded")
            throw new InvalidOperationException("决策答案被截断；请检查输出预算或上下文长度。");
        if (stop is "refusal" or "content_filter") throw new InvalidOperationException("模型拒绝回答此决策问题。");
        if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("模型拒绝回答此决策问题。");
        return choice;
    }

    internal static string Text(JsonElement choice)
    {
        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString()))
            throw new JsonException("模型未返回可读取的答案正文。");
        return content.GetString()!;
    }

    internal static double[]? Logprobs(JsonElement choice, PreparedQuestion question, DecisionDiagnostic diagnostic, out string reason)
    {
        reason = "模型未返回答案 token 的 logprobs。";
        if (!choice.TryGetProperty("logprobs", out var logprobs) || logprobs.ValueKind != JsonValueKind.Object ||
            !logprobs.TryGetProperty("content", out var tokens) || tokens.ValueKind != JsonValueKind.Array || tokens.GetArrayLength() == 0)
            return null;
        var labels = question.Labels;
        foreach (var token in tokens.EnumerateArray())
        {
            if (!token.TryGetProperty("token", out var text) || text.ValueKind != JsonValueKind.String) throw new JsonException("logprobs 缺少 token 文本。");
            var label = text.GetString()!.Trim().ToUpperInvariant();
            if (label.Length == 0) continue;
            var sampled = Array.IndexOf(labels, label);
            if (sampled < 0 || !string.Equals(Text(choice).Trim(), label, StringComparison.OrdinalIgnoreCase))
                throw new JsonException("答案正文与首个标签 token 不一致。");
            var sampledLogprob = Logprob(token);
            if (labels.Length == 1)
            {
                if (double.IsNegativeInfinity(sampledLogprob)) throw new JsonException("答案 token 没有概率质量。");
                return [1];
            }
            reason = "logprobs 没有可用于比较的候选分布。";
            if (!token.TryGetProperty("top_logprobs", out var alternatives) || alternatives.ValueKind != JsonValueKind.Array || alternatives.GetArrayLength() < 2)
                return null;
            var weights = Enumerable.Repeat(double.NegativeInfinity, labels.Length).ToArray();
            weights[sampled] = sampledLogprob;
            var rivals = 0;
            foreach (var alternative in alternatives.EnumerateArray())
            {
                if (alternative.ValueKind != JsonValueKind.Object) throw new JsonException("候选 logprob 必须是对象。");
                if (!alternative.TryGetProperty("logprob", out var reported) || reported.ValueKind == JsonValueKind.Null) continue;
                var value = Logprob(alternative);
                if (!alternative.TryGetProperty("token", out var alternativeToken) || alternativeToken.ValueKind != JsonValueKind.String)
                    throw new JsonException("候选 logprob 缺少 token 文本。");
                var index = Array.IndexOf(labels, alternativeToken.GetString()!.Trim().ToUpperInvariant());
                if (index < 0 || index == sampled) continue;
                weights[index] = value;
                rivals++;
            }
            if (rivals == 0) return null;
            diagnostic.MissingLabels = labels.Where((_, index) => double.IsNegativeInfinity(weights[index])).ToArray();
            return DecisionMath.Softmax(weights);
        }
        throw new JsonException("logprobs 仅包含空白 token。");
    }

    internal static double[] Json(string text, PreparedQuestion question, string method, DecisionOptions options, DecisionDiagnostic diagnostic)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var field = method == "discrete" ? question.Kind : question.Kind == "noul" ? "noul" : "probabilities";
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty(field, out var value))
            throw new JsonException("答案必须仅包含字段 " + field + "。");
        if (method == "discrete")
        {
            int index;
            if (question.Kind == "noul")
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException("discrete noul 必须是布尔值。");
                index = value.GetBoolean() ? 0 : 1;
            }
            else if (question.Kind == "score")
            {
                var number = Number(value);
                if (number != Math.Truncate(number) || number >= question.Keys.Length) throw new JsonException("discrete score 必须是合法等级索引。");
                index = (int)number;
            }
            else
            {
                index = value.ValueKind == JsonValueKind.String ? Array.IndexOf(question.Labels, value.GetString()) : -1;
                if (index < 0) throw new JsonException("discrete choice 必须是题目中的标签。");
            }
            return Enumerable.Range(0, question.Keys.Length).Select(candidate => candidate == index ? 1.0 : 0.0).ToArray();
        }
        if (question.Kind == "noul")
        {
            var probability = Number(value);
            if (probability > 1) throw new JsonException("noul 必须在 0 到 1 之间。");
            return [probability, 1 - probability];
        }
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("probabilities 必须是对象。");
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != question.Keys.Length || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != question.Keys.Length ||
            properties.Any(property => !question.Keys.Contains(property.Name))) throw new JsonException("probabilities 必须完整且仅包含题目的选项键。");
        var distribution = question.Keys.Select(key => Number(value.GetProperty(key))).ToArray();
        if (options.NormalizeProbabilities && Math.Abs(distribution.Sum() - 1) > 1e-6)
        {
            diagnostic.OriginalProbabilities = question.Keys.Select((key, index) => new { key, value = distribution[index] }).ToDictionary(item => item.key, item => item.value, StringComparer.Ordinal);
            distribution = DecisionMath.Normalize(distribution);
        }
        return distribution;
    }

    internal static SystemOneAnswer Answer(PreparedQuestion question, double[] distribution)
    {
        if (question.Kind == "noul") return new SystemOneAnswer { Type = "noul", Noul = distribution[0] };
        var probabilities = question.Keys.Select((key, index) => new { key, value = distribution[index] }).ToDictionary(item => item.key, item => item.value, StringComparer.Ordinal);
        if (question.Kind == "choice") return new SystemOneAnswer
        {
            Type = "choice",
            Choice = question.Keys[Array.IndexOf(distribution, distribution.Max())],
            Probabilities = probabilities,
            Confidence = DecisionMath.ChoiceConfidence(distribution),
        };
        return new SystemOneAnswer
        {
            Type = "score",
            Score = DecisionMath.Normalize(distribution).Select((probability, index) => probability * index).Sum(),
            Probabilities = probabilities,
            Confidence = DecisionMath.ScoreConfidence(distribution),
            Legend = question.Legend,
        };
    }

    private static double Logprob(JsonElement value)
    {
        if (!value.TryGetProperty("logprob", out var probability) || probability.ValueKind != JsonValueKind.Number ||
            !probability.TryGetDouble(out var number) || double.IsNaN(number) || number > 0)
            throw new JsonException("logprob 必须是非正数，不能是 NaN 或正无穷。");
        return number;
    }

    private static double Number(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !DecisionMath.IsFinite(number) || number < 0)
            throw new JsonException("概率或等级必须是有限非负数。");
        return number;
    }
}
