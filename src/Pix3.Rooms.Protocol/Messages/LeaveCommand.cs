using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.LeaveRequest"/>. Empty payload — a voluntary goodbye, so
/// peers see <c>LeaveReason.LeftVoluntarily</c> instead of a plain disconnect.
/// </summary>
[MemoryPackable]
public sealed partial class LeaveRequest
{
    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public LeaveRequest()
    {
    }
}
