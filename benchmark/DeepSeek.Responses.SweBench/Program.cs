using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeepSeek;
using DeepSeek.Agents.AI;
using Microsoft.Extensions.AI;

namespace DeepSeek.Responses.SweBench;

internal static class Program
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Any(static arg => arg is "--help" or "-h"))
        {
            Console.WriteLine(BenchmarkOptions.Usage);
            return 0;
        }

        try
        {
            var options = BenchmarkOptions.Parse(args);
            if (options.SelfTest)
            {
                await SelfTest.RunAsync().ConfigureAwait(false);
                return 0;
            }

            var apiKey = ApiKeyResolver.Resolve(options.ApiKey);
            if (string.IsNullOrWhiteSpace(apiKey) || string.Equals(apiKey, "test-key", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "A real DeepSeek API key is required. Set DEEPSEEK_API_KEY, pass --api-key, or create sample/appsettings.json.");
            }

            Directory.CreateDirectory(options.OutputDirectory);
            Directory.CreateDirectory(options.WorkRoot);
            var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
            var runRoot = Path.Combine(options.WorkRoot, runId);
            Directory.CreateDirectory(runRoot);

            var manifest = await TaskManifest.LoadAsync(options.ManifestPath).ConfigureAwait(false);
            using var dataset = new SweSharpBenchDatasetClient(manifest.Dataset, manifest.Split, manifest.Revision);
            var selectedTasks = await dataset.SelectTasksAsync(manifest.Tasks, options.InstanceIds, CancellationToken.None).ConfigureAwait(false);
            if (selectedTasks.Count == 0)
            {
                throw new InvalidOperationException("No benchmark instances were selected.");
            }

            Console.WriteLine($"SWE-Sharp-Bench local evaluation: {selectedTasks.Count} task(s), model={options.Model}");
            Console.WriteLine($"Workspaces: {runRoot}");

            var allResults = new List<TaskRunResult>(selectedTasks.Count);
            foreach (var task in selectedTasks)
            {
                Console.WriteLine($"\n[{task.InstanceId}] starting");
                var result = await TaskRunner.RunAsync(task, manifest, options, apiKey, runRoot, dataset).ConfigureAwait(false);
                allResults.Add(result);
                Console.WriteLine($"[{task.InstanceId}] {result.Status.ToWireValue()} ({result.Rounds} round(s), {result.DurationSeconds:F1}s)");
            }

            await ResultWriter.WriteAsync(options.OutputDirectory, manifest, options, allResults).ConfigureAwait(false);
            var scored = allResults.Where(static result => result.Status != TaskRunStatus.EnvironmentError).ToList();
            var resolved = scored.Count(static result => result.Status == TaskRunStatus.Resolved);
            Console.WriteLine($"\nLocal resolved rate: {(scored.Count == 0 ? "n/a" : $"{resolved}/{scored.Count} ({resolved / (double)scored.Count:P1})")}");
            Console.WriteLine($"Results: {Path.Combine(options.OutputDirectory, "results.json")}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Benchmark cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Benchmark failed: {SecretRedactor.Redact(exception.Message)}");
            return 1;
        }
    }
}

internal sealed class BenchmarkOptions
{
    public const int DefaultMaxTurns = 50;
    public const int DefaultTaskTimeoutSeconds = 30 * 60;
    public const int DefaultCommandTimeoutSeconds = 5 * 60;
    public const int ApiRequestTimeoutSeconds = 120;
    public const int MaxToolOutputCharacters = 12_000;

    public string? ApiKey { get; private set; }
    public string Model { get; private set; } = "deepseek-v4-flash";
    public string WorkRoot { get; private set; } = Path.GetFullPath(Path.Combine("artifacts", "swe-sharp-bench", "workspaces"));
    public string OutputDirectory { get; private set; } = Path.GetFullPath(Path.Combine("artifacts", "swe-sharp-bench", "output"));
    public string ManifestPath { get; private set; } = Path.Combine(AppContext.BaseDirectory, "tasks.json");
    public IReadOnlyList<string>? InstanceIds { get; private set; }
    public int MaxTurns { get; private set; } = DefaultMaxTurns;
    public TimeSpan TaskTimeout { get; private set; } = TimeSpan.FromSeconds(DefaultTaskTimeoutSeconds);
    public TimeSpan CommandTimeout { get; private set; } = TimeSpan.FromSeconds(DefaultCommandTimeoutSeconds);
    public bool Setup { get; private set; } = true;
    public bool SelfTest { get; private set; }

    public static string Usage => """
        Usage: dotnet run --project benchmark/DeepSeek.Responses.SweBench -- [options]

        Options:
          --instances <id,id,...>  Run only the selected instance IDs (default: all manifest tasks).
          --model <name>           Responses model (default: deepseek-v4-flash).
          --api-key <key>          API key; otherwise DEEPSEEK_API_KEY or sample/appsettings.json is used.
          --work-root <directory>  Temporary workspace root (default: artifacts/swe-sharp-bench/workspaces).
          --output-dir <directory> Output files (default: artifacts/swe-sharp-bench/output).
          --max-turns <number>     Maximum Responses/tool rounds per task (default: 50).
          --task-timeout <time>    Total task timeout, in seconds or 30m/1h format (default: 30m).
          --command-timeout <time> Per-tool command timeout (default: 5m).
          --setup[=true|false]     Restore and build repository dependencies (default: true).
          --manifest <file>        Override the copied task manifest.
          --self-test              Run deterministic safety and parser checks without an API call.
          --help                   Show this help.

        This is a Docker-free local SWE-Sharp-Bench-compatible evaluation. It does not produce official leaderboard scores.
        """;

    public static BenchmarkOptions Parse(IReadOnlyList<string> args)
    {
        var result = new BenchmarkOptions();
        var instances = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            var (name, inlineValue) = SplitArgument(argument);
            if (name is "--self-test")
            {
                result.SelfTest = true;
                continue;
            }

            if (name is "--no-setup")
            {
                result.Setup = false;
                continue;
            }

            if (name is "--setup")
            {
                result.Setup = ParseBoolean(GetValue(name, inlineValue, args, ref index, optional: true), defaultValue: true);
                continue;
            }

            var value = GetValue(name, inlineValue, args, ref index);
            switch (name)
            {
                case "--instances":
                    instances.AddRange(value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--model":
                    result.Model = RequireValue(name, value);
                    break;
                case "--api-key":
                    result.ApiKey = RequireValue(name, value);
                    break;
                case "--work-root":
                    result.WorkRoot = Path.GetFullPath(RequireValue(name, value));
                    break;
                case "--output-dir":
                    result.OutputDirectory = Path.GetFullPath(RequireValue(name, value));
                    break;
                case "--manifest":
                    result.ManifestPath = Path.GetFullPath(RequireValue(name, value));
                    break;
                case "--max-turns":
                    result.MaxTurns = ParsePositiveInt(name, value);
                    break;
                case "--task-timeout":
                    result.TaskTimeout = ParseDuration(name, value);
                    break;
                case "--command-timeout":
                    result.CommandTimeout = ParseDuration(name, value);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{name}'. Use --help for usage.");
            }
        }

        result.InstanceIds = instances.Count == 0 ? null : instances;
        return result;
    }

    private static (string Name, string? Value) SplitArgument(string argument)
    {
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument '{argument}'. Use --help for usage.");
        }

        var separator = argument.IndexOf('=');
        return separator < 0 ? (argument, null) : (argument[..separator], argument[(separator + 1)..]);
    }

    private static string GetValue(string name, string? inlineValue, IReadOnlyList<string> args, ref int index, bool optional = false)
    {
        if (inlineValue is not null)
        {
            return inlineValue;
        }

        if (optional && (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal)))
        {
            return "true";
        }

        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"Option '{name}' requires a value.");
        }

        index++;
        return args[index];
    }

    private static string RequireValue(string name, string value)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"Option '{name}' requires a non-empty value.") : value;

    private static bool ParseBoolean(string value, bool defaultValue)
        => string.IsNullOrWhiteSpace(value) ? defaultValue : bool.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"Invalid boolean '{value}'.");

    private static int ParsePositiveInt(string name, string value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : throw new ArgumentException($"Option '{name}' requires a positive integer.");

    private static TimeSpan ParseDuration(string name, string value)
    {
        if (TimeSpan.TryParse(value, out var timeSpan) && timeSpan > TimeSpan.Zero)
        {
            return timeSpan;
        }

        var match = Regex.Match(value.Trim(), "^(?<number>[0-9]+(?:\\.[0-9]+)?)(?<unit>[smh]?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !double.TryParse(match.Groups["number"].Value, out var number) || number <= 0)
        {
            throw new ArgumentException($"Option '{name}' requires a duration such as 120, 5m, or 1h.");
        }

        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        return unit switch
        {
            "h" => TimeSpan.FromHours(number),
            "m" => TimeSpan.FromMinutes(number),
            _ => TimeSpan.FromSeconds(number),
        };
    }
}

internal static class ApiKeyResolver
{
    public static string? Resolve(string? explicitKey)
    {
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey;
        }

        foreach (var variable in new[] { "DEEPSEEK_API_KEY", "DEEPSEEK_APIKEY" })
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            {
                return Environment.GetEnvironmentVariable(variable);
            }
        }

        foreach (var path in new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "sample", "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "sample", "DeepSeek.Sample", "appsettings.json"),
        })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("apiKey", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
            catch (JsonException)
            {
                // An invalid local config is treated as no key; the error is reported without echoing its contents.
            }
        }

        return null;
    }
}

