using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// A <c>netId</c> is an opaque <see cref="uint"/> handle for one entity inside one room, packed as
/// <c>slot | (generation &lt;&lt; 20)</c>: the low 20 bits index a slot in the entity table, the high
/// 12 bits are the slot's reuse generation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reuse rule.</b> When an entity is despawned its slot goes back to the free list; the next
/// entity placed in that slot must be packed with <c>generation + 1</c>. A
/// <c>(slot, generation)</c> pair is therefore never reused within a room's lifetime, so a stale
/// client reference resolves to "unknown entity" instead of silently addressing a new one. A slot
/// whose generation would exceed <see cref="MaxGeneration"/> must be retired, not wrapped.
/// </para>
/// <para>
/// <b>Generations start at 1.</b> That keeps <see cref="None"/> (0) permanently unusable as a live
/// id, so 0 is a safe "no entity" sentinel in tables, queues and wire payloads.
/// </para>
/// <para>Clients must treat the value as opaque: they never compute slots or generations.</para>
/// </remarks>
public static class NetId
{
    /// <summary>Bits reserved for the slot index.</summary>
    public const int SlotBits = 20;

    /// <summary>Bits reserved for the reuse generation.</summary>
    public const int GenerationBits = 12;

    /// <summary>Mask isolating the slot bits.</summary>
    public const uint SlotMask = (1u << SlotBits) - 1u;          // 0x000FFFFF

    /// <summary>Mask isolating the generation bits, already shifted into place.</summary>
    public const uint GenerationMask = ~SlotMask;                // 0xFFF00000

    /// <summary>Largest addressable slot index.</summary>
    public const int MaxSlot = (1 << SlotBits) - 1;              // 1_048_575

    /// <summary>Largest generation a slot may reach before it must be retired.</summary>
    public const int MaxGeneration = (1 << GenerationBits) - 1;  // 4_095

    /// <summary>Reserved sentinel meaning "no entity". Never a valid live id (generations start at 1).</summary>
    public const uint None = 0u;

    /// <summary>
    /// Packs a slot and a generation into a wire id. Out-of-range inputs are masked rather than
    /// throwing, because this sits on the spawn path; callers must respect
    /// <see cref="MaxSlot"/>/<see cref="MaxGeneration"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Pack(int slot, int generation)
    {
        Debug.Assert(slot >= 0 && slot <= MaxSlot, "slot out of range");
        Debug.Assert(generation >= 1 && generation <= MaxGeneration, "generation out of range (must start at 1)");
        return ((uint)slot & SlotMask) | (((uint)generation & (uint)MaxGeneration) << SlotBits);
    }

    /// <summary>Extracts the entity-table slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Slot(uint netId) => (int)(netId & SlotMask);

    /// <summary>Extracts the slot's reuse generation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Generation(uint netId) => (int)(netId >> SlotBits);

    /// <summary>
    /// True when the id could name a live entity, i.e. its generation is not 0. Cheap first-pass
    /// validation for client-supplied ids; the entity table still has to confirm the generation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValid(uint netId) => (netId & GenerationMask) != 0u;
}
