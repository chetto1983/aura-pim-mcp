using System.Net;
using System.Text;
using CalendarMcp.Core.Providers;
using Google.Apis.Http;

namespace CalendarMcp.Tests.Helpers;

/// <summary>
/// An HTTP handler answering from a script of routes, recording every request. A request no route
/// matches fails the test, so an adapter cannot reach a URL its test did not name.
/// </summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Answer)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHttp On(string urlFragment, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(request => Url(request).Contains(urlFragment, StringComparison.OrdinalIgnoreCase), _ => Json(json, status));

    public FakeHttp On(string urlFragment, Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        On(request => Url(request).Contains(urlFragment, StringComparison.OrdinalIgnoreCase), answer);

    public FakeHttp On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        _routes.Add((match, answer));
        return this;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string Url(HttpRequestMessage request) =>
        Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);

    public IEnumerable<string> Urls => Requests.Select(Url);

    public int CountOf(string urlFragment) =>
        Urls.Count(url => url.Contains(urlFragment, StringComparison.OrdinalIgnoreCase));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        foreach (var (match, answer) in Enumerable.Reverse(_routes))
        {
            if (match(request))
                return Task.FromResult(answer(request));
        }
        throw new AssertFailedException($"Unscripted request: {request.Method} {request.RequestUri}");
    }
}

/// <summary>Points a Google API client at a <see cref="FakeHttp"/>, with no credential on disk.</summary>
internal sealed class FakeGoogleTransport(FakeHttp http) : GoogleTransport
{
    public override IConfigurableHttpClientInitializer Credential { get; } = new NoCredential();

    public override Google.Apis.Http.IHttpClientFactory HttpClientFactory { get; } =
        new HttpClientFromMessageHandlerFactory(_ =>
            new HttpClientFromMessageHandlerFactory.ConfiguredHttpMessageHandler(http, false, false));

    private sealed class NoCredential : IConfigurableHttpClientInitializer
    {
        public void Initialize(ConfigurableHttpClient httpClient) { }
    }
}
