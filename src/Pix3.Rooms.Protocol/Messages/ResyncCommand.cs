using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.ResyncCommand"/>. Empty payload, same shape as
/// <see cref="LeaveCommand"/>: "my known set is untrustworthy, re-send it".
/// </summary>
/// <remarks>
/// The cure for the one lossy link in this system, which is our own bounded send queue rather than the
/// network. The server clears this client's known-set bitset and re-sends a full snapshot on the next
/// tick through the existing continuation cursor, so one primitive covers queue overflow, tab refocus,
/// reconnect and future datagram loss. Quota-limited to 2/s. A client sends it when it sees a
/// <c>Seq</c> gap.
/// </remarks>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class ResyncCommand
{
    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public ResyncCommand()
    {
    }
}
