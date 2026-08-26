using System.Security.Claims;

namespace CalendarMcp.Core.Tenancy;

public interface ITenantContext
{
    string RequireTenantId();
    IDisposable Bind(string tenantId);
}

/// <summary>
/// Carries the authenticated OAuth subject through one asynchronous request.
/// The singleton service is safe because AsyncLocal isolates concurrent flows.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private readonly AsyncLocal<string?> _current = new();

    public string RequireTenantId() =>
        _current.Value ?? throw new InvalidOperationException("No authenticated tenant is bound to this request.");

    public IDisposable Bind(string tenantId)
    {
        var normalized = TenantIdentity.Normalize(tenantId);
        var previous = _current.Value;
        _current.Value = normalized;
        return new Binding(this, previous);
    }

    private sealed class Binding(TenantContext owner, string? previous) : IDisposable
    {
        private TenantContext? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null)
            {
                current._current.Value = previous;
            }
        }
    }
}

public static class TenantIdentity
{
    public const string OAuthClaimName = "sub";

    public static string Normalize(string? value)
    {
        if (!Guid.TryParse(value?.Trim(), out var tenantId) || tenantId == Guid.Empty)
        {
            throw new ArgumentException("OAuth subject must be a non-empty UUID.", nameof(value));
        }
        return tenantId.ToString("D");
    }

    public static string FromPrincipal(ClaimsPrincipal? principal) =>
        Normalize(principal?.FindFirst(OAuthClaimName)?.Value);

    /// <summary>
    /// Produces a globally unique provider/cache key while preserving the human slug.
    /// </summary>
    public static string AccountId(string tenantId, string localAccountId)
    {
        var tenant = Guid.Parse(Normalize(tenantId)).ToString("N");
        return $"{tenant}__{localAccountId}";
    }
}