internal sealed class TaskManifest
{
    [JsonPropertyName("dataset")]
    public string Dataset { get; init; } = "microsoft/SWE-Sharp-Bench";

    [JsonPropertyName("split")]
    public string Split { get; init; } = "train";

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = string.Empty;

    [JsonPropertyName("tasks")]
    public List<TaskDefinition> Tasks { get; init; } = [];

    public static async Task<TaskManifest> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<TaskManifest>(stream, new JsonSerializerOptions(Program.JsonOptions)
        {
            PropertyNameCaseInsensitive = true,
        }).ConfigureAwait(false);
        return manifest ?? throw new InvalidDataException($"Manifest '{path}' is empty.");
    }

    public IReadOnlyList<TaskDefinition> Select(IReadOnlyList<string>? instanceIds)
    {
        if (instanceIds is null || instanceIds.Count == 0)
        {
            return Tasks;
        }

        var requested = instanceIds.ToHashSet(StringComparer.Ordinal);
        var selected = Tasks.Where(task => requested.Contains(task.InstanceId)).ToList();
        var missing = requested.Where(id => selected.All(task => !string.Equals(task.InstanceId, id, StringComparison.Ordinal))).ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException("Unknown instance ID(s): " + string.Join(", ", missing));
        }

        return selected;
    }
}

internal sealed class TaskDefinition
{
    [JsonPropertyName("instance_id")]
    public string InstanceId { get; init; } = string.Empty;

    [JsonPropertyName("repo")]
    public string Repo { get; init; } = string.Empty;

    [JsonPropertyName("repo_url")]
    public string RepoUrl { get; init; } = string.Empty;

    [JsonPropertyName("base_commit")]
    public string BaseCommit { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("issue_url")]
    public string IssueUrl { get; init; } = string.Empty;

    [JsonPropertyName("problem_statement")]
    public string ProblemStatement { get; init; } = string.Empty;

    [JsonPropertyName("fail_to_pass")]
    public List<string> FailToPass { get; init; } = [];

    [JsonPropertyName("pass_to_pass")]
    public List<string> PassToPass { get; init; } = [];

}

internal sealed class SweSharpBenchDatasetClient : IDisposable
{
    private const string RowsEndpoint = "https://datasets-server.huggingface.co/rows";
    private const string DatasetApiEndpoint = "https://huggingface.co/api/datasets";
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly string _dataset;
    private readonly string _split;
    private readonly string _revision;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _patches = new(StringComparer.Ordinal);
    private Task<IReadOnlyDictionary<string, JsonElement>>? _rowsTask;
    private bool _revisionChecked;

    public SweSharpBenchDatasetClient(string dataset, string split, string revision)
    {
        _dataset = dataset;
        _split = split;
        _revision = revision;
    }

    public async Task<IReadOnlyList<TaskDefinition>> SelectTasksAsync(
        IReadOnlyList<TaskDefinition> manifestTasks,
        IReadOnlyList<string>? instanceIds,
        CancellationToken cancellationToken)
    {
        if (instanceIds is null || instanceIds.Count == 0)
        {
            return manifestTasks;
        }

        var requested = instanceIds.Distinct(StringComparer.Ordinal).ToArray();
        var selected = requested
            .Select(id => manifestTasks.FirstOrDefault(task => string.Equals(task.InstanceId, id, StringComparison.Ordinal)))
            .Where(static task => task is not null)
            .Cast<TaskDefinition>()
            .ToList();
        var missing = requested.Where(id => selected.All(task => !string.Equals(task.InstanceId, id, StringComparison.Ordinal))).ToArray();
        if (missing.Length > 0)
        {
            var rows = await LoadRowsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var instanceId in missing)
            {
                if (!rows.TryGetValue(instanceId, out var row))
                {
                    throw new ArgumentException("Unknown instance ID: " + instanceId);
                }

                selected.Add(CreateTaskDefinition(row));
            }
        }

        return selected;
    }

    public Task<string> GetTestPatchAsync(string instanceId, CancellationToken cancellationToken)
        => _patches.GetOrAdd(instanceId, id => new Lazy<Task<string>>(() => FetchTestPatchAsync(id, cancellationToken))).Value;

    private async Task<string> FetchTestPatchAsync(string instanceId, CancellationToken cancellationToken)
    {
        var rows = await LoadRowsAsync(cancellationToken).ConfigureAwait(false);
        if (!rows.TryGetValue(instanceId, out var value))
        {
            throw new InvalidDataException($"The SWE-Sharp-Bench dataset did not contain '{instanceId}'.");
        }

        var testPatch = value.GetProperty("test_patch").GetString();
        if (string.IsNullOrWhiteSpace(testPatch))
        {
            throw new InvalidDataException($"The SWE-Sharp-Bench dataset has no test patch for '{instanceId}'.");
        }

        return testPatch;
    }

    private async Task<IReadOnlyDictionary<string, JsonElement>> LoadRowsAsync(CancellationToken cancellationToken)
    {
        if (_rowsTask is not null)
        {
            return await _rowsTask.ConfigureAwait(false);
        }

        _rowsTask = LoadRowsCoreAsync(cancellationToken);
        return await _rowsTask.ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<string, JsonElement>> LoadRowsCoreAsync(CancellationToken cancellationToken)
    {
        await EnsureRevisionAsync(cancellationToken).ConfigureAwait(false);
        var rows = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var offset = 0; offset < 150; offset += 100)
        {
            var length = Math.Min(100, 150 - offset);
            var query = $"{RowsEndpoint}?dataset={Uri.EscapeDataString(_dataset)}&config=default&split={Uri.EscapeDataString(_split)}&offset={offset}&length={length}";
            using var response = await _httpClient.GetAsync(query, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
            {
                var value = row.GetProperty("row");
                var instanceId = value.GetProperty("instance_id").GetString();
                if (!string.IsNullOrWhiteSpace(instanceId))
                {
                    rows[instanceId] = value.Clone();
                }
            }
        }

        return rows;
    }

    private static TaskDefinition CreateTaskDefinition(JsonElement value)
    {
        var instanceId = value.GetProperty("instance_id").GetString() ?? throw new InvalidDataException("Dataset row has no instance_id.");
        var repo = value.GetProperty("repo").GetString() ?? throw new InvalidDataException($"Dataset row '{instanceId}' has no repo.");
        var issueNumber = instanceId[(instanceId.LastIndexOf('-') + 1)..];
        return new TaskDefinition
        {
            InstanceId = instanceId,
            Repo = repo,
            RepoUrl = "https://github.com/" + repo + ".git",
            BaseCommit = value.GetProperty("base_commit").GetString() ?? string.Empty,
            Version = value.GetProperty("version").GetString() ?? string.Empty,
            IssueUrl = "https://github.com/" + repo + "/issues/" + issueNumber,
            ProblemStatement = value.GetProperty("problem_statement").GetString() ?? string.Empty,
            FailToPass = ParseTestList(value.GetProperty("FAIL_TO_PASS").GetString()),
            PassToPass = ParseTestList(value.GetProperty("PASS_TO_PASS").GetString()),
        };
    }

    private static List<string> ParseTestList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "[]")
        {
            return [];
        }

        return Regex.Matches(value, "'(?:\\\\.|[^'])*'", RegexOptions.CultureInvariant)
            .Select(match => match.Value[1..^1].Replace("\\'", "'", StringComparison.Ordinal))
            .ToList();
    }

    private async Task EnsureRevisionAsync(CancellationToken cancellationToken)
    {
        if (_revisionChecked || string.IsNullOrWhiteSpace(_revision))
        {
            return;
        }

        var datasetPath = string.Join('/', _dataset.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        using var response = await _httpClient.GetAsync($"{DatasetApiEndpoint}/{datasetPath}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var currentRevision = document.RootElement.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
        if (!string.Equals(currentRevision, _revision, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The dataset HEAD is '{currentRevision}', not the pinned revision '{_revision}'. Refusing to evaluate moving task data.");
        }

        _revisionChecked = true;
    }

    public void Dispose() => _httpClient.Dispose();
}

internal enum TaskRunStatus
{
    Resolved,
    Unresolved,
    EnvironmentError,
    ApiError,
    Timeout,
    AgentError,
}

internal static class TaskRunStatusExtensions
{
    public static string ToWireValue(this TaskRunStatus status)
        => status switch
        {
            TaskRunStatus.Resolved => "resolved",
            TaskRunStatus.Unresolved => "unresolved",
            TaskRunStatus.EnvironmentError => "environment_error",
            TaskRunStatus.ApiError => "api_error",
            TaskRunStatus.Timeout => "timeout",
            TaskRunStatus.AgentError => "agent_error",
            _ => "unknown",
        };
}

internal sealed record TaskRunResult
{
    public string InstanceId { get; init; } = string.Empty;
    public TaskRunStatus Status { get; init; }
    public string ErrorCategory { get; init; } = string.Empty;
    public string? Error { get; init; }
    public string TerminationReason { get; init; } = string.Empty;
    public int Rounds { get; init; }
    public double DurationSeconds { get; init; }
    public TokenTotals Tokens { get; init; } = new();
    public TestSummary? BaselineFailToPass { get; init; }
    public TestSummary? BaselinePassToPass { get; init; }
    public TestSummary? AgentFailToPass { get; init; }
    public TestSummary? AgentPassToPass { get; init; }
    public string TrajectoryPath { get; init; } = string.Empty;

    [JsonIgnore]
    public string ModelPatch { get; init; } = string.Empty;
}

internal sealed class TokenTotals
{
    public int Input { get; set; }
    public int Output { get; set; }
    public int Total { get; set; }
}

internal sealed class TestSummary
{
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool OutputTruncated { get; init; }
    public double DurationSeconds { get; init; }
    public string Result { get; init; } = string.Empty;
}

internal sealed class CommandResult
{
    public int ExitCode { get; init; } = -1;
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
    public bool OutputTruncated { get; init; }
    public double DurationSeconds { get; init; }

    public string CombinedOutput => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;
}

internal sealed class ProcessRunner
{
    private readonly string _workingDirectory;
    private readonly int _maxOutputCharacters;
    private readonly string? _secret;

    public ProcessRunner(string workingDirectory, int maxOutputCharacters, string? secret)
    {
        _workingDirectory = workingDirectory;
        _maxOutputCharacters = maxOutputCharacters;
        _secret = secret;
    }

    public async Task<CommandResult> RunAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null,
        int? maxOutputCharacters = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var startInfo = CreateStartInfo(command);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                return new CommandResult
                {
                    StandardError = "The process did not start.",
                    DurationSeconds = stopwatch.Elapsed.TotalSeconds,
                };
            }

            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            }

            process.StandardInput.Close();
            var outputTask = ReadLimitedAsync(process.StandardOutput, maxOutputCharacters ?? _maxOutputCharacters);
            var errorTask = ReadLimitedAsync(process.StandardError, maxOutputCharacters ?? _maxOutputCharacters);
            var readTask = Task.WhenAll(outputTask, errorTask);
            var timeoutTask = Task.Delay(timeout);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completed = await Task.WhenAny(readTask, timeoutTask, cancellationTask).ConfigureAwait(false);
            var timedOut = completed == timeoutTask;
            var cancelled = completed == cancellationTask;
            if (timedOut || cancelled)
            {
                TryKill(process);
            }

            try
            {
                await readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timedOut || cancelled)
            {
            }

            if (cancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            return new CommandResult
            {
                ExitCode = process.HasExited ? process.ExitCode : -1,
                StandardOutput = SecretRedactor.Redact(outputTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? outputTask.Result.Text : string.Empty, _secret),
                StandardError = SecretRedactor.Redact(errorTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? errorTask.Result.Text : string.Empty, _secret),
                TimedOut = timedOut,
                OutputTruncated = (outputTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && outputTask.Result.Truncated)
                    || (errorTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && errorTask.Result.Truncated),
                DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            };
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception exception)
        {
            TryKill(process);
            return new CommandResult
            {
                StandardError = SecretRedactor.Redact(exception.Message, _secret),
                DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            };
        }
    }

    private ProcessStartInfo CreateStartInfo(string command)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.FileName = "/bin/bash";
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);
        }

        foreach (var key in startInfo.Environment.Keys.Where(static key =>
                     key.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)
                     || key.Contains("APIKEY", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.Environment.Remove(key);
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static async Task<LimitedText> ReadLimitedAsync(StreamReader reader, int maxCharacters)
    {
        var builder = new StringBuilder(Math.Min(maxCharacters, 8192));
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var remaining = maxCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(remaining, count));
            }

            if (count > remaining)
            {
                truncated = true;
            }
        }

        if (truncated)
        {
            builder.Append("\n...[output truncated]...");
        }

        return new LimitedText(builder.ToString(), truncated);
    }

    private readonly record struct LimitedText(string Text, bool Truncated);
}

