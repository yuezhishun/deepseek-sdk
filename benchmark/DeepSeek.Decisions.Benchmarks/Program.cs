using DeepSeek.Decisions.Benchmarks;
using System.Globalization;

try
{
    var command = args.FirstOrDefault() ?? "help";
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--") || i + 1 >= args.Length) throw new ArgumentException("参数必须采用 --名称 值。");
        if (!options.TryAdd(args[i][2..], args[i + 1])) throw new ArgumentException("重复参数：" + args[i]);
    }
    var allowed = command switch
    {
        "run" => new[] { "root", "name", "budget-usd", "retry-errors" },
        "report" => ["root", "name"],
        "prepare" or "estimate" or "replay" or "--self-test" => ["root"],
        "help" or "--help" or "-h" => [],
        _ => throw new ArgumentException("未知命令：" + command),
    };
    if (options.Keys.Any(key => !allowed.Contains(key))) throw new ArgumentException("存在当前命令不支持的参数。");
    if (options.TryGetValue("retry-errors", out var retry) && retry is not ("true" or "false")) throw new ArgumentException("--retry-errors 仅接受 true 或 false。");
    var root = Path.GetFullPath(options.GetValueOrDefault("root", "artifacts/workflowevals"));
    switch (command)
    {
        case "prepare": await Data.Prepare(root); break;
        case "--self-test": await SelfTests.Run(root); break;
        case "estimate": await Estimation.Estimate(root, await Data.Load(root)); break;
        case "run":
            if (!double.TryParse(options.GetValueOrDefault("budget-usd"), CultureInfo.InvariantCulture, out var budget) || !double.IsFinite(budget) || budget <= 0)
                throw new ArgumentException("run 必须显式传入已确认的 --budget-usd 正数。");
            return await Evaluation.Run(root, options.GetValueOrDefault("name", "flash-auto"), budget, options.GetValueOrDefault("retry-errors", "false") == "true");
        case "report": await Reporting.Report(root, options.GetValueOrDefault("name", "flash-auto"), await Data.Load(root)); break;
        case "replay":
            await J.Write(Path.Combine(root, "replay.json"), await Workflows.Replay(await Data.Load(root)));
            break;
        default: Console.WriteLine("WorkflowEvals：prepare | estimate | replay | run | report | --self-test；--root artifacts/workflowevals；run 必须提供 --budget-usd；--name flash-auto；--retry-errors true"); break;
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
    Console.Error.WriteLine(exception.StackTrace);
    return 1;
}
