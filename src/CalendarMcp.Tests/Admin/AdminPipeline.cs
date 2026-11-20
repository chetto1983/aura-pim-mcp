using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Security;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.HttpServer.Admin;
using CalendarMcp.Tests.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CalendarMcp.Tests.Admin;

/// <summary>
/// The admin API in memory, wired the way <c>Program.cs</c> wires it: <c>AddCalendarMcpCore()</c>,
/// <c>AddAdminServices()</c>, <see cref="AdminAuthMiddleware"/> and a real <c>appsettings.json</c> in a temp
/// <c>CALENDAR_MCP_CONFIG</c>, read by the real <c>AccountConfigurationService</c> and the real registry. Only the
/// bearer's validation, the clock and the mail servers are replaced: the IMAP provider is a Rocks mock with no
/// expectations, so anything that would open a pooled connection fails the test.
/// </summary>
internal sealed class AdminPipeline : IAsyncDisposable
{
    public const string Password = "secret";
    public const string AllowPrivateHeader = "X-Aura-Allow-Private-Hosts";

    public static readonly string MailId = TenantIdentity.AccountId(TestData.TenantA, "mail");
    public static readonly string GmailId = TenantIdentity.AccountId(TestData.TenantA, "gmail");
    public static readonly string ForeignMailId = TenantIdentity.AccountId(TestData.TenantB, "mail");

    private readonly string _directory;
    private readonly string? _previousConfig;
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    /// <param name="verifier">Null keeps the real <see cref="IImapAccountVerifier"/> from the container.</param>
    public AdminPipeline(FakeVerifier? verifier)
    {
        _directory = Path.Combine(Path.GetTempPath(), "calendar-mcp-admin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _previousConfig = Environment.GetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable);
        // Before AddCalendarMcpCore: the key ring's directory is taken when DataProtection is registered.
        Environment.SetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable, _directory);
        File.WriteAllText(ConfigPath, """{ "CalendarMcp": { "Accounts": [] } }""");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddJsonFile(ConfigPath, optional: false, reloadOnChange: false);
        builder.Services.Configure<CalendarMcpConfiguration>(builder.Configuration.GetSection("CalendarMcp"));
        builder.Services.AddCalendarMcpCore();
        builder.Services.AddAdminServices();
        builder.Services.AddSingleton(new VerifyThrottle(Clock));
        builder.Services.AddSingleton(new IImapProviderServiceCreateExpectations().Instance());
        if (verifier is not null)
            builder.Services.AddSingleton<IImapAccountVerifier>(verifier);
        SubjectFromToken.AddTo(builder.Services);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/admin"),
            admin => admin.UseMiddleware<AdminAuthMiddleware>("mcp:tools"));
        _app.MapAdminEndpoints();
        _app.StartAsync().GetAwaiter().GetResult();
        _client = _app.GetTestClient();

