using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace CalendarMcp.Core.Discovery;

/// <summary>
/// Where a mailbox lives, as the connect screen needs it. <c>Kind</c> is <c>google</c>, <c>microsoft365</c>,
/// <c>outlook.com</c>, <c>imap</c> or <c>unknown</c>; <c>Source</c> says which step found it (<c>preset</c>, <c>isp</c>,
/// <c>ispdb</c>, or the same followed by <c>-mx</c> when the MX led there). Security is <c>ssl</c> or <c>starttls</c>.
/// </summary>
public sealed record DiscoveryResult(
    string Kind,
    string? Source = null,
    string? ImapHost = null,
    int? ImapPort = null,
    string? ImapSecurity = null,
    string? SmtpHost = null,
    int? SmtpPort = null,
    string? SmtpSecurity = null,
    string? Username = null,
    AppPasswordHint? AppPassword = null,
    bool? PasswordFallback = null);

/// <summary>Whether the provider takes only an app password from a mail client, and where to make one.</summary>
public sealed record AppPasswordHint(bool Required, string? HelpUrl);

/// <summary>How long one remote step may take, and the whole discovery.</summary>
public sealed record DiscoveryBudget(TimeSpan Step, TimeSpan Overall)
{
    public static DiscoveryBudget Default { get; } = new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12));
}

/// <summary>A mail address as discovery uses it: lowercased, the domain in its ASCII form, a <c>+tag</c> removed.</summary>
public sealed record DiscoveryAddress(string LocalPart, string Domain);

