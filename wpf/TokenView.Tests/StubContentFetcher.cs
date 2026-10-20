using TokenView.Wpf.Services;

namespace TokenView.Tests;

internal sealed class StubContentFetcher : IContentFetcher
{
    public Func<ContentKind, Task<ContentFetchResult>> OnFetch { get; set; } =
        _ => Task.FromResult(ContentFetchResult.Failure("not configured", DateTimeOffset.UnixEpoch));

    public int Calls { get; private set; }

    public Task<MirrorsConfig> ResolveConfigAsync(
        MirrorsConfig fallback,
        CancellationToken cancellationToken) => Task.FromResult(fallback);

    public Task<ContentFetchResult> FetchAsync(
        MirrorsConfig config,
        ContentKind kind,
        CancellationToken cancellationToken)
    {
        Calls++;
        return OnFetch(kind);
    }
}
