using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.RejectedEvent"/>. Sent before every close whose reason is
/// known, so the client can show a real message instead of a bare socket error.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class RejectedEvent
{
    /// <summary>
    /// A <see cref="RejectCode"/> value. Kept as <see cref="ushort"/> on the wire so an unknown code
    /// survives a round trip.
    /// </summary>
    [MemoryPackOrder(0)]
    public ushort Code { get; set; }

    /// <summary>Human-readable detail. Never contains secrets or stack traces.</summary>
    [MemoryPackOrder(1)]
    public string Message { get; set; } = "";

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public RejectedEvent()
    {
    }
}