/// <summary>
/// Finds a mailbox's servers from its address in Thunderbird's order (Aura's account connect/repair design, A6):
/// the preset table, the domain's own autoconfig, ISPDB, then the MX, whose host is tried against the same three
/// from its next subdomain down to two labels. The first usable answer wins. Only the domain, or a suffix of the
/// MX host, ever leaves the box. A step out of time, or whose source could not be asked, falls through and is not
/// cached; a miss is cached for an hour and a hit for a day.
/// </summary>
public sealed class MailDiscovery(
    IAutoconfigClient autoconfig,
    IMxLookup mx,
    IMemoryCache cache,
    TimeProvider time,
    DiscoveryBudget budget,
    ILogger<MailDiscovery> logger)
{
    private static readonly TimeSpan HitLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan MissLifetime = TimeSpan.FromHours(1);
    private static readonly IdnMapping Idn = new();
    private static readonly DiscoveryResult Unknown = new(DiscoveryKinds.Unknown);

    /// <summary>The address as discovery reads it, or null when it is not one a mailbox can have.</summary>
    public static DiscoveryAddress? Normalise(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !MailboxAddress.TryParse(email.Trim(), out var mailbox))
            return null;

        var localPart = mailbox.LocalPart.ToLowerInvariant();
        string? domain;
        try
        {
            domain = HostName.Dns(Idn.GetAscii(mailbox.Domain.ToLowerInvariant()));
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (localPart.Length == 0 || domain is null)
            return null;

        var tag = localPart.IndexOf('+');
        return new DiscoveryAddress(tag > 0 ? localPart[..tag] : localPart, domain);
    }

    /// <exception cref="ArgumentException">The address is not one <see cref="Normalise"/> takes.</exception>
    public async Task<DiscoveryResult> DiscoverAsync(string email, CancellationToken ct)
    {
        var address = Normalise(email) ?? throw new ArgumentException("Not a mail address.", nameof(email));

        using var overall = new CancellationTokenSource(budget.Overall, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, overall.Token);
        try
        {
            return await FindAsync(address, linked.Token) ?? Unknown;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation("Mail discovery for {Domain} ran out of its {Budget}", address.Domain, budget.Overall);
            return Unknown;
        }
    }

    private async Task<DiscoveryResult?> FindAsync(DiscoveryAddress address, CancellationToken ct)
    {
        if (await FromDomainAsync(address.Domain, address, viaMx: false, ct) is { } direct)
            return direct;

        var mxHost = await StepAsync("mx", address.Domain, step => mx.PreferredMxHostAsync(address.Domain, step), ct);
        if (mxHost is null)
            return null;

        if (MailPresets.ByMxHost(mxHost, address) is { } mapped)
            return mapped;

        // Thunderbird stops when the MX's base domain is the address's: the MX says nothing the domain did not.
        if (mxHost == address.Domain || mxHost.EndsWith("." + address.Domain, StringComparison.Ordinal))
            return null;

        foreach (var suffix in Suffixes(mxHost))
        {
            if (await FromDomainAsync(suffix, address, viaMx: true, ct) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>Preset, the domain's autoconfig, then ISPDB, for the address's domain or a suffix of its MX.</summary>
    private async Task<DiscoveryResult?> FromDomainAsync(string lookup, DiscoveryAddress address, bool viaMx, CancellationToken ct)
    {
        var suffix = viaMx ? "-mx" : "";
        var preset = viaMx ? MailPresets.ForMxSuffix(lookup) : MailPresets.ForDomain(lookup);
        if (preset is not null)
            return preset.Answer(address, "preset" + suffix);

        if (await FetchAsync("isp", lookup, autoconfig.FetchIspAsync, ct) is { } isp
            && AutoconfigParser.Parse(isp, address.LocalPart, address.Domain) is { } fromIsp)
            return fromIsp with { Source = "isp" + suffix };

        if (await FetchAsync("ispdb", lookup, autoconfig.FetchIspdbAsync, ct) is { } ispdb
            && AutoconfigParser.Parse(ispdb, address.LocalPart, address.Domain) is { } fromIspdb)
            return fromIspdb with { Source = "ispdb" + suffix };

        return null;
    }

    /// <summary>
    /// The XML a source holds for a domain, through the cache. The XML is cached, not the answer, because the
    /// username and some hostnames are made from the address.
    /// </summary>
    private async Task<string?> FetchAsync(
        string source, string domain, Func<string, CancellationToken, Task<string?>> fetch, CancellationToken ct)
    {
        var key = (source, domain);
        if (cache.TryGetValue(key, out Fetched? cached))
            return cached!.Xml;

        var fetched = await StepAsync(source, domain, async step => new Fetched(await fetch(domain, step)), ct);
        if (fetched is null)
            return null;

        cache.Set(key, fetched, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = fetched.Xml is null ? MissLifetime : HitLifetime
        });
        return fetched.Xml;
    }

    /// <summary>
    /// One remote step in its own budget: null when it ran out or its source could not be asked, which is not an
    /// answer about the domain.
    /// </summary>
    private async Task<T?> StepAsync<T>(string step, string domain, Func<CancellationToken, Task<T?>> work, CancellationToken ct)
        where T : class
    {
        using var stepBudget = new CancellationTokenSource(budget.Step, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stepBudget.Token);
        try
        {
            return await work(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation("Mail discovery: {Step} for {Domain} took more than {Budget}, trying the next step", step, domain, budget.Step);
            return null;
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("Mail discovery: {Step} for {Domain} could not be asked ({Error}), trying the next step", step, domain, ex.Message);
            return null;
        }
    }

    /// <summary><c>a.b.example.net</c> gives <c>b.example.net</c> and <c>example.net</c>: the next subdomain down to two labels.</summary>
    private static IEnumerable<string> Suffixes(string host)
    {
        var labels = host.Split('.');
        for (var i = 1; i <= labels.Length - 2; i++)
            yield return string.Join('.', labels[i..]);
    }

    private sealed record Fetched(string? Xml);
}


/// <summary>The one host name rule discovery uses, for an address's domain and for a server an answer names.</summary>
internal static class HostName
{
    /// <summary>
    /// The host lowercased and without one final dot, or null unless it is a DNS name with at least one dot and at
    /// most 253 characters. <c>Uri.CheckHostName</c> takes a bare label, so <c>localhost</c> or a container's service
    /// name would pass it alone.
    /// </summary>
    public static string? Dns(string? host)
    {
        if (host is null)
            return null;

        var name = (host.EndsWith('.') ? host[..^1] : host).ToLowerInvariant();
        return name.Length <= 253 && name.Contains('.') && Uri.CheckHostName(name) == UriHostNameType.Dns ? name : null;
    }
}

/// <summary>The kinds a discovery answers.</summary>
public static class DiscoveryKinds
{
    public const string Google = "google";
    public const string Microsoft365 = "microsoft365";
    public const string OutlookCom = "outlook.com";
    public const string Imap = "imap";
    public const string Unknown = "unknown";
}
