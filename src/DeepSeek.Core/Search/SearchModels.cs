using System.Text.Json.Serialization;

namespace DeepSeek.Search;

public sealed class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public int? Count { get; set; }

    [JsonPropertyName("ori_question")]
    public string? OriginalQuestion { get; set; }

    public string? Localization { get; set; }
    public bool NeedContent { get; set; }
}

public sealed class SearchResult
{
    public double RerankScore { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? SiteName { get; set; }
    public string? SiteIcon { get; set; }
    public DateTimeOffset? Date { get; set; }
    public int Authority { get; set; }
}

public sealed class FetchResult
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