internal static class SecretRedactor
{
    public static string Redact(string? value, string? secret = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = string.IsNullOrEmpty(secret)
            ? value
            : value.Replace(secret, "[REDACTED_API_KEY]", StringComparison.Ordinal);
        return Regex.Replace(
            redacted,
            "(?i)(api[\\s_-]*key\\s*[:=]\\s*)[^\\s,;]+",
            "$1[REDACTED_API_KEY]",
            RegexOptions.CultureInvariant);
    }
}

internal static class WorkspacePath
{
    public static string Resolve(string workspace, string? requestedPath)
    {
        var root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = string.IsNullOrWhiteSpace(requestedPath) ? root : Path.GetFullPath(requestedPath, root);
        if (!IsWithin(root, path))
        {
            throw new UnauthorizedAccessException("The requested path is outside the task workspace.");
        }

        RejectSymlinkEscape(root, path);

        return path;
    }

    private static void RejectSymlinkEscape(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var fileInfo = new FileInfo(current);
            if (fileInfo.LinkTarget is not null)
            {
                throw new UnauthorizedAccessException("The requested path traverses a symbolic link.");
            }
        }
    }

    public static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static string Relative(string workspace, string path)
        => Path.GetRelativePath(workspace, path).Replace(Path.DirectorySeparatorChar, '/');

    public static string SanitizeTaskId(string instanceId)
        => Regex.Replace(instanceId, "[^A-Za-z0-9_.-]", "_", RegexOptions.CultureInvariant);
}

internal sealed class ToolExecution
{
    public string Output { get; init; } = string.Empty;
    public bool Failed { get; init; }
    public bool TimedOut { get; init; }
    public bool OutputTruncated { get; init; }
}

internal sealed class ToolDefinition
{
    public string Name { get; init; } = string.Empty;
    public AITool Declaration { get; init; } = null!;
    public Func<JsonElement, CancellationToken, Task<ToolExecution>> ExecuteAsync { get; init; } = null!;
}

internal sealed class ToolCatalog
{
    private readonly Dictionary<string, ToolDefinition> _definitions;

