using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// Per-room defaults applied to a <see cref="RoomConfig"/> whose numeric fields were left unset
/// (0 or negative). Bound from configuration section <c>Rooms:Defaults</c>.
/// </summary>
/// <remarks>
/// These are only <i>defaults</i>. The hard admissible ranges live in
/// <see cref="RoomConfigValidator"/>; a value outside them is rejected, never silently clamped.
/// </remarks>
public sealed class RoomDefaultsOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Rooms:Defaults";

    /// <summary>Default member cap for a room that did not ask for one.</summary>
    public int MaxPlayers { get; set; } = 64;

    /// <summary>Default room tick rate in Hz.</summary>
    public int TickHz { get; set; } = 20;

    /// <summary>Default area-of-interest radius in world units.</summary>
    public float AoiRadius { get; set; } = 1200f;

    /// <summary>Default seconds a room may stay empty before the sweeper destroys it.</summary>
    public int IdleTtlSeconds { get; set; } = 300;

    /// <summary>Default entity-table capacity.</summary>
    public int MaxEntities { get; set; } = 4096;
}

/// <summary>
/// Server-wide room-fabric knobs: registry cap, queue sizes, tick-loop safety limits and the
/// room-scoped quotas the room itself enforces (chat, room vars, cold props, remote events).
/// Bound from configuration section <c>Rooms:Server</c>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>Rooms:Server</c> section also carries transport-owned keys (<c>MaxTotalConnections</c>,
/// <c>OutboundQueueCapacity</c>); they are deliberately absent here because
/// <c>Pix3.Rooms.Server.Net</c> owns them. Unknown keys are ignored by configuration binding, so both
/// modules can bind the same section independently.
/// </para>
/// <para>
/// Values come from an operator-edited file, so a nonsensical one must not take the process down:
/// <see cref="Normalize"/> clamps every field into a usable range and is called by the types that
/// consume these options.
/// </para>
/// </remarks>
public sealed class RoomServerOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Rooms:Server";

    /// <summary>Maximum rooms alive at once; <c>TryCreate</c> beyond this fails with <c>QuotaExceeded</c>.</summary>
    public int MaxRooms { get; set; } = 256;

    /// <summary>
    /// Capacity of each room's bounded inbound queue. Once full, <c>TryEnqueueInbound</c> returns false
    /// and the socket layer drops (and counts) the message instead of blocking.
    /// </summary>
    public int InboundQueueCapacity { get; set; } = 4096;

    /// <summary>
    /// Upper bound on inbound messages a single tick will drain. A flood is therefore spread across
    /// ticks instead of starving the tick itself; the remainder stays queued.
    /// </summary>
    public int MaxDrainPerTick { get; set; } = 2048;

    /// <summary>
    /// Largest frame the room will emit, matching the transport's <c>MaxPayloadBytes</c>. Snapshot and
    /// delta writers are handed exactly this many bytes, which is what keeps the server from ever
    /// exceeding the wire limit.
    /// </summary>
    public int MaxFrameBytes { get; set; } = 16 * 1024;

    /// <summary>
    /// Snapshot frames a single joiner may receive per tick. A large snapshot is emitted across
    /// several ticks rather than monopolising one.
    /// </summary>
    public int MaxSnapshotFramesPerTick { get; set; } = 8;

    /// <summary>
    /// Consecutive failing ticks after which the room gives up: members are closed with
    /// <c>InternalError</c> and the loop exits so the manager can destroy the room.
    /// </summary>
    public int MaxConsecutiveTickFailures { get; set; } = 5;

    /// <summary>How often <see cref="RoomIdleSweeper"/> scans for idle rooms.</summary>
    public int IdleSweepIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// A room younger than this is never reaped, so a room created just before its first player
    /// connects cannot be swept away underneath them.
    /// </summary>
    public int RoomCreationGraceSeconds { get; set; } = 30;

    /// <summary>Chat messages one member may send per minute; the rest are dropped and counted.</summary>
    public int MaxChatPerMinute { get; set; } = 20;

    /// <summary>Maximum chat characters kept after sanitisation.</summary>
    public int MaxChatLength { get; set; } = 512;

    /// <summary>
    /// When true (default) only the room host may write room vars. The wire protocol has no host or
    /// role concept, so a deployment that wants every member to be able to write vars sets this false.
    /// </summary>
    public bool RestrictRoomVarsToHost { get; set; } = true;

    /// <summary>Distinct room-var keys a room may hold.</summary>
    public int MaxRoomVars { get; set; } = 64;

    /// <summary>Maximum room-var key length after sanitisation.</summary>
    public int MaxRoomVarKeyLength { get; set; } = 64;

    /// <summary>Maximum bytes in a single room-var value.</summary>
    public int MaxRoomVarValueBytes { get; set; } = 4096;

    /// <summary>Maximum bytes of cold props the room will store and fan out for one entity.</summary>
    public int MaxColdPropsBytes { get; set; } = 4096;

    /// <summary>Entities one member may own at once, on top of the room-wide <c>MaxEntities</c>.</summary>
    public int MaxEntitiesPerOwner { get; set; } = 256;

    /// <summary>Maximum remote-event name length after sanitisation.</summary>
    public int MaxRemoteEventNameLength { get; set; } = 64;

    /// <summary>Maximum remote-event payload size.</summary>
    public int MaxRemoteEventPayloadBytes { get; set; } = 4096;

    /// <summary>
    /// Length of one tick-duration histogram window in seconds. Percentiles are reported over the
    /// live window plus the previous complete one, so they reflect recent behaviour instead of
    /// process lifetime.
    /// </summary>
    public int TickHistogramWindowSeconds { get; set; } = 10;

    /// <summary>Seconds <c>RoomManager</c> waits for room loops to stop during shutdown.</summary>
    public int ShutdownTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Clamps every field into a usable range. Idempotent, and safe to call on options that came
    /// straight from configuration.
    /// </summary>
    public void Normalize()
    {
        MaxRooms = Math.Clamp(MaxRooms, 1, 100_000);
        InboundQueueCapacity = Math.Clamp(InboundQueueCapacity, 16, 1_000_000);
        MaxDrainPerTick = Math.Clamp(MaxDrainPerTick, 1, InboundQueueCapacity);
        MaxFrameBytes = Math.Clamp(MaxFrameBytes, 1024, 1024 * 1024);
        MaxSnapshotFramesPerTick = Math.Clamp(MaxSnapshotFramesPerTick, 1, 1024);
        MaxConsecutiveTickFailures = Math.Clamp(MaxConsecutiveTickFailures, 1, 1000);
        IdleSweepIntervalSeconds = Math.Clamp(IdleSweepIntervalSeconds, 1, 3600);
        RoomCreationGraceSeconds = Math.Clamp(RoomCreationGraceSeconds, 0, 86_400);
        MaxChatPerMinute = Math.Clamp(MaxChatPerMinute, 0, 10_000);
        MaxChatLength = Math.Clamp(MaxChatLength, 1, 4096);
        MaxRoomVars = Math.Clamp(MaxRoomVars, 0, 100_000);
        MaxRoomVarKeyLength = Math.Clamp(MaxRoomVarKeyLength, 1, 1024);
        MaxRoomVarValueBytes = Math.Clamp(MaxRoomVarValueBytes, 0, 1024 * 1024);
        MaxColdPropsBytes = Math.Clamp(MaxColdPropsBytes, 0, 1024 * 1024);
        MaxEntitiesPerOwner = Math.Clamp(MaxEntitiesPerOwner, 1, NetId.MaxSlot);
        MaxRemoteEventNameLength = Math.Clamp(MaxRemoteEventNameLength, 1, 1024);
        MaxRemoteEventPayloadBytes = Math.Clamp(MaxRemoteEventPayloadBytes, 0, 1024 * 1024);
        TickHistogramWindowSeconds = Math.Clamp(TickHistogramWindowSeconds, 1, 3600);
        ShutdownTimeoutSeconds = Math.Clamp(ShutdownTimeoutSeconds, 0, 600);
    }
}
