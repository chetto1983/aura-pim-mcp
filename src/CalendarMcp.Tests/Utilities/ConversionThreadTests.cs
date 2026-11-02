using System.Diagnostics;
using System.Runtime.InteropServices;
using CalendarMcp.Core.Changes;
using CalendarMcp.Core.Configuration;
using CalendarMcp.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CalendarMcp.Tests.Utilities;

/// <summary>
/// The layer that protects against the shape nobody has found yet. A conversion here never really
/// hangs: it waits on an event the test sets, so the abandoned threads end with the test.
/// </summary>
[TestClass]
public class ConversionThreadTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromMilliseconds(50);

    private readonly List<ManualResetEventSlim> _hanging = [];

    private readonly RecordingLogger _log = new();

    [TestInitialize]
    public void Listen() => ConversionThread.Logger = _log;

    [TestCleanup]
    public void LetGo()
    {
        ConversionThread.Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        foreach (var hang in _hanging)
            hang.Set();

        var watch = Stopwatch.StartNew();
        while (ConversionThread.Abandoned > 0 && watch.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(5);

        Assert.AreEqual(0, ConversionThread.Abandoned);
    }

    private void AbandonOne()
    {
        var hang = new ManualResetEventSlim();
        _hanging.Add(hang);

        Assert.ThrowsExactly<UnconvertibleMailException>(() => ConversionThread.Run(
            () =>
            {
                hang.Wait();
                return "too late";
            },
            new ConversionBudget(Soon)));
    }

    [TestMethod]
    public async Task TheHost_GivesTheThreadsItsLogger()
    {
        var services = new ServiceCollection().AddLogging().AddCalendarMcpCore().BuildServiceProvider();
        var logging = services.GetServices<IHostedService>().OfType<ConversionThreadLogging>().Single();

        await logging.StartAsync(CancellationToken.None);

        Assert.AreNotSame(_log, ConversionThread.Logger);
    }

    [TestMethod]
    public void AConversionThatReturns_AnswersWithItsText()
    {
        Assert.AreEqual("Parole", ConversionThread.Run(() => "Parole", new ConversionBudget(TimeSpan.FromSeconds(5))));
        Assert.AreEqual(0, ConversionThread.Abandoned);
    }

    [TestMethod]
    public void AConversionThatFails_FailsWithItsOwnException()
    {
        Assert.ThrowsExactly<UnconvertibleMailException>(() => ConversionThread.Run(
            () => throw new UnconvertibleMailException("no"), new ConversionBudget(TimeSpan.FromSeconds(5))));
        Assert.ThrowsExactly<OperationCanceledException>(() => ConversionThread.Run(
            () => throw new OperationCanceledException(), new ConversionBudget(TimeSpan.FromSeconds(5))));
        Assert.AreEqual(0, ConversionThread.Abandoned);
    }

    [TestMethod]
    public void AConversionThatNeverReturns_IsLeftBehindCloseToItsBudget()
    {
        var watch = Stopwatch.StartNew();

        AbandonOne();

        Assert.AreEqual(1, ConversionThread.Abandoned);
        Assert.IsTrue(watch.Elapsed >= Soon + ConversionThread.Grace - TimeSpan.FromMilliseconds(10), $"{watch.Elapsed}");
        Assert.IsTrue(watch.Elapsed < Soon + ConversionThread.Grace + TimeSpan.FromSeconds(1), $"{watch.Elapsed}");
    }

    [TestMethod]
    public void PastTheLimit_AConversionIsRefusedAtOnceAndNotStarted()
    {
        for (var i = 0; i < ConversionThread.MaxAbandoned; i++)
            AbandonOne();

        var started = false;
        var watch = Stopwatch.StartNew();

        Assert.ThrowsExactly<UnconvertibleMailException>(() => ConversionThread.Run(
            () =>
            {
                started = true;
                return string.Empty;
            },
            new ConversionBudget(TimeSpan.FromSeconds(5))));

        Assert.IsFalse(started);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromMilliseconds(250), $"{watch.Elapsed}");
    }

    [TestMethod]
    public void WhenAnAbandonedThreadEnds_ItsPlaceComesBack()
    {
        for (var i = 0; i < ConversionThread.MaxAbandoned; i++)
            AbandonOne();

        _hanging[0].Set();
        var watch = Stopwatch.StartNew();
        while (ConversionThread.Abandoned >= ConversionThread.MaxAbandoned && watch.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(5);

        Assert.AreEqual("Parole", ConversionThread.Run(() => "Parole", new ConversionBudget(TimeSpan.FromSeconds(5))));
    }

    /// <summary>
    /// A limit read only when a conversion starts would let every conversion running at that moment
    /// hang past it, so the conversions that may be running at once are the ones that may be left behind.
    /// </summary>
    [TestMethod]
    public void ConversionsThatHangTogether_StayInsideTheLimit()
    {
        var everyone = new ManualResetEventSlim();
        _hanging.Add(everyone);

        var threads = Enumerable.Range(0, ConversionThread.MaxAbandoned * 4).Select(_ => new Thread(() =>
        {
            try
            {
                ConversionThread.Run(
                    () =>
                    {
                        everyone.Wait();
                        return string.Empty;
                    },
                    new ConversionBudget(TimeSpan.FromMilliseconds(100)));
            }
            catch (UnconvertibleMailException)
            {
            }
        })).ToList();

        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());

        Assert.AreEqual(ConversionThread.MaxAbandoned, ConversionThread.Abandoned);
    }

    [TestMethod]
    public void LeavingAConversionBehind_WarnsWithNoMailContent()
    {
        var html = "<p>SECRET</p>";

        var hang = new ManualResetEventSlim();
        _hanging.Add(hang);
        Assert.ThrowsExactly<UnconvertibleMailException>(() => ConversionThread.Run(
            () =>
            {
                hang.Wait();
                return html;
            },
            new ConversionBudget(Soon)));

        Assert.AreEqual(1, _log.Warnings.Count);
        Assert.IsFalse(_log.Warnings[0].Contains("SECRET", StringComparison.Ordinal), _log.Warnings[0]);
    }

    /// <summary>The caller's stack is whatever the process was started with: 2 MiB when <c>ulimit -s</c> is unlimited.</summary>
    [TestMethod]
    public void AConversion_RunsOnAStackOfItsOwnSize()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Reads the stack from glibc.");

        Assert.AreEqual(8 * 1024 * 1024, ConversionThread.StackBytes);

        // Larger: the default here is 8 MiB too, and could not tell a stated size from none, and glibc
        // hands a smaller request a cached stack of up to four times the size (allocatestack.c).
        var usual = ConversionThread.StackBytes;
        ConversionThread.StackBytes = 16 * 1024 * 1024;

        try
        {
            var size = ConversionThread.Run(() => ThreadStack.Size().ToString(), new ConversionBudget(TimeSpan.FromSeconds(5)));

            Assert.AreEqual(ConversionThread.StackBytes, long.Parse(size));
        }
        finally
        {
            ConversionThread.StackBytes = usual;
        }
    }

    [TestMethod]
    public void AToolReading_AMailTheThreadsAreFullFor_SaysSoAndKeepsItsFormat()
    {
        for (var i = 0; i < ConversionThread.MaxAbandoned; i++)
            AbandonOne();

        var (body, format) = MailBody.ForModel("<p>Parole</p>", MailBody.HtmlFormat);

        Assert.AreEqual(MailBody.OmittedFormat, format);
        Assert.IsFalse(body.Contains("Parole", StringComparison.Ordinal), body);
    }

    [TestMethod]
    public void ASnippet_TheThreadsAreFullFor_IsEmpty()
    {
        for (var i = 0; i < ConversionThread.MaxAbandoned; i++)
            AbandonOne();

        Assert.AreEqual(string.Empty, MailSnippet.Of("<p>Parole</p>", MailBody.HtmlFormat));
    }

    [TestMethod]
    public void PlainText_DoesNotNeedAThread()
    {
        for (var i = 0; i < ConversionThread.MaxAbandoned; i++)
            AbandonOne();

        Assert.AreEqual("Parole", MailSnippet.Of("Parole", "text"));
        Assert.AreEqual(("Parole", "text"), MailBody.ForModel("Parole", "text"));
    }

    private static class ThreadStack
    {
        [DllImport("libc")]
        private static extern nint pthread_self();

        [DllImport("libc")]
        private static extern int pthread_getattr_np(nint thread, nint attributes);

        [DllImport("libc")]
        private static extern int pthread_attr_getstacksize(nint attributes, out nuint size);

        [DllImport("libc")]
        private static extern int pthread_attr_destroy(nint attributes);

        public static long Size()
        {
            var attributes = Marshal.AllocHGlobal(128);
            pthread_getattr_np(pthread_self(), attributes);
            pthread_attr_getstacksize(attributes, out var size);
            pthread_attr_destroy(attributes);
            Marshal.FreeHGlobal(attributes);
            return (long)size;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
