using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Per-client-address admission control: how many sockets one address may hold open at once, and how
/// fast it may attempt handshakes. Both live in the same table entry so the memory footprint is bounded
/// by "addresses seen recently" instead of "addresses seen ever".
/// </summary>
/// <remarks>
/// <para>
/// The connection cap is enforced <i>before</i> the WebSocket upgrade, so a flood costs one HTTP 429.
/// The join throttle is enforced during the handshake, which is where the expensive work (token
/// validation, room lookup) would otherwise happen.
/// </para>
/// <para>
/// <b>Bounded growth.</b> An entry is created on first sight and removed by <see cref="Prune"/> once it
/// has held no connections for <see cref="GraceSeconds"/>. The grace window is what makes the join
/// throttle survive a disconnect/reconnect loop; <see cref="MaxTrackedAddresses"/> is the hard ceiling
/// if that window is ever flooded.
/// </para>
/// </remarks>
public sealed class IpConnectionLimiter
{
    /// <summary>Hard ceiling on tracked addresses. Beyond it, a prune runs and new addresses are refused.</summary>
    public const int MaxTrackedAddresses = 65_536;

    /// <summary>Seconds an entry with no live connections is kept, so reconnect churn stays throttled.</summary>
    public const int GraceSeconds = 60;

    /// <summary>Window the join allowance refills over.</summary>
    private const int JoinWindowSeconds = 10;

    /// <summary>Join allowance when the per-IP connection cap is disabled or very small.</summary>
    private const int MinimumJoinBurst = 4;

    private readonly ConcurrentDictionary<string, IpEntry> _entries = new(StringComparer.Ordinal);
    private readonly QuotaOptions _quotas;
    private readonly NetMetrics _metrics;
    private readonly ILogger<IpConnectionLimiter> _logger;
    private readonly double _joinBurst;
    private readonly double _joinRatePerSecond;
    private readonly long _graceTimestampTicks;

    /// <summary>Creates the limiter. One instance per process.</summary>
    public IpConnectionLimiter(QuotaOptions quotas, NetMetrics metrics, ILogger<IpConnectionLimiter> logger)
    {
        ArgumentNullException.ThrowIfNull(quotas);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _quotas = quotas;
        _metrics = metrics;
        _logger = logger;

        // Derived from the connection cap rather than configured separately: a client that may hold N
        // sockets legitimately needs about N handshakes per window, and no more.
        int burst = Math.Max(MinimumJoinBurst, quotas.MaxConnectionsPerIp);
        _joinBurst = burst;
        _joinRatePerSecond = (double)burst / JoinWindowSeconds;
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
    /// <see cref="GraceSeconds"/> so its join allowance keeps applying across a reconnect.
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
    /// Takes one handshake attempt from <paramref name="remoteIp"/>'s allowance. False when the address
    /// is reconnecting faster than <see cref="JoinWindowSeconds"/> permits.
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
                expired = entry.Connections <= 0 && nowTimestamp - entry.IdleSinceTimestamp >= _graceTimestampTicks;
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

            var created = new IpEntry(new TokenBucket(_joinRatePerSecond, _joinBurst), Stopwatch.GetTimestamp());
            if (_entries.TryAdd(remoteIp, created))
            {
                entry = created;
                return true;
            }
        }
    }

    /// <summary>
    /// One address's state. Mutated under <c>lock (entry)</c> — the token bucket is a mutable struct and
    /// the two fields must move together, so this is a lock rather than a pile of interlocked CAS loops.
    /// The lock is only taken on connect/disconnect/handshake, never per message.
    /// </summary>
    private sealed class IpEntry(TokenBucket joinBucket, long idleSinceTimestamp)
    {
        internal int Connections;
        internal TokenBucket JoinBucket = joinBucket;
        internal long IdleSinceTimestamp = idleSinceTimestamp;
        internal bool Removed;
    }
}
