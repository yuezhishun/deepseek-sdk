using System.ComponentModel;

namespace Sample;

internal class TestTools
{
    [Description("获取实时天气预报，通过城市和日期获取天气预报")]
    public static string GetWeather(
        [Description("城市")] string city,
        [Description("日期，格式为 yyyy-MM-dd，默认今天")] string? date = null)
    {
        var weatherDate = DateOnly.TryParse(date, out var parsedDate)
            ? parsedDate
            : DateOnly.FromDateTime(DateTime.Now);
        return $"The weather in {city} on {weatherDate:yyyy-MM-dd} is sunny with a high of 25°C and a low of 15°C.";
    }

    [Description("获取当前 UTC 时间，返回 ISO 8601 格式时间戳")]
    public static string GetCurrentTime()
    {
        return DateTime.UtcNow.ToString("o"); // ISO 8601 format
    }

    [Description("根据时区标识获取当前本地时间，timezone 例如 Asia/Shanghai、America/New_York")]
    public static string GetCurrentTimeByTimezone(string timezone)
    {
        var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var localTime = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZoneInfo);
        return $"{timezone}: {localTime:O}";
    }
}
