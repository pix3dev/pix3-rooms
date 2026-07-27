using System.Runtime.CompilerServices;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// Converts between floats and the quantized integers that <b>are</b> the replicated values, against
/// one room's world bounds. Positions are <c>u16</c> across <see cref="Size"/>, rotation is
/// <c>u8</c> across a full turn, velocity is <c>i16</c> at 1/8 u/s.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rounding is normative.</b> <c>round(v)</c> means <c>floor(v + 0.5)</c> — JavaScript's
/// <c>Math.round</c> semantics, half rounding towards +∞. <see cref="MathF.Round(float)"/> uses
/// banker's rounding and <see cref="MidpointRounding.AwayFromZero"/> disagrees on negative halves, so
/// this type spells <c>floor(v + 0.5)</c> out explicitly. The hand-written TypeScript client codecs
/// must agree with this bit for bit, and golden vectors pin it down; do not "simplify" it.
/// </para>
/// <para>
/// The intermediate arithmetic is <see cref="double"/> even though the inputs and outputs are
/// <see cref="float"/>, for the same reason: a JavaScript client has no float32 arithmetic at all, so
/// double intermediates are the only way both sides land on the same integer at a rounding boundary.
/// Widening a <see cref="float"/> to <see cref="double"/> is exact, so nothing is lost.
/// </para>
/// <para>
/// <b>Non-finite input is rejected, never quantized.</b> One NaN poisons the spatial hash, so
/// <c>TryQuantize…</c> returns <c>false</c> and the caller counts a <c>nan</c> violation. In-range
/// clamping, by contrast, is silent — that is what the <c>clamp</c> in the spec table means.
/// </para>
/// <para>
/// <b>Round-trip property.</b> Quantization is idempotent through a dequantize:
/// <c>Quantize(Dequantize(Quantize(v))) == Quantize(v)</c>, i.e. a dequantized value is a fixed point
/// under a second quantize. That is what lets an owning client render its own entity from the
/// dequantized value without ever chasing a divergence pop, and what lets dirty detection compare
/// integers instead of floats. It holds only while the world respects
/// <see cref="MaxCoordinateToSizeRatio"/>, which the constructor enforces.
/// </para>
/// <para>
/// Rotation and velocity are world-independent and therefore <c>static</c>. Only positions need the
/// room's bounds. Nothing in this struct throws on the hot path — the only throw is the constructor,
/// which runs on the control path when a room is created.
/// </para>
/// </remarks>
public readonly struct WorldQuantizer
{
    /// <summary>Largest quantized position value; also the number of intervals across <see cref="Size"/>.</summary>
    public const int PositionMax = 65_535;

    /// <summary>Quantized rotation steps in a full turn. The wire value is a <c>u8</c>, so this is 256.</summary>
    public const int RotationSteps = 256;

    /// <summary>Fixed-point scale for velocity: 1/8 u/s per step, −4096.0…+4095.875 u/s.</summary>
    public const float VelocityScale = 8f;

    /// <summary>Smallest legal <see cref="Size"/>. A degenerate world would divide by ~0.</summary>
    public const float MinWorldSize = 1f;

    /// <summary>
    /// Largest ratio of any world coordinate's magnitude to <see cref="Size"/> that still preserves the
    /// round-trip fixed point.
    /// </summary>
    /// <remarks>
    /// Dequantized values reach the game as <see cref="float"/>, whose relative error is 2⁻²⁴. Requantizing
    /// lands on the same integer only while <c>M × 2⁻²⁴ × 65535 / Size &lt; ½</c>, i.e.
    /// <c>M &lt; ½ × 2²⁴ / 65535 × Size ≈ 128 × Size</c>, where <c>M</c> is the largest coordinate
    /// magnitude in the world. A world far from the origin relative to its own size (say origin 10⁷ with
    /// size 100) would break replication *silently* — positions would oscillate by a quantum forever — so
    /// it is refused at construction instead.
    /// </remarks>
    public const float MaxCoordinateToSizeRatio = 128f;

    private const double TwoPi = Math.PI * 2.0;

    /// <summary>World-space X of the low corner of the quantization range.</summary>
    public readonly float OriginX;

    /// <summary>World-space Y of the low corner of the quantization range.</summary>
    public readonly float OriginY;

    /// <summary>Side length of the square world the room quantizes against. Defaults to 4096 per room config.</summary>
    public readonly float Size;

    /// <summary>
    /// Binds a quantizer to one room's world bounds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Any bound is non-finite, or <paramref name="size"/> is below <see cref="MinWorldSize"/>. Rooms
    /// are constructed on the control path, so throwing here is correct: an invalid world is a
    /// configuration bug, not a runtime event, and it must not be allowed to reach the tick loop.
    /// </exception>
    public WorldQuantizer(float originX, float originY, float size)
    {
        if (!float.IsFinite(originX))
        {
            throw new ArgumentOutOfRangeException(nameof(originX), originX, "World origin X must be finite.");
        }

        if (!float.IsFinite(originY))
        {
            throw new ArgumentOutOfRangeException(nameof(originY), originY, "World origin Y must be finite.");
        }

        if (!float.IsFinite(size) || size < MinWorldSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                size,
                $"World size must be finite and at least {MinWorldSize}.");
        }

        if (!IsRatioSafe(originX, originY, size))
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                size,
                "World bounds are too far from the origin for their size: every coordinate magnitude must "
                + $"stay below {MaxCoordinateToSizeRatio} × size, or float32 round-tripping stops being a "
                + "fixed point and positions oscillate by a quantum forever.");
        }

        OriginX = originX;
        OriginY = originY;
        Size = size;
    }

    /// <summary>
    /// True when these bounds are usable: all three finite, <paramref name="size"/> at least
    /// <see cref="MinWorldSize"/>, and every coordinate magnitude within
    /// <see cref="MaxCoordinateToSizeRatio"/> × <paramref name="size"/>. Call this to validate
    /// configuration before constructing.
    /// </summary>
    public static bool IsValidWorld(float originX, float originY, float size)
        => float.IsFinite(originX)
        && float.IsFinite(originY)
        && float.IsFinite(size)
        && size >= MinWorldSize
        && IsRatioSafe(originX, originY, size);

    /// <summary>
    /// The float32 precision guard behind <see cref="MaxCoordinateToSizeRatio"/>, applied to both corners
    /// of the world on both axes. Assumes the three inputs are already known finite.
    /// </summary>
    private static bool IsRatioSafe(float originX, float originY, float size)
    {
        float limit = MaxCoordinateToSizeRatio * size;
        return MathF.Abs(originX) < limit
            && MathF.Abs(originY) < limit
            && MathF.Abs(originX + size) < limit
            && MathF.Abs(originY + size) < limit;
    }

    /// <summary>World-space X of the far edge of the quantization range.</summary>
    public float MaxX => OriginX + Size;

    /// <summary>World-space Y of the far edge of the quantization range.</summary>
    public float MaxY => OriginY + Size;

    // ── Position ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Quantizes a world position: <c>clamp(round((v − origin) × 65535 / Size), 0, 65535)</c> per axis.
    /// Returns <c>false</c> — writing nothing — when either coordinate is not finite, so the caller can
    /// count a <c>nan</c> violation and drop the record. Out-of-world coordinates are clamped silently.
    /// </summary>
    public bool TryQuantizePosition(float x, float y, out ushort qx, out ushort qy)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            qx = 0;
            qy = 0;
            return false;
        }

        qx = QuantizeAxis(x, OriginX, Size);
        qy = QuantizeAxis(y, OriginY, Size);
        return true;
    }

    /// <summary>Dequantizes an X coordinate: <c>origin + q × Size / 65535</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float DequantizeX(ushort qx) => (float)(OriginX + (qx * (double)Size / PositionMax));

    /// <summary>Dequantizes a Y coordinate: <c>origin + q × Size / 65535</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float DequantizeY(ushort qy) => (float)(OriginY + (qy * (double)Size / PositionMax));

    private static ushort QuantizeAxis(float v, float origin, float size)
    {
        // floor(v + 0.5), NOT MathF.Round: see the rounding note on this type.
        double scaled = Math.Floor(((v - (double)origin) * PositionMax / size) + 0.5);
        if (scaled <= 0.0)
        {
            return 0;
        }

        if (scaled >= PositionMax)
        {
            return PositionMax;
        }

        return (ushort)scaled;
    }

    // ── Rotation (world-independent) ───────────────────────────────────────────

    /// <summary>
    /// Quantizes a rotation in radians: wrapped into <c>[0, 2π)</c> first, then
    /// <c>round(w / 2π × 256) &amp; 0xFF</c>. The mask is what folds a value that rounded up to a full
    /// turn back onto 0. Returns <c>false</c> when <paramref name="rot"/> is not finite.
    /// </summary>
    public static bool TryQuantizeRotation(float rot, out byte q)
    {
        if (!float.IsFinite(rot))
        {
            q = 0;
            return false;
        }

        double wrapped = rot % TwoPi;
        if (wrapped < 0.0)
        {
            wrapped += TwoPi;
        }

        // floor(v + 0.5), then & 0xFF so a rotation that rounds up to 256 steps wraps to 0.
        double steps = Math.Floor((wrapped / TwoPi * RotationSteps) + 0.5);
        q = (byte)((long)steps & 0xFF);
        return true;
    }

    /// <summary>Dequantizes a rotation: <c>q × 2π / 256</c> radians, always in <c>[0, 2π)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DequantizeRotation(byte q) => (float)(q * TwoPi / RotationSteps);

    // ── Velocity (world-independent) ───────────────────────────────────────────

    /// <summary>
    /// Quantizes a velocity component: <c>clamp(round(v × 8), −32768, 32767)</c>, i.e. 1/8 u/s
    /// resolution over ±4095 u/s. Returns <c>false</c> when <paramref name="v"/> is not finite;
    /// out-of-range speeds are clamped silently.
    /// </summary>
    public static bool TryQuantizeVelocity(float v, out short q)
    {
        if (!float.IsFinite(v))
        {
            q = 0;
            return false;
        }

        // floor(v + 0.5), NOT MathF.Round: see the rounding note on this type.
        double scaled = Math.Floor((v * (double)VelocityScale) + 0.5);
        if (scaled <= short.MinValue)
        {
            q = short.MinValue;
            return true;
        }

        if (scaled >= short.MaxValue)
        {
            q = short.MaxValue;
            return true;
        }

        q = (short)scaled;
        return true;
    }

    /// <summary>Dequantizes a velocity component: <c>q / 8</c> units per second.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DequantizeVelocity(short q) => q / VelocityScale;
}
