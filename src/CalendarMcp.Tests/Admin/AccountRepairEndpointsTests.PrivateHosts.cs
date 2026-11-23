using CalendarMcp.Core.Providers;

namespace CalendarMcp.Tests.Admin;

/// <summary>
/// The stored account remembers whether its servers may be on a private network, so every later connection, not
/// only the check at save time, can hold it to that (ledger Ruling 25). Only the server writes the key: from the
/// private-hosts header of the request that last named the servers.
/// </summary>
public sealed partial class AccountRepairEndpointsTests
{
    private const string AllowKey = ImapProviderService.AllowPrivateHostsKey;

    private static string? StoredPermission(AdminPipeline pipeline, string accountId) =>
        pipeline.StoredConfig(accountId)[AllowKey]?.GetValue<string>();

    [TestMethod]
    [DataRow(true, "true")]
    [DataRow(false, "false")]
    public async Task Create_StoresThePermission_FromTheHeader(bool header, string stored)
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);

        var (status, _) = await pipeline.PostAsync("/admin/accounts", Create(ImapConfig()), allowPrivate: header);

        Assert.AreEqual(201, status);
        Assert.AreEqual(stored, StoredPermission(pipeline, NewId));
        Assert.AreEqual(header, verifier.Calls.Single().Read!.AllowPrivateHosts, "the check reads what is stored");
    }

    [TestMethod]
    [DataRow("allowPrivateHosts", "true", false, "a member claiming the permission")]
    [DataRow("ALLOWPRIVATEHOSTS", "true", false, "in another case, which every reader of the file takes")]
    [DataRow("allowPrivateHosts", "false", true, "even a value the header would have written")]
    public async Task Create_APermissionInTheBody_Is400(string key, string value, bool header, string why)
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        var config = ImapConfig();
        config[key] = value;

        var (status, body) = await pipeline.PostAsync("/admin/accounts", Create(config), allowPrivate: header);

        Assert.AreEqual(400, status, why);
        Assert.AreEqual("server_owned_key", body.GetProperty("error").GetString());
        Assert.IsEmpty(verifier.Calls, "refused before anything logs in");
        Assert.IsFalse(Exists(pipeline, NewId));
    }

    [TestMethod]
    [DataRow(true, "true")]
    [DataRow(false, "false")]
    public async Task Patch_StoresThePermission_FromTheHeader(bool header, string stored)
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        Assert.IsNull(StoredPermission(pipeline, AdminPipeline.MailId), "the seeded account predates the key");

        var (status, _) = await pipeline.PatchAsync(
            ProviderConfig(AdminPipeline.MailId), Patch(new() { ["password"] = "nuova" }), allowPrivate: header);

        Assert.AreEqual(200, status);
        Assert.AreEqual(stored, StoredPermission(pipeline, AdminPipeline.MailId));
        Assert.AreEqual(header, verifier.Calls.Single().Read!.AllowPrivateHosts, "the check reads what is stored");
    }

    [TestMethod]
    public async Task Patch_ByAMember_OnAnAccountAnAdminMade_StoresFalse()
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        await pipeline.PostAsync("/admin/accounts", Create(ImapConfig()), allowPrivate: true);
        Assert.AreEqual("true", StoredPermission(pipeline, NewId));
        pipeline.Clock.Now += TimeSpan.FromSeconds(30);

        var (status, _) = await pipeline.PatchAsync(ProviderConfig(NewId), Patch(new() { ["inboxFolder"] = "Archivio" }));

        Assert.AreEqual(200, status);
        Assert.AreEqual("false", StoredPermission(pipeline, NewId), "whoever last changed the servers is whose permission counts");
    }

    [TestMethod]
    [DataRow("allowPrivateHosts", "true")]
    [DataRow("AllowPrivateHosts", "true")]
    [DataRow("allowPrivateHosts", "")]
    public async Task Patch_APermissionInTheBody_Is400(string key, string value)
    {
        var verifier = new FakeVerifier();
        await using var pipeline = new AdminPipeline(verifier);
        var before = pipeline.StoredAccount(AdminPipeline.MailId).ToJsonString();

        var (status, body) = await pipeline.PatchAsync(
            ProviderConfig(AdminPipeline.MailId), Patch(new() { [key] = value, ["password"] = "nuova" }));

        Assert.AreEqual(400, status);
        Assert.AreEqual("server_owned_key", body.GetProperty("error").GetString());
        Assert.IsEmpty(verifier.Calls);
        Assert.AreEqual(before, pipeline.StoredAccount(AdminPipeline.MailId).ToJsonString());
    }
}
