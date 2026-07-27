using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// Per-client replication state — known set, AOI focus, sequencing, carry cursors, violation tallies and
/// the uncommitted-frame record — owned by <see cref="RoomReplication"/> and pooled across joins so a
/// churnful room never reallocates its bitsets.
/// </summary>
/// <remarks>
/// <para><b>Known set.</b> <see cref="Known"/> is the set of slots this client currently has a full
/// record for; <see cref="KnownGeneration"/> remembers <i>which generation</i> of each slot the client
/// knows. The pair is what keeps slot reuse honest: a client is deemed to know an entity only when the
/// known bit is set <i>and</i> the generation matches, so an entity that despawns and a different entity
/// that reuses its slot are never confused — the client is first told to drop the slot, then given a fresh
/// full record.</para>
/// <para><b>Generation 0 is a refresh marker.</b> Generations start at 1, so <c>Known.Get(slot) == true</c>
/// with <c>KnownGeneration[slot] == 0</c> can never match a live entity. Host migration uses exactly that
/// to force a remove-then-re-enter for an entity whose <c>OwnerId</c> changed: <c>OwnerId</c> travels only
/// in a <c>FullRecord</c>, so a re-enter is the only way an observer learns about a new owner.</para>
/// <para><b>Sequencing.</b> <see cref="NextSeq"/> is the value the <i>next</i> frame will stamp — a peek.
/// It advances only in <see cref="RoomReplication.Commit"/>, so a frame that was written but never
/// enqueued leaves no gap for the client to detect.</para>
/// <para><b>Owed updates.</b> <see cref="PendingUpdates"/> holds slots whose current state this client has
/// not been told. A dropped absolute position only self-heals if the entity keeps changing, so an entity that
/// moves once and then stops would otherwise leave a truncated (or skipped, or mid-snapshot) client
/// permanently stale.</para>
/// <para><b>Scratch.</b> <see cref="VisibleInner"/>/<see cref="VisibleOuter"/> receive the grid query,
/// stamped with <see cref="VisibilityStamp"/> so the tick's first writer pays for it and the rest reuse it.
/// <see cref="KnownBeforeEnters"/> snapshots the known set between the exit and enter passes, so an entity
/// that both enters AOI and is dirty in the same tick is sent as a full record only, never full + delta —
/// and it is per-subscriber rather than shared because <see cref="RoomReplication.Commit"/> reads it after
/// the write returns.</para>
/// </remarks>
public sealed class SubscriberState
{
    /// <summary>Client this state is currently bound to; meaningful only while checked out of the pool.</summary>
    public uint ClientId;

    // ── AOI focus ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Entity whose server-side position drives this client's AOI centre; 0 = none. The normal path.
    /// </summary>
    public uint FocusNetId;

    /// <summary>
    /// True when the focus is a free coordinate pair (a spectator) rather than a bound entity. Free
    /// focuses are the only ones that are speed-clamped, because they are the only ones a client controls.
    /// </summary>
    public bool FocusIsFree;

    /// <summary>Last resolved AOI focus X.</summary>
    public float FocusX;

    /// <summary>Last resolved AOI focus Y.</summary>
    public float FocusY;

    /// <summary>
    /// False until a focus has ever been set. The first free focus is accepted verbatim — clamping it
    /// against the (0, 0) initial value would trap a joining spectator near the world origin.
    /// </summary>
    public bool HasFocus;

    // ── Sequencing and client preferences ───────────────────────────────────────

    /// <summary>The <c>Seq</c> the next emitted frame will carry. Advanced only by a commit.</summary>
    public ushort NextSeq;

    /// <summary>
    /// Hidden clients get no hot frames at all and <see cref="NextSeq"/> stops advancing: a backgrounded
    /// tab cannot drain a 20 Hz stream, it buffers it.
    /// </summary>
    public bool Hidden;

    /// <summary>Serve this client every nth tick. Always 1..8; 1 = every tick.</summary>
    public byte SendDivisor;

    // ── Cursors ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Where the next enter scan starts. Carried between ticks so the same low slots do not win the
    /// <c>MaxEntersPerTick</c> budget every tick and a starved client eventually sees everything.
    /// </summary>
    public int EnterCursor;

    /// <summary>Index into the table's dense live list where the next snapshot frame resumes.</summary>
    public int SnapshotCursor;

