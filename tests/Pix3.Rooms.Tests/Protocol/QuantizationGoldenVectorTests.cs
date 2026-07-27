using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Tests.Protocol;

/// <summary>
/// Golden vectors for the quantization rules in <c>docs/protocol.md</c> → Quantization.
/// </summary>
/// <remarks>
/// <para>
/// Every expected integer here was computed by hand from the formulas in the spec table, with
/// <c>double</c> intermediates, exactly as the spec requires a conforming implementation (including
/// the hand-written TypeScript client) to do. None of them was read off a run of
/// <see cref="WorldQuantizer"/>.
/// </para>
/// <para>
/// The half-way vectors are the load-bearing ones. <c>round(v)</c> is normatively
/// <c>floor(v + 0.5)</c>, which disagrees with <see cref="MathF.Round(float)"/> (banker's) on positive
/// halves and with <see cref="MidpointRounding.AwayFromZero"/> on negative ones — so a vector pair like
/// <c>+0.0625 → 1</c> and <c>−0.0625 → 0</c> pins the rule down uniquely: no other rounding mode
/// produces both.
/// </para>
/// </remarks>
public class QuantizationGoldenVectorTests
{
    /// <summary>The default room world: origin (−2048, −2048), size 4096.</summary>
    private static WorldQuantizer DefaultWorld() => new(-2048f, -2048f, 4096f);

    // ── Position: clamp(round((v − origin) × 65535 / Size), 0, 65535) ─────────

