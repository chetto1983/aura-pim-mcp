using CalendarMcp.Core.Discovery;

namespace CalendarMcp.Tests.Discovery;

/// <summary>
/// Stands in for the domain's autoconfig server and ISPDB. Answers what a test put in it, null otherwise, and records
/// every argument it was handed, in order, as <c>isp:domain</c> or <c>ispdb:domain</c>.
/// </summary>
internal sealed class FakeAutoconfig : IAutoconfigClient
{
    public Dictionary<string, string> Isp { get; } = [];

    public Dictionary<string, string> Ispdb { get; } = [];

    /// <summary>Runs in place of the ISP answer when set, so a test can make a step hang.</summary>
    public Func<string, CancellationToken, Task<string?>>? IspHook { get; set; }

    public List<string> Calls { get; } = [];

    public Task<string?> FetchIspAsync(string domain, CancellationToken ct)
    {
        Calls.Add($"isp:{domain}");
        return IspHook?.Invoke(domain, ct) ?? Task.FromResult(Isp.GetValueOrDefault(domain));
    }

    /// <summary>Runs in place of the ISPDB answer when set.</summary>
    public Func<string, CancellationToken, Task<string?>>? IspdbHook { get; set; }

    public Task<string?> FetchIspdbAsync(string domain, CancellationToken ct)
    {
        Calls.Add($"ispdb:{domain}");
        return IspdbHook?.Invoke(domain, ct) ?? Task.FromResult(Ispdb.GetValueOrDefault(domain));
    }
}

/// <summary>A DNS that knows the MX hosts a test gave it, recording each domain it was asked about.</summary>
internal sealed class FakeMx : IMxLookup
{
    public Dictionary<string, string> Hosts { get; } = [];

    public List<string> Calls { get; } = [];

    public Task<string?> PreferredMxHostAsync(string domain, CancellationToken ct)
    {
        Calls.Add(domain);
        return Task.FromResult(Hosts.GetValueOrDefault(domain));
    }
}
