using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.SpawnEntityRequest"/>. Asks the room to create an entity
/// owned by the sender. Answered by exactly one <see cref="SpawnEntityResponse"/>.
/// </summary>
/// <remarks>
/// The initial transform is carried <b>quantized</b>, not as floats: the quantized integers are the
/// replicated values everywhere, so a spawn must not be able to introduce a value the delta plane could
/// not have expressed. Convert with <see cref="WorldQuantizer"/> before sending, and reject a
/// non-finite input at that edge rather than here.
/// </remarks>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class SpawnEntityRequest
{
    /// <summary>Client-chosen correlation id, echoed in the response. Not a net id.</summary>
    [MemoryPackOrder(0)]
    public uint RequestId { get; set; }

    /// <summary>
    /// Entity kind, indexing the build's prefab table. Checked against the room's allowlist; an unknown
    /// kind is refused with <see cref="RejectCode.KindNotAllowed"/> because it would fault every
    /// observer's scene code.
    /// </summary>
    [MemoryPackOrder(1)]
    public ushort Kind { get; set; }

    /// <summary>Quantized initial world X.</summary>
    [MemoryPackOrder(2)]
    public ushort QX { get; set; }

    /// <summary>Quantized initial world Y.</summary>
    [MemoryPackOrder(3)]
    public ushort QY { get; set; }

    /// <summary>Quantized initial rotation, 256 steps per turn.</summary>
    [MemoryPackOrder(4)]
    public byte QRot { get; set; }

    /// <summary>Quantized initial velocity along X, 1/8 u/s per step.</summary>
    [MemoryPackOrder(5)]
    public short QVx { get; set; }

    /// <summary>Quantized initial velocity along Y, 1/8 u/s per step.</summary>
    [MemoryPackOrder(6)]
    public short QVy { get; set; }

    /// <summary>
    /// Initial flags: ownership policy in bits 0-1, app bits in 3-7. See <see cref="EntityFlags"/>.
    /// The policy declared here is what decides the entity's fate when its owner leaves.
    /// </summary>
    [MemoryPackOrder(7)]
    public byte Flags { get; set; }

    /// <summary>Optional opaque cold props (JSON bytes by convention). Size-capped by the server.</summary>
    [MemoryPackOrder(8)]
    public byte[]? Props { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public SpawnEntityRequest()
    {
    }
}