    /// <summary>True while this client still owes a (possibly split) full snapshot.</summary>
    public bool SnapshotPending;

    // ── Known set and owed updates ──────────────────────────────────────────────

    /// <summary>Slots the client has been sent a full record for (subject to generation match).</summary>
    public readonly SlotBitset Known;

    /// <summary>Generation of each known slot at the time its full record was sent; 0 = must be refreshed.</summary>
    public readonly ushort[] KnownGeneration;

    /// <summary>
    /// Slots whose current state this client is still owed. Every dirty entity the client knows is
    /// provisionally owed at the top of each tick and the debt is cleared only when a record for it actually
    /// ships, so a truncated section, a skipped tick (send divisor) and a mid-snapshot change all resolve the
    /// same way. Owed updates are re-sent from <i>current</i> state, never replayed.
    /// </summary>
    public readonly SlotBitset PendingUpdates;

    // ── Per-tick scratch ────────────────────────────────────────────────────────

    /// <summary>Scratch: slots within the AOI enter radius, after the k-nearest cap.</summary>
    public readonly SlotBitset VisibleInner;

    /// <summary>Scratch: slots within the AOI exit radius, after the k-nearest cap.</summary>
    public readonly SlotBitset VisibleOuter;

    /// <summary>Scratch: <see cref="Known"/> as of after the exit pass, before enters mark it.</summary>
    public readonly SlotBitset KnownBeforeEnters;

    /// <summary>Internal tick stamp of the visibility currently in the two visible sets; 0 = none.</summary>
    public ulong VisibilityStamp;

    // ── Violation tallies (per client; Quota stays 0 — Rooms owns that number) ───

    /// <summary>Entity mutations aimed at an entity this client does not own.</summary>
    public long ViolationOwnership;

    /// <summary>Moves that failed the Level-1 plausibility check. Counted, never enforced.</summary>
    public long ViolationSpeed;

    /// <summary>Illegal delta masks and records the decoder refused.</summary>
    public long ViolationMask;

    /// <summary>Non-finite floats — in practice only spectator focus coordinates.</summary>
    public long ViolationNan;

    /// <summary>Spawns naming a kind outside the room's allowlist (attributed by Rooms).</summary>
    public long ViolationKind;

    /// <summary>Spectator focus moves that hit the per-tick speed clamp.</summary>
    public long ViolationFocusClamp;

    /// <summary>Client-set teleport bits.</summary>
    public long ViolationTeleport;

    // ── Uncommitted-frame record (two-phase known-set commit) ───────────────────

    /// <summary>
    /// Pairing token of the currently open pending frame; 0 = none. Bumped on every frame open, so a
    /// duplicate or stale <see cref="PendingKnownSetCommit"/> fails the pairing test instead of corrupting
    /// the known set.
    /// </summary>
    public uint PendingToken;

    /// <summary>True while a written frame is awaiting <c>Commit</c> or <c>Rollback</c>.</summary>
    public bool HasPendingFrame;

    /// <summary>Internal tick stamp of the pending frame; a commit must land in the tick that wrote it.</summary>
    public ulong PendingTickStamp;

    /// <summary>True when the pending frame advanced the enter carry cursor.</summary>
    public bool PendingHasEnterCursor;

    /// <summary><see cref="EnterCursor"/> value the pending frame intends to install.</summary>
    public int PendingEnterCursor;

    /// <summary>True when the pending frame is a snapshot and therefore governs the snapshot cursor.</summary>
    public bool PendingHasSnapshotCursor;

    /// <summary><see cref="SnapshotCursor"/> value the pending frame intends to install.</summary>
    public int PendingSnapshotCursor;

    /// <summary>True when the pending snapshot frame carried the last records (stamped <c>Final</c>).</summary>
    public bool PendingIsFinalSnapshot;

    /// <summary>Slots the pending frame wrote removals for; committing un-knows them.</summary>
    public readonly int[] PendingRemovals;

    /// <summary>Number of valid entries in <see cref="PendingRemovals"/>.</summary>
    public int PendingRemovalCount;

    /// <summary>Slots the pending frame wrote full records for; committing marks them known.</summary>
    public readonly int[] PendingEnters;

    /// <summary>Generation to record for each entry of <see cref="PendingEnters"/>.</summary>
    public readonly ushort[] PendingEnterGenerations;

