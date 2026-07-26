namespace Pix3.Rooms.Protocol;

/// <summary>
/// The single authoritative TypeId map. Every WebSocket binary frame is <c>[u8 TypeId][payload]</c>.
/// Ranges are reserved; never allocate an id outside its range.
/// </summary>
/// <remarks>
/// <para>0–63 core (handshake, session, chat, room vars) — MemoryPack payloads.</para>
/// <para>64–127 state sync — MemoryPack except the three hot-plane frames (67/68/69), which are hand-packed.</para>
/// <para>128–191 remote events / RPC — MemoryPack payloads.</para>
/// <para>192–255 reserved for app/game-specific extensions; this server never interprets them.</para>
/// </remarks>
public static class MessageTypeIds
{
    // ── Range boundaries (inclusive) ───────────────────────────────────────────

    /// <summary>First id of the core range.</summary>
    public const byte CoreRangeFirst = 0;

    /// <summary>Last id of the core range.</summary>
    public const byte CoreRangeLast = 63;

    /// <summary>First id of the state-sync range.</summary>
    public const byte StateRangeFirst = 64;

    /// <summary>Last id of the state-sync range.</summary>
    public const byte StateRangeLast = 127;

    /// <summary>First id of the remote-event range.</summary>
    public const byte RemoteEventRangeFirst = 128;

    /// <summary>Last id of the remote-event range.</summary>
    public const byte RemoteEventRangeLast = 191;

    /// <summary>First id reserved for application/game extensions.</summary>
    public const byte AppRangeFirst = 192;

    /// <summary>Last id reserved for application/game extensions.</summary>
    public const byte AppRangeLast = 255;

    // ── Core: handshake, session, chat, room vars (0–63) ───────────────────────

    /// <summary>C→S <see cref="Protocol.HelloRequest"/>. Must be the first frame a client sends.</summary>
    public const byte HelloRequest = 1;

    /// <summary>S→C <see cref="Protocol.WelcomeEvent"/>. Handshake accepted.</summary>
    public const byte WelcomeEvent = 2;

    /// <summary>S→C <see cref="Protocol.RejectEvent"/>. Always precedes a close whose reason is known.</summary>
    public const byte RejectEvent = 3;

    /// <summary>C→S <see cref="Protocol.PingRequest"/>.</summary>
    public const byte PingRequest = 4;

    /// <summary>S→C <see cref="Protocol.PongEvent"/>.</summary>
    public const byte PongEvent = 5;

    /// <summary>S→C <see cref="Protocol.PeerJoinedEvent"/>.</summary>
    public const byte PeerJoinedEvent = 6;

    /// <summary>S→C <see cref="Protocol.PeerLeftEvent"/>.</summary>
    public const byte PeerLeftEvent = 7;

    /// <summary>S→C <see cref="Protocol.RoomInfoEvent"/>, sent at roughly 1 Hz.</summary>
    public const byte RoomInfoEvent = 8;

    /// <summary>C→S <see cref="Protocol.ChatMessageRequest"/>.</summary>
    public const byte ChatMessageRequest = 9;

    /// <summary>S→C <see cref="Protocol.ChatMessageEvent"/>.</summary>
    public const byte ChatMessageEvent = 10;

    /// <summary>C→S <see cref="Protocol.LeaveRequest"/> (empty payload).</summary>
    public const byte LeaveRequest = 11;

    /// <summary>C→S <see cref="Protocol.SetRoomVarRequest"/>.</summary>
    public const byte SetRoomVarRequest = 12;

    /// <summary>S→C <see cref="Protocol.RoomVarsEvent"/>. Full set on join, changed subset afterwards.</summary>
    public const byte RoomVarsEvent = 13;

    // ── State sync: entities (64–127) ─────────────────────────────────────────

    /// <summary>C→S <see cref="Protocol.EntitySpawnRequest"/>.</summary>
    public const byte EntitySpawnRequest = 64;

    /// <summary>S→C <see cref="Protocol.EntitySpawnAckEvent"/>.</summary>
    public const byte EntitySpawnAckEvent = 65;

    /// <summary>C→S <see cref="Protocol.EntityDespawnRequest"/>.</summary>
    public const byte EntityDespawnRequest = 66;

    /// <summary>C→S hot plane, hand-packed. See <see cref="HotWire.WriteEntityUpdateFrameHeader"/>.</summary>
    public const byte EntityUpdateFrame = 67;

    /// <summary>S→C hot plane, hand-packed. See <see cref="HotWire.WriteSnapshotFrameHeader"/>.</summary>
    public const byte SnapshotFrame = 68;

    /// <summary>S→C hot plane, hand-packed. See <see cref="HotWire.WriteDeltaFrameHeader"/>.</summary>
    public const byte DeltaFrame = 69;

    /// <summary>C→S <see cref="Protocol.SetEntityColdPropsRequest"/>.</summary>
    public const byte SetEntityColdPropsRequest = 70;

    /// <summary>S→C <see cref="Protocol.EntityColdPropsEvent"/>.</summary>
    public const byte EntityColdPropsEvent = 71;

    // ── Remote events / RPC (128–191) ─────────────────────────────────────────

    /// <summary>C→S <see cref="Protocol.RemoteEventRequest"/>.</summary>
    public const byte RemoteEventRequest = 128;

    /// <summary>S→C <see cref="Protocol.RemoteEventBroadcast"/>.</summary>
    public const byte RemoteEventBroadcast = 129;

    /// <summary>
    /// True for the three hand-packed frames (67/68/69). Those must never go through MemoryPack.
    /// </summary>
    public static bool IsHotPlane(byte typeId)
        => typeId is EntityUpdateFrame or SnapshotFrame or DeltaFrame;

    /// <summary>
    /// Human-readable name for logs and metrics labels. Returns <c>"Unknown"</c> for unmapped ids
    /// (never allocates for a known id).
    /// </summary>
    public static string GetName(byte typeId) => typeId switch
    {
        HelloRequest => nameof(HelloRequest),
        WelcomeEvent => nameof(WelcomeEvent),
        RejectEvent => nameof(RejectEvent),
        PingRequest => nameof(PingRequest),
        PongEvent => nameof(PongEvent),
        PeerJoinedEvent => nameof(PeerJoinedEvent),
        PeerLeftEvent => nameof(PeerLeftEvent),
        RoomInfoEvent => nameof(RoomInfoEvent),
        ChatMessageRequest => nameof(ChatMessageRequest),
        ChatMessageEvent => nameof(ChatMessageEvent),
        LeaveRequest => nameof(LeaveRequest),
        SetRoomVarRequest => nameof(SetRoomVarRequest),
        RoomVarsEvent => nameof(RoomVarsEvent),
        EntitySpawnRequest => nameof(EntitySpawnRequest),
        EntitySpawnAckEvent => nameof(EntitySpawnAckEvent),
        EntityDespawnRequest => nameof(EntityDespawnRequest),
        EntityUpdateFrame => nameof(EntityUpdateFrame),
        SnapshotFrame => nameof(SnapshotFrame),
        DeltaFrame => nameof(DeltaFrame),
        SetEntityColdPropsRequest => nameof(SetEntityColdPropsRequest),
        EntityColdPropsEvent => nameof(EntityColdPropsEvent),
        RemoteEventRequest => nameof(RemoteEventRequest),
        RemoteEventBroadcast => nameof(RemoteEventBroadcast),
        _ => "Unknown",
    };
}
