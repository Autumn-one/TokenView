namespace TokenView.Wpf.Services;

public sealed record MarkdownContent(
    string Markdown,
    string ContentHash,
    string SourceId,
    string SourceUrl,
    DateTimeOffset FetchedAtUtc);
