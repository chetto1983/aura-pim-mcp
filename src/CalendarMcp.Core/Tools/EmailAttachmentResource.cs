using System.Security.Claims;
using CalendarMcp.Core.Services;
using CalendarMcp.Core.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CalendarMcp.Core.Tools;

/// <summary>
/// <c>attachment://{attachmentId}</c>: the bytes get_email_attachment stashed, read back by the
/// client its result linked them to. The tenant comes from the request principal -- the bearer's
/// <c>sub</c> over HTTP, the local tenant the stdio filter sets -- so an id minted for one tenant
/// reads as unknown to every other, exactly as it already does for send_email.
/// </summary>
/// <remarks>
/// Reads through <see cref="IAttachmentStore.TryRead"/>, which does not consume: a client that
/// opens the file and then forwards it with send_email must still find the stash.
/// </remarks>
public sealed class EmailAttachmentResource(IAttachmentStore store, ITenantContext tenantContext, ClaimsPrincipal? user)
{
    public const string UriTemplate = "attachment://{attachmentId}";

    public static string UriFor(string attachmentId) => "attachment://" + attachmentId;

    public BlobResourceContents Read(string attachmentId)
    {
        IDisposable scope;
        try
        {
            scope = tenantContext.Bind(TenantIdentity.FromPrincipal(user));
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }
        using (scope)
        {
            var stored = store.TryRead(attachmentId)
                ?? throw new McpException("attachment expired or unknown; call get_email_attachment again");
            return BlobResourceContents.FromBytes(stored.Bytes, UriFor(stored.Id), MimeTypeFor(stored.Name, stored.ContentType));
        }
    }

    /// <summary>
    /// Providers often label an attachment <c>application/octet-stream</c>; the file name then
    /// knows more than the header, and a client choosing how to open the file needs the better one.
    /// </summary>
    internal static string MimeTypeFor(string name, string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) || contentType == "application/octet-stream"
            ? MimeKit.MimeTypes.GetMimeType(name)
            : contentType;
}

/// <summary>
/// Registers <see cref="EmailAttachmentResource"/> through the factory overload of
/// <c>McpServerResource.Create</c>: it hands over the <c>RequestContext</c>, and with it the
/// request principal the tenant is bound from. Neither HTTP nor stdio exposes that principal
/// any other way to a resource method.
/// </summary>
public static class EmailAttachmentResourceServiceExtensions
{
    public static IMcpServerBuilder WithEmailAttachmentResource(this IMcpServerBuilder builder)
    {
        var method = typeof(EmailAttachmentResource).GetMethod(nameof(EmailAttachmentResource.Read))
            ?? throw new InvalidOperationException("EmailAttachmentResource.Read method not found.");
        builder.Services.AddSingleton<McpServerResource>(services => McpServerResource.Create(
            method,
            request =>
            {
                var provider = request.Services ?? services;
                return new EmailAttachmentResource(
                    provider.GetRequiredService<IAttachmentStore>(),
                    provider.GetRequiredService<ITenantContext>(),
                    request.User);
            },
            new McpServerResourceCreateOptions
            {
                UriTemplate = EmailAttachmentResource.UriTemplate,
                Name = "email-attachment",
                Title = "Email attachment",
                Description = "The bytes of an attachment get_email_attachment fetched; readable until the attachment expires.",
                MimeType = "application/octet-stream",
                Services = services,
            }));
        return builder;
    }
}
