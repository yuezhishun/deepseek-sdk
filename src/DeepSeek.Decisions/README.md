# DeepSeek.Decisions

An independent decision library built on DeepSeek.Core, with Jev/System One and OpenAI Decisions-style requests for text, JSON, and images.

The package targets `netstandard2.0` and depends on DeepSeek.Core. Each question uses a separate Chat Completions call, with a default concurrency of eight. Applications handle HTTP service hosting.

Release 1.0.8 passed all 59 Decisions unit tests on .NET 11 GA (SDK 11.0.100 and runtime 11.0.0) on November 11, 2026. The 34 live integration tests included decision scenarios.

## Installation

Version 1.1.0 depends on DeepSeek.Core 1.1.0. The source project supports direct project references and includes a WorkflowEvals benchmark. [Benchmark guide](https://github.com/yuezhishun/deepseek-sdk/tree/v1.1.0/benchmark/DeepSeek.Decisions.Benchmarks)

```bash
dotnet add package DeepSeek.Decisions --version 1.1.0
```

```csharp
using DeepSeek;
using DeepSeek.Decisions;

var decisions = new DeepSeekClient(apiKey)
    .GetDecisionsClient("deepseek-flash");

// Reuse an existing ChatClient to keep its authentication, endpoint, and pipeline settings.
var existing = new DeepSeekClient(apiKey).GetChatClient("deepseek-flash");
var sameModel = new DecisionsClient(existing);
```

`GetDecisionsClient` is an extension method in `DeepSeek.Decisions`. A request can omit Model or specify the model bound by the underlying `ChatClient.Model`. Choose a vision-capable model for image inputs.

## Decisions

```csharp
var result = await decisions.CreateDecisionAsync(new DecisionRequest
{
    Input = "I was charged twice for the same subscription. Please refund the duplicate charge.",
    Questions =
    [
        new() { Name = "duplicate", Instructions = "Did the customer report a duplicate charge?" },
        new()
        {
            Type = "choice",
            Name = "department",
            Instructions = "Which department should handle the request?",
            Choices =
            [
                new() { Value = "billing", Description = "Payments and refunds" },
                new() { Value = "technical", Description = "Software issues" },
            ],
        },
        new()
        {
            Type = "score",
            Name = "urgency",
            Instructions = "Assess the priority.",
            Levels = [new() { Label = "Low" }, new() { Label = "Medium" }, new() { Label = "High" }],
        },
    ],
});

Console.WriteLine(result.Answers[0].Probability);
Console.WriteLine(result.Answers[1].Choice);
Console.WriteLine(result.Answers[2].Score);
```

Type defaults to `predicate`. Choice values can be strings or booleans. Boolean `true` and the string `"true"` are distinct. Repeating a value of the same type is invalid. Answers follow input order. Names are optional and can be repeated.

Input accepts a string or an array of DecisionInputMessage objects. Message Content and question Instructions accept strings or arrays of DecisionContentPart objects. A deserialized JsonElement also works for those fields.

## Jev state, criteria, and named answers

```csharp
var result = await decisions.SystemOneAsync(new SystemOneRequest
{
    State = new { ticket = "The website is inaccessible to every user and has no workaround." },
    Questions = new Dictionary<string, SystemOneQuestion>
    {
        ["outage"] = new()
        {
            Instructions = "Is there a complete outage?",
            Criteria = new Dictionary<string, string>
            {
                ["true"] = "The service is unavailable to all users",
                ["false"] = "The service is usable or only some users are affected",
            },
        },
        ["severity"] = new()
        {
            Type = "score",
            Instructions = "Assess outage severity.",
            Criteria = new[] { "Visual issue", "Partial outage with a workaround", "Full outage with no workaround" },
        },
    },
});

Console.WriteLine(result.Answers["outage"].Noul);
Console.WriteLine(result.Answers["severity"].Score);
Console.WriteLine(result.Answers["severity"].Legend!["2"]);
```

Jev Type defaults to `noul`. Choice Criteria map option names to descriptions. Score criteria list levels from low to high. Noul accepts true and false descriptions. State, instructions, and criteria descriptions accept JSON structures. Score Legend preserves the original level descriptions.

## JSON and image evidence

Decisions accepts HTTP(S) image URLs and PNG, JPEG, GIF, or WebP base64 data URLs. The SDK sends images as Core ChatImageContentPart instances.

```csharp
var result = await decisions.CreateDecisionAsync(new DecisionRequest
{
    Input = new[]
    {
        new DecisionInputMessage
        {
            Content = new DecisionContentPart[]
            {
                new() { Text = "Here are the reference photo and order details." },
                new() { Type = "input_image", ImageUrl = referenceImageUrl, Detail = "high" },
                new() { Type = "input_json", Data = new { order = 42, expected = "undamaged" } },
            },
        },
    },
    Questions =
    [
        new()
        {
            Name = "damage",
            Instructions = new DecisionContentPart[]
            {
                new() { Text = "Does the package in the candidate photo show more damage than the reference?" },
                new() { Type = "input_image", ImageUrl = candidateImageUrl },
            },
        },
    ],
});
```

`input_json.Data` accepts an object or an array of objects. Fields named role, type, or image_url inside that JSON remain data values.

Jev Assets accepts PNG and JPEG as raw base64 without a data URL prefix. Reference each asset by key in a string value in State, instructions, or criteria.

```csharp
var result = await decisions.SystemOneAsync(new SystemOneRequest
{
    State = new { reference = "img-reference" },
    Assets = new Dictionary<string, SystemOneAsset>
    {
        ["img-reference"] = new() { Format = "png", DataBase64 = referencePngBase64 },
        ["img-candidate"] = new() { Format = "png", DataBase64 = candidatePngBase64 },
    },
    Questions = new Dictionary<string, SystemOneQuestion>
    {
        ["same"] = new() { Instructions = "Do the reference photo and img-candidate show the same animal?" },
    },
});
```

Asset references can fill a string or appear inside a sentence. The SDK matches whole values first, then scans from left to right and uses the longest match at each position. Object keys remain literal. Repeated references send the image again. Each question receives shared evidence and its own image references. Every asset must be referenced.

## JSON protocol entry points

`CreateOpenAiDecisionAsync(JsonElement, CancellationToken)` and
`CreateJevSystemOneAsync(JsonElement, bool enableImages, bool enableLongContext, CancellationToken)`
accept the two JSON request shapes and return a JSON response. Each request specifies the
bound DeepSeek model. Invalid requests throw `DecisionProtocolException` with a status, a code,
and the parameter location. Hosting applications handle HTTP routes, authentication,
compression, multipart files, and full image decoding.

- OpenAI Decisions covers predicate, choice, score, and per-question refusal. It accepts
  system, developer, user, and assistant messages and verified function or custom tool call
  history. Roles and tool history are kept in order as evidence, with no execution of tools.
  Use of OpenAI cache settings or the safety identifier yields `unsupported_capability`.
- Jev System One covers noul, choice, score, and box. Enable images explicitly for PNG/JPEG
  assets and box questions. Box needs a vision model and yields normalized coordinates with
  model-reported confidence from 0 to 1, or `confidence: null` if unavailable. Set the
  long-context flag to request a larger local budget. The
  compatibility layer budgets one token per UTF-8 byte of rendered text and 1024 per image.
- The response uses actual DeepSeek token counts. A missing required count yields
  `upstream_usage_missing`. The OpenAI shape reports zero cache writes by this compatibility
  layer and zero reasoning tokens when thinking is disabled and the model emits no reasoning.

## Methods, probabilities, and options

```csharp
using System.ClientModel.Primitives;

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var result = await decisions.CreateDecisionAsync(request,
    new DecisionOptions { Method = "auto", MaxConcurrency = 4, MaxTokens = 256 },
    new RequestOptions { CancellationToken = cancellation.Token });
```

| Option | Default | Meaning |
| --- | --- | --- |
| `Method` | auto | auto, logprobs, structured, or discrete |
| `TopLogprobs` | 20 | Candidate label tokens, from 2 to 20 |
| `MaxConcurrency` | 8 | Maximum concurrent questions, at least 1 |
| `NormalizeProbabilities` | true | Normalize structured weights when their sum differs from 1 by over 1e-6 |
| `MaxMalformedRetries` | 1 | Number of format repairs, either 0 or 1 |
| `Thinking` | Disabled | Native thinking mode |
| `ReasoningEffort` | null | Optional Core reasoning effort |
| `MaxTokens`, `Temperature` | null | Use the service defaults |

- **Logprobs** reads the first answer label token and normalizes over the options. Missing labels contribute zero to this estimate and appear in MissingLabels. The estimate is conditional on the observed candidates. A comparable competing option is required except for a single-option Jev question.
- **Structured** requests JSON weights and validates their shape locally. Weights must be finite, nonnegative, and present for all options. The default normalization converts all-zero weights to a uniform distribution. With normalization disabled, the response retains raw weights while score and confidence still use normalized values.
- **Discrete** selects one option and describes the selection as a one-hot distribution.
- **Auto** tries Logprobs before Structured. The client caches explicit lack of support immediately and missing readings after two consecutive attempts. A successful reading resets the count. Questions with more than 26 options use Structured. Explicit Logprobs has no fallback.

Choice selects the highest-probability option, with ties resolved in input order. Score is the probability-weighted mean of level indices. Choice Confidence compares the peak to a uniform distribution. Score Confidence also accounts for distance from the modal level. Noul reports its probability. Calibration and latency depend on the backing chat model.

## Diagnostics, errors, and usage

```csharp
var diagnostic = result.Diagnostics[0];
Console.WriteLine($"{diagnostic.QuestionId}: {diagnostic.Method}/{diagnostic.ProbabilitySource}");
Console.WriteLine(diagnostic.FallbackReason);
Console.WriteLine(string.Join(",", diagnostic.MissingLabels));

// The protocol JSON contains model, answers, and usage. Diagnostics remain available on the result.
var json = System.Text.Json.JsonSerializer.Serialize(result);
```

Diagnostics track calls, format repairs, missing labels, fallback reasons, and raw weights when normalization occurs. CallCount counts Core calls. The pipeline manages HTTP retries. Model capability caching is scoped to the Decisions client instance. The SDK excludes request text, image bytes, and credentials from diagnostics.

Auto falls back for the current question after Core retries for a 5xx failure are exhausted. Such failures leave the capability cache unchanged. Authentication failures, rate limits, parameter errors, truncation, and refusals surface as errors. A terminal failure cancels active calls and stops queued questions. The exception keeps its original type and carries QuestionId, DecisionDiagnostics, and DecisionUsage in Data.

The SDK validates all questions before sending the first request and snapshots inputs, options, and transport settings. The synchronous CreateDecision and SystemOne methods use the same engine as their asynchronous counterparts.

Usage aggregates all attempts visible to the SDK. Unreported counts remain null and reported zeros stay zero. A failed request can leave aggregate usage unknown. Chat omits cache_write_tokens.

The original typed methods support 1–400 questions. Decisions Choice accepts 2–255 options, Jev Choice accepts 1–255 options, and Score accepts 2–10 levels. These methods accept user messages with text, JSON evidence, or images.

## Build and test

Run from the repository root.

```powershell
dotnet build DeepSeek.slnx -c Release
dotnet test test/DeepSeek.Decisions.UnitTests/DeepSeek.Decisions.UnitTests.csproj -c Release
dotnet test test/DeepSeek.IntegrationTests/DeepSeek.IntegrationTests.csproj -c Release --filter FullyQualifiedName~DecisionsIntegrationTests
dotnet pack src/DeepSeek.Decisions/DeepSeek.Decisions.csproj -c Release
```

LiveFact reads DEEPSEEK_API_KEY, then deepseek-api-key, then local sample/appsettings.json. Live tests skip when local credentials are absent. Release CI supplies the DEEPSEEK_API_KEY secret and sets DEEPSEEK_REQUIRE_LIVE_TESTS=true so missing credentials fail the run.

DeepSeek.Decisions is an independent implementation. [jevper 0.7.12](https://github.com/zhulinchng/jevper/tree/4fdc054082e6328f2d42bc3750d7ebbf9380d3b4) · [Decisions API](https://developers.openai.com/api/docs/guides/decisions) · [Jev images](https://docs.typesafe.ai/images) · [Confidence formulas](https://docs.typesafe.ai/confidence)
