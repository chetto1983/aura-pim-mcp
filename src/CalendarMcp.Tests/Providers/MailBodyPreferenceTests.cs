using System.Text;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Utilities;
using CalendarMcp.Tests.Helpers;
using Google.Apis.Gmail.v1.Data;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using MessagePart = Google.Apis.Gmail.v1.Data.MessagePart;

namespace CalendarMcp.Tests.Providers;

/// <summary>
/// A mail that offers text/plain is read from that part, and only HTML-only mail needs converting.
/// </summary>
[TestClass]
public class MailBodyPreferenceTests
{
    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static MessagePart Part(string mimeType, string text, string? filename = null) =>
        new() { MimeType = mimeType, Filename = filename, Body = new MessagePartBody { Data = Base64Url(text) } };

    private static Message Gmail(MessagePart payload) => new() { Id = "m1", Payload = payload };

    [TestMethod]
    public void Gmail_Alternative_PrefersThePlainPart()
    {
        var message = Gmail(new MessagePart
        {
            MimeType = "multipart/alternative",
            Parts = [Part("text/html", "<p>rich</p>"), Part("text/plain", "plain")]
        });

        Assert.AreEqual(("plain", "text"), GoogleProviderService.GetMessageBody(message));
    }

    [TestMethod]
    public void Gmail_HtmlOnly_SaysItIsHtml()
    {
        // An HTML part inside multipart/mixed used to claim "text", because the format was read
        // from the top-level MIME type.
        var message = Gmail(new MessagePart { MimeType = "multipart/mixed", Parts = [Part("text/html", "<p>rich</p>")] });

        Assert.AreEqual(("<p>rich</p>", "html"), GoogleProviderService.GetMessageBody(message));
    }

    [TestMethod]
    public void Gmail_TextAttachment_IsNotTheBody()
    {
        var message = Gmail(new MessagePart
        {
            MimeType = "multipart/mixed",
            Parts = [Part("text/html", "<p>rich</p>"), Part("text/plain", "attached notes", "notes.txt")]
        });

        Assert.AreEqual(("<p>rich</p>", "html"), GoogleProviderService.GetMessageBody(message));
    }

    [TestMethod]
    public void Gmail_ForwardedMessage_IsNotTheBody()
    {
        var forwarded = new MessagePart { MimeType = "message/rfc822", Parts = [Part("text/plain", "someone else's words")] };
        var message = Gmail(new MessagePart
        {
            MimeType = "multipart/mixed",
            Parts = [forwarded, Part("text/html", "<p>rich</p>")]
        });

        Assert.AreEqual(("<p>rich</p>", "html"), GoogleProviderService.GetMessageBody(message));
    }

    [TestMethod]
    public void Gmail_TextTrailingTheHtml_IsAFooterAndNotTheBody()
    {
        var message = Gmail(new MessagePart
        {
            MimeType = "multipart/mixed",
            Parts = [Part("text/html", "<p>rich</p>"), Part("text/plain", "To unsubscribe from this list")]
        });

        Assert.AreEqual(("<p>rich</p>", "html"), GoogleProviderService.GetMessageBody(message));
    }

    [TestMethod]
    public void Gmail_TextLeadingTheHtml_IsTheBody()
    {
        var message = Gmail(new MessagePart
        {
            MimeType = "multipart/mixed",
            Parts = [Part("text/plain", "plain"), Part("text/html", "<p>a footer</p>")]
        });

        Assert.AreEqual(("plain", "text"), GoogleProviderService.GetMessageBody(message));
    }

    [TestMethod]
    public void Gmail_AnAlternativeInsideMixed_StillPrefersItsPlainPart()
    {
        var message = Gmail(new MessagePart
        {
            MimeType = "multipart/mixed",
            Parts =
            [
                new MessagePart { MimeType = "multipart/alternative", Parts = [Part("text/html", "<p>rich</p>"), Part("text/plain", "plain")] },
                Part("text/plain", "To unsubscribe from this list")
            ]
        });

        Assert.AreEqual(("plain", "text"), GoogleProviderService.GetMessageBody(message));
    }

