using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Per-client-address admission control: how many sockets one address may hold open at once, how many of
/// those may still be unauthenticated, and how fast it may open sockets and attempt handshakes. All four
/// live in the same table entry so the memory footprint is bounded by "addresses seen recently" instead of
/// "addresses seen ever".
/// </summary>
/// <remarks>
/// <para>
/// The connection caps and the connect-rate bucket are enforced <i>before</i> the WebSocket upgrade, so a
/// flood costs one HTTP 429. The join throttle is enforced during the handshake, which is where the
/// expensive work (token validation, room lookup) would otherwise happen.
/// </para>
/// <para>
/// <b>Why a separate pre-auth cap.</b> An unauthenticated socket has proved nothing yet still holds a
/// receive buffer, a connection slot and a task. Capping only authenticated sockets would let one address
/// park <see cref="QuotaOptions.MaxConnectionsPerIp"/> half-open handshakes and repeat, so the pre-auth
/// count is tracked and capped separately, tighter, and released the moment a socket authenticates.
/// </para>
/// <para>
/// <b>Bounded growth.</b> An entry is created on first sight and removed by <see cref="Prune"/> once it
/// has held no connections for <see cref="GraceSeconds"/>. The grace window is what makes the rate buckets
/// survive a disconnect/reconnect loop; <see cref="MaxTrackedAddresses"/> is the hard ceiling if that
/// window is ever flooded.
/// </para>
/// </remarks>
public sealed class IpConnectionLimiter
{
    /// <summary>Hard ceiling on tracked addresses. Beyond it, a prune runs and new addresses are refused.</summary>
    public const int MaxTrackedAddresses = 65_536;

    /// <summary>Seconds an entry with no live connections is kept, so reconnect churn stays throttled.</summary>
    public const int GraceSeconds = 60;

    /// <summary>Window the join and connect allowances refill over.</summary>
    private const int RateWindowSeconds = 10;

    /// <summary>Allowance when the per-IP connection cap is disabled or very small.</summary>
    private const int MinimumBurst = 4;

    private readonly ConcurrentDictionary<string, IpEntry> _entries = new(StringComparer.Ordinal);
    private readonly QuotaOptions _quotas;
    private readonly NetOptions _netOptions;
    private readonly NetMetrics _metrics;
    private readonly ILogger<IpConnectionLimiter> _logger;
    private readonly double _burst;
    private readonly double _ratePerSecond;
    private readonly long _graceTimestampTicks;

    /// <summary>Creates the limiter. One instance per process.</summary>
    /// <param name="quotas">Per-IP connection cap and the rate derivation it drives.</param>
    /// <param name="netOptions">Transport options, for <see cref="NetOptions.MaxPreAuthConnectionsPerIp"/>.</param>
    /// <param name="metrics">Counter surface.</param>
    /// <param name="logger">Logger for saturation warnings.</param>
    public IpConnectionLimiter(
        QuotaOptions quotas,
        NetOptions netOptions,
        NetMetrics metrics,
        ILogger<IpConnectionLimiter> logger)
    {
        ArgumentNullException.ThrowIfNull(quotas);
        ArgumentNullException.ThrowIfNull(netOptions);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _quotas = quotas;
        _netOptions = netOptions;
        _metrics = metrics;
        _logger = logger;

        // Derived from the connection cap rather than configured separately: a client that may hold N
        // sockets legitimately needs about N handshakes per window, and no more.
        int burst = Math.Max(MinimumBurst, quotas.MaxConnectionsPerIp);
        _burst = burst;
        _ratePerSecond = (double)burst / RateWindowSeconds;
        _graceTimestampTicks = GraceSeconds * Stopwatch.Frequency;
    }

    /// <summary>Addresses currently in the table. Bounded by <see cref="MaxTrackedAddresses"/>.</summary>
    public int TrackedAddresses => _entries.Count;

    /// <summary>Open connections currently attributed to <paramref name="remoteIp"/>.</summary>
    public int ConnectionsFor(string remoteIp)
    {
        ArgumentNullException.ThrowIfNull(remoteIp);
        if (!_entries.TryGetValue(remoteIp, out IpEntry? entry))
        {
            return 0;
        }

        lock (entry)
        {
            return entry.Connections;
        }
    }

    /// <summary>Unauthenticated sockets currently attributed to <paramref name="remoteIp"/>.</summary>
    public int PreAuthConnectionsFor(string remoteIp)
    {
        ArgumentNullException.ThrowIfNull(remoteIp);
        if (!_entries.TryGetValue(remoteIp, out IpEntry? entry))
        {
            return 0;
        }

        lock (entry)
        {
            return entry.PreAuthConnections;
        }
    }

