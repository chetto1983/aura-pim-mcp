using System.Net;
using System.Net.Sockets;
using CalendarMcp.Core.Models;
using CalendarMcp.Core.Providers;
using CalendarMcp.Core.Utilities;
using CalendarMcp.Tests.Admin;
using CalendarMcp.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Tests.Utilities;

/// <summary>
/// A one-click unsubscribe POSTs to a URL a mail's sender wrote. Whoever can mail this inbox names it, so it must
/// not reach the PIM's own network, must not be redirected there, and what the far side answers must not come back
/// to the caller: that would make the tool a way to read the PIM's neighbours.
/// </summary>
[TestClass]
public class UnsubscribeExecutorTests
{
    private const string Url = "https://unsub.example.org/u/1";

    private static UnsubscribeInfo OneClick() => new() { SupportsOneClick = true, HttpsUrl = Url };

    private static UnsubscribeExecutor Executor(HttpMessageHandler handler) =>
        new(new OneClient(handler), NullLogger<UnsubscribeExecutor>.Instance);

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [TestMethod]
    public async Task AUrlThatResolvesPrivate_IsRefusedWithoutConnecting()
    {
        var resolved = new List<string>();
        var connected = new List<IPEndPoint>();
        var guard = new HostGuard(
            (host, _) =>
            {
                resolved.Add(host);
                return Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") });
            },
            (endpoint, _) =>
            {
                connected.Add(endpoint);
                return Task.FromException<Socket>(new SocketException((int)SocketError.ConnectionRefused));
            });
        using var handler = guard.CreatePublicHttpHandler();

        var result = await Executor(handler).ExecuteOneClickAsync(OneClick());

        Assert.IsFalse(result.Success);
        CollectionAssert.AreEqual(new[] { "unsub.example.org" }, resolved);
        Assert.IsEmpty(connected, "a private address is never connected");
        Assert.IsNull(result.ErrorDetails);
        Assert.DoesNotContain("10.0.0.5", result.Message);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task TheRegisteredClient_IsGuardedAndFollowsNoRedirect()
    {
        await using var pipeline = new AdminPipeline(verifier: null);
        HttpMessageHandler handler = pipeline.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(UnsubscribeExecutor.HttpClientName);
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler!;

        var primary = handler as SocketsHttpHandler;
        Assert.IsNotNull(primary, "the primary handler is the guarded one");
        Assert.IsFalse(primary.AllowAutoRedirect);
        Assert.IsFalse(primary.UseProxy);
        Assert.IsNotNull(primary.ConnectCallback);
    }

    [TestMethod]
    public async Task ARedirect_IsNotFollowed_AndIsNoSuccess()
    {
        var http = new FakeHttp().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://10.0.0.5/admin") }
        });

        var result = await Executor(http).ExecuteOneClickAsync(OneClick());

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Unsubscribe request returned HTTP 302", result.Message);
        Assert.HasCount(1, http.Requests);
    }

    [TestMethod]
    public async Task WhatTheFarSideAnswers_IsNotReturned()
    {
        var http = new FakeHttp().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("internal page the caller must not read")
        });

        var result = await Executor(http).ExecuteOneClickAsync(OneClick());

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Unsubscribe request returned HTTP 500", result.Message);
        Assert.IsNull(result.ErrorDetails);
    }

    [TestMethod]
    public async Task AFailure_ReturnsNoExceptionText()
    {
        var http = new FakeHttp().On(_ => true, _ => throw new HttpRequestException("'unsub.example.org' resolves to 10.0.0.5"));

        var result = await Executor(http).ExecuteOneClickAsync(OneClick());

        Assert.IsFalse(result.Success);
        Assert.IsNull(result.ErrorDetails);
        Assert.DoesNotContain("10.0.0.5", result.Message);
    }
}
