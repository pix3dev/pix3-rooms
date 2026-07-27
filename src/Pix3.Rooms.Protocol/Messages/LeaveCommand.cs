using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.LeaveCommand"/>. Empty payload: a voluntary goodbye, so
/// peers see <see cref="LeaveReason.LeftVoluntarily"/> instead of a plain disconnect.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class LeaveCommand
{
    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public LeaveCommand()
    {
    }
}
