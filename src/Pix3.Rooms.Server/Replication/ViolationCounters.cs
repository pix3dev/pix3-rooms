namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// Per-client tallies of everything a client did that the fabric refused. Lives here rather than in
/// <c>Rooms</c> because Replication produces most of them and the dependency arrow only points this
/// way; Rooms merges its own quota and chat numbers in before exposing the record through <c>IRoom</c>.
/// Build the dataset now, the detector later.
/// </summary>
/// <param name="Ownership">Entity mutations aimed at an entity the sender does not own.</param>
/// <param name="Speed">
/// Moves that failed <c>|Δpos| ≤ maxSpeed × Δt × 1.25</c>. <b>Counted, never enforced</b> at Level 1 —
/// this is the Level-2 validator, written early behind the same seam.
/// </param>
/// <param name="Mask">
/// Illegal delta masks (a client setting a server-authored bit) and records the decoder refused.
/// </param>
/// <param name="Nan">Non-finite floats. After quantization the only inbound float left is spectator focus.</param>
/// <param name="Kind">
/// Spawns naming an entity kind outside the room's allowlist. The allowlist is Rooms' data, so Rooms
/// attributes these through <c>RoomReplication.CountKindViolation</c>.
/// </param>
/// <param name="Quota">
/// Always zero from Replication: connection- and room-level quotas are Rooms' and Net's to count, and
/// Rooms merges its numbers into this field.
/// </param>
/// <param name="FocusClamp">Spectator focus moves that hit the per-tick speed clamp.</param>
/// <param name="Teleport">
/// Client-set teleport bits. Legitimate under client authority (respawns), so counted rather than
/// refused; the bit is stripped at Level 2.
/// </param>
public readonly record struct ViolationCounters(
    long Ownership, long Speed, long Mask, long Nan,
    long Kind, long Quota, long FocusClamp, long Teleport);
