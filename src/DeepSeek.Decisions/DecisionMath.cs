namespace DeepSeek.Decisions;

// 公式参考 jevper 0.7.12 (4fdc054) 与 TypeSafe confidence 文档；此处独立实现 C# 数值计算。
internal static class DecisionMath
{
    internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    internal static double[] Normalize(double[] weights)
    {
        var largest = weights.Max();
        if (largest == 0) return weights.Select(_ => 1.0 / weights.Length).ToArray();
        var scaled = weights.Select(weight => weight / largest).ToArray();
        var sum = scaled.Sum();
        return scaled.Select(weight => weight / sum).ToArray();
    }

    internal static double[] Softmax(double[] logprobs)
    {
        var best = logprobs.Max();
        if (double.IsNegativeInfinity(best)) throw new System.Text.Json.JsonException("所有标签的概率质量均为零。");
        return Normalize(logprobs.Select(value => Math.Exp(value - best)).ToArray());
    }

    internal static double ChoiceConfidence(double[] distribution)
    {
        if (distribution.Length == 1) return 1;
        var probabilities = Normalize(distribution);
        var uniform = 1.0 / probabilities.Length;
        return Math.Max(0, Math.Min(1, (probabilities.Max() - uniform) / (1 - uniform)));
    }

    internal static double ScoreConfidence(double[] distribution)
    {
        var probabilities = Normalize(distribution);
        var mode = Array.IndexOf(probabilities, probabilities.Max());
        var spread = probabilities.Select((probability, index) => probability * Math.Abs(index - mode)).Sum();
        var center = (probabilities.Length - 1) / 2.0;
        var uniformSpread = Enumerable.Range(0, probabilities.Length).Average(index => Math.Abs(index - center));
        return Math.Max(0, Math.Min(1, 1 - spread / uniformSpread));
    }
}