    [TestMethod]
    [DataRow("multipart/alternative")]
    [DataRow("multipart/mixed")]
    public void Gmail_ABlankTextPart_IsNoTextPart(string container)
    {
        var message = Gmail(new MessagePart { MimeType = container, Parts = [Part("text/plain", " \r\n\t \r\n"), Part("text/html", "<p>rich</p>")] });

        Assert.AreEqual(("<p>rich</p>", "html"), GoogleProviderService.GetMessageBody(message));
    }

    [TestMethod]
    public void Gmail_TextOfNothingButInvisibleCharacters_IsBlank()
    {
        var message = Gmail(new MessagePart { MimeType = "multipart/alternative", Parts = [Part("text/plain", "\u200b\u200d "), Part("text/html", "<p>rich</p>")] });

        Assert.AreEqual(("<p>rich</p>", "html"), GoogleProviderService.GetMessageBody(message));
    }

    [TestMethod]
    public void Gmail_EveryPartBlank_IsEmptyText()
    {
        var message = Gmail(new MessagePart { MimeType = "multipart/mixed", Parts = [Part("text/plain", "  "), Part("text/html", " ")] });

        Assert.AreEqual((string.Empty, "text"), GoogleProviderService.GetMessageBody(message));
    }

    private static MessagePart NestedIn(int levels, MessagePart part)
    {
        for (var i = 0; i < levels; i++)
            part = new MessagePart { MimeType = "multipart/mixed", Parts = [part] };

        return part;
    }

    [TestMethod]
    public void Gmail_PartsNestedPastAnyRealMail_SayTheBodyIsLeftOut()
    {
        var message = Gmail(NestedIn(100_000, Part("text/plain", "plain")));

        Assert.AreEqual((MailBody.TooDeep, MailBody.OmittedFormat), GoogleProviderService.GetMessageBody(message));
    }

    [TestMethod]
    public void Gmail_PartsAtTheLastLevelRead_AreRead()
    {
        Assert.AreEqual(("plain", "text"), GoogleProviderService.GetMessageBody(Gmail(NestedIn(32, Part("text/plain", "plain")))));
    }

    [TestMethod]
    public void Gmail_APartBeforeTheDepth_IsTheBody()
    {
        var message = Gmail(new MessagePart { MimeType = "multipart/mixed", Parts = [Part("text/plain", "plain"), NestedIn(100, Part("text/plain", "deep"))] });

        Assert.AreEqual(("plain", "text"), GoogleProviderService.GetMessageBody(message));
    }

    [TestMethod]
    public void Gmail_OnlyBlankBeforeTheDepth_SaysTheBodyIsLeftOut()
    {
        var message = Gmail(new MessagePart { MimeType = "multipart/mixed", Parts = [Part("text/plain", " "), NestedIn(100, Part("text/plain", "deep"))] });

        Assert.AreEqual((MailBody.TooDeep, MailBody.OmittedFormat), GoogleProviderService.GetMessageBody(message));
    }

    [TestMethod]
    public void ABodyLeftOut_IsNoSnippet()
    {
        Assert.AreEqual(string.Empty, MailSnippet.Of(MailBody.TooDeep, MailBody.OmittedFormat));
    }

    [TestMethod]
    public void Gmail_NoBody_IsEmptyText()
    {
        Assert.AreEqual((string.Empty, "text"), GoogleProviderService.GetMessageBody(new Message { Id = "m1" }));
    }

