using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using CalendarMcp.HttpServer.Admin;
using CalendarMcp.HttpServer.Security;
using CalendarMcp.Tests.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CalendarMcp.Tests.Changes;

/// <summary>
/// The real admin pipeline in memory: <see cref="AdminAuthMiddleware"/>, the real
/// <see cref="AccountRegistry"/> and the real JSON binding. Only the bearer's validation is
/// replaced, by a scheme that takes the subject from the token's text.
/// </summary>
internal sealed class ChangesPipeline : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    public ChangesPipeline(
        IEnumerable<AccountInfo> accounts,
        IEnumerable<IChangesSource> sources,
        ILoggerProvider? logs = null,
        IProviderServiceFactory? providers = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (logs is not null)
            builder.Logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace);

        builder.Services.AddSingleton<ITenantContext, TenantContext>();
        builder.Services.AddSingleton<IOptionsMonitor<CalendarMcpConfiguration>>(
            new TestOptionsMonitor<CalendarMcpConfiguration>(new CalendarMcpConfiguration { Accounts = [.. accounts] }));
        builder.Services.AddSingleton<IAccountRegistry, AccountRegistry>();
        foreach (var source in sources)
            builder.Services.AddSingleton(source);
        builder.Services.AddSingleton<ChangesService>();
        if (providers is not null)
            builder.Services.AddSingleton(providers);
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, SubjectFromToken>("test", _ => { });
        builder.Services.AddAuthorization();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/admin"),
            admin => admin.UseMiddleware<AdminAuthMiddleware>("mcp:tools"));
        _app.MapGroup("/admin").MapChangesEndpoints();
        _app.StartAsync().GetAwaiter().GetResult();
        _client = _app.GetTestClient();
    }

    /// <summary>Posts as the tenant, or with no bearer at all when it is null.</summary>
    public async Task<(int Status, JsonElement Body)> PostAsync(string path, object body, string? tenant = TestData.TenantA)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (tenant is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenant);

        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class SubjectFromToken(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logs, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logs, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim(TenantIdentity.OAuthClaimName, header["Bearer ".Length..]), new Claim("scope", "mcp:tools")],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}

/// <summary>A source that answers what the test scripts, and counts how often it was asked.</summary>
internal sealed class ScriptedSource(string provider, string resource) : IChangesSource
{
    public string Provider { get; } = provider;
    public string Resource { get; } = resource;
    public int Reads { get; private set; }
    public int Starts { get; private set; }
    public int? LastLimit { get; private set; }
    public Func<ChangesCursor, ChangesBatch>? Read { get; set; }
    public Func<ChangesCursor>? Start { get; set; }
    public Func<ItemRef, ItemFacts?>? Item { get; set; }

    private string Kind => ChangesCursor.KindOf(Provider, Resource);

    public ChangesCursor Position(string position) => ChangesCursor.Of(Kind, "INBOX", position);

    public Task<ChangesCursor> StartAsync(string accountId, CancellationToken cancellationToken)
    {
        Starts++;
        return Task.FromResult(Start is null ? Position("now") : Start());
    }

    public Task<ChangesBatch> ReadAsync(string accountId, ChangesCursor cursor, int limit, CancellationToken cancellationToken)
    {
        Reads++;
        LastLimit = limit;
        return Task.FromResult(Read!(cursor));
    }

    public Task<ItemFacts?> ReadItemAsync(string accountId, ItemRef item, CancellationToken cancellationToken) =>
        Task.FromResult(Item!(item));
}

/// <summary>Keeps every line written through the logging pipeline, framework ones included.</summary>
internal sealed class CapturingLogs : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines) return [.. _lines];
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(_lines);

    public void Dispose()
    {
    }

    private sealed class Capture(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (lines) lines.Add($"{formatter(state, exception)} {exception}");
        }
    }
}
