using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// Fixed limits and tuning for one room's <see cref="RoomReplication"/>. Everything here is sized
/// once at construction — the replication core never grows, so a room's memory footprint is known
/// the moment it is created.
/// </summary>
public sealed record ReplicationOptions
{
    /// <summary>Entity-table capacity. Spawns beyond it fail with <see cref="RejectCode.EntityLimitReached"/>.</summary>
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
    /// Extra AOI <i>exit</i> margin in world units: an entity enters at <see cref="AoiRadius"/> but only
    /// exits beyond <c>AoiRadius + AoiHysteresis</c>, so an entity oscillating on the boundary does not
    /// flap between enter and exit every tick. <c>0</c> (the default) means "5% of <see cref="AoiRadius"/>".
    /// </summary>
    public float AoiHysteresis { get; init; }

    /// <summary>
    /// Hard cap on one outgoing frame (TypeId included). Delta/snapshot assembly emits what fits and
    /// carries the rest to the next tick. Default 16 KiB per the protocol spec.
    /// </summary>
    public int MaxPayloadBytes { get; init; } = 16 * 1024;

    /// <summary>Resolved cell size (see <see cref="CellSize"/>).</summary>
    public float EffectiveCellSize => CellSize > 0f ? CellSize : AoiRadius;

    /// <summary>Resolved hysteresis margin (see <see cref="AoiHysteresis"/>).</summary>
    public float EffectiveHysteresis => AoiHysteresis > 0f ? AoiHysteresis : AoiRadius * 0.05f;

    /// <summary>Throws when a limit is out of range. Called once by <see cref="RoomReplication"/>.</summary>
    public void Validate()
    {
        if (MaxEntities < 1 || MaxEntities > NetId.MaxSlot + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntities), MaxEntities,
                $"must be 1..{NetId.MaxSlot + 1}");
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

        if (AoiHysteresis < 0f || !float.IsFinite(AoiHysteresis))
        {
            throw new ArgumentOutOfRangeException(nameof(AoiHysteresis), AoiHysteresis, "must be finite and >= 0");
        }

        if (MaxPayloadBytes < HotWire.SnapshotFrameHeaderSize + HotWire.FullRecordSize)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes), MaxPayloadBytes,
                "too small to make progress on any frame");
        }
    }
}
