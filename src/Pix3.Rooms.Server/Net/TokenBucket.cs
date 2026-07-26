using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// A monotonic, allocation-free token bucket. Used for every per-connection and per-IP rate limit, so
/// it sits on the inbound hot path: no locks, no timers, no <see cref="DateTime"/>, just
/// <see cref="Stopwatch.GetTimestamp"/> arithmetic on struct fields.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mutable struct.</b> <see cref="TryConsume(double)"/> refills and debits in place, so a bucket must
/// live in a <i>field</i> (or be passed by <c>ref</c>). Copying it into a local or exposing it through a
/// property silently discards the refill and the debit.
/// </para>
/// <para>
/// <b>A <c>default</c> bucket is unlimited.</b> Zero rate means "no limit configured", which is what
/// disabling a quota looks like. Construct every bucket explicitly so the failure mode is a configured
/// limit, never an accidental one.
/// </para>
/// </remarks>
public struct TokenBucket
{
    private readonly double _capacity;
    private readonly double _ratePerSecond;
    private readonly double _tokensPerTimestampTick;
    private double _tokens;
    private long _timestamp;

    /// <summary>
    /// Creates a full bucket that refills at <paramref name="ratePerSecond"/> and holds at most
    /// <paramref name="burstCapacity"/> tokens.
    /// </summary>
    /// <param name="ratePerSecond">Sustained allowance. Zero or negative disables the limit.</param>
    /// <param name="burstCapacity">Maximum saved-up allowance. Zero or negative disables the limit.</param>
    public TokenBucket(double ratePerSecond, double burstCapacity)
    {
        bool enabled = ratePerSecond > 0 && burstCapacity > 0;
        _ratePerSecond = enabled ? ratePerSecond : 0d;
        _capacity = enabled ? burstCapacity : 0d;
        _tokensPerTimestampTick = enabled ? ratePerSecond / Stopwatch.Frequency : 0d;
        _tokens = _capacity;
        _timestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Builds a bucket for a "<paramref name="amountPerMinute"/> per minute" limit: the whole minute's
    /// allowance may be spent at once, then it trickles back.
    /// </summary>
    public static TokenBucket PerMinute(double amountPerMinute)
        => new(amountPerMinute / 60d, amountPerMinute);

    /// <summary>
    /// Builds a bucket for a "<paramref name="amountPerSecond"/> per second" limit with a one-second
    /// burst allowance.
    /// </summary>
    public static TokenBucket PerSecond(double amountPerSecond)
        => new(amountPerSecond, amountPerSecond);

    /// <summary>True when no limit is configured, in which case every consume succeeds.</summary>
    public readonly bool IsUnlimited => _ratePerSecond <= 0d;

    /// <summary>Sustained allowance per second; 0 when unlimited.</summary>
    public readonly double RatePerSecond => _ratePerSecond;

    /// <summary>Maximum saved-up allowance; 0 when unlimited.</summary>
    public readonly double Capacity => _capacity;

    /// <summary>Tokens available as of the last <see cref="TryConsume(double)"/>. Diagnostics only.</summary>
    public readonly double AvailableTokens => _tokens;

    /// <summary>Takes one token. False when the bucket is empty, i.e. the limit was breached.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryConsume() => TryConsume(1d);

    /// <summary>
    /// Refills for the time elapsed since the previous call, then takes <paramref name="amount"/>
    /// tokens. False when the bucket holds less than that, in which case nothing is taken.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryConsume(double amount)
    {
        if (_ratePerSecond <= 0d)
        {
            return true;
        }

        long now = Stopwatch.GetTimestamp();
        long elapsed = now - _timestamp;
        if (elapsed > 0)
        {
            _timestamp = now;
            double refilled = _tokens + (elapsed * _tokensPerTimestampTick);
            _tokens = refilled > _capacity ? _capacity : refilled;
        }

        if (amount <= 0d)
        {
            return true;
        }

        if (_tokens < amount)
        {
            return false;
        }

        _tokens -= amount;
        return true;
    }

    /// <summary>Refills the bucket and restarts its clock. For tests and for reusing a pooled holder.</summary>
    public void Refill()
    {
        _tokens = _capacity;
        _timestamp = Stopwatch.GetTimestamp();
    }
}