    /// <summary>Number of valid entries in <see cref="PendingEnters"/>.</summary>
    public int PendingEnterCount;

    /// <summary>Slots the pending frame wrote update records for; committing clears their owed bit.</summary>
    public readonly int[] PendingUpdateSlots;

    /// <summary>Number of valid entries in <see cref="PendingUpdateSlots"/>.</summary>
    public int PendingUpdateSlotCount;

    /// <summary>
    /// Allocates all per-client storage. The pending-intent arrays are sized from the byte budget, so no
    /// section can ever record more entries than a frame could physically carry.
    /// </summary>
    /// <param name="maxEntities">Slot capacity of the room's entity table.</param>
    /// <param name="maxRemovals">
    /// <c>(MaxBytesPerClientPerTick − DeltaPacketFixedOverhead) / RemovedSlotSize</c>.
    /// </param>
    /// <param name="maxEnters">
    /// The larger of <c>MaxEntersPerTick</c> and what one snapshot frame's byte budget allows — a snapshot
    /// is bounded by bytes, not by the per-tick enter cap.
    /// </param>
    /// <param name="maxUpdates">
    /// <c>(MaxBytesPerClientPerTick − DeltaPacketFixedOverhead) / MinUpdateRecordSize</c>.
    /// </param>
    public SubscriberState(int maxEntities, int maxRemovals, int maxEnters, int maxUpdates)
    {
        Known = new SlotBitset(maxEntities);
        KnownGeneration = new ushort[maxEntities];
        PendingUpdates = new SlotBitset(maxEntities);
        VisibleInner = new SlotBitset(maxEntities);
        VisibleOuter = new SlotBitset(maxEntities);
        KnownBeforeEnters = new SlotBitset(maxEntities);

        PendingRemovals = new int[Math.Max(maxRemovals, 1)];
        PendingEnters = new int[Math.Max(maxEnters, 1)];
        PendingEnterGenerations = new ushort[PendingEnters.Length];
        PendingUpdateSlots = new int[Math.Max(maxUpdates, 1)];

        SendDivisor = 1;
    }

    /// <summary>
    /// Rebinds a pooled instance to a new client with a clean slate: a rejoin forgets everything it knew.
    /// Nothing is carried over — not the known set, not the cursors, not the tallies, not an uncommitted
    /// frame — because the client on the other end is a fresh session with an empty receive table.
    /// </summary>
    public void Reset(uint clientId)
    {
        ClientId = clientId;

        FocusNetId = NetId.None;
        FocusIsFree = false;
        FocusX = 0f;
        FocusY = 0f;
        HasFocus = false;

        NextSeq = 0;
        Hidden = false;
        SendDivisor = 1;

        EnterCursor = 0;
        SnapshotCursor = 0;
        SnapshotPending = false;

        Known.Clear();
        Array.Clear(KnownGeneration, 0, KnownGeneration.Length);
        PendingUpdates.Clear();

        VisibilityStamp = 0;

        ViolationOwnership = 0;
        ViolationSpeed = 0;
        ViolationMask = 0;
        ViolationNan = 0;
        ViolationKind = 0;
        ViolationFocusClamp = 0;
        ViolationTeleport = 0;

        DiscardPendingFrame();
        PendingToken = 0;

        // Visible/scratch sets are cleared by every query pass; no need to touch them here.
    }

    /// <summary>
    /// Drops the recorded intent of an open pending frame without applying any of it. Shared by rollback,
    /// resync and reset — the difference between them is what happens <i>around</i> this call.
    /// </summary>
    public void DiscardPendingFrame()
    {
        HasPendingFrame = false;
        PendingTickStamp = 0;
        PendingHasEnterCursor = false;
        PendingEnterCursor = 0;
        PendingHasSnapshotCursor = false;
        PendingSnapshotCursor = 0;
        PendingIsFinalSnapshot = false;
        PendingRemovalCount = 0;
        PendingEnterCount = 0;
        PendingUpdateSlotCount = 0;
    }

    /// <summary>This client's violation tallies. <c>Quota</c> is left at 0 for Rooms to merge in.</summary>
    public ViolationCounters SnapshotViolations() => new(
        ViolationOwnership,
        ViolationSpeed,
        ViolationMask,
        ViolationNan,
        ViolationKind,
        Quota: 0,
        ViolationFocusClamp,
        ViolationTeleport);
}