    private ToolCatalog(IEnumerable<ToolDefinition> definitions)
    {
        _definitions = definitions.ToDictionary(static definition => definition.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<AITool> Declarations => _definitions.Values.Select(static definition => definition.Declaration).ToArray();

    public bool TryGet(string name, out ToolDefinition definition) => _definitions.TryGetValue(name, out definition!);

    public static ToolCatalog Create(string workspace, BenchmarkOptions options, string apiKey)
    {
        var processRunner = new ProcessRunner(workspace, BenchmarkOptions.MaxToolOutputCharacters, apiKey);
        var git = new GitService(workspace, options, apiKey);
        var definitions = new List<ToolDefinition>
        {
            CreateTool(
                "list_files",
                "List files and directories in the task workspace. Paths are relative to the workspace.",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"Relative directory, default .\"},\"recursive\":{\"type\":\"boolean\",\"description\":\"Whether to include descendants\"}},\"additionalProperties\":false}",
                async (arguments, _) => await ListFilesAsync(workspace, arguments).ConfigureAwait(false)),
            CreateTool(
                "read_file",
                "Read a text file from the task workspace with line numbers. Never use this to access a path outside the workspace.",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"start_line\":{\"type\":\"integer\",\"minimum\":1},\"max_lines\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}},\"required\":[\"path\"],\"additionalProperties\":false}",
                async (arguments, _) => await ReadFileAsync(workspace, arguments).ConfigureAwait(false)),
            CreateTool(
                "search_repository",
                "Search text in repository files. The search is workspace-scoped and skips .git and virtualenv directories.",
                "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"path\":{\"type\":\"string\",\"description\":\"Relative directory or file, default .\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"required\":[\"query\"],\"additionalProperties\":false}",
                async (arguments, _) => await SearchRepositoryAsync(workspace, processRunner, arguments).ConfigureAwait(false)),
            CreateTool(
                "run_command",
                "Run a non-destructive repository command with the task workspace as its working directory. Do not use absolute paths, parent traversal, Docker, or commands that delete/reset the workspace.",
                "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}},\"required\":[\"command\"],\"additionalProperties\":false}",
                async (arguments, cancellationToken) => await RunCommandAsync(processRunner, arguments, options.CommandTimeout, cancellationToken).ConfigureAwait(false)),
            CreateTool(
                "apply_patch",
                "Apply a unified diff to files in the task workspace. Use paths relative to the repository and keep changes focused on the issue.",
                "{\"type\":\"object\",\"properties\":{\"patch\":{\"type\":\"string\"}},\"required\":[\"patch\"],\"additionalProperties\":false}",
                async (arguments, cancellationToken) => await ApplyPatchAsync(git, arguments, options.CommandTimeout, cancellationToken).ConfigureAwait(false)),
            CreateTool(
                "git_diff",
                "Show the current tracked and untracked diff for the task workspace.",
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
                async (_, cancellationToken) => await GetDiffAsync(git, cancellationToken).ConfigureAwait(false)),
            CreateTool(
                "run_tests",
                "Run focused tests in the task workspace. Use the repository's own test runner and keep the command read-only.",
                "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}},\"required\":[\"command\"],\"additionalProperties\":false}",
                async (arguments, cancellationToken) => await RunTestsAsync(processRunner, arguments, options.CommandTimeout, cancellationToken).ConfigureAwait(false)),
        };
        return new ToolCatalog(definitions);
    }

    private static ToolDefinition CreateTool(
        string name,
        string description,
        string schema,
        Func<JsonElement, CancellationToken, Task<ToolExecution>> executeAsync)
    {
        using var document = JsonDocument.Parse(schema);
        return new ToolDefinition
        {
            Name = name,
            Declaration = AIFunctionFactory.CreateDeclaration(name, description, document.RootElement.Clone()),
            ExecuteAsync = executeAsync,
        };
    }

    private static async Task<ToolExecution> ListFilesAsync(string workspace, JsonElement arguments)
    {
        try
        {
            var path = WorkspacePath.Resolve(workspace, GetString(arguments, "path") ?? ".");
            var recursive = GetBoolean(arguments, "recursive") ?? false;
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var entries = Directory.EnumerateFileSystemEntries(path, "*", searchOption)
                .Where(entry => !IsIgnoredPath(workspace, entry))
                .Select(entry => (Relative: WorkspacePath.Relative(workspace, entry), IsDirectory: Directory.Exists(entry)))
                .OrderBy(static entry => entry.Relative, StringComparer.Ordinal)
                .Take(2000)
                .ToList();
            var builder = new StringBuilder();
            var truncated = false;
            foreach (var entry in entries)
            {
                var formattedEntry = (entry.IsDirectory ? "[dir] " : "      ") + entry.Relative + Environment.NewLine;
                if (builder.Length + formattedEntry.Length > BenchmarkOptions.MaxToolOutputCharacters)
                {
                    truncated = true;
                    break;
                }

                builder.Append(formattedEntry);
            }

            if (entries.Count == 2000 || truncated)
            {
                builder.AppendLine("...[file list truncated]...");
            }

            return new ToolExecution { Output = builder.Length == 0 ? "(empty)" : builder.ToString(), OutputTruncated = entries.Count == 2000 || truncated };
        }
        catch (Exception exception)
        {
            return new ToolExecution { Failed = true, Output = "list_files failed: " + exception.Message };
        }
    }

    private static async Task<ToolExecution> ReadFileAsync(string workspace, JsonElement arguments)
    {
        try
        {
            var path = WorkspacePath.Resolve(workspace, GetString(arguments, "path"));
            if (!File.Exists(path))
            {
                return new ToolExecution { Failed = true, Output = $"File not found: {WorkspacePath.Relative(workspace, path)}" };
            }

            var startLine = Math.Max(1, GetInt32(arguments, "start_line") ?? 1);
            var maxLines = Math.Clamp(GetInt32(arguments, "max_lines") ?? 400, 1, 1000);
            var builder = new StringBuilder();
            using var reader = new StreamReader(path);
            var lineNumber = 0;
            var returned = 0;
            var reachedLimit = false;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                if (lineNumber < startLine)
                {
                    continue;
                }

                if (returned >= maxLines)
                {
                    reachedLimit = true;
                    break;
                }

                var formattedLine = lineNumber.ToString("D6") + ": " + line + Environment.NewLine;
                var remainingCharacters = BenchmarkOptions.MaxToolOutputCharacters - builder.Length;
                if (formattedLine.Length > remainingCharacters)
                {
                    if (remainingCharacters > 0)
                    {
                        builder.Append(formattedLine.AsSpan(0, remainingCharacters));
                    }

                    reachedLimit = true;
                    break;
                }

                builder.Append(formattedLine);
                returned++;
            }

            var truncated = reachedLimit;
            if (truncated)
            {
                builder.AppendLine("...[file output truncated]...");
            }

            return new ToolExecution { Output = builder.ToString(), OutputTruncated = truncated };
        }
        catch (Exception exception)
        {
            return new ToolExecution { Failed = true, Output = "read_file failed: " + exception.Message };
        }
    }

    private static async Task<ToolExecution> SearchRepositoryAsync(string workspace, ProcessRunner processRunner, JsonElement arguments)
    {
        try
        {
            var query = GetString(arguments, "query");
            if (string.IsNullOrWhiteSpace(query))
            {
                return new ToolExecution { Failed = true, Output = "search_repository requires a non-empty query." };
            }

            var path = WorkspacePath.Resolve(workspace, GetString(arguments, "path") ?? ".");
            var relativePath = WorkspacePath.Relative(workspace, path);
            var maxResults = Math.Clamp(GetInt32(arguments, "max_results") ?? 50, 1, 200);
            var command = "rg --line-number --no-heading --color never --hidden --glob '!.git/**' --max-count "
                + maxResults.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " " + ShellQuote(query) + " " + ShellQuote(relativePath);
            var result = await processRunner.RunAsync(command, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            if (result.ExitCode is not 0 and not 1)
            {
                return new ToolExecution { Failed = true, Output = result.CombinedOutput };
            }

            return new ToolExecution { Output = string.IsNullOrWhiteSpace(result.StandardOutput) ? "(no matches)" : result.StandardOutput, OutputTruncated = result.OutputTruncated };
        }
        catch (Exception exception)
        {
            return new ToolExecution { Failed = true, Output = "search_repository failed: " + exception.Message };
        }
    }

    private static async Task<ToolExecution> RunCommandAsync(ProcessRunner runner, JsonElement arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var command = GetString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ToolExecution { Failed = true, Output = "run_command requires a non-empty command." };
        }

        var validationError = WorkspaceCommandPolicy.Validate(command);
        if (validationError is not null)
        {
            return new ToolExecution { Failed = true, Output = validationError };
        }

        var result = await runner.RunAsync(command, timeout, cancellationToken).ConfigureAwait(false);
        return ToToolExecution(result);
    }

    private static async Task<ToolExecution> RunTestsAsync(ProcessRunner runner, JsonElement arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var command = GetString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ToolExecution { Failed = true, Output = "run_tests requires a non-empty test command." };
        }

        var validationError = WorkspaceCommandPolicy.Validate(command);
        if (validationError is not null)
        {
            return new ToolExecution { Failed = true, Output = validationError };
        }

        var result = await runner.RunAsync(command, timeout, cancellationToken).ConfigureAwait(false);
        return ToToolExecution(result);
    }

    private static async Task<ToolExecution> ApplyPatchAsync(GitService git, JsonElement arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var patch = GetString(arguments, "patch");
        if (string.IsNullOrWhiteSpace(patch))
        {
            return new ToolExecution { Failed = true, Output = "apply_patch requires a non-empty unified diff." };
        }

        if (patch.Length > 5_000_000)
        {
            return new ToolExecution { Failed = true, Output = "The patch is larger than the 5 MB safety limit." };
        }

        var result = await git.ApplyPatchAsync(patch, timeout, cancellationToken).ConfigureAwait(false);
        return ToToolExecution(result, result.ExitCode == 0 ? "Patch applied." : null);
    }

    private static async Task<ToolExecution> GetDiffAsync(GitService git, CancellationToken cancellationToken)
    {
        var result = await git.GetDiffAsync(cancellationToken, BenchmarkOptions.MaxToolOutputCharacters).ConfigureAwait(false);
        return ToToolExecution(result, string.IsNullOrWhiteSpace(result.CombinedOutput) ? "(no changes)" : null);
    }

    private static ToolExecution ToToolExecution(CommandResult result, string? successText = null)
    {
        var output = string.IsNullOrWhiteSpace(result.CombinedOutput) ? successText ?? "(no output)" : result.CombinedOutput;
        var outputTruncated = result.OutputTruncated;
        if (output.Length > BenchmarkOptions.MaxToolOutputCharacters)
        {
            output = output[..BenchmarkOptions.MaxToolOutputCharacters] + "\n...[output truncated]...";
            outputTruncated = true;
        }

        if (result.TimedOut)
        {
            output += "\n[command timed out and was terminated]";
        }

        return new ToolExecution
        {
            Output = output,
            Failed = result.ExitCode != 0 || result.TimedOut,
            TimedOut = result.TimedOut,
            OutputTruncated = outputTruncated,
        };
    }

