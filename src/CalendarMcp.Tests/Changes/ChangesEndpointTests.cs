using System.Text.Json;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;
using CalendarMcp.Tests.Helpers;

namespace CalendarMcp.Tests.Changes;

[TestClass]
public class ChangesEndpointTests
{
    private const string Mine = "mine";
    private const string Theirs = "theirs";

    private static AccountInfo Account(string id, string provider, string tenant = TestData.TenantA, AccountPermissions? permissions = null) =>
        TestData.CreateAccount(id: id, provider: provider, tenantId: tenant, permissions: permissions);

    private static object Ask(string account = Mine, string resource = "mail", string? cursor = null, int? limit = null) =>
        new { accountId = account, resource, cursor, limit };

    private static string Error(JsonElement body) => body.GetProperty("error").GetString()!;

    [TestMethod]
    public async Task EmptyCursor_StartsFromNowWithNoChanges()
    {
        var mail = new ScriptedSource(ChangesProviders.Google, ChangeResource.Mail);
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);

        var (status, body) = await pim.PostAsync("/admin/changes", Ask());

        Assert.AreEqual(200, status);
        Assert.AreEqual(0, body.GetProperty("changes").GetArrayLength());
        Assert.AreEqual(mail.Position("now").Encode(), body.GetProperty("nextCursor").GetString());
        Assert.IsFalse(body.GetProperty("hasMore").GetBoolean());
        Assert.IsFalse(body.GetProperty("resetRequired").GetBoolean());
        Assert.AreEqual(0, mail.Reads);
    }

    [TestMethod]
    public async Task Page_IsWrittenAsTheContractPinsIt()
    {
        var mail = new ScriptedSource(ChangesProviders.Google, ChangeResource.Mail);
        mail.Read = _ => new ChangesBatch(
        [
            new Change
            {
                Type = ChangeType.Added,
                Id = "m1",
                OccurredAt = new DateTimeOffset(2026, 10, 31, 14, 47, 0, TimeSpan.Zero),
                From = "sender@example.com",
                Subject = "Fattura",
                Snippet = "Testo",
                Signals = new MailSignals { Bulk = false, ThreadId = "t1", WrittenTo = null }
            },
            new Change { Type = ChangeType.Deleted, Id = "m2" }
        ], mail.Position("next"), true);
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);

        var (status, body) = await pim.PostAsync("/admin/changes", Ask(cursor: mail.Position("old").Encode()));

        Assert.AreEqual(200, status);
        var changes = body.GetProperty("changes");
        Assert.AreEqual("added", changes[0].GetProperty("type").GetString());
        Assert.AreEqual("2026-10-31T14:47:00Z", changes[0].GetProperty("occurredAt").GetString());
        Assert.AreEqual(JsonValueKind.Null, changes[0].GetProperty("signals").GetProperty("writtenTo").ValueKind,
            "null is a fact here, so it has to be written");
        Assert.IsFalse(changes[1].TryGetProperty("subject", out _), "a deleted row carries its id alone");
        Assert.IsFalse(changes[1].TryGetProperty("signals", out _));
        Assert.IsTrue(body.GetProperty("hasMore").GetBoolean());
        Assert.AreEqual(mail.Position("next").Encode(), body.GetProperty("nextCursor").GetString());
    }

    [TestMethod]
    public async Task AnInstantWithAnOffset_IsWrittenInUtc()
    {
        var mail = new ScriptedSource(ChangesProviders.Google, ChangeResource.Mail);
        mail.Read = _ => new ChangesBatch(
        [
            new Change { Type = ChangeType.Added, Id = "m1", OccurredAt = new DateTimeOffset(2026, 10, 31, 16, 47, 0, TimeSpan.FromHours(2)) }
        ], mail.Position("next"), false);
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);

        var (_, body) = await pim.PostAsync("/admin/changes", Ask(cursor: mail.Position("old").Encode()));

        Assert.AreEqual("2026-10-31T14:47:00Z", body.GetProperty("changes")[0].GetProperty("occurredAt").GetString());
    }

    [TestMethod]
    public void AWrittenInstant_ReadsBack()
    {
        var change = new Change { Type = ChangeType.Added, Id = "m1", OccurredAt = new DateTimeOffset(2026, 10, 31, 14, 47, 0, TimeSpan.Zero) };

        var read = JsonSerializer.Deserialize<Change>(JsonSerializer.Serialize(change));

        Assert.AreEqual(change.OccurredAt, read!.OccurredAt);
    }

    [TestMethod]
    public async Task ExpiredCursor_ComesBackWithNoChangesAndAFreshCursor()
    {
        var mail = new ScriptedSource(ChangesProviders.Google, ChangeResource.Mail)
        {
            Read = _ => throw new CursorExpiredException()
        };
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);

        var (status, body) = await pim.PostAsync("/admin/changes", Ask(cursor: mail.Position("old").Encode()));

        Assert.AreEqual(200, status);
        Assert.IsTrue(body.GetProperty("resetRequired").GetBoolean());
        Assert.AreEqual(0, body.GetProperty("changes").GetArrayLength());
        Assert.AreEqual(mail.Position("now").Encode(), body.GetProperty("nextCursor").GetString());
    }

    [TestMethod]
    public async Task PartlyExpiredCursor_KeepsThePositionsThatSurvived()
    {
        var calendar = new ScriptedSource(ChangesProviders.Google, ChangeResource.Calendar);
        var replacement = calendar.Position("kept");
        calendar.Read = _ => throw new CursorExpiredException(replacement);
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [calendar]);

        var (_, body) = await pim.PostAsync(
            "/admin/changes", Ask(resource: "calendar", cursor: calendar.Position("old").Encode()));

        Assert.IsTrue(body.GetProperty("resetRequired").GetBoolean());
        Assert.AreEqual(replacement.Encode(), body.GetProperty("nextCursor").GetString());
        Assert.AreEqual(0, calendar.Starts);
    }

    [TestMethod]
    public async Task ACursorFromAnotherVersion_Resets()
    {
        var mail = new ScriptedSource(ChangesProviders.Google, ChangeResource.Mail);
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);
        var future = System.Buffers.Text.Base64Url.EncodeToString("""{"v":9,"k":"google.mail","f":{}}"""u8);

        var (status, body) = await pim.PostAsync("/admin/changes", Ask(cursor: future));

        Assert.AreEqual(200, status);
        Assert.IsTrue(body.GetProperty("resetRequired").GetBoolean());
        Assert.AreEqual(0, mail.Reads);
    }

    [TestMethod]
    [DataRow(null, 50)]
    [DataRow(0, 50)]
    [DataRow(-5, 50)]
    [DataRow(10, 10)]
    [DataRow(200, 200)]
    [DataRow(100000, 200)]
    public async Task Limit_IsBounded(int? asked, int effective)
    {
        var mail = new ScriptedSource(ChangesProviders.Google, ChangeResource.Mail);
        mail.Read = cursor => new ChangesBatch([], cursor, false);
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);

        await pim.PostAsync("/admin/changes", Ask(cursor: mail.Position("old").Encode(), limit: asked));

        Assert.AreEqual(effective, mail.LastLimit);
    }

    [TestMethod]
    public async Task AForeignTenantsAccount_AnswersExactlyLikeOneThatDoesNotExist()
    {
        var mail = new ScriptedSource(ChangesProviders.Google, ChangeResource.Mail);
        await using var pim = new ChangesPipeline(
            [Account(Mine, "google"), Account(Theirs, "google", TestData.TenantB)], [mail]);

        var foreign = await pim.PostAsync("/admin/changes", Ask(account: Theirs));
        var missing = await pim.PostAsync("/admin/changes", Ask(account: "nobody"));

        Assert.AreEqual(404, foreign.Status);
        Assert.AreEqual(missing.Status, foreign.Status);
        Assert.AreEqual(missing.Body.GetRawText(), foreign.Body.GetRawText());
        Assert.AreEqual(0, mail.Starts + mail.Reads, "the provider must not be touched for another tenant's account");
    }

    [TestMethod]
    public async Task NoBearer_IsRefusedBeforeAnythingIsRead()
    {
        var mail = new ScriptedSource(ChangesProviders.Google, ChangeResource.Mail);
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);

        var (status, _) = await pim.PostAsync("/admin/changes", Ask(), tenant: null);

        Assert.AreEqual(401, status);
        Assert.AreEqual(0, mail.Starts);
    }

    [TestMethod]
    [DataRow("ics")]
    [DataRow("json")]
    [DataRow("dav")]
    [DataRow("caldav")]
    public async Task AProviderWithNoSource_NamesItsAnswer(string provider)
    {
        await using var pim = new ChangesPipeline([Account(Mine, provider)], []);

        var (status, body) = await pim.PostAsync("/admin/changes", Ask(resource: "calendar"));

        Assert.AreEqual(422, status);
        Assert.AreEqual("unsupported_resource", Error(body));
    }

    [TestMethod]
    public async Task CalendarOnAnImapAccount_IsUnsupported()
    {
        var mail = new ScriptedSource(ChangesProviders.Imap, ChangeResource.Mail);
        await using var pim = new ChangesPipeline([Account(Mine, "imap")], [mail]);

        var (status, body) = await pim.PostAsync("/admin/changes", Ask(resource: "calendar"));

        Assert.AreEqual(422, status);
        Assert.AreEqual("unsupported_resource", Error(body));
    }

    [TestMethod]
    [DataRow("mail", "EmailRead")]
    [DataRow("calendar", "CalendarRead")]
    public async Task ALostPermission_IsRefusedBeforeTheProviderIsAsked(string resource, string permission)
    {
        var source = new ScriptedSource(ChangesProviders.Google, resource);
        var denied = AccountPermissions.All.With(Enum.Parse<AccountPermission>(permission), false);
        await using var pim = new ChangesPipeline([Account(Mine, "google", permissions: denied)], [source]);

        var (status, body) = await pim.PostAsync("/admin/changes", Ask(resource: resource));

        Assert.AreEqual(403, status);
        Assert.AreEqual("permission_denied", Error(body));
        Assert.AreEqual(0, source.Starts);
    }

    public static IEnumerable<object[]> ProviderFailuresByName =>
    [
        [new HttpRequestException("reset"), 503, "provider_transient"],
        [new AccountAuthenticationRequiredException("a"), 502, "provider_auth"],
        [new Microsoft.Kiota.Abstractions.ApiException { ResponseStatusCode = 403 }, 403, "permission_denied"],
    ];

    [TestMethod]
    [DynamicData(nameof(ProviderFailuresByName))]
    public async Task AProviderFailure_IsAnHttpErrorNeverAnEmptySuccess(Exception failure, int status, string error)
    {
        var mail = new ScriptedSource(ChangesProviders.Google, ChangeResource.Mail) { Read = _ => throw failure };
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);

        var answer = await pim.PostAsync("/admin/changes", Ask(cursor: mail.Position("old").Encode()));

        Assert.AreEqual(status, answer.Status);
        Assert.AreEqual(error, Error(answer.Body));
    }

    [TestMethod]
    public async Task AProviderFailureWhileStarting_IsNamedToo()
    {
        var mail = new ScriptedSource(ChangesProviders.Google, ChangeResource.Mail)
        {
            Start = () => throw new TimeoutException()
        };
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);

        var (status, body) = await pim.PostAsync("/admin/changes", Ask());

        Assert.AreEqual(503, status);
        Assert.AreEqual("provider_transient", Error(body));
    }

    [TestMethod]
    public async Task AGarbageCursor_IsNamedAndIsNotAReset()
    {
        var mail = new ScriptedSource(ChangesProviders.Google, ChangeResource.Mail);
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail]);

        var (status, body) = await pim.PostAsync("/admin/changes", Ask(cursor: "@@not a cursor@@"));

        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_cursor", Error(body));
        Assert.AreEqual(0, mail.Starts);
    }

    [TestMethod]
    [DataRow("contacts")]
    [DataRow("")]
    public async Task AnUnknownResource_IsTheCallersError(string resource)
    {
        await using var pim = new ChangesPipeline([Account(Mine, "google")], []);

        var (status, body) = await pim.PostAsync("/admin/changes", Ask(resource: resource));

        Assert.AreEqual(400, status);
        Assert.AreEqual("invalid_resource", Error(body));
    }

    [TestMethod]
    public async Task NoCursorEverReachesTheLogs()
    {
        const string Secret = "https://graph.microsoft.com/v1.0/me/delta?$deltatoken=TOKEN-THAT-MUST-NOT-LEAK";
        var logs = new CapturingLogs();
        var mail = new ScriptedSource(ChangesProviders.Google, ChangeResource.Mail);
        var cursor = mail.Position(Secret).Encode();
        var failing = new ScriptedSource(ChangesProviders.Google, ChangeResource.Calendar)
        {
            Read = _ => throw new HttpRequestException($"failed {Secret}")
        };
        mail.Read = _ => throw new CursorExpiredException();
        await using var pim = new ChangesPipeline([Account(Mine, "google")], [mail, failing], logs);

        await pim.PostAsync("/admin/changes", Ask(cursor: cursor));
        await pim.PostAsync("/admin/changes", Ask(resource: "calendar", cursor: failing.Position(Secret).Encode()));
        await pim.PostAsync("/admin/changes", Ask(cursor: "garbage" + cursor));

        Assert.IsTrue(logs.Lines.Count > 0, "the capture must have seen the pipeline's logging");
        foreach (var line in logs.Lines)
        {
            Assert.IsFalse(line.Contains("TOKEN-THAT-MUST-NOT-LEAK"), line);
            Assert.IsFalse(line.Contains(cursor), line);
        }
    }
}
