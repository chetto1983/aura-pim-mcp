using Google.Apis.Http;

namespace CalendarMcp.Core.Providers;

/// <summary>
/// What a Google API client needs in order to reach Google. Production uses the account's stored
/// credential and the library's default HTTP stack; tests replace both, so no token file is read.
/// </summary>
internal abstract class GoogleTransport
{
    public abstract IConfigurableHttpClientInitializer Credential { get; }

    public abstract Google.Apis.Http.IHttpClientFactory HttpClientFactory { get; }
}