    private static bool IsIgnoredPath(string workspace, string path)
    {
        var relative = WorkspacePath.Relative(workspace, path);
        return relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal)
            || relative == "bin" || relative.StartsWith("bin/", StringComparison.Ordinal)
            || relative == "obj" || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    private static string? GetString(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static bool? GetBoolean(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    internal static string ShellQuote(string value)
        => OperatingSystem.IsWindows()
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

internal static class WorkspaceCommandPolicy
{
    private static readonly Regex AbsolutePathPattern = new(
        @"(?<![A-Za-z0-9._/\\:-])(?:/|~(?:[\\/]|$)|[A-Za-z]:[\\/])",
        RegexOptions.CultureInvariant);

    public static string? Validate(string command)
    {
        if (command.Length > 20_000)
        {
            return "The command is larger than the 20,000 character safety limit.";
        }

        if (command.Contains("..", StringComparison.Ordinal)
            || command.Contains("docker", StringComparison.OrdinalIgnoreCase)
            || command.Contains("podman", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(command, "(^|[;&|])\\s*(git\\s+clone|curl|wget|ssh|scp|nc)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || AbsolutePathPattern.IsMatch(command)
            || Regex.IsMatch(command, "(^|[;&|])\\s*(rm\\s+-rf|git\\s+(?:clean|reset\\s+--hard))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || command.IndexOfAny([';', '|', '&', '<', '>', '`', '\r', '\n']) >= 0
            || command.Contains('$'))
        {
            return "The command was rejected because it contains an unsafe path, shell expansion/control operator, or destructive/container operation.";
        }

        return null;
    }
}

internal sealed class GitService
{
    private readonly string _workspace;
    private readonly ProcessRunner _runner;
    private readonly BenchmarkOptions _options;

    public GitService(string workspace, BenchmarkOptions options, string secret)
    {
        _workspace = workspace;
        _runner = new ProcessRunner(workspace, BenchmarkOptions.MaxToolOutputCharacters, secret);
        _options = options;
    }

    public Task<CommandResult> ApplyPatchAsync(string patch, TimeSpan timeout, CancellationToken cancellationToken)
        => _runner.RunAsync("git apply --whitespace=nowarn --recount -", timeout, cancellationToken, patch, 5_000_000);

    public Task<CommandResult> GetDiffAsync(CancellationToken cancellationToken, int maxOutputCharacters)
        => GetDiffCoreAsync(cancellationToken, maxOutputCharacters);

    public async Task<string> GetPatchAsync(CancellationToken cancellationToken)
    {
        var tracked = await _runner.RunAsync(
            "git diff --no-ext-diff --binary -- .",
            _options.CommandTimeout,
            cancellationToken,
            maxOutputCharacters: 20_000_000).ConfigureAwait(false);
        if (tracked.ExitCode != 0)
        {
            throw new InvalidOperationException("git diff failed: " + tracked.CombinedOutput);
        }

        var builder = new StringBuilder(tracked.StandardOutput);
        var untracked = await _runner.RunAsync(
            "git ls-files --others --exclude-standard -- .",
            _options.CommandTimeout,
            cancellationToken,
            maxOutputCharacters: 100_000).ConfigureAwait(false);
        if (untracked.ExitCode != 0)
        {
            throw new InvalidOperationException("git ls-files failed: " + untracked.CombinedOutput);
        }

        foreach (var relativePath in untracked.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var path = WorkspacePath.Resolve(_workspace, relativePath.Trim());
            if (Directory.Exists(path) || !File.Exists(path))
            {
                continue;
            }

            var noIndex = await _runner.RunAsync(
                "git diff --no-ext-diff --binary --no-index -- " + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null") + " " + ToolCatalog.ShellQuote(relativePath.Trim()),
                _options.CommandTimeout,
                cancellationToken,
                maxOutputCharacters: 20_000_000).ConfigureAwait(false);
            if (noIndex.ExitCode is not 0 and not 1)
            {
                throw new InvalidOperationException("git diff for untracked file failed: " + noIndex.CombinedOutput);
            }

            if (noIndex.StandardOutput.Length > 0)
            {
                if (builder.Length > 0 && !builder.ToString().EndsWith(Environment.NewLine, StringComparison.Ordinal))
                {
                    builder.AppendLine();
                }

                builder.Append(noIndex.StandardOutput);
            }
        }

        return builder.ToString();
    }

    private async Task<CommandResult> GetDiffCoreAsync(CancellationToken cancellationToken, int maxOutputCharacters)
    {
        var result = await _runner.RunAsync(
            "git diff --no-ext-diff --binary -- .",
            _options.CommandTimeout,
            cancellationToken,
            maxOutputCharacters: maxOutputCharacters).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return result;
        }

        var untracked = await _runner.RunAsync(
            "git ls-files --others --exclude-standard -- .",
            _options.CommandTimeout,
            cancellationToken,
            maxOutputCharacters: 100_000).ConfigureAwait(false);
        if (untracked.ExitCode != 0 || string.IsNullOrWhiteSpace(untracked.StandardOutput))
        {
            return result;
        }

        // The interactive tool keeps the untracked-file summary bounded. Full untracked files are included
        // later by GetPatchAsync for the predictions file.
        return new CommandResult
        {
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput + Environment.NewLine + "Untracked files:\n" + untracked.StandardOutput,
            StandardError = result.StandardError,
            OutputTruncated = result.OutputTruncated || untracked.OutputTruncated,
            DurationSeconds = result.DurationSeconds + untracked.DurationSeconds,
        };
    }
}

internal static class WorkspaceProvisioner
{
    public static async Task ProvisionAsync(
        TaskDefinition task,
        string workspace,
        BenchmarkOptions options,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(workspace))
        {
            throw new IOException($"Workspace already exists: {workspace}");
        }

        if (!Uri.TryCreate(task.RepoUrl, UriKind.Absolute, out var repositoryUri)
            || repositoryUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(repositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Task repository URL is not an HTTPS GitHub URL: {task.RepoUrl}");
        }

        var parent = Directory.GetParent(workspace)?.FullName ?? throw new InvalidOperationException("Workspace has no parent directory.");
        Directory.CreateDirectory(parent);
        var cloneRunner = new ProcessRunner(parent, BenchmarkOptions.MaxToolOutputCharacters, apiKey);
        var clone = await cloneRunner.RunAsync(
            "git clone --no-tags " + ToolCatalog.ShellQuote(task.RepoUrl) + " " + ToolCatalog.ShellQuote(Path.GetFileName(workspace)),
            options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(clone, "git clone", task.InstanceId);

        var workspaceRunner = new ProcessRunner(workspace, BenchmarkOptions.MaxToolOutputCharacters, apiKey);
        var checkout = await workspaceRunner.RunAsync(
            "git -c advice.detachedHead=false checkout --detach " + ToolCatalog.ShellQuote(task.BaseCommit),
            options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(checkout, "git checkout", task.InstanceId);

        if (!options.Setup)
        {
            return;
        }

        var dependencyRunner = new ProcessRunner(workspace, BenchmarkOptions.MaxToolOutputCharacters, apiKey);
        var restore = await dependencyRunner.RunAsync(
            "dotnet restore --nologo",
            options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(restore, "dotnet restore", task.InstanceId);

        var build = await dependencyRunner.RunAsync(
            "dotnet build --no-restore --nologo",
            options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(build, "dotnet build", task.InstanceId);
    }

    private static void EnsureSuccess(CommandResult result, string operation, string instanceId)
    {
        if (result.ExitCode == 0 && !result.TimedOut)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.CombinedOutput) ? "no process output" : Tail(result.CombinedOutput, 2000);
        throw new InvalidOperationException($"{operation} failed for {instanceId}: {detail}");
    }

    private static string Tail(string value, int maxCharacters)
        => value.Length <= maxCharacters ? value : value[^maxCharacters..];
}

internal static class TestCommandBuilder
{
    public static string Build(TaskDefinition task, bool failToPass)
    {
        var rawTests = failToPass ? task.FailToPass : task.PassToPass;
        if (rawTests.Count == 0)
        {
            return "dotnet test --no-restore --nologo --filter 'FullyQualifiedName~__swe_sharp_no_tests__'";
        }

        var filter = rawTests
            .Distinct(StringComparer.Ordinal)
            .Select(static test => "FullyQualifiedName~" + test);
        return "dotnet test --no-restore --nologo --filter " + ToolCatalog.ShellQuote(string.Join("|", filter));
    }
}

internal sealed class AgentRunResult
{
    public int Rounds { get; init; }
    public string TerminationReason { get; init; } = string.Empty;
    public TokenTotals Tokens { get; init; } = new();
}

internal sealed class AgentLoop
{
    private readonly TaskDefinition _task;
    private readonly BenchmarkOptions _options;
    private readonly string _apiKey;
    private readonly string _workspace;
    private readonly ToolCatalog _tools;
    private readonly TrajectoryWriter _trajectory;

    public AgentLoop(
        TaskDefinition task,
        BenchmarkOptions options,
        string apiKey,
        string workspace,
        TrajectoryWriter trajectory)
    {
        _task = task;
        _options = options;
        _apiKey = apiKey;
        _workspace = workspace;
        _tools = ToolCatalog.Create(workspace, options, apiKey);
        _trajectory = trajectory;
    }

    public async Task<AgentRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, BuildUserPrompt(_task)),
        };
        var chatOptions = new ChatOptions
        {
            Instructions = BuildSystemPrompt(),
            Tools = _tools.Declarations.ToList(),
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = true,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
        };
        var tokenTotals = new TokenTotals();
        var responsesClient = new DeepSeekClient(_apiKey).GetResponsesClient(_options.Model);
        var chatClient = responsesClient.AsIChatClient();
        var terminationReason = "max_turns";
        var completedRounds = 0;

        for (var round = 1; round <= _options.MaxTurns; round++)
        {
            completedRounds = round;
            await _trajectory.WriteAsync(new
            {
                kind = "request",
                turn = round,
                at = DateTimeOffset.UtcNow,
                model = _options.Model,
                workspace = _workspace,
                history = history.Select(TraceFormatting.Message).ToArray(),
            }).ConfigureAwait(false);

            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var functionCalls = new List<FunctionCallContent>();
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(BenchmarkOptions.ApiRequestTimeoutSeconds));
            try
            {
                await foreach (var update in chatClient.GetStreamingResponseAsync(history, chatOptions, requestTimeout.Token).ConfigureAwait(false))
                {
                    await _trajectory.WriteAsync(TraceFormatting.Update(round, update)).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        content.Append(update.Text);
                    }

                    foreach (var reasoningContent in update.Contents.OfType<TextReasoningContent>())
                    {
                        reasoning.Append(reasoningContent.Text);
                    }

                    foreach (var functionCall in update.Contents.OfType<FunctionCallContent>())
                    {
                        var callId = functionCall.CallId ?? string.Empty;
                        if (string.IsNullOrEmpty(callId)
                            || functionCalls.All(existing => !string.Equals(existing.CallId, callId, StringComparison.Ordinal)))
                        {
                            functionCalls.Add(functionCall);
                        }
                    }

                    TraceFormatting.AccumulateUsage(tokenTotals, update);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && requestTimeout.IsCancellationRequested)
            {
                await _trajectory.WriteAsync(new { kind = "termination", turn = round, reason = "api_request_timeout" }).ConfigureAwait(false);
                throw new AgentTimeoutException("Responses API request exceeded the 120 second timeout.");
            }

            var assistantMessage = new ChatMessage(ChatRole.Assistant, content.ToString());
            if (reasoning.Length > 0)
            {
                assistantMessage.AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["reasoning_content"] = reasoning.ToString(),
                };
                assistantMessage.Contents.Add(new TextReasoningContent(reasoning.ToString()));
            }

            foreach (var functionCall in functionCalls)
            {
                assistantMessage.Contents.Add(functionCall);
            }

            if (assistantMessage.Text is not null || reasoning.Length > 0 || functionCalls.Count > 0)
            {
                history.Add(assistantMessage);
            }

            if (functionCalls.Count == 0)
            {
                terminationReason = "completed_without_tool_call";
                await _trajectory.WriteAsync(new
                {
                    kind = "termination",
                    turn = round,
                    reason = terminationReason,
                    final_text = content.ToString(),
                    token_usage = tokenTotals,
                }).ConfigureAwait(false);
                break;
            }

            foreach (var functionCall in functionCalls)
            {
                var argumentsJson = SerializeArguments(functionCall.Arguments ?? new Dictionary<string, object?>());
                await _trajectory.WriteAsync(new
                {
                    kind = "tool_call",
                    turn = round,
                    call_id = functionCall.CallId,
                    name = functionCall.Name,
                    arguments = argumentsJson,
                }).ConfigureAwait(false);

                ToolExecution execution;
                if (!_tools.TryGet(functionCall.Name, out var definition))
                {
                    execution = new ToolExecution { Failed = true, Output = $"Unknown tool '{functionCall.Name}'." };
                }
                else
                {
                    try
                    {
                        using var toolTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        toolTimeout.CancelAfter(_options.CommandTimeout);
                        using var argumentsDocument = JsonDocument.Parse(argumentsJson);
                        execution = await definition.ExecuteAsync(argumentsDocument.RootElement.Clone(), toolTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        execution = new ToolExecution { Failed = true, TimedOut = true, Output = "Tool execution timed out and was terminated." };
                    }
                    catch (Exception exception)
                    {
                        execution = new ToolExecution { Failed = true, Output = "Tool execution failed: " + exception.Message };
                    }
                }

                var toolOutput = FormatToolOutput(execution);
                history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(functionCall.CallId, toolOutput)]));
                await _trajectory.WriteAsync(new
                {
                    kind = "tool_result",
                    turn = round,
                    call_id = functionCall.CallId,
                    name = functionCall.Name,
                    failed = execution.Failed,
                    timed_out = execution.TimedOut,
                    output_truncated = execution.OutputTruncated,
                    output = toolOutput,
                }).ConfigureAwait(false);
            }
        }

        return new AgentRunResult
        {
            Rounds = completedRounds,
            TerminationReason = terminationReason,
            Tokens = tokenTotals,
        };
    }

    private string BuildSystemPrompt()
        => """
        You are a careful long-running repository coding agent. Solve the supplied SWE-Sharp-Bench issue in the checked-out C#/.NET repository.

        Work rules:
        - Inspect the repository before changing it. Use the provided workspace tools for all file operations.
        - The workspace is the only allowed filesystem scope. Do not use parent paths, absolute paths, Docker, or external repositories.
        - Make the smallest correct production change. Do not add or inspect hidden tests, test_patch data, gold patches, or benchmark answers.
        - Use apply_patch with a standard unified diff. Use dotnet build and dotnet test for focused regression tests, then inspect git_diff.
        - If a test fails, diagnose it and iterate. Keep the complete reasoning, assistant tool calls, and tool results in the conversation.
        - When the change is ready, leave the workspace with the intended source diff and briefly summarize the fix and tests.
        """;

    private static string BuildUserPrompt(TaskDefinition task)
    {
        var failToPass = string.Join(Environment.NewLine, task.FailToPass.Select(static test => "- " + test));
        var passToPass = string.Join(Environment.NewLine, task.PassToPass.Take(20).Select(static test => "- " + test));
        return $"""
            SWE-Sharp-Bench task: {task.InstanceId}
            Repository: {task.Repo}
            Base commit: {task.BaseCommit}
            Issue URL: {task.IssueUrl}
            Package version: {task.Version}

            Issue:
            {task.ProblemStatement}

            Public FAIL_TO_PASS tests:
            {failToPass}

            Public PASS_TO_PASS examples:
            {passToPass}

            Start by examining the relevant code and tests. Implement and validate the fix in the workspace.
            """;
    }

    private static string SerializeArguments(IDictionary<string, object?> arguments)
        => JsonSerializer.Serialize(arguments);

    private static string FormatToolOutput(ToolExecution execution)
    {
        var prefix = execution.Failed ? "[tool error] " : string.Empty;
        var timeout = execution.TimedOut ? "\n[timeout]" : string.Empty;
        return prefix + execution.Output + timeout;
    }
}

internal sealed class AgentTimeoutException : Exception
{
    public AgentTimeoutException(string message) : base(message)
    {
    }
}

internal sealed class TrajectoryWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly string? _secret;

