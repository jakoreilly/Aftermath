namespace Aftermath.Tests;

using System.Net;

/// <summary>Routes a request path to canned fixture content — the seam every HTTP-based
/// source's client tests use to run with the network unplugged (hard constraint 6).</summary>
public sealed class StubHttpMessageHandler(Func<string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
{
    public List<string> RequestedPaths { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string pathAndQuery = request.RequestUri!.PathAndQuery;
        this.RequestedPaths.Add(pathAndQuery);
        (HttpStatusCode status, string body) = respond(pathAndQuery);

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }
}
