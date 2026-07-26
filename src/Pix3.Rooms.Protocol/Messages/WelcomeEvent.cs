using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.WelcomeEvent"/>. Handshake accepted; the client is now a
/// room member. Followed by <see cref="RoomVarsEvent"/>, then one or more SnapshotFrames.
/// </summary>
[MemoryPackable]
public sealed partial class WelcomeEvent
{
    /// <summary>Room-unique, monotonic id assigned to this connection.</summary>
    public uint ClientId { get; set; }

    /// <summary>The room actually joined.</summary>
    public string RoomId { get; set; } = "";

    /// <summary>Room tick rate, for client-side interpolation buffers.</summary>
    public byte TickHz { get; set; }

    /// <summary>Server wall clock in Unix milliseconds, for clock-offset estimation.</summary>
    public long ServerTimeMs { get; set; }

    /// <summary>Tick the join was processed on.</summary>
    public uint ServerTick { get; set; }

    /// <summary>Area-of-interest radius in world units the server will filter by.</summary>
    public float AoiRadius { get; set; }

    /// <summary>Room capacity.</summary>
    public ushort MaxPlayers { get; set; }

    /// <summary>Echo of the negotiated protocol version.</summary>
    public ushort ProtocolVersion { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public WelcomeEvent()
    {
    }
}
