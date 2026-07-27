using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// Fixed limits and tuning for one room's <see cref="RoomReplication"/>. Everything here is sized once
/// at construction — the replication core never grows, so a room's memory footprint is known the moment
/// it is created.
/// </summary>
/// <remarks>
/// An AOI <i>radius</i> does not bound worst-case egress: 600 players stacked on one point all see each
/// other. Three caps here turn the ceiling from a hope into a guarantee —
/// <see cref="MaxVisibleEntities"/> (k-nearest), <see cref="MaxEntersPerTick"/> (with a carry cursor) and
/// <see cref="MaxBytesPerClientPerTick"/>. Their defaults come from <c>docs/architecture.md</c> →
/// Configuration and match <c>docs/protocol.md</c> → Bandwidth caps.
/// </remarks>
public sealed record ReplicationOptions
{
    /// <summary>
    /// Entity-table capacity. Spawns beyond it fail with <see cref="RejectCode.EntityLimitReached"/>.
    /// Must be ≤ <see cref="NetId.MaxSlot"/> (65535), because server→client records address entities by
    /// <c>u16 Slot</c>.
    /// </summary>
    public required int MaxEntities { get; init; }

    /// <summary>Maximum concurrent subscribers (players). Sizes the subscriber pool.</summary>
    public required int MaxPlayers { get; init; }

    /// <summary>Area-of-interest radius in world units — the AOI <i>enter</i> radius.</summary>
    public required float AoiRadius { get; init; }

    /// <summary>
    /// Spatial-hash cell size in world units. <c>0</c> (the default) means "use <see cref="AoiRadius"/>",
    /// which keeps every AOI query inside a 3×3 cell neighbourhood.
    /// </summary>
    public float CellSize { get; init; }

    /// <summary>
    /// AOI hysteresis as a <i>factor</i> of <see cref="AoiRadius"/>: an entity enters at
    /// <see cref="AoiRadius"/> and only exits beyond <c>AoiRadius × AoiExitFactor</c>. At 600 players the
    /// arena edge is all boundary, and a flapping pair would cost ~22 B/tick each. Default 1.25 per the
    /// protocol.
    /// </summary>
    public float AoiExitFactor { get; init; } = 1.25f;

    /// <summary>
    /// Hard per-client per-tick frame budget in bytes — one MSS, and one future QUIC datagram. Every hot
    /// frame is bounded by <c>min(destination.Length, MaxBytesPerClientPerTick)</c>; assembly emits what
    /// fits and carries the rest.
    /// </summary>
    public int MaxBytesPerClientPerTick { get; init; } = 1100;

    /// <summary>
    /// New full records per client per tick in a <c>DeltaPacket</c>. The enter scan resumes from a
    /// per-client carry cursor, so the remainder arrives on following ticks instead of the same low slots
    /// winning every tick. Snapshots are bounded by the byte budget instead — a resync is supposed to be
    /// one or two frames, not a 24-record-per-tick trickle.
    /// </summary>
    public int MaxEntersPerTick { get; init; } = 24;

    /// <summary>
    /// Per client, the maximum number of entities that may be visible <i>at once</i>: the k in
    /// k-nearest-by-squared-distance. This bounds the client's known set, hence its receive tables and its
    /// update-section cost, which is what makes the dogpile case survivable.
    /// </summary>
    public int MaxVisibleEntities { get; init; } = 64;

    /// <summary>
    /// Room tick rate, needed to turn the per-second speed limits below into per-tick budgets. Mirrors
    /// <c>RoomConfig.TickHz</c>; the replication core never schedules anything itself.
    /// </summary>
    public int TickHz { get; init; } = 20;

    /// <summary>
    /// Ceiling on how fast a <i>spectator</i> focus may travel, in world units per second. Per-tick
    /// movement is clamped to <c>MaxSpectatorFocusSpeed / TickHz</c> and a clamp increments the client's
    /// <c>focusClamp</c> counter.
    /// </summary>
    /// <remarks>
    /// The default of 2000 u/s is chosen against the default world (4096 units across) and tick rate
    /// (20 Hz): it lets a spectator pan the full arena in about two seconds — comfortably faster than any
    /// player — while capping a single tick's focus jump at 100 units, roughly 1/12 of the default AOI
    /// radius. That matters because free-position focus is the "teleport my focus every tick to force
    /// enormous enter sets and amplify to N peers" exploit: at 100 units per tick an attacker can only
    /// crawl its AOI onto a new crowd, and <see cref="MaxEntersPerTick"/> bounds what the crawl yields.
    /// Bound focuses (the normal path) are not clamped at all — they come from server-side positions.
    /// </remarks>
    public float MaxSpectatorFocusSpeed { get; init; } = 2000f;

    /// <summary>
    /// Plausible entity speed in world units per second, used by the Level-1 speed check
    /// (<c>|Δpos| ≤ MaxEntitySpeed × Δt × 1.25</c>). <b>Counted, never enforced</b> at Level 1 — the check
    /// exists to build the dataset that Level 2 will enforce on.
    /// </summary>
    /// <remarks>
    /// Deliberately generous: at 20 Hz the default allows 125 world units of movement per tick, so normal
    /// play never trips it while a full-world hop (4096 units) always does. The fabric knows nothing about
    /// any game's movement rules, so a room that cares must set this from its own design — which is also
    /// why it must not be enforced until Level 2 has real per-kind limits.
    /// </remarks>
    public float MaxEntitySpeed { get; init; } = 2000f;