    public TrajectoryWriter(string path, string? secret)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        _secret = secret;
        Path = path;
    }

    public string Path { get; }

    public async Task WriteAsync(object value)
    {
        var serialized = JsonSerializer.Serialize(value);
        await _writer.WriteLineAsync(SecretRedactor.Redact(serialized, _secret)).ConfigureAwait(false);
    }

    public void Dispose() => _writer.Dispose();
}

internal static class TraceFormatting
{
    public static object Message(ChatMessage message)
        => new
        {
            role = message.Role.ToString(),
            text = message.Text,
            contents = message.Contents.Select(Content).ToArray(),
        };

    public static object Update(int turn, ChatResponseUpdate update)
        => new
        {
            kind = "response_update",
            turn,
            role = update.Role.ToString(),
            text = update.Text,
            contents = update.Contents.Select(Content).ToArray(),
            properties = EventProperties(update.AdditionalProperties),
        };

    public static void AccumulateUsage(TokenTotals totals, ChatResponseUpdate update)
    {
        if (update.AdditionalProperties is null)
        {
            return;
        }

        if (update.AdditionalProperties.TryGetValue("response", out var responseValue)
            && responseValue is DeepSeek.Responses.Response response
            && response.Usage is not null)
        {
            totals.Input = response.Usage.InputTokens;
            totals.Output = response.Usage.OutputTokens;
            totals.Total = response.Usage.TotalTokens;
            return;
        }

        totals.Input = ReadInt(update.AdditionalProperties, "usage_input_tokens", totals.Input);
        totals.Output = ReadInt(update.AdditionalProperties, "usage_output_tokens", totals.Output);
        totals.Total = ReadInt(update.AdditionalProperties, "usage_total_tokens", totals.Total);
    }

    private static object Content(AIContent content)
    {
        return content switch
        {
            TextReasoningContent reasoning => new { type = "reasoning", text = reasoning.Text },
            FunctionCallContent call => new
            {
                type = "function_call",
                call_id = call.CallId,
                name = call.Name,
                arguments = call.Arguments,
            },
            FunctionResultContent result => new
            {
                type = "function_result",
                call_id = result.CallId,
                result = result.Result,
            },
            _ => new { type = content.GetType().Name, text = content.ToString() },
        };
    }

    private static Dictionary<string, object?> EventProperties(AdditionalPropertiesDictionary? properties)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (properties is null)
        {
            return result;
        }

        foreach (var pair in properties)
        {
            if (pair.Key is "response")
            {
                if (pair.Value is DeepSeek.Responses.Response response)
                {
                    result[pair.Key] = new
                    {
                        id = response.Id,
                        status = response.Status,
                        model = response.Model,
                        usage = response.Usage,
                    };
                }

                continue;
            }

            if (pair.Key.StartsWith("usage_", StringComparison.Ordinal)
                || pair.Key is "response_status" or "response_error" or "is_reasoning")
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            int integer => integer,
            long longValue when longValue <= int.MaxValue => (int)longValue,
            JsonElement element when element.TryGetInt32(out var jsonValue) => jsonValue,
            _ => fallback,
        };
    }
}

