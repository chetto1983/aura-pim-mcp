namespace CalendarMcp.Core.Changes;

/// <summary>
/// A request the changes contract refuses or a read that failed, named the way the HTTP answer
/// names it. Aura treats a transient failure and a hard one differently, so the name is the contract.
/// </summary>
public sealed class ChangesException(int statusCode, string error, Exception? innerException = null)
    : Exception(error, innerException)
{
    public int StatusCode { get; } = statusCode;
    public string Error { get; } = error;

    public static ChangesException AccountNotFound() => new(404, "account_not_found");
    public static ChangesException InvalidRequest(string error) => new(400, error);
    public static ChangesException InvalidCursor(Exception? cause = null) => new(400, "invalid_cursor", cause);
    public static ChangesException Unsupported() => new(422, "unsupported_resource");
    public static ChangesException PermissionDenied(Exception? cause = null) => new(403, "permission_denied", cause);
    public static ChangesException ProviderAuth(Exception? cause = null) => new(502, "provider_auth", cause);
    public static ChangesException ProviderTransient(Exception? cause = null) => new(503, "provider_transient", cause);
}

/// <summary>
/// The cursor's position no longer exists at the provider. <paramref name="replacement"/> is the
/// cursor to go on from when only part of the old one was lost; null means start again from now.
/// </summary>
public sealed class CursorExpiredException(ChangesCursor? replacement = null) : Exception("The cursor expired.")
{
    public ChangesCursor? Replacement { get; } = replacement;
}
