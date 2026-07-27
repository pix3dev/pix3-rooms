using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.RejectEvent"/>. Sent before every close whose reason is
/// known, so the client can show a real message instead of a bare socket error.
/// </summary>
[MemoryPackable]
public sealed partial class RejectEvent
{
    /// <summary>A <c>RejectCode</c> value. Kept as <see cref="ushort"/> on the wire so unknown codes survive a round trip.</summary>
    public ushort Code { get; set; }

    /// <summary>Human-readable detail. Never contains secrets or stack traces.</summary>
    public string Message { get; set; } = "";

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public RejectEvent()
    {
    }
}