internal static class TaskRunner
{
    public static async Task<TaskRunResult> RunAsync(
        TaskDefinition task,
        TaskManifest manifest,
        BenchmarkOptions options,
        string apiKey,
        string runRoot,
        SweSharpBenchDatasetClient dataset)
    {
        var stopwatch = Stopwatch.StartNew();
        var taskDirectory = Path.Combine(runRoot, WorkspacePath.SanitizeTaskId(task.InstanceId));
        var agentWorkspace = Path.Combine(taskDirectory, "agent");
        var evaluationWorkspace = Path.Combine(taskDirectory, "evaluation");
        var trajectoryPath = Path.Combine(options.OutputDirectory, "trajectory", task.InstanceId + ".jsonl");
        Directory.CreateDirectory(taskDirectory);

        TestSummary? baselineFailToPass = null;
        TestSummary? baselinePassToPass = null;
        TestSummary? agentFailToPass = null;
        TestSummary? agentPassToPass = null;
        AgentRunResult? agentRun = null;
        var modelPatch = string.Empty;
        var baselineReady = false;
        var environmentStage = true;
        TaskRunResult result;

        using var trajectory = new TrajectoryWriter(trajectoryPath, apiKey);
        using var taskTimeout = new CancellationTokenSource(options.TaskTimeout);
        try
        {
            await trajectory.WriteAsync(new
            {
                kind = "task_started",
                instance_id = task.InstanceId,
                dataset = manifest.Dataset,
                dataset_revision = manifest.Revision,
                base_commit = task.BaseCommit,
                at = DateTimeOffset.UtcNow,
            }).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(agentWorkspace)!);
            await WorkspaceProvisioner.ProvisionAsync(task, agentWorkspace, options, apiKey, taskTimeout.Token).ConfigureAwait(false);
            await WorkspaceProvisioner.ProvisionAsync(task, evaluationWorkspace, options, apiKey, taskTimeout.Token).ConfigureAwait(false);

            // The hidden regression tests are fetched only for the separate evaluation workspace.
            // They are never placed in agentWorkspace or sent to the model.
            var testPatch = await dataset.GetTestPatchAsync(task.InstanceId, taskTimeout.Token).ConfigureAwait(false);
            var evaluationGit = new GitService(evaluationWorkspace, options, apiKey);
            var appliedTests = await evaluationGit.ApplyPatchAsync(testPatch, options.CommandTimeout, taskTimeout.Token).ConfigureAwait(false);
            if (appliedTests.ExitCode != 0 || appliedTests.TimedOut)
            {
                throw new EnvironmentSetupException("The SWE-Sharp-Bench test patch could not be applied.");
            }

            // C# test patches can add a regression test that intentionally does not compile at the
            // base commit. Run P2P on the untouched base workspace and F2P on the hidden-test workspace.
            baselinePassToPass = await RunTestsAsync(task, agentWorkspace, options, failToPass: false, taskTimeout.Token).ConfigureAwait(false);
            baselineFailToPass = await RunTestsAsync(task, evaluationWorkspace, options, failToPass: true, taskTimeout.Token).ConfigureAwait(false);
            if (!BaselineIsUsable(baselineFailToPass, baselinePassToPass))
            {
                throw new EnvironmentSetupException("Baseline test status does not match the SWE-Sharp-Bench metadata.");
            }

            baselineReady = true;
            environmentStage = false;
            var agentGit = new GitService(agentWorkspace, options, apiKey);
            agentRun = await new AgentLoop(task, options, apiKey, agentWorkspace, trajectory).RunAsync(taskTimeout.Token).ConfigureAwait(false);
            modelPatch = await agentGit.GetPatchAsync(CancellationToken.None).ConfigureAwait(false);

            var appliedAgentPatch = string.IsNullOrWhiteSpace(modelPatch)
                ? new CommandResult { ExitCode = 0 }
                : await evaluationGit.ApplyPatchAsync(modelPatch, options.CommandTimeout, taskTimeout.Token).ConfigureAwait(false);
            if (appliedAgentPatch.ExitCode != 0 || appliedAgentPatch.TimedOut)
            {
                throw new AgentPatchException("The generated patch could not be applied to the clean evaluation workspace.");
            }

            agentFailToPass = await RunTestsAsync(task, evaluationWorkspace, options, failToPass: true, taskTimeout.Token).ConfigureAwait(false);
            agentPassToPass = await RunTestsAsync(task, evaluationWorkspace, options, failToPass: false, taskTimeout.Token).ConfigureAwait(false);
            var resolved = agentFailToPass.ExitCode == 0 && agentPassToPass.ExitCode == 0 && !agentFailToPass.TimedOut && !agentPassToPass.TimedOut;
            result = new TaskRunResult
            {
                InstanceId = task.InstanceId,
                Status = resolved ? TaskRunStatus.Resolved : TaskRunStatus.Unresolved,
                ErrorCategory = resolved ? string.Empty : agentPassToPass.ExitCode != 0 ? "regression" : "fail_to_pass_failed",
                TerminationReason = agentRun.TerminationReason,
                Rounds = agentRun.Rounds,
                DurationSeconds = stopwatch.Elapsed.TotalSeconds,
                Tokens = agentRun.Tokens,
                BaselineFailToPass = baselineFailToPass,
                BaselinePassToPass = baselinePassToPass,
                AgentFailToPass = agentFailToPass,
                AgentPassToPass = agentPassToPass,
                TrajectoryPath = trajectoryPath,
                ModelPatch = modelPatch,
            };
        }
        catch (EnvironmentSetupException exception)
        {
            result = CreateFailure(TaskRunStatus.EnvironmentError, "environment_error", exception.Message);
        }
        catch (AgentPatchException exception)
        {
            if (Directory.Exists(agentWorkspace))
            {
                modelPatch = await TryGetPatchAsync(agentWorkspace, options, apiKey).ConfigureAwait(false);
            }

            result = CreateFailure(TaskRunStatus.AgentError, "patch_error", exception.Message);
        }
        catch (AgentTimeoutException exception)
        {
            if (Directory.Exists(agentWorkspace))
            {
                modelPatch = await TryGetPatchAsync(agentWorkspace, options, apiKey).ConfigureAwait(false);
            }

            result = CreateFailure(TaskRunStatus.Timeout, "timeout", exception.Message);
        }
        catch (OperationCanceledException exception)
        {
            if (Directory.Exists(agentWorkspace))
            {
                modelPatch = await TryGetPatchAsync(agentWorkspace, options, apiKey).ConfigureAwait(false);
            }

            result = CreateFailure(environmentStage ? TaskRunStatus.EnvironmentError : TaskRunStatus.Timeout, environmentStage ? "environment_error" : "timeout", exception.Message);
        }
        catch (DeepSeekException exception)
        {
            if (Directory.Exists(agentWorkspace))
            {
                modelPatch = await TryGetPatchAsync(agentWorkspace, options, apiKey).ConfigureAwait(false);
            }

            result = CreateFailure(TaskRunStatus.ApiError, CategorizeApiError(exception), exception.Message);
        }
        catch (Exception exception)
        {
            if (Directory.Exists(agentWorkspace))
            {
                modelPatch = await TryGetPatchAsync(agentWorkspace, options, apiKey).ConfigureAwait(false);
            }

            result = CreateFailure(baselineReady ? TaskRunStatus.AgentError : TaskRunStatus.EnvironmentError,
                baselineReady ? "agent_error" : "environment_error", exception.Message);
        }

        result = result with
        {
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            BaselineFailToPass = baselineFailToPass,
            BaselinePassToPass = baselinePassToPass,
            AgentFailToPass = agentFailToPass,
            AgentPassToPass = agentPassToPass,
            Rounds = agentRun?.Rounds ?? result.Rounds,
            Tokens = agentRun?.Tokens ?? result.Tokens,
            TrajectoryPath = trajectoryPath,
            ModelPatch = modelPatch,
        };
        await trajectory.WriteAsync(new
        {
            kind = "task_finished",
            status = result.Status.ToWireValue(),
            error_category = result.ErrorCategory,
            error = result.Error,
            rounds = result.Rounds,
            duration_seconds = result.DurationSeconds,
            token_usage = result.Tokens,
        }).ConfigureAwait(false);
        return result;

