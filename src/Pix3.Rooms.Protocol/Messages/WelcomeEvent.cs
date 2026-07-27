using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.WelcomeEvent"/>. Handshake accepted; the client is now a
/// room member. Followed by <see cref="RoomVarsChangedEvent"/>, then one or more
/// <c>SnapshotPacket</c>s (the last with <see cref="FrameFlags.Final"/>).
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class WelcomeEvent
{
    /// <summary>Room-unique, monotonic id for this session. Preserved across a successful resume.</summary>
    [MemoryPackOrder(0)]
    public uint ClientId { get; set; }

    /// <summary>The room actually joined.</summary>
    [MemoryPackOrder(1)]
    public string RoomId { get; set; } = "";

    /// <summary>Room tick rate, for client-side interpolation buffers.</summary>
    [MemoryPackOrder(2)]
    public byte TickHz { get; set; }

    /// <summary>Server wall clock in Unix milliseconds at send, for clock-offset estimation.</summary>
    [MemoryPackOrder(3)]
    public long ServerTimeMs { get; set; }

    /// <summary>Tick the join was processed on.</summary>
    [MemoryPackOrder(4)]
    public uint ServerTick { get; set; }

    /// <summary>AOI <b>enter</b> radius in world units. Exit is 1.25 x this (hysteresis).</summary>
    [MemoryPackOrder(5)]
    public float AoiRadius { get; set; }

    /// <summary>Room member cap.</summary>
    [MemoryPackOrder(6)]
    public ushort MaxPlayers { get; set; }

    /// <summary>
    /// The <b>negotiated</b> session version, <c>min(client, Current)</c>. Both sides speak it for the
    /// whole session.
    /// </summary>
    [MemoryPackOrder(7)]
    public ushort ProtocolVersion { get; set; }

    /// <summary>World-bounds origin X this room quantizes against. See <see cref="WorldQuantizer"/>.</summary>
    [MemoryPackOrder(8)]
    public float WorldOriginX { get; set; }

    /// <summary>World-bounds origin Y this room quantizes against. See <see cref="WorldQuantizer"/>.</summary>
    [MemoryPackOrder(9)]
    public float WorldOriginY { get; set; }

    /// <summary>World side length this room quantizes against. See <see cref="WorldQuantizer"/>.</summary>
    [MemoryPackOrder(10)]
    public float WorldSize { get; set; }

    /// <summary>
    /// The room's authority mode: <c>0</c> relay (client authority, Level 1), <c>1</c> authoritative.
    /// Part of the wire contract, which is what makes Level-2 validation a zero-byte upgrade.
    /// </summary>
    [MemoryPackOrder(11)]
    public byte Mode { get; set; }

    /// <summary>
    /// Hard cap on entities this client can be told about at once: a sizing hint for its receive
    /// tables, and the k-nearest cap the server applies per tick.
    /// </summary>
    [MemoryPackOrder(12)]
    public ushort MaxVisibleEntities { get; set; }

    /// <summary>Current host (longest-present member), or 0 when none.</summary>
    [MemoryPackOrder(13)]
    public uint HostClientId { get; set; }

    /// <summary>
    /// 16 bytes, <b>regenerated on every connect</b> so a leaked key cannot be replayed for a later
    /// session. Present it in <see cref="HelloCommand.ResumeKey"/> to resume.
    /// </summary>
    [MemoryPackOrder(14)]
    public byte[] ResumeKey { get; set; } = [];

    /// <summary>
    /// True when this welcome answered a <b>successful resume</b>: the client's entities are still
    /// alive and its known set was rebuilt, so it must not reset its local state.
    /// </summary>
    [MemoryPackOrder(15)]
    public bool Resumed { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public WelcomeEvent()
    {
    }
}
