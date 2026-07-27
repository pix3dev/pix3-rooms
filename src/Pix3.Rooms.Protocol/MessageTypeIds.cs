namespace Pix3.Rooms.Protocol;

/// <summary>
/// The single authoritative TypeId map. Every WebSocket binary frame is <c>[u8 TypeId][payload]</c>.
/// Ranges are reserved; never allocate an id outside its range.
/// </summary>
/// <remarks>
/// <para>0–63 core (handshake, session, chat, room vars, client prefs) — MemoryPack payloads.</para>
/// <para>64–127 state sync — MemoryPack except the three hot-plane packets (67/68/69), which are hand-packed.</para>
/// <para>128–191 signals (networked game events) — MemoryPack except <see cref="SignalBatchPacket"/> (130).</para>
/// <para>192–255 reserved for app/game-specific extensions; this server never interprets them.</para>
/// <para>
/// A constant here is spelled <b>exactly</b> like the class it names, so one grep finds a message's
/// whole path — wire id, class, codec. <see cref="SignalBatchPacket"/> is the one id with no class:
/// it is hand-packed and lives entirely in <see cref="HotWire"/>.
/// </para>
/// <para>
/// An unknown TypeId is <b>ignored and counted, never fatal</b>, in both directions. That is what lets
/// a game published six months ago keep working when the fabric adds messages.
/// </para>
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

    /// <summary>First id of the signal range.</summary>
    public const byte SignalRangeFirst = 128;

    /// <summary>Last id of the signal range.</summary>
    public const byte SignalRangeLast = 191;

    /// <summary>First id reserved for application/game extensions.</summary>
    public const byte AppRangeFirst = 192;

    /// <summary>Last id reserved for application/game extensions.</summary>
    public const byte AppRangeLast = 255;

    // ── Core: handshake, session, chat, room vars, client prefs (0–63) ─────────

    /// <summary>C→S <see cref="Protocol.HelloCommand"/>. Must be the first frame a client sends.</summary>
    public const byte HelloCommand = 1;

    /// <summary>S→C <see cref="Protocol.WelcomeEvent"/>. Handshake accepted.</summary>
    public const byte WelcomeEvent = 2;

    /// <summary>S→C <see cref="Protocol.RejectedEvent"/>. Always precedes a close whose reason is known.</summary>
    public const byte RejectedEvent = 3;

    /// <summary>C→S <see cref="Protocol.PingCommand"/>.</summary>
    public const byte PingCommand = 4;

    /// <summary>S→C <see cref="Protocol.PongEvent"/>.</summary>
    public const byte PongEvent = 5;

    /// <summary>S→C <see cref="Protocol.PeerJoinedEvent"/>.</summary>
    public const byte PeerJoinedEvent = 6;

    /// <summary>S→C <see cref="Protocol.PeerLeftEvent"/>.</summary>
    public const byte PeerLeftEvent = 7;

    /// <summary>S→C <see cref="Protocol.RoomInfoEvent"/>, sent at roughly 1 Hz.</summary>
    public const byte RoomInfoEvent = 8;

    /// <summary>C→S <see cref="Protocol.SendChatCommand"/>.</summary>
    public const byte SendChatCommand = 9;

    /// <summary>S→C <see cref="Protocol.ChatMessageEvent"/>.</summary>
    public const byte ChatMessageEvent = 10;

    /// <summary>C→S <see cref="Protocol.LeaveCommand"/> (empty payload).</summary>
    public const byte LeaveCommand = 11;

    /// <summary>C→S <see cref="Protocol.SetRoomVarCommand"/>.</summary>
    public const byte SetRoomVarCommand = 12;

    /// <summary>S→C <see cref="Protocol.RoomVarsChangedEvent"/>. Full set on join, changed subset afterwards.</summary>
    public const byte RoomVarsChangedEvent = 13;

    /// <summary>C→S <see cref="Protocol.ResyncCommand"/> (empty payload). "My known set is untrustworthy."</summary>
    public const byte ResyncCommand = 14;

    /// <summary>C→S <see cref="Protocol.SetClientPrefsCommand"/>. Hidden tabs and send-rate division.</summary>
    public const byte SetClientPrefsCommand = 15;

    /// <summary>S→C <see cref="Protocol.HostChangedEvent"/>. Host migration announcement.</summary>
    public const byte HostChangedEvent = 16;

    // ── State sync: entities (64–127) ─────────────────────────────────────────

    /// <summary>C→S <see cref="Protocol.SpawnEntityRequest"/>.</summary>
    public const byte SpawnEntityRequest = 64;

    /// <summary>S→C <see cref="Protocol.SpawnEntityResponse"/>.</summary>
    public const byte SpawnEntityResponse = 65;

    /// <summary>C→S <see cref="Protocol.DespawnEntityCommand"/>.</summary>
    public const byte DespawnEntityCommand = 66;

    /// <summary>C→S hot plane, hand-packed. See <see cref="HotWire.WriteEntityUpdatePacketHeader"/>.</summary>
    public const byte EntityUpdatePacket = 67;

    /// <summary>S→C hot plane, hand-packed. See <see cref="HotWire.WriteSnapshotPacketHeader"/>.</summary>
    public const byte SnapshotPacket = 68;

    /// <summary>S→C hot plane, hand-packed. See <see cref="HotWire.WriteDeltaPacketHeader"/>.</summary>
    public const byte DeltaPacket = 69;

    /// <summary>C→S <see cref="Protocol.SetEntityPropsCommand"/>.</summary>
    public const byte SetEntityPropsCommand = 70;

    /// <summary>S→C <see cref="Protocol.EntityPropsChangedEvent"/>.</summary>
    public const byte EntityPropsChangedEvent = 71;

    // ── Signals: networked game events (128–191) ───────────────────────────────

    /// <summary>C→S <see cref="Protocol.EmitSignalCommand"/>.</summary>
    public const byte EmitSignalCommand = 128;

    /// <summary>S→C <see cref="Protocol.SignalEvent"/>. One frame per recipient.</summary>
    public const byte SignalEvent = 129;

    /// <summary>
    /// S→C hot plane, hand-packed; <b>no class</b>. AOI-scoped signals batched into one packet per
    /// client per tick and flushed with that client's delta. See
    /// <see cref="HotWire.WriteSignalBatchPacketHeader"/>.
    /// </summary>
    public const byte SignalBatchPacket = 130;

    /// <summary>
    /// True for the four hand-packed packets (67/68/69/130). Those must never go through MemoryPack.
    /// </summary>
    public static bool IsHotPlane(byte typeId)
        => typeId is EntityUpdatePacket or SnapshotPacket or DeltaPacket or SignalBatchPacket;

    /// <summary>
    /// Human-readable name for logs and metrics labels. Returns <c>"Unknown"</c> for unmapped ids
    /// (never allocates for a known id).
    /// </summary>
    /// <remarks>
    /// This feeds Prometheus label values, so every known id must return its exact class name — the same
    /// spelling as its constant here and as the type in <c>docs/protocol.md</c>.
    /// </remarks>
    public static string GetName(byte typeId) => typeId switch
    {
        HelloCommand => nameof(HelloCommand),
        WelcomeEvent => nameof(WelcomeEvent),
        RejectedEvent => nameof(RejectedEvent),
        PingCommand => nameof(PingCommand),
        PongEvent => nameof(PongEvent),
        PeerJoinedEvent => nameof(PeerJoinedEvent),
        PeerLeftEvent => nameof(PeerLeftEvent),
        RoomInfoEvent => nameof(RoomInfoEvent),
        SendChatCommand => nameof(SendChatCommand),
        ChatMessageEvent => nameof(ChatMessageEvent),
        LeaveCommand => nameof(LeaveCommand),
        SetRoomVarCommand => nameof(SetRoomVarCommand),
        RoomVarsChangedEvent => nameof(RoomVarsChangedEvent),
        ResyncCommand => nameof(ResyncCommand),
        SetClientPrefsCommand => nameof(SetClientPrefsCommand),
        HostChangedEvent => nameof(HostChangedEvent),
        SpawnEntityRequest => nameof(SpawnEntityRequest),
        SpawnEntityResponse => nameof(SpawnEntityResponse),
        DespawnEntityCommand => nameof(DespawnEntityCommand),
        EntityUpdatePacket => nameof(EntityUpdatePacket),
        SnapshotPacket => nameof(SnapshotPacket),
        DeltaPacket => nameof(DeltaPacket),
        SetEntityPropsCommand => nameof(SetEntityPropsCommand),
        EntityPropsChangedEvent => nameof(EntityPropsChangedEvent),
        EmitSignalCommand => nameof(EmitSignalCommand),
        SignalEvent => nameof(SignalEvent),
        SignalBatchPacket => nameof(SignalBatchPacket),
        _ => "Unknown",
    };
}