        TaskRunResult CreateFailure(TaskRunStatus status, string category, string? error)
            => new()
            {
                InstanceId = task.InstanceId,
                Status = status,
                ErrorCategory = category,
                Error = SecretRedactor.Redact(error, apiKey),
                TerminationReason = agentRun?.TerminationReason ?? category,
                Rounds = agentRun?.Rounds ?? 0,
                DurationSeconds = stopwatch.Elapsed.TotalSeconds,
                Tokens = agentRun?.Tokens ?? new TokenTotals(),
                TrajectoryPath = trajectoryPath,
                ModelPatch = modelPatch,
            };
    }

    private static async Task<TestSummary> RunTestsAsync(
        TaskDefinition task,
        string workspace,
        BenchmarkOptions options,
        bool failToPass,
        CancellationToken cancellationToken)
    {
        var tests = failToPass ? task.FailToPass : task.PassToPass;
        if (tests.Count == 0)
        {
            return new TestSummary
            {
                ExitCode = 0,
                Result = "not_applicable",
            };
        }

        var runner = new ProcessRunner(workspace, BenchmarkOptions.MaxToolOutputCharacters, null);
        var command = TestCommandBuilder.Build(task, failToPass);
        var result = await runner.RunAsync(command, options.CommandTimeout, cancellationToken, maxOutputCharacters: BenchmarkOptions.MaxToolOutputCharacters).ConfigureAwait(false);
        return new TestSummary
        {
            ExitCode = result.ExitCode,
            TimedOut = result.TimedOut,
            OutputTruncated = result.OutputTruncated,
            DurationSeconds = result.DurationSeconds,
            Result = result.ExitCode == 0 && !result.TimedOut ? "passed" : result.TimedOut ? "timeout" : "failed",
        };
    }

    private static bool BaselineIsUsable(TestSummary failToPass, TestSummary passToPass)
    {
        // F2P is expected to fail at base; P2P must pass. The dotnet test exit code is the
        // portable signal across the supported .NET test adapters.
        return (failToPass.Result == "not_applicable"
                || failToPass.ExitCode != 0 && !failToPass.TimedOut)
            && passToPass.ExitCode == 0
            && !passToPass.TimedOut;
    }

    private static string CategorizeApiError(DeepSeekException exception)
    {
        var text = exception.Message + " " + exception.ResponseContent;
        return text.Contains("context", StringComparison.OrdinalIgnoreCase)
            || text.Contains("token", StringComparison.OrdinalIgnoreCase) && text.Contains("length", StringComparison.OrdinalIgnoreCase)
            ? "context_length"
            : "api_error";
    }

    private static async Task<string> TryGetPatchAsync(string workspace, BenchmarkOptions options, string apiKey)
    {
        try
        {
            return await new GitService(workspace, options, apiKey).GetPatchAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }
}

internal sealed class EnvironmentSetupException : Exception
{
    public EnvironmentSetupException(string message) : base(message)
    {
    }
}

internal sealed class AgentPatchException : Exception
{
    public AgentPatchException(string message) : base(message)
    {
    }
}

internal static class ResultWriter
{
    public static async Task WriteAsync(
        string outputDirectory,
        TaskManifest manifest,
        BenchmarkOptions options,
        IReadOnlyList<TaskRunResult> results)
    {
        var trajectoryDirectory = Path.Combine(outputDirectory, "trajectory");
        Directory.CreateDirectory(trajectoryDirectory);
        var predictionsPath = Path.Combine(outputDirectory, "predictions.json");
        var predictions = results.Select(result => new
        {
            instance_id = result.InstanceId,
            model_name_or_path = options.Model,
            model_patch = result.ModelPatch,
        }).ToArray();
        await File.WriteAllTextAsync(
            predictionsPath,
            JsonSerializer.Serialize(predictions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)).ConfigureAwait(false);

        var scored = results.Where(static result => result.Status != TaskRunStatus.EnvironmentError).ToList();
        var resolved = scored.Count(static result => result.Status == TaskRunStatus.Resolved);
        var report = new
        {
            dataset = manifest.Dataset,
            split = manifest.Split,
            revision = manifest.Revision,
            model = options.Model,
            generated_at = DateTimeOffset.UtcNow,
            resolved_rate = scored.Count == 0 ? (double?)null : resolved / (double)scored.Count,
            resolved_tasks = resolved,
            scored_tasks = scored.Count,
            environment_error_tasks = results.Count(static result => result.Status == TaskRunStatus.EnvironmentError),
            tasks = results.Select(result => new
            {
                instance_id = result.InstanceId,
                status = result.Status.ToWireValue(),
                error_category = result.ErrorCategory,
                error = result.Error,
                termination_reason = result.TerminationReason,
                rounds = result.Rounds,
                duration_seconds = result.DurationSeconds,
                tokens = result.Tokens,
                baseline_fail_to_pass = result.BaselineFailToPass,
                baseline_pass_to_pass = result.BaselinePassToPass,
                agent_fail_to_pass = result.AgentFailToPass,
                agent_pass_to_pass = result.AgentPassToPass,
                trajectory_path = result.TrajectoryPath,
            }).ToArray(),
        };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(Program.JsonOptions)), Encoding.UTF8).ConfigureAwait(false);
    }
}

internal static class SelfTest
{
    public static async Task RunAsync()
    {
        var parsed = BenchmarkOptions.Parse(["--instances", "a,b", "--max-turns", "3", "--task-timeout", "2m", "--setup=false"]);
        Assert(parsed.InstanceIds is { Count: 2 } && parsed.MaxTurns == 3 && parsed.TaskTimeout == TimeSpan.FromMinutes(2) && !parsed.Setup, "CLI parsing");
        var expectedQuote = OperatingSystem.IsWindows() ? "\"a'b\"" : "'a'\\''b'";
        Assert(ToolCatalog.ShellQuote("a'b") == expectedQuote, "shell quoting");
        Assert(WorkspaceCommandPolicy.Validate("cat ../outside") is not null, "parent traversal rejection");
        Assert(WorkspaceCommandPolicy.Validate("docker ps") is not null, "container rejection");
        Assert(WorkspaceCommandPolicy.Validate("git clone https://example.test/repo") is not null, "network command rejection");
        Assert(WorkspaceCommandPolicy.Validate("dotnet test --no-restore --filter 'FullyQualifiedName~ExampleTests.Example'") is null, "normal .NET test command acceptance");
        Assert(WorkspaceCommandPolicy.Validate("git diff -- src/DeepSeek.Core/Files/FilesClient.cs") is null, "normal relative-path git command acceptance");
        Assert(WorkspaceCommandPolicy.Validate("printf \"$(cat${IFS}/etc/hostname)\"") is not null, "shell substitution rejection");
        Assert(WorkspaceCommandPolicy.Validate("printf '/etc/hostname'") is not null, "absolute path rejection anywhere in command");
        Assert(WorkspaceCommandPolicy.Validate("printf test > output.txt") is not null, "redirection rejection");
        var testCommand = TestCommandBuilder.Build(new TaskDefinition
        {
            FailToPass = ["Example.Tests.ExampleTest"],
        }, true);
        Assert(testCommand.Contains("dotnet test", StringComparison.Ordinal)
            && testCommand.Contains("FullyQualifiedName~Example.Tests.ExampleTest", StringComparison.Ordinal), "C# test filter mapping");
        Assert(TestCommandBuilder.Build(new TaskDefinition(), false).Contains("__swe_sharp_no_tests__", StringComparison.Ordinal), "empty test set handling");

        var root = Directory.CreateTempSubdirectory("deepseek-swebench-selftest-");
        try
        {
            var runner = new ProcessRunner(root.FullName, 10, "secret-value");
            var catalog = ToolCatalog.Create(root.FullName, parsed, "secret-value");
            Assert(catalog.Declarations.Count == 7, "tool catalog shape");
            Assert(catalog.Declarations.All(static tool => tool.GetService<AIFunctionDeclaration>() is not null), "tool declarations");
            Assert(catalog.TryGet("run_command", out var runCommand), "run_command declaration");
            using (var maliciousArguments = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                command = "printf \"$(cat${IFS}/etc/hostname)\"",
            })))
            {
                var maliciousResult = await runCommand.ExecuteAsync(maliciousArguments.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
                Assert(maliciousResult.Failed, "shell substitution is rejected before execution");
            }
            var output = await runner.RunAsync("printf '123456789012345'", TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            Assert(output.ExitCode == 0 && output.OutputTruncated && output.StandardOutput.Contains("truncated", StringComparison.Ordinal), "bounded process output");
            var delayCommand = OperatingSystem.IsWindows() ? "ping 127.0.0.1 -n 3 > NUL" : "sleep 2";
            var timeout = await runner.RunAsync(delayCommand, TimeSpan.FromMilliseconds(100), CancellationToken.None).ConfigureAwait(false);
            Assert(timeout.TimedOut, "process timeout");
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(root.FullName, "outside-link"), "/tmp");
                try
                {
                    _ = WorkspacePath.Resolve(root.FullName, "outside-link/file.txt");
                    throw new InvalidOperationException("Self-test failed: symbolic-link rejection");
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            var resultDirectory = Directory.CreateTempSubdirectory("deepseek-swebench-results-");
            try
            {
                await ResultWriter.WriteAsync(
                    resultDirectory.FullName,
                    new TaskManifest(),
                    parsed,
                    [new TaskRunResult { InstanceId = "example-1", ModelPatch = "diff --git a/a b/a" }]).ConfigureAwait(false);
                using var predictions = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(resultDirectory.FullName, "predictions.json")).ConfigureAwait(false));
                Assert(predictions.RootElement.ValueKind == JsonValueKind.Array && predictions.RootElement.GetArrayLength() == 1, "predictions is one JSON array");

                await ResultWriter.WriteAsync(resultDirectory.FullName, new TaskManifest(), parsed, []).ConfigureAwait(false);
                using var emptyPredictions = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(resultDirectory.FullName, "predictions.json")).ConfigureAwait(false));
                Assert(emptyPredictions.RootElement.ValueKind == JsonValueKind.Array && emptyPredictions.RootElement.GetArrayLength() == 0, "empty predictions is []");
            }
            finally
            {
                resultDirectory.Delete(recursive: true);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }

        Console.WriteLine("Self-test passed: CLI parsing, path/command safety, test mapping, output limits, and command timeout.");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Self-test failed: " + name);
        }
    }
}
