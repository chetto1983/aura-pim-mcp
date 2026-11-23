using CalendarMcp.Auth;
using CalendarMcp.Core.Security;

namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// At startup, encrypts every password an earlier version left in the account file in clear.
/// A failure is logged once and never stops the host: the plaintext still works, the
/// encrypted form is only better.
/// </summary>
internal sealed class ProtectStoredPasswordsService(
    IAccountConfigurationService accounts,
    PasswordProtector protector,
    ILogger<ProtectStoredPasswordsService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var count = await accounts.ProtectStoredPasswordsAsync(protector, cancellationToken);
            logger.LogInformation("Protected {Count} stored passwords", count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not protect the stored passwords; they stay as they are");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
