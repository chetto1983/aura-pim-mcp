using System.Text;
using CalendarMcp.Core.Providers;
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

    [TestMethod]
    public void Imap_NoBody_IsEmptyText()
    {
        Assert.AreEqual((string.Empty, "text"), ImapProviderService.ExtractBody(new MimeMessage { Body = new Multipart("mixed") }));
    }
}