    /// <summary>World-space X of the low corner of this room's quantization range.</summary>
    public float WorldOriginX { get; init; } = -2048f;

    /// <summary>World-space Y of the low corner of this room's quantization range.</summary>
    public float WorldOriginY { get; init; } = -2048f;

    /// <summary>Side length of the square world every quantized value in this room is expressed against.</summary>
    public float WorldSize { get; init; } = 4096f;

    /// <summary>Resolved cell size (see <see cref="CellSize"/>).</summary>
    public float EffectiveCellSize => CellSize > 0f ? CellSize : AoiRadius;

    /// <summary>Resolved AOI exit radius: <c>AoiRadius × AoiExitFactor</c>.</summary>
    public float EffectiveExitRadius => AoiRadius * AoiExitFactor;

    /// <summary>The quantizer these world bounds describe. Built once per room, on the control path.</summary>
    public WorldQuantizer CreateQuantizer() => new(WorldOriginX, WorldOriginY, WorldSize);

    /// <summary>
    /// Smallest byte budget that can make progress on every hot frame kind: a <c>DeltaPacket</c> with one
    /// full record is the largest fixed floor (13 + 20).
    /// </summary>
    /// <remarks>
    /// A budget below <c>SignalBatchPacketHeaderSize + SignalEntryOverheadSize + MaxSignalNameLength +
    /// MaxSignalPayloadLength</c> (333) cannot carry a <i>maximal</i> signal entry, which is a starvation
    /// question rather than a correctness one, so it is documented rather than rejected. The 1100-byte
    /// default is far above both numbers.
    /// </remarks>
    public static int MinViableFrameBytes =>
        Math.Max(
            HotWire.DeltaPacketFixedOverhead + HotWire.FullRecordSize,
            HotWire.SnapshotPacketHeaderSize + HotWire.FullRecordSize);

    /// <summary>Throws when a limit is out of range. Called once by <see cref="RoomReplication"/>.</summary>
    public void Validate()
    {
        // Slot addressing: server->client records carry a u16 Slot, so the table can never exceed the
        // 16-bit slot space of the 16/16 NetId split.
        if (MaxEntities < 1 || MaxEntities > NetId.MaxSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntities), MaxEntities,
                $"must be 1..{NetId.MaxSlot} (server→client records address entities by u16 Slot)");
        }

        if (MaxPlayers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPlayers), MaxPlayers, "must be >= 1");
        }

        if (!float.IsFinite(AoiRadius) || AoiRadius <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(AoiRadius), AoiRadius, "must be finite and > 0");
        }

        if (CellSize < 0f || !float.IsFinite(CellSize))
        {
            throw new ArgumentOutOfRangeException(nameof(CellSize), CellSize, "must be finite and >= 0");
        }

        // < 1 would put the exit radius inside the enter radius — an entity would exit while still
        // entering, flapping every tick, which is the exact failure hysteresis exists to prevent.
        if (!float.IsFinite(AoiExitFactor) || AoiExitFactor < 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(AoiExitFactor), AoiExitFactor, "must be finite and >= 1");
        }

        if (!float.IsFinite(EffectiveExitRadius))
        {
            throw new ArgumentOutOfRangeException(nameof(AoiExitFactor), AoiExitFactor,
                "AoiRadius × AoiExitFactor overflowed to a non-finite radius");
        }

        if (MaxBytesPerClientPerTick < MinViableFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBytesPerClientPerTick), MaxBytesPerClientPerTick,
                $"must be >= {MinViableFrameBytes} to make progress on any frame");
        }

        if (MaxEntersPerTick < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntersPerTick), MaxEntersPerTick, "must be >= 1");
        }

        if (MaxVisibleEntities < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxVisibleEntities), MaxVisibleEntities, "must be >= 1");
        }

        if (TickHz < 1 || TickHz > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(TickHz), TickHz, "must be 1..1000");
        }

        if (!float.IsFinite(MaxSpectatorFocusSpeed) || MaxSpectatorFocusSpeed <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSpectatorFocusSpeed), MaxSpectatorFocusSpeed,
                "must be finite and > 0");
        }

        if (!float.IsFinite(MaxEntitySpeed) || MaxEntitySpeed <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntitySpeed), MaxEntitySpeed, "must be finite and > 0");
        }

        // WorldQuantizer.IsValidWorld also enforces the float32 precision ratio: outside it the
        // encode→decode→encode fixed point silently stops holding and positions oscillate by a quantum
        // forever, so an unusable world is refused at room creation instead.
        if (!WorldQuantizer.IsValidWorld(WorldOriginX, WorldOriginY, WorldSize))
        {
            throw new ArgumentOutOfRangeException(nameof(WorldSize), WorldSize,
                $"world bounds ({WorldOriginX}, {WorldOriginY}, size {WorldSize}) are unusable: all three must "
                + $"be finite, size >= {WorldQuantizer.MinWorldSize}, and every coordinate magnitude below "
                + $"{WorldQuantizer.MaxCoordinateToSizeRatio} × size or float32 round-tripping stops being a "
                + "fixed point");
        }
    }
}
