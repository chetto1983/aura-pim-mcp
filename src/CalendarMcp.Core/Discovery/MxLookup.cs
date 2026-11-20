using DnsClient;

namespace CalendarMcp.Core.Discovery;

/// <summary>The domain's most preferred MX host.</summary>
public interface IMxLookup
{
    /// <summary>The host of the lowest-preference MX record, lowercased and without its final dot; null when there is none.</summary>
    Task<string?> PreferredMxHostAsync(string domain, CancellationToken ct);
}

/// <summary>
/// MX through DnsClient. A DNS error, a response with an error code, no MX record and a null MX (RFC 7505, the
/// exchange <c>.</c>) all mean no MX. DnsClient throws <see cref="DnsResponseException"/> on a timeout even with
/// <c>ThrowDnsErrors</c> off (<c>src/DnsClient/LookupClient.cs</c> at 1.8.0, <c>DnsResponseCode.ConnectionTimeout</c>).
/// </summary>
public sealed class MxLookup(ILookupClient dns) : IMxLookup
{
    public async Task<string?> PreferredMxHostAsync(string domain, CancellationToken ct)
    {
        IDnsQueryResponse response;
        try
        {
            response = await dns.QueryAsync(domain, QueryType.MX, QueryClass.IN, ct);
        }
        catch (DnsResponseException)
        {
            return null;
        }

        if (response.HasError)
            return null;

        var host = response.Answers.MxRecords()
            .OrderBy(record => record.Preference)
            .Select(record => record.Exchange.Value.TrimEnd('.').ToLowerInvariant())
            .FirstOrDefault();
        return string.IsNullOrEmpty(host) || Uri.CheckHostName(host) != UriHostNameType.Dns ? null : host;
    }
}
