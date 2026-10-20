using System.Net;
using System.Net.Http;
using System.Text;

namespace TokenView.Tests;

internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<string> Requests { get; } = [];

    public static HttpResponseMessage Ok(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };

    public static HttpResponseMessage Bytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    public static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new ByteArrayContent([]) };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RequestUri?.ToString() ?? "");
        return Task.FromResult(_responder(request));
    }
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;
}
