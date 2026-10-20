namespace TokenView.Wpf.Services;

public sealed record ContentFetchResult(
    bool Success,
    string? ErrorMessage,
    string? Markdown,
    string? ContentHash,
    string? SourceId,
    string? SourceUrl,
    DateTimeOffset FetchedAtUtc)
{
    public static ContentFetchResult Ok(MarkdownContent content) => new(
        true,
        null,
        content.Markdown,
        content.ContentHash,
        content.SourceId,
        content.SourceUrl,
        content.FetchedAtUtc);

    public static ContentFetchResult Failure(string message, DateTimeOffset fetchedAtUtc) => new(
        false,
        message,
        null,
        null,
        null,
        null,
        fetchedAtUtc);
}