    /// <summary>
    /// Takes one connection slot for <paramref name="remoteIp"/>. False when the address is at
    /// <see cref="QuotaOptions.MaxConnectionsPerIp"/> or the table is saturated. Every successful call
    /// must be paired with exactly one <see cref="Release"/>.
    /// </summary>
    public bool TryAcquire(string remoteIp)
    {
        ArgumentNullException.ThrowIfNull(remoteIp);

        int cap = _quotas.MaxConnectionsPerIp;
        while (true)
        {
            if (!TryGetOrCreate(remoteIp, out IpEntry? entry))
            {
                _metrics.Increment(NetCounter.ConnectionsRejectedUntrackable);
                _logger.LogWarning(
                    "Refusing {RemoteIp}: the address table is saturated at {Tracked} entries",
                    remoteIp,
                    _entries.Count);
                return false;
            }

            lock (entry)
            {
                if (entry.Removed)
                {
                    // Raced with Prune; the entry is gone from the table, so start over.
                    continue;
                }

                if (cap > 0 && entry.Connections >= cap)
                {
                    return false;
                }

                entry.Connections++;
                return true;
            }
        }
    }

    /// <summary>
    /// Gives back a slot taken by <see cref="TryAcquire"/>. The entry itself stays for
    /// <see cref="GraceSeconds"/> so its rate allowances keep applying across a reconnect.
    /// </summary>
    public void Release(string remoteIp)
    {
        ArgumentNullException.ThrowIfNull(remoteIp);
        if (!_entries.TryGetValue(remoteIp, out IpEntry? entry))
        {
            return;
        }

        lock (entry)
        {
            if (entry.Connections > 0)
            {
                entry.Connections--;
            }

            entry.IdleSinceTimestamp = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Takes one new-connection token from <paramref name="remoteIp"/>'s allowance, before the upgrade is
    /// accepted. False when the address is opening sockets faster than
    /// <see cref="RateWindowSeconds"/> permits.
    /// </summary>
    /// <remarks>
    /// The connection <i>cap</i> bounds concurrency; this bounds churn. Without it an address can open,
    /// abandon and reopen sockets indefinitely without ever exceeding the cap, and every cycle costs an
    /// accept, a buffer rental and a task.
    /// </remarks>
    public bool TryAcquireNewConnection(string remoteIp)
    {
        ArgumentNullException.ThrowIfNull(remoteIp);

        if (!TryGetOrCreate(remoteIp, out IpEntry? entry))
        {
            // The saturation path is already counted and refused by TryAcquire, which runs next; failing
            // open here keeps one decision in one place.
            return true;
        }

        lock (entry)
        {
            return entry.ConnectBucket.TryConsume();
        }
    }

    /// <summary>
    /// Takes one <i>pre-auth</i> slot for <paramref name="remoteIp"/>. False when the address already holds
    /// <see cref="NetOptions.MaxPreAuthConnectionsPerIp"/> unauthenticated sockets. Every successful call
    /// must be released exactly once — use the returned lease, never
    /// <see cref="ReleasePreAuth"/> directly.
    /// </summary>
    /// <param name="remoteIp">Canonical client address.</param>
    /// <param name="lease">
    /// The release handle on success; null on failure. It is idempotent, so the endpoint can release it in a
    /// <c>finally</c> while the connection also releases it the moment the handshake authenticates.
    /// </param>
    public bool TryAcquirePreAuth(string remoteIp, [NotNullWhen(true)] out PreAuthLease? lease)
    {
        ArgumentNullException.ThrowIfNull(remoteIp);

        lease = null;
        int cap = _netOptions.MaxPreAuthConnectionsPerIp;
        while (true)
        {
            if (!TryGetOrCreate(remoteIp, out IpEntry? entry))
            {
                return false;
            }

            lock (entry)
            {
                if (entry.Removed)
                {
                    continue;
                }

                if (cap > 0 && entry.PreAuthConnections >= cap)
                {
                    return false;
                }

                entry.PreAuthConnections++;
            }

            lease = new PreAuthLease(this, remoteIp);
            return true;
        }
    }

    /// <summary>
    /// Gives back a pre-auth slot. Call it through <see cref="PreAuthLease"/> rather than directly: this
    /// method is not idempotent, and releasing twice would open the cap by one for the process's lifetime.
    /// </summary>
    internal void ReleasePreAuth(string remoteIp)
    {
        if (!_entries.TryGetValue(remoteIp, out IpEntry? entry))
        {
            return;
        }

        lock (entry)
        {
            if (entry.PreAuthConnections > 0)
            {
                entry.PreAuthConnections--;
            }
        }
    }

    /// <summary>
    /// Takes one handshake attempt from <paramref name="remoteIp"/>'s allowance. False when the address
    /// is reconnecting faster than <see cref="RateWindowSeconds"/> permits.
    /// </summary>
    public bool TryAcquireJoin(string remoteIp)
    {
        ArgumentNullException.ThrowIfNull(remoteIp);

        if (!TryGetOrCreate(remoteIp, out IpEntry? entry))
        {
            // The socket is already accepted at this point; failing the handshake open is better than
            // rejecting a legitimate join because the table is momentarily full. The connection cap has
            // already limited how many such sockets can exist.
            return true;
        }

        lock (entry)
        {
            return entry.JoinBucket.TryConsume();
        }
    }

    /// <summary>
    /// Drops entries that have held no connection for <see cref="GraceSeconds"/>. Called from the
    /// connection supervisor's sweep; returns how many entries were removed.
    /// </summary>
    public int Prune(long nowTimestamp)
    {
        int removed = 0;
        foreach (KeyValuePair<string, IpEntry> pair in _entries)
        {
            IpEntry entry = pair.Value;
            bool expired;
            lock (entry)
            {
                expired = entry.Connections <= 0
                    && entry.PreAuthConnections <= 0
                    && nowTimestamp - entry.IdleSinceTimestamp >= _graceTimestampTicks;
                if (expired)
                {
                    // Marked inside the lock so a concurrent TryAcquire retries against a fresh entry
                    // instead of incrementing one that is about to leave the table.
                    entry.Removed = true;
                }
            }

            if (expired && _entries.TryRemove(pair))
            {
                removed++;
            }
        }

        return removed;
    }

    private bool TryGetOrCreate(string remoteIp, [NotNullWhen(true)] out IpEntry? entry)
    {
        while (true)
        {
            if (_entries.TryGetValue(remoteIp, out entry))
            {
                return true;
            }

            if (_entries.Count >= MaxTrackedAddresses)
            {
                Prune(Stopwatch.GetTimestamp());
                if (_entries.Count >= MaxTrackedAddresses)
                {
                    entry = null;
                    return false;
                }
            }

            var created = new IpEntry(
                new TokenBucket(_ratePerSecond, _burst),
                new TokenBucket(_ratePerSecond, _burst),
                Stopwatch.GetTimestamp());
            if (_entries.TryAdd(remoteIp, created))
            {
                entry = created;
                return true;
            }
        }
    }

    /// <summary>
    /// One address's state. Mutated under <c>lock (entry)</c> — the token buckets are mutable structs and
    /// the fields must move together, so this is a lock rather than a pile of interlocked CAS loops.
    /// The lock is only taken on connect/disconnect/handshake, never per message.
    /// </summary>
    private sealed class IpEntry(TokenBucket connectBucket, TokenBucket joinBucket, long idleSinceTimestamp)
    {
        internal int Connections;
        internal int PreAuthConnections;
        internal TokenBucket ConnectBucket = connectBucket;
        internal TokenBucket JoinBucket = joinBucket;
        internal long IdleSinceTimestamp = idleSinceTimestamp;
        internal bool Removed;
    }
}

/// <summary>
/// A one-shot release handle for a pre-auth slot taken by
/// <see cref="IpConnectionLimiter.TryAcquirePreAuth"/>.
/// </summary>
/// <remarks>
/// Two owners legitimately want to release the slot: the connection releases it the instant the handshake
/// authenticates (so the address may start another handshake immediately), and the endpoint releases it in
/// its <c>finally</c> (so an aborted upgrade or a failed handshake never leaks one). Exactly one of those
/// must actually take effect, which is what the interlocked flag guarantees — the same
/// return-it-exactly-once discipline the frame pool uses, for the same reason: a double release opens the
/// cap permanently, and a missed release closes it permanently.
/// </remarks>
public sealed class PreAuthLease
{
    private readonly IpConnectionLimiter _limiter;
    private readonly string _remoteIp;
    private int _released;

    internal PreAuthLease(IpConnectionLimiter limiter, string remoteIp)
    {
        _limiter = limiter;
        _remoteIp = remoteIp;
    }

    /// <summary>True once the slot has been given back.</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>Gives the slot back. Safe to call any number of times from any thread; only the first counts.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _limiter.ReleasePreAuth(_remoteIp);
        }
    }
}