    [TestMethod]
    public async Task Gmail_DetailsCarryTheRightFormatThroughTheProvider()
    {
        var http = new FakeHttp().On("/users/me/messages/m1", $$$"""
            {"id":"m1","payload":{"mimeType":"multipart/mixed","parts":[{"mimeType":"text/html","body":{"data":"{{{Base64Url("<p>rich</p>")}}}"}}]}}
            """);
        var provider = new GoogleProviderService(NullLogger<GoogleProviderService>.Instance, new SingleAccountRegistry())
        {
            Transport = new FakeGoogleTransport(http)
        };

        var email = await provider.GetEmailDetailsAsync("a", "m1");

        Assert.AreEqual("<p>rich</p>", email!.Body);
        Assert.AreEqual("html", email.BodyFormat);
    }

    [TestMethod]
    public void Imap_Alternative_PrefersThePlainPart()
    {
        var builder = new BodyBuilder { TextBody = "plain", HtmlBody = "<p>rich</p>" };
        var message = new MimeMessage { Body = builder.ToMessageBody() };

        Assert.AreEqual(("plain", "text"), ImapProviderService.ExtractBody(message));
    }

    [TestMethod]
    public void Imap_HtmlOnly_SaysItIsHtml()
    {
        var message = new MimeMessage { Body = new BodyBuilder { HtmlBody = "<p>rich</p>" }.ToMessageBody() };

        Assert.AreEqual(("<p>rich</p>", "html"), ImapProviderService.ExtractBody(message));
    }

    private static MimeMessage Tree(string subtype, params MimeEntity[] parts)
    {
        var multipart = new Multipart(subtype);
        foreach (var part in parts)
            multipart.Add(part);

        return new MimeMessage { Body = multipart };
    }

    private static TextPart Text(string subtype, string text) => new(subtype) { Text = text };

    [TestMethod]
    public void Imap_TextTrailingTheHtml_IsAFooterAndNotTheBody()
    {
        var message = Tree("mixed", Text("html", "<p>rich</p>"), Text("plain", "To unsubscribe from this list"));

        Assert.AreEqual(("<p>rich</p>", "html"), ImapProviderService.ExtractBody(message));
    }

    [TestMethod]
    public void Imap_TextLeadingTheHtml_IsTheBody()
    {
        var message = Tree("mixed", Text("plain", "plain"), Text("html", "<p>a footer</p>"));

        Assert.AreEqual(("plain", "text"), ImapProviderService.ExtractBody(message));
    }

    [TestMethod]
    [DataRow("alternative")]
    [DataRow("mixed")]
    public void Imap_ABlankTextPart_IsNoTextPart(string subtype)
    {
        var message = Tree(subtype, Text("plain", " \r\n\t \r\n"), Text("html", "<p>rich</p>"));

        Assert.AreEqual(("<p>rich</p>", "html"), ImapProviderService.ExtractBody(message));
    }

    [TestMethod]
    public void Imap_ATextAttachment_IsNotTheBody()
    {
        var attached = Text("plain", "attached notes");
        attached.FileName = "notes.txt";
        var message = Tree("mixed", attached, Text("html", "<p>rich</p>"));

        Assert.AreEqual(("<p>rich</p>", "html"), ImapProviderService.ExtractBody(message));
    }

    [TestMethod]
    public void Imap_FlowedText_IsStillUnflowed()
    {
        var flowed = Text("plain", "una riga \r\nsola\r\n");
        flowed.ContentType.Format = "flowed";

        Assert.AreEqual("una riga sola", ImapProviderService.ExtractBody(new MimeMessage { Body = flowed }).Body.Trim());
    }

    [TestMethod]
    public void Imap_FlowedTextWithDelSp_DropsTheSpace()
    {
        var flowed = Text("plain", "una ri \r\nga sola\r\n");
        flowed.ContentType.Format = "flowed";
        flowed.ContentType.Parameters.Add("delsp", "yes");

        Assert.AreEqual("una riga sola", ImapProviderService.ExtractBody(new MimeMessage { Body = flowed }).Body.Trim());
    }

    [TestMethod]
    public void Imap_NoBody_IsEmptyText()
    {
        Assert.AreEqual((string.Empty, "text"), ImapProviderService.ExtractBody(new MimeMessage { Body = new Multipart("mixed") }));
    }
}
