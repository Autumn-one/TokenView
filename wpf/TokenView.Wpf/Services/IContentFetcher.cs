namespace TokenView.Wpf.Services;

public interface IContentFetcher
{
    Task<MirrorsConfig> ResolveConfigAsync(MirrorsConfig fallback, CancellationToken cancellationToken);

    Task<ContentFetchResult> FetchAsync(
        MirrorsConfig config,
        ContentKind kind,
        CancellationToken cancellationToken);
}
