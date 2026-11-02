using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalendarMcp.Core.Utilities;

/// <summary>
/// Where a conversion runs, so that none can ever hold a page. .NET cannot abort a running thread, and
/// the engine's loops read no token and no clock, so a shape nobody has found yet would spin for the
/// life of the process. A caller waits for the budget and a little more, and then goes on without the
/// answer. The thread is left behind, which is why so few may run: each one left spinning keeps a core
/// until it ends or the process restarts, and past the limit a conversion is refused at once.
/// </summary>
/// <remarks>
/// The limit has to hold for conversions that hang together too, so it is on what runs at once and not
/// only on what has been left: a thread keeps its place until it ends, whether anyone still waits for it.
/// A conversion is a few milliseconds of CPU, so waiting for a place costs a mail nothing in practice.
/// </remarks>
internal static class ConversionThread
{
    /// <summary>The conversions that may run at once, and so the most that can ever be left behind.</summary>
    internal const int MaxAbandoned = 2;

    /// <summary>
    /// A thread's size has to be stated: the default comes from <c>ulimit -s</c>, measured in the image
    /// at 8 MiB, and is 2 MiB when that is unlimited. The worst mail allowed needs 608 KB on the real
    /// path, of which 25 KB is what stands above the conversion, so this is 13 times what it takes
    /// wherever it runs. The JIT works on this stack too, and near the end of one it was seen to block
    /// for good instead of overflowing, which only a thread that can be left behind survives.
    /// </summary>
    internal static int StackBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// How long past its budget a conversion may take. It reads the budget between steps, so this only
    /// has to outlast one step, and leaving a thread behind costs far more than waiting for it.
    /// </summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(500);

    private const string NoRoom = "The body of this mail could not be converted just now, so it is left out. The other fields are unaffected.";

    private const string LeftBehind = "The body of this mail took too long to convert, so it is left out. The other fields are unaffected.";

    private static readonly SemaphoreSlim Places = new(MaxAbandoned);

    private static int _abandoned;

    /// <summary>Set when the host starts. A conversion has no container to ask, and its callers are static.</summary>
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    internal static int Abandoned => Volatile.Read(ref _abandoned);

    /// <summary>The conversion's answer, or its own exception.</summary>
    /// <exception cref="UnconvertibleMailException">It did not return in time, or the threads left behind take every place.</exception>
    public static string Run(Func<string> convert, ConversionBudget budget)
    {
        if (Abandoned >= MaxAbandoned || !Places.Wait(budget.Left))
            throw new UnconvertibleMailException(NoRoom);

        Conversion conversion;
        Thread thread;

        try
        {
            conversion = new Conversion(convert);
            thread = new Thread(conversion.Run, StackBytes) { IsBackground = true, Name = "Mail conversion" };
            thread.Start();
        }
        catch
        {
            Places.Release();
            throw;
        }

        if (!thread.Join(budget.Left + Grace) && conversion.TryAbandon())
        {
            Logger.LogWarning(
                "A mail conversion ran past its budget and was left behind, {Abandoned} of {Limit}. Conversions are refused at the limit, until those threads end or the process restarts",
                Abandoned, MaxAbandoned);
            throw new UnconvertibleMailException(LeftBehind);
        }

        return conversion.Answer();
    }

    private sealed class Conversion(Func<string> convert)
    {
        private const int Running = 0;
        private const int Ended = 1;
        private const int LeftBehind = 2;

        private int _state = Running;
        private string? _text;
        private ExceptionDispatchInfo? _failure;

        public void Run()
        {
            try
            {
                _text = convert();
            }
            catch (Exception e)
            {
                _failure = ExceptionDispatchInfo.Capture(e);
            }

            if (Interlocked.Exchange(ref _state, Ended) == LeftBehind)
                Interlocked.Decrement(ref _abandoned);

            Places.Release();
        }

        // False when the thread got there first, and the answer is in after all.
        public bool TryAbandon()
        {
            Interlocked.Increment(ref _abandoned);
            if (Interlocked.CompareExchange(ref _state, LeftBehind, Running) == Running)
                return true;

            Interlocked.Decrement(ref _abandoned);
            return false;
        }

        public string Answer()
        {
            _failure?.Throw();
            return _text!;
        }
    }
}

/// <summary>Hands <see cref="ConversionThread"/> the host's logger, on both servers, with no line in a <c>Program.cs</c>.</summary>
internal sealed class ConversionThreadLogging(ILoggerFactory loggers) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ConversionThread.Logger = loggers.CreateLogger(nameof(ConversionThread));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
