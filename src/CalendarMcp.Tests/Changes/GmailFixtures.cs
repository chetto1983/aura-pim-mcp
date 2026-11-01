using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using CalendarMcp.Core.Providers;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Tests.Changes;

/// <summary>A Gmail mailbox on a <see cref="FakeHttp"/>, answering what the API reference documents.</summary>
internal static class GmailFixtures
{
    public static GoogleProviderService Provider(FakeHttp http) =>
        new(NullLogger<GoogleProviderService>.Instance, new SingleAccountRegistry()) { Transport = new FakeGoogleTransport(http) };

    public static string Json(object body) => JsonSerializer.Serialize(body);

    public static object Added(string id, params string[] labels) => new { message = new { id, labelIds = labels } };

    public static object LabelChange(string id, string[] changed, params string[] now) =>
        new { message = new { id, labelIds = now }, labelIds = changed };

    public static object Deleted(string id) => new { message = new { id } };

    /// <summary>A <c>messages.get</c> answer in format full.</summary>
    public static string Message(
        string id,
        string from = "Mario Rossi <mario@example.com>",
        string subject = "Fattura",
        string body = "Testo della mail",
        string[]? labels = null,
        string thread = "t1",
        (string Name, string Value)[]? headers = null) => Json(new
        {
            id,
            threadId = thread,
            labelIds = labels ?? ["INBOX"],
            internalDate = "1793457600000",
            payload = new
            {
                mimeType = "text/plain",
                headers = new[] { ("From", from), ("Subject", subject) }.Concat(headers ?? [])
                    .Select(h => new { name = h.Item1, value = h.Item2 }),
                body = new { data = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(body)) }
            }
        });

    public static FakeHttp NoSentMail(this FakeHttp http) =>
        http.On("/users/me/messages?", Json(new { resultSizeEstimate = 0 }));

    public const string NotFound = """{"error":{"code":404,"message":"Requested entity was not found.","errors":[{"reason":"notFound"}]}}""";

    public static FakeHttp OnHistory(this FakeHttp http, string fragment, object answer) =>
        http.On("/users/me/history?", request =>
            FakeHttp.Url(request).Contains(fragment, StringComparison.Ordinal)
                ? FakeHttp.Json(Json(answer))
                : throw new AssertFailedException($"Unscripted history read: {FakeHttp.Url(request)}"));

    public static HttpResponseMessage Fail(HttpStatusCode status, string reason) => FakeHttp.Json(
        Json(new { error = new { code = (int)status, message = reason, errors = new[] { new { reason } } } }), status);
}
