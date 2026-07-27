using System.Collections.Concurrent;
using System.Diagnostics;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Owns the live connection set: the process-wide connection cap, the deadline sweep (handshake timeout
/// and idle timeout) and draining every socket at shutdown.
/// </summary>
/// <remarks>
/// <para>
/// <b>One sweep, not 600 timers.</b> Deadlines are checked by a single one-second loop that walks the
/// connection set, instead of a per-connection timer. At 600 players in a room that is the difference
/// between one wakeup and six hundred.
/// </para>
/// <para>
/// Registering this as an <see cref="IHostedService"/> gives it a clean shutdown, but it does not have
/// to be: <see cref="EnsureStarted"/> is called by the endpoint on first use, so the sweep runs even if
/// the composition root only registers it as a plain singleton.
/// </para>
/// </remarks>
public sealed class ConnectionSupervisor : IHostedService, IAsyncDisposable
{
    /// <summary>How often deadlines are checked.</summary>
    public const int SweepIntervalMilliseconds = 1_000;

    /// <summary>
    /// Live connections, keyed by <see cref="ClientConnection.SessionId"/>.
    /// </summary>
    /// <remarks>
    /// Keyed on the session id, <b>not</b> the client id: a client id does not exist until the handshake
    /// authenticates, and the sockets that most need supervising are precisely the ones that have not got
    /// there yet — the handshake deadline is what removes them.
    /// </remarks>
    private readonly ConcurrentDictionary<long, ClientConnection> _connections = new();
    private readonly NetOptions _options;
    private readonly IpConnectionLimiter _ipLimiter;
    private readonly NetMetrics _metrics;
    private readonly ILogger<ConnectionSupervisor> _logger;
    private readonly Lock _startGate = new();

    private CancellationTokenSource? _sweepCts;
    private Task? _sweepTask;
    private int _reservedSlots;

    /// <summary>Creates the supervisor. One instance per process.</summary>
    public ConnectionSupervisor(
        NetOptions options,
        IpConnectionLimiter ipLimiter,
        NetMetrics metrics,
        ILogger<ConnectionSupervisor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ipLimiter);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _ipLimiter = ipLimiter;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>Connections currently registered (i.e. accepted and still running).</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Slots taken against <see cref="NetOptions.MaxTotalConnections"/>. Slightly ahead of
    /// <see cref="ConnectionCount"/> because a slot is reserved before the upgrade is accepted.
    /// </summary>
    public int ReservedSlots => Volatile.Read(ref _reservedSlots);

    /// <summary>True once the deadline sweep is running.</summary>
    public bool IsSweeping => Volatile.Read(ref _sweepTask) is not null;

    /// <summary>
    /// Takes a slot against the process-wide connection cap. Called <b>before</b> the WebSocket upgrade,
    /// so a server at capacity answers a cheap HTTP 503 instead of accepting a socket it cannot serve.
    /// Every successful call must be paired with exactly one <see cref="ReleaseSlot"/>.
    /// </summary>
    public bool TryReserveSlot()
    {
        int cap = _options.MaxTotalConnections;
        while (true)
        {
            int current = Volatile.Read(ref _reservedSlots);
            if (current >= cap)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _reservedSlots, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>Gives back a slot taken by <see cref="TryReserveSlot"/>.</summary>
    public void ReleaseSlot()
    {
        int remaining = Interlocked.Decrement(ref _reservedSlots);
        if (remaining < 0)
        {
            // Unbalanced release: clamp rather than let the cap drift open, and say so loudly.
            Interlocked.Exchange(ref _reservedSlots, 0);
            _logger.LogError("Connection slot accounting went negative; clamped to zero");
        }
    }

    /// <summary>Adds a running connection to the deadline sweep and the live gauge.</summary>
    public void Register(ClientConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_connections.TryAdd(connection.SessionId, connection))
        {
            _metrics.OnConnectionOpened();
            return;
        }

        // Session ids come from a 64-bit monotonic counter, so a collision is not reachable in practice.
        // Refusing rather than replacing keeps the impossible case from silently dropping a live socket out
        // of the deadline sweep.
        _logger.LogError("Session id {SessionId} is already registered; closing the new session", connection.SessionId);
        connection.RequestClose(RejectCode.SessionReplaced, "this session id is already in use");
    }

    /// <summary>Removes a finished connection.</summary>
    public void Unregister(ClientConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_connections.TryRemove(connection.SessionId, out _))
        {
            _metrics.OnConnectionClosed();
        }
    }

    /// <summary>Starts the deadline sweep if it is not already running. Idempotent and cheap.</summary>
    public void EnsureStarted()
    {
        if (Volatile.Read(ref _sweepTask) is not null)
        {
            return;
        }

        lock (_startGate)
        {
            if (_sweepTask is not null)
            {
                return;
            }

            _sweepCts = new CancellationTokenSource();
            _sweepTask = SweepLoopAsync(_sweepCts.Token);
        }
    }

    /// <summary>Closes every live connection with <paramref name="code"/>. Used at shutdown.</summary>
    public int CloseAll(RejectCode code, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        int closed = 0;
        foreach (KeyValuePair<long, ClientConnection> pair in _connections)
        {
            try
            {
                pair.Value.RequestClose(code, reason);
                closed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to close session {SessionId} during shutdown", pair.Key);
            }
        }

        return closed;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        int closed = CloseAll(RejectCode.ServerShuttingDown, "the server is shutting down");
        if (closed > 0)
        {
            _logger.LogInformation("Asked {Count} client(s) to close for shutdown", closed);
        }

        await StopSweepAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopSweepAsync().ConfigureAwait(false);
        _sweepCts?.Dispose();
        _sweepCts = null;
    }

    private async Task StopSweepAsync()
    {
        Task? sweep;
        CancellationTokenSource? cts;
        lock (_startGate)
        {
            sweep = _sweepTask;
            cts = _sweepCts;
            _sweepTask = null;
        }

        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by a concurrent shutdown path.
            }
        }

        if (sweep is not null)
        {
            try
            {
                await sweep.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The connection sweep ended with an error");
            }
        }
    }

    private async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(SweepIntervalMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                long now = Stopwatch.GetTimestamp();

                foreach (KeyValuePair<long, ClientConnection> pair in _connections)
                {
                    try
                    {
                        pair.Value.CheckDeadlines(now);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Deadline check for session {SessionId} threw", pair.Key);
                    }
                }

                try
                {
                    _ipLimiter.Prune(now);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Pruning the address table threw");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
