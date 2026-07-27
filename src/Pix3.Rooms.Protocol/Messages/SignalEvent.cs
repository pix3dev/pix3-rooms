using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.SignalEvent"/>. One relayed signal with the sender stamped by
/// the server, delivered one frame per recipient. This is the
/// <see cref="SignalTarget.AllPeers"/>/<see cref="SignalTarget.SinglePeer"/> path; AOI-scoped signals
/// travel in a <c>SignalBatchPacket</c> instead.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class SignalEvent
{
    /// <summary>Sender, resolved from the session, never copied from the command payload.</summary>
    [MemoryPackOrder(0)]
    public uint SenderClientId { get; set; }

    /// <summary>The signal name as sent.</summary>
    [MemoryPackOrder(1)]
    public string Name { get; set; } = "";

    /// <summary>The payload as sent.</summary>
    [MemoryPackOrder(2)]
    public byte[] Payload { get; set; } = [];

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public SignalEvent()
    {
    }
}