        if (verifier is not null)
            verifier.Protector = Protector;
        Seed();
    }

    public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));

    public IServiceProvider Services => _app.Services;

    public PasswordProtector Protector => _app.Services.GetRequiredService<PasswordProtector>();

    public string ConfigPath => Path.Combine(_directory, "appsettings.json");

    /// <summary>The stored provider config of an account, as the file holds it.</summary>
    public JsonObject StoredConfig(string accountId) => (JsonObject)StoredAccount(accountId)["ProviderConfig"]!;

    public JsonObject StoredAccount(string accountId) =>
        JsonNode.Parse(File.ReadAllText(ConfigPath))!["CalendarMcp"]!["Accounts"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(node => node["Id"]!.GetValue<string>() == accountId);

    /// <summary>Puts a value straight into the file, the way another writer would, and reloads. Null removes the key.</summary>
    public void SetStored(string accountId, string key, string? value)
    {
        var root = JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        var account = root["CalendarMcp"]!["Accounts"]!.AsArray().Single(node => node!["Id"]!.GetValue<string>() == accountId)!;
        var config = (JsonObject)account["ProviderConfig"]!;
        if (value is null)
            config.Remove(key);
        else
            config[key] = value;
        File.WriteAllText(ConfigPath, root.ToJsonString());
        Reload();
    }

    public Task<(int Status, JsonElement Body)> GetAsync(string path, bool allowPrivate = false) =>
        SendAsync(HttpMethod.Get, path, null, allowPrivate);

    public Task<(int Status, JsonElement Body)> PostAsync(string path, object? body = null, bool allowPrivate = false) =>
        SendAsync(HttpMethod.Post, path, body, allowPrivate);

    public Task<(int Status, JsonElement Body)> PatchAsync(string path, object body, bool allowPrivate = false) =>
        SendAsync(HttpMethod.Patch, path, body, allowPrivate);

    public Task<(int Status, JsonElement Body)> PutAsync(string path, object body, bool allowPrivate = false) =>
        SendAsync(HttpMethod.Put, path, body, allowPrivate);

    /// <summary>A body as raw JSON, for what an object cannot say: two keys that differ only in case.</summary>
    public static HttpContent Json(string json) => new StringContent(json, System.Text.Encoding.UTF8, "application/json");

    private async Task<(int Status, JsonElement Body)> SendAsync(HttpMethod method, string path, object? body, bool allowPrivate)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = body switch
            {
                null => null,
                HttpContent content => content,
                _ => JsonContent.Create(body)
            }
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestData.TenantA);
        if (allowPrivate)
            request.Headers.Add(AllowPrivateHeader, "true");

        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private void Seed()
    {
        var accounts = new JsonArray(
            Account(MailId, TestData.TenantA, "imap", ImapConfig(Protector.Protect(Password))),
            Account(GmailId, TestData.TenantA, "google", new JsonObject { ["ClientId"] = "client", ["ClientSecret"] = "shh" }),
            Account(ForeignMailId, TestData.TenantB, "imap", ImapConfig(Protector.Protect(Password))));
        File.WriteAllText(ConfigPath, new JsonObject { ["CalendarMcp"] = new JsonObject { ["Accounts"] = accounts } }.ToJsonString());
        Reload();
    }

    private void Reload() => ((IConfigurationRoot)_app.Configuration).Reload();

    private static JsonObject ImapConfig(string password) => new()
    {
        ["imapHost"] = "imap.example.com",
        ["imapPort"] = "993",
        ["imapSecurity"] = "ssl",
        ["smtpHost"] = "smtp.example.com",
        ["smtpPort"] = "465",
        ["smtpSecurity"] = "ssl",
        ["username"] = "person@example.com",
        ["password"] = password
    };

    private static JsonObject Account(string id, string tenant, string provider, JsonObject providerConfig) => new()
    {
        ["Id"] = id,
        ["TenantId"] = tenant,
        ["DisplayName"] = "Posta",
        ["Provider"] = provider,
        ["Enabled"] = true,
        ["Priority"] = 3,
        ["Domains"] = new JsonArray("example.com"),
        ["Permissions"] = new JsonObject { ["emailSend"] = false },
        ["ProviderConfig"] = providerConfig
    };

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Environment.SetEnvironmentVariable(ConfigurationPaths.ConfigEnvVariable, _previousConfig);
        Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>
/// Stands in for the mail servers. It reads the config through the real <c>ImapAccountConfig.From</c>, as the real
/// verifier does, so an <c>ENC:</c> value arrives decrypted and one from another key ring is a refused login.
/// </summary>
internal sealed class FakeVerifier : IImapAccountVerifier
{
    private readonly List<Call> _calls = [];

    public sealed record Call(string AccountId, Dictionary<string, string> Config, bool AllowPrivate, ImapProviderService.ImapAccountConfig? Read);

    public PasswordProtector Protector { get; set; } = null!;

    /// <summary>What the servers answer, from what was read. Passes by default.</summary>
    public Func<ImapProviderService.ImapAccountConfig, VerifyOutcome> Answer { get; set; } = _ => new VerifyOutcome(null, null);

    /// <summary>Runs while the check is in flight.</summary>
    public Action? During { get; set; }

    public IReadOnlyList<Call> Calls => _calls;

    public Task<VerifyOutcome> VerifyAsync(
        string accountId, IReadOnlyDictionary<string, string> providerConfig, bool allowPrivate, CancellationToken ct)
    {
        During?.Invoke();
        var config = new Dictionary<string, string>(providerConfig, StringComparer.OrdinalIgnoreCase);
        try
        {
            var read = ImapProviderService.ImapAccountConfig.From(accountId, providerConfig, Protector);
            _calls.Add(new Call(accountId, config, allowPrivate, read));
            return Task.FromResult(Answer(read));
        }
        catch (CryptographicException)
        {
            _calls.Add(new Call(accountId, config, allowPrivate, null));
            return Task.FromResult(new VerifyOutcome(new VerifyFailure(VerifyReasons.Imap, VerifyReasons.AuthFailed), null));
        }
    }

    /// <summary>Refuses the login unless the password read is <paramref name="password"/>.</summary>
    public static Func<ImapProviderService.ImapAccountConfig, VerifyOutcome> PasswordIs(string password) =>
        read => read.Password == password
            ? new VerifyOutcome(null, null)
            : new VerifyOutcome(new VerifyFailure(VerifyReasons.Imap, VerifyReasons.AuthFailed), null);
}