    [Theory]
    // (v − origin) = 0 → 0.
    [InlineData(-2048f, 0)]
    // (v − origin) = 4096 → 4096 × 65535 / 4096 = 65535 exactly, the top of the range.
    [InlineData(2048f, 65535)]
    // (v − origin) = 2048 → 2048 × 65535 / 4096 = 32767.5 → floor(32768.0) = 32768.
    [InlineData(0f, 32768)]
    // (v − origin) = 1024 → 1024 × 65535 / 4096 = 16383.75 → floor(16384.25) = 16384.
    [InlineData(-1024f, 16384)]
    // (v − origin) = 3072 → 3072 × 65535 / 4096 = 49151.25 → floor(49151.75) = 49151.
    [InlineData(1024f, 49151)]
    // (v − origin) = 1 → 65535 / 4096 = 15.99975… → floor(16.49975…) = 16, i.e. ~1/16 unit per step.
    [InlineData(-2047f, 16)]
    // Out of world, low: silently clamped. "In-range clamping is silent" is what the spec's clamp means.
    [InlineData(-10000f, 0)]
    // Out of world, high: silently clamped.
    [InlineData(10000f, 65535)]
    public void Position_quantization_matches_the_hand_computed_vectors(float v, int expected)
    {
        WorldQuantizer world = DefaultWorld();

        Assert.True(world.TryQuantizePosition(v, v, out ushort qx, out ushort qy));

        Assert.Equal(expected, qx);
        Assert.Equal(expected, qy);
    }

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(0f, float.NegativeInfinity)]
    public void Non_finite_positions_are_refused_rather_than_quantized(float x, float y)
    {
        // "One NaN poisons the spatial hash" — the edge refuses and counts, it never clamps.
        WorldQuantizer world = DefaultWorld();

        Assert.False(world.TryQuantizePosition(x, y, out ushort qx, out ushort qy));
        Assert.Equal(0, qx);
        Assert.Equal(0, qy);
    }

    [Fact]
    public void Position_dequantization_lands_within_half_a_quantum_of_the_input()
    {
        // Decode is origin + q × Size / 65535; one quantum is 4096/65535 ≈ 0.0625 units at this size.
        WorldQuantizer world = DefaultWorld();
        const float quantum = 4096f / 65535f;

        foreach (float v in new[] { -2048f, -1024f, -0.5f, 0f, 1f, 1024f, 2047.9f, 2048f })
        {
            Assert.True(world.TryQuantizePosition(v, v, out ushort qx, out ushort qy));
            Assert.InRange(world.DequantizeX(qx), v - (quantum / 2f) - 1e-3f, v + (quantum / 2f) + 1e-3f);
            Assert.InRange(world.DequantizeY(qy), v - (quantum / 2f) - 1e-3f, v + (quantum / 2f) + 1e-3f);
        }
    }

    // ── Rotation: round(w / 2π × 256) & 0xFF, w wrapped into [0, 2π) ──────────

    [Theory]
    // 0 → 0.
    [InlineData(0f, 0)]
    // π/2 → 0.25 × 256 = 64.
    [InlineData(1.5707964f, 64)]
    // π → 0.5 × 256 = 128.
    [InlineData(3.1415927f, 128)]
    // −π/2 wraps to 3π/2 → 0.75 × 256 = 192. The wrap happens before the scale, per the spec.
    [InlineData(-1.5707964f, 192)]
    // 6.28 rad is just under a full turn: 255.870… + 0.5 → floor 256, and & 0xFF folds it onto 0.
    // Without the mask this would encode as an illegal 256th step.
    [InlineData(6.28f, 0)]
    // A hair below zero wraps to just under 2π and folds the same way.
    [InlineData(-0.001f, 0)]
    public void Rotation_quantization_matches_the_hand_computed_vectors(float rot, int expected)
    {
        Assert.True(WorldQuantizer.TryQuantizeRotation(rot, out byte q));
        Assert.Equal(expected, q);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Non_finite_rotation_is_refused(float rot)
    {
        Assert.False(WorldQuantizer.TryQuantizeRotation(rot, out byte q));
        Assert.Equal(0, q);
    }

    [Fact]
    public void Rotation_dequantization_is_the_step_index_times_one_step()
    {
        // q × 2π / 256; 1 step = 1.40625°.
        Assert.Equal(0f, WorldQuantizer.DequantizeRotation(0));
        AssertClose((float)(Math.PI / 2.0), WorldQuantizer.DequantizeRotation(64));
        AssertClose((float)Math.PI, WorldQuantizer.DequantizeRotation(128));
        AssertClose((float)(Math.PI * 2.0 * 255.0 / 256.0), WorldQuantizer.DequantizeRotation(255));

        static void AssertClose(float expected, float actual)
            => Assert.True(MathF.Abs(expected - actual) < 1e-5f, $"expected {expected}, got {actual}");
    }

    // ── Velocity: clamp(round(v × 8), −32768, 32767) ──────────────────────────

    [Theory]
    [InlineData(0f, 0)]
    // 100 × 8 = 800.
    [InlineData(100f, 800)]
    // −100 × 8 = −800.
    [InlineData(-100f, -800)]
    // 0.0625 × 8 = 0.5 → floor(1.0) = 1. Banker's rounding would give 0 here.
    [InlineData(0.0625f, 1)]
    // −0.0625 × 8 = −0.5 → floor(0.0) = 0. AwayFromZero would give −1 here.
    [InlineData(-0.0625f, 0)]
    // 0.3125 × 8 = 2.5 → floor(3.0) = 3. Banker's rounding would give 2 here.
    [InlineData(0.3125f, 3)]
    // −0.3125 × 8 = −2.5 → floor(−2.0) = −2. AwayFromZero would give −3 here.
    [InlineData(-0.3125f, -2)]
    // The exact ends of the representable range: 4095.875 × 8 = 32767, −4096 × 8 = −32768.
    [InlineData(4095.875f, 32767)]
    [InlineData(-4096f, -32768)]
    // Beyond the range, clamped silently.
    [InlineData(5000f, 32767)]
    [InlineData(-5000f, -32768)]
    public void Velocity_quantization_matches_the_hand_computed_vectors(float v, int expected)
    {
        Assert.True(WorldQuantizer.TryQuantizeVelocity(v, out short q));
        Assert.Equal(expected, q);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    public void Non_finite_velocity_is_refused(float v)
    {
        Assert.False(WorldQuantizer.TryQuantizeVelocity(v, out short q));
        Assert.Equal(0, q);
    }

    [Fact]
    public void Velocity_dequantization_is_the_step_index_over_eight()
    {
        Assert.Equal(0f, WorldQuantizer.DequantizeVelocity(0));
        Assert.Equal(100f, WorldQuantizer.DequantizeVelocity(800));
        Assert.Equal(-100f, WorldQuantizer.DequantizeVelocity(-800));
        Assert.Equal(4095.875f, WorldQuantizer.DequantizeVelocity(32767));
        Assert.Equal(-4096f, WorldQuantizer.DequantizeVelocity(-32768));
    }

    // ── The round-trip fixed point ────────────────────────────────────────────

    [Fact]
    public void Requantizing_a_dequantized_position_is_a_fixed_point_across_the_whole_range()
    {
        // The property the whole "quantized integers are the replicated values" rule rests on: an owning
        // client renders from the dequantized value and republishes it, so if this were not a fixed point
        // an idle entity would oscillate by a quantum forever and stay dirty forever.
        WorldQuantizer world = DefaultWorld();

        for (int q = 0; q <= WorldQuantizer.PositionMax; q++)
        {
            float x = world.DequantizeX((ushort)q);
            float y = world.DequantizeY((ushort)q);

            Assert.True(world.TryQuantizePosition(x, y, out ushort rqx, out ushort rqy));
            Assert.Equal(q, rqx);
            Assert.Equal(q, rqy);
        }
    }

    [Fact]
    public void Requantizing_a_dequantized_rotation_and_velocity_is_a_fixed_point()
    {
        for (int q = 0; q < WorldQuantizer.RotationSteps; q++)
        {
            Assert.True(WorldQuantizer.TryQuantizeRotation(WorldQuantizer.DequantizeRotation((byte)q), out byte rq));
            Assert.Equal(q, rq);
        }

        for (int q = short.MinValue; q <= short.MaxValue; q++)
        {
            Assert.True(WorldQuantizer.TryQuantizeVelocity(WorldQuantizer.DequantizeVelocity((short)q), out short rq));
            Assert.Equal(q, rq);
        }
    }

    [Fact]
    public void Sub_quantum_noise_around_a_stored_position_does_not_change_the_quantized_value()
    {
        // Dirty detection compares these integers precisely so float jitter cannot keep an idle entity
        // dirty. The state a server holds is always dequantized-from-quantized, i.e. it sits at the
        // centre of a rounding cell, so noise up to ±½ a quantum around it must be invisible on the wire.
        // (Noise around an arbitrary float can of course cross a boundary — that is not the claim.)
        WorldQuantizer world = DefaultWorld();
        const float quantum = 4096f / 65535f;

        foreach (int q in new[] { 1, 4321, 32768, 34739, 65534 })
        {
            float x = world.DequantizeX((ushort)q);
            float y = world.DequantizeY((ushort)q);

            foreach (float noise in new[] { -0.4f * quantum, -0.1f * quantum, 0.1f * quantum, 0.4f * quantum })
            {
                Assert.True(world.TryQuantizePosition(x + noise, y + noise, out ushort nx, out ushort ny));
                Assert.Equal(q, nx);
                Assert.Equal(q, ny);
            }
        }
    }

    // ── World bounds validation ───────────────────────────────────────────────

    [Fact]
    public void A_world_too_far_from_the_origin_for_its_size_is_refused_at_construction()
    {
        // M < 128 × Size or the float32 round-trip stops being a fixed point. The default world has a
        // 256× margin; this one is 1000× out.
        Assert.False(WorldQuantizer.IsValidWorld(100_000f, 0f, 100f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldQuantizer(100_000f, 0f, 100f));

        Assert.True(WorldQuantizer.IsValidWorld(-2048f, -2048f, 4096f));
    }

    [Fact]
    public void Degenerate_and_non_finite_world_bounds_are_refused()
    {
        Assert.False(WorldQuantizer.IsValidWorld(0f, 0f, 0f));
        Assert.False(WorldQuantizer.IsValidWorld(0f, 0f, float.NaN));
        Assert.False(WorldQuantizer.IsValidWorld(float.PositiveInfinity, 0f, 4096f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldQuantizer(0f, 0f, 0f));
    }

    [Fact]
    public void Quantization_constants_match_the_spec_table()
    {
        Assert.Equal(65_535, WorldQuantizer.PositionMax);
        Assert.Equal(256, WorldQuantizer.RotationSteps);
        Assert.Equal(8f, WorldQuantizer.VelocityScale);
        Assert.Equal(128f, WorldQuantizer.MaxCoordinateToSizeRatio);
    }
}
