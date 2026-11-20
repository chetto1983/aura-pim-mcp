using CalendarMcp.Core.Discovery;
using DnsClient;
using DnsClient.Protocol;
using Rocks;

namespace CalendarMcp.Tests.Discovery;

/// <summary>The most preferred MX, and every way a DNS answer says there is none.</summary>
[TestClass]
public class MxLookupTests
{
    [TestMethod]
    public async Task TheLowestPreference_Wins_LowercasedWithoutItsDot()
    {
        var found = await Lookup(Answer(false, Mx(20, "backup.example.net."), Mx(10, "MX1.Example.NET.")));

        Assert.AreEqual("mx1.example.net", found);
    }

    [TestMethod]
    public async Task ANullMx_IsNoMx()
    {
        Assert.IsNull(await Lookup(Answer(false, Mx(0, "."))));
    }

    [TestMethod]
    public async Task AnErrorCode_IsNoMx()
    {
        Assert.IsNull(await Lookup(Answer(true, Mx(10, "mx1.example.net."))));
    }

    [TestMethod]
    public async Task ATimeout_IsNoMx()
    {
        var dns = new ILookupClientCreateExpectations();
        dns.Setups.QueryAsync("example.org", QueryType.MX, QueryClass.IN, Arg.Any<CancellationToken>())
            .Callback((_, _, _, _) => throw new DnsResponseException(DnsResponseCode.ConnectionTimeout, "timed out"));

        Assert.IsNull(await new MxLookup(dns.Instance()).PreferredMxHostAsync("example.org", default));
        dns.Verify();
    }

    private static async Task<string?> Lookup(IDnsQueryResponse response)
    {
        var dns = new ILookupClientCreateExpectations();
        dns.Setups.QueryAsync("example.org", QueryType.MX, QueryClass.IN, Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(response));
        var host = await new MxLookup(dns.Instance()).PreferredMxHostAsync("example.org", default);
        dns.Verify();
        return host;
    }

    private static IDnsQueryResponse Answer(bool hasError, params DnsResourceRecord[] answers)
    {
        var response = new IDnsQueryResponseCreateExpectations();
        response.Setups.HasError.Gets().ReturnValue(hasError);
        if (!hasError)
            response.Setups.Answers.Gets().ReturnValue(answers);
        return response.Instance();
    }

    private static MxRecord Mx(ushort preference, string exchange) => new(
        new ResourceRecordInfo("example.org.", ResourceRecordType.MX, QueryClass.IN, 300, 0), preference, DnsString.Parse(exchange));
}
