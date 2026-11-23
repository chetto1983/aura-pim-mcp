using System.Net;
using System.Net.Sockets;
using System.Text;
using CalendarMcp.Core.Providers;
using Microsoft.Extensions.Logging;

namespace CalendarMcp.Core.Discovery;

/// <summary>
/// Fetches autoconfig XML for a domain. Only the domain is ever sent, never the address. Null means the source has
/// no config for the domain, which may be cached; an <see cref="HttpRequestException"/> means it could not be asked
/// (a server error, a refused or dropped connection), which says nothing about the domain.
/// </summary>
public interface IAutoconfigClient
{
    /// <summary>The domain's own autoconfig, or null when it has none.</summary>
    Task<string?> FetchIspAsync(string domain, CancellationToken ct);

    /// <summary>Thunderbird's ISPDB answer for the domain, or null when it has none.</summary>
    Task<string?> FetchIspdbAsync(string domain, CancellationToken ct);
}

/// <summary>
/// The domain's autoconfig (<c>https://autoconfig.&lt;d&gt;/mail/config-v1.1.xml</c>, then
/// <c>https://&lt;d&gt;/.well-known/autoconfig/mail/config-v1.1.xml</c>) and ISPDB
/// (<c>https://autoconfig.thunderbird.net/v1.1/&lt;d&gt;</c>), as Thunderbird's <c>FetchConfig.sys.mjs</c> asks them
/// but with HTTPS only and without its <c>emailaddress</c> parameter. No config is: a redirect, a status below 500
/// other than 200, a body over 64 KiB or one that is not a <c>clientConfig</c>, a host that does not resolve, and a
/// private address. A domain names its own autoconfig host, so connections go through <see cref="HostGuard"/>.
/// </summary>
public sealed class AutoconfigClient(HttpClient http, ILogger<AutoconfigClient> logger) : IAutoconfigClient
{
    public const string HttpClientName = "Autoconfig";
    public const int MaxBodyBytes = 64 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    /// <summary>The first of the two URLs that holds a config; if neither does and one could not be asked, that failure.</summary>
    public async Task<string?> FetchIspAsync(string domain, CancellationToken ct)
    {
        HttpRequestException? failed = null;
        string[] urls =
        [
            $"https://autoconfig.{domain}/mail/config-v1.1.xml",
            $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml"
        ];
        foreach (var url in urls)
        {
            try
            {
                if (await FetchAsync(url, ct) is { } xml)
                    return xml;
            }
            catch (HttpRequestException ex)
            {
                failed ??= ex;
            }
        }

        if (failed is not null)
            throw failed;
        return null;
    }

    public Task<string?> FetchIspdbAsync(string domain, CancellationToken ct) =>
        FetchAsync($"https://autoconfig.thunderbird.net/v1.1/{domain}", ct);

    private async Task<string?> FetchAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/xml");
        request.Headers.Accept.ParseAdd("text/xml");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode >= 500)
                throw new HttpRequestException($"{url} answered {(int)response.StatusCode}.", null, response.StatusCode);
            if (response.StatusCode != HttpStatusCode.OK)
                return null;

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var xml = await ReadCappedAsync(body, ct);
            return xml is not null && AutoconfigParser.IsClientConfig(xml) ? xml : null;
        }
        catch (HttpRequestException ex) when (ex.InnerException is PrivateAddressException refused)
        {
            logger.LogWarning("Mail discovery refused {Url}: {Reason}", url, refused.Message);
            return null;
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData })
        {
            return null;
        }
        catch (IOException ex)
        {
            throw new HttpRequestException($"{url} broke off.", ex);
        }
    }

    /// <summary>The body as text, or null when it is longer than <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<string?> ReadCappedAsync(Stream body, CancellationToken ct)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(length), ct)) > 0)
            length += read;

        return length > MaxBodyBytes ? null : Encoding.UTF8.GetString(buffer, 0, length).TrimStart('\uFEFF');
    }
}
