using System.Net;
using System.Text.Json;
using CalendarMcp.Core.Providers;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Tests.Changes;

/// <summary>A Microsoft mailbox on a <see cref="FakeHttp"/>, shaped like the samples in Graph's documentation.</summary>
internal static class GraphFixtures
{
    public const string Base = "https://graph.microsoft.com/v1.0";

    private static SingleAccountRegistry Registry() => new(
        TestData.CreateAccount(id: "m365", providerConfig: new Dictionary<string, string>
        {
            ["tenantId"] = "tenant",
            ["clientId"] = "client"
        }));

    public static M365ProviderService Provider(FakeHttp http) =>
        new(NullLogger<M365ProviderService>.Instance, new StaticTokenAuthentication(), Registry()) { Transport = http };

    public static OutlookComProviderService Outlook(FakeHttp http) =>
        new(NullLogger<OutlookComProviderService>.Instance, new StaticTokenAuthentication(), Registry()) { Transport = http };

    public static string Json(object body) => JsonSerializer.Serialize(body);

    public static string Delta(string link, params object[] rows) =>
        Json(new Dictionary<string, object> { ["@odata.deltaLink"] = link, ["value"] = rows });

    public static string Next(string link, params object[] rows) =>
        Json(new Dictionary<string, object> { ["@odata.nextLink"] = link, ["value"] = rows });

    public static object Row(string id) => new { id };

    public static object Removed(string id) => new Dictionary<string, object>
    {
        ["id"] = id,
        ["@removed"] = new { reason = "deleted" }
    };

    public static string Failure(string code) => Json(new { error = new { code, message = code } });

    public static FakeHttp OnError(this FakeHttp http, string fragment, HttpStatusCode status, string code) =>
        http.On(fragment, Failure(code), status);

    /// <summary>A <c>GET /me/messages/{id}</c> answer.</summary>
    public static string Message(
        string id,
        string address = "mario@example.com",
        string subject = "Fattura",
        string body = "Testo della mail",
        string contentType = "text",
        string importance = "normal",
        string classification = "focused",
        string conversation = "conv-1",
        (string Name, string Value)[]? headers = null) => Json(new
        {
            id,
            subject,
            from = new { emailAddress = new { name = "Mario Rossi", address } },
            receivedDateTime = "2026-11-01T11:00:00Z",
            body = new { contentType, content = body },
            importance,
            inferenceClassification = classification,
            conversationId = conversation,
            internetMessageHeaders = (headers ?? []).Select(h => new { name = h.Name, value = h.Value })
        });

    public static FakeHttp NoSentItems(this FakeHttp http) =>
        http.On("/mailFolders/sentitems/messages", Json(new { value = Array.Empty<object>() }));
}
