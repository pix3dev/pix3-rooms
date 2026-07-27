using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// S→C, TypeId <see cref="MessageTypeIds.HostChangedEvent"/>. The room promoted a new host (the
/// longest-present member) and reassigned the <see cref="OwnershipPolicy.Shared"/> entities to it.
/// </summary>
/// <remarks>
/// The id is reserved and this class exists now so clients can be written against it; server emission
/// lands with host migration in Phase 1-2. Because an unknown TypeId is ignored rather than fatal, a
/// client may handle it before the server ever sends it.
/// </remarks>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class HostChangedEvent
{
    /// <summary>The newly promoted host, or 0 when the room has no members left.</summary>
    [MemoryPackOrder(0)]
    public uint HostClientId { get; set; }

    /// <summary>The host being replaced, or 0 when there was none.</summary>
    [MemoryPackOrder(1)]
    public uint PreviousHostClientId { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public HostChangedEvent()
    {
    }
}
