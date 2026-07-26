using System.Diagnostics;
using System.Runtime.CompilerServices;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// Structure-of-arrays entity storage for one room, fixed capacity, everything allocated once.
/// </summary>
/// <remarks>
/// <para><b>Data layout.</b> Entity fields live in parallel arrays indexed by <i>slot</i>
/// (<see cref="X"/>, <see cref="Y"/>, <see cref="Rot"/>, <see cref="Vx"/>, <see cref="Vy"/>,
/// <see cref="Kind"/>, <see cref="OwnerId"/>, <see cref="Flags"/>, <see cref="Generation"/>,
/// <see cref="LastChangedTick"/>), so per-tick loops stream through memory instead of chasing
/// objects. The arrays are exposed directly — "C on top of C#" — and are only ever touched by the
/// owning room's tick thread.</para>
/// <para><b>Slot lifecycle.</b> A free-slot stack gives O(1) spawn/despawn. <see cref="Generation"/>
/// starts at 1 (so <see cref="NetId.None"/> is never live) and is bumped on despawn; a slot whose
/// generation would exceed <see cref="NetId.MaxGeneration"/> is retired instead of wrapped, so a
/// <c>(slot, generation)</c> pair is never reused in a room's lifetime. Every inbound
/// <c>netId</c> goes through <see cref="TryResolve"/>: slot range, aliveness and generation match.</para>
/// <para><b>Dense iteration.</b> <see cref="LiveSlots"/> is a packed array of live slots maintained by
/// swap-remove, so encoder loops never scan dead slots.</para>
/// <para><b>Dirty tracking.</b> <see cref="Dirty"/> is a bitset of slots changed since the last
/// <see cref="ClearDirty"/> plus a per-slot accumulated <see cref="DeltaMask"/> in
/// <see cref="DirtyMask"/>. Despawn scrubs the slot's dirty state so a reused slot never inherits
/// a stale mask.</para>
/// <para><b>Owner index.</b> An intrusive doubly-linked list threaded through
/// <c>_ownerNext/_ownerPrev</c> (heads in a dictionary) makes <see cref="RemoveOwner"/> O(owned)
/// with zero allocation — no per-owner list objects.</para>
/// </remarks>
public sealed class EntityTable
{
    /// <summary>Slot capacity, fixed at construction.</summary>
    public readonly int Capacity;

    /// <summary>World X per slot.</summary>
    public readonly float[] X;

    /// <summary>World Y per slot.</summary>
    public readonly float[] Y;

    /// <summary>Rotation per slot, radians.</summary>
    public readonly float[] Rot;

    /// <summary>X velocity per slot.</summary>
    public readonly float[] Vx;

    /// <summary>Y velocity per slot.</summary>
    public readonly float[] Vy;

    /// <summary>Application-defined kind per slot (immutable after spawn).</summary>
    public readonly ushort[] Kind;

    /// <summary>Owning client id per slot; 0 = server-owned.</summary>
    public readonly uint[] OwnerId;

    /// <summary>Application-defined flag bits per slot.</summary>
    public readonly byte[] Flags;

    /// <summary>Current reuse generation per slot; starts at 1, bumped on despawn.</summary>
    public readonly ushort[] Generation;

    /// <summary>Tick stamp of the slot's last mutation (spawn or update). Telemetry only.</summary>
    public readonly uint[] LastChangedTick;

    /// <summary>Aliveness per slot.</summary>
    public readonly SlotBitset Alive;

    /// <summary>Slots mutated since the last <see cref="ClearDirty"/>.</summary>
    public readonly SlotBitset Dirty;

    /// <summary>Accumulated <see cref="DeltaMask"/> bits per dirty slot; 0 when clean.</summary>
    public readonly byte[] DirtyMask;

    private readonly int[] _dense;            // packed live slots
    private readonly int[] _denseIndexOfSlot; // slot -> index into _dense
    private int _liveCount;

    private readonly int[] _freeStack;
    private int _freeTop;

    private readonly Dictionary<uint, int> _ownerHead; // ownerId -> first owned slot (-1 never stored)
    private readonly int[] _ownerNext;
    private readonly int[] _ownerPrev;

    private int _retiredSlotCount;

    /// <summary>Allocates all storage for <paramref name="capacity"/> slots up front.</summary>
    public EntityTable(int capacity)
    {
        if (capacity < 1 || capacity > NetId.MaxSlot + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, $"must be 1..{NetId.MaxSlot + 1}");
        }

        Capacity = capacity;
        X = new float[capacity];
        Y = new float[capacity];
        Rot = new float[capacity];
        Vx = new float[capacity];
        Vy = new float[capacity];
        Kind = new ushort[capacity];
        OwnerId = new uint[capacity];
        Flags = new byte[capacity];
        Generation = new ushort[capacity];
        LastChangedTick = new uint[capacity];
        Alive = new SlotBitset(capacity);
        Dirty = new SlotBitset(capacity);
        DirtyMask = new byte[capacity];

        _dense = new int[capacity];
        _denseIndexOfSlot = new int[capacity];
        _freeStack = new int[capacity];
        _ownerNext = new int[capacity];
        _ownerPrev = new int[capacity];
        _ownerHead = new Dictionary<uint, int>(capacity: 64);

        for (int slot = 0; slot < capacity; slot++)
        {
            Generation[slot] = 1;                          // generations start at 1 — NetId.None stays dead
            _freeStack[slot] = capacity - 1 - slot;        // pop order: slot 0 first (cosmetic)
            _denseIndexOfSlot[slot] = -1;
            _ownerNext[slot] = -1;
            _ownerPrev[slot] = -1;
        }

        _freeTop = capacity;
    }

    /// <summary>Live entity count.</summary>
    public int LiveCount => _liveCount;

    /// <summary>Packed live slots in dense (encoder) order. Order changes on despawn (swap-remove).</summary>
    public ReadOnlySpan<int> LiveSlots => _dense.AsSpan(0, _liveCount);

    /// <summary>Slots permanently retired because their generation space was exhausted.</summary>
    public int RetiredSlotCount => _retiredSlotCount;

    /// <summary>True when the slot currently holds a live entity.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAlive(int slot) => Alive.Get(slot);

    /// <summary>Packs the slot's current wire id.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint PackId(int slot) => NetId.Pack(slot, Generation[slot]);

    /// <summary>
    /// Validates an inbound wire id: slot in range, slot alive, generation matches. A stale id (the
    /// slot was despawned, or despawned and reused by a different entity) resolves to false — this is
    /// where the generation scheme pays off.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryResolve(uint netId, out int slot)
    {
        slot = NetId.Slot(netId);
        if ((uint)slot >= (uint)Capacity || !Alive.Get(slot))
        {
            return false;
        }

        return Generation[slot] == (ushort)NetId.Generation(netId);
    }

    /// <summary>
    /// Spawns an entity into a free slot. False when the table is full (including slots lost to
    /// generation retirement). <paramref name="kind"/> and <paramref name="ownerId"/> are the
    /// authoritative values — the same-named fields inside <paramref name="state"/> are ignored.
    /// Spawn does NOT mark the slot dirty: propagation to clients happens through the AOI
    /// enter/full-record path, never through a delta.
    /// </summary>
    public bool TrySpawn(uint ownerId, ushort kind, in EntityWireState state, uint tick, out uint netId)
    {
        if (_freeTop == 0)
        {
            netId = NetId.None;
            return false;
        }

        int slot = _freeStack[--_freeTop];

        X[slot] = state.X;
        Y[slot] = state.Y;
        Rot[slot] = state.Rot;
        Vx[slot] = state.Vx;
        Vy[slot] = state.Vy;
        Kind[slot] = kind;
        OwnerId[slot] = ownerId;
        Flags[slot] = state.Flags;
        LastChangedTick[slot] = tick;

        Alive.Set(slot);
        _dense[_liveCount] = slot;
        _denseIndexOfSlot[slot] = _liveCount;
        _liveCount++;

        // Owner index: push at head of the owner's intrusive chain.
        if (_ownerHead.TryGetValue(ownerId, out int head))
        {
            _ownerPrev[head] = slot;
            _ownerNext[slot] = head;
        }
        else
        {
            _ownerNext[slot] = -1;
        }

        _ownerPrev[slot] = -1;
        _ownerHead[ownerId] = slot;

        netId = NetId.Pack(slot, Generation[slot]);
        return true;
    }

    /// <summary>
    /// Despawns the entity in <paramref name="slot"/> (must be alive): bumps the generation (or
    /// retires the slot), swap-removes it from the dense list, unlinks it from its owner chain and
    /// scrubs its dirty state so the next occupant starts clean.
    /// </summary>
    public void Despawn(int slot)
    {
        Debug.Assert(Alive.Get(slot), "despawn of a dead slot");

        Alive.Unset(slot);

        // Scrub dirty state — a delta must never be encoded for a dead slot, and a reused slot
        // must not inherit the previous entity's accumulated mask.
        Dirty.Unset(slot);
        DirtyMask[slot] = 0;

        // Dense list swap-remove: O(1), keeps live slots packed for encoder loops.
        int denseIndex = _denseIndexOfSlot[slot];
        int lastSlot = _dense[_liveCount - 1];
        _dense[denseIndex] = lastSlot;
        _denseIndexOfSlot[lastSlot] = denseIndex;
        _denseIndexOfSlot[slot] = -1;
        _liveCount--;

        // Owner chain unlink.
        uint owner = OwnerId[slot];
        int prev = _ownerPrev[slot];
        int next = _ownerNext[slot];
        if (prev != -1)
        {
            _ownerNext[prev] = next;
        }
        else if (next != -1)
        {
            _ownerHead[owner] = next;
        }
        else
        {
            _ownerHead.Remove(owner);
        }

        if (next != -1)
        {
            _ownerPrev[next] = prev;
        }

        _ownerNext[slot] = -1;
        _ownerPrev[slot] = -1;

        // Generation bump. At the ceiling the slot is retired (never pushed back) — wrapping would
        // let a stale id address a brand-new entity.
        ushort generation = Generation[slot];
        if (generation >= NetId.MaxGeneration)
        {
            _retiredSlotCount++;
            return;
        }

        Generation[slot] = (ushort)(generation + 1);
        _freeStack[_freeTop++] = slot;
    }

    /// <summary>
    /// Merges the masked fields of <paramref name="state"/> into the slot's arrays (unmasked fields
    /// survive untouched, mirroring <see cref="EntityWireState.Apply"/>) and accumulates the mask into
    /// the slot's dirty state. Signal bits (<see cref="DeltaMask.ColdDirty"/>,
    /// <see cref="DeltaMask.Teleport"/>) carry no fields but are still accumulated so they reach the
    /// encoded delta record.
    /// </summary>
    public void ApplyUpdate(int slot, byte mask, in EntityWireState state, uint tick)
    {
        if ((mask & DeltaMask.X) != 0)
        {
            X[slot] = state.X;
        }

        if ((mask & DeltaMask.Y) != 0)
        {
            Y[slot] = state.Y;
        }

        if ((mask & DeltaMask.Rot) != 0)
        {
            Rot[slot] = state.Rot;
        }

        if ((mask & DeltaMask.Vx) != 0)
        {
            Vx[slot] = state.Vx;
        }

        if ((mask & DeltaMask.Vy) != 0)
        {
            Vy[slot] = state.Vy;
        }

        if ((mask & DeltaMask.Flags) != 0)
        {
            Flags[slot] = state.Flags;
        }

        MarkDirty(slot, mask, tick);
    }

    /// <summary>Accumulates dirty state for a slot without touching field values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkDirty(int slot, byte mask, uint tick)
    {
        if (mask == DeltaMask.None)
        {
            return;   // legal no-op: nothing to replicate
        }

        DirtyMask[slot] |= mask;
        Dirty.Set(slot);
        LastChangedTick[slot] = tick;
    }

    /// <summary>
    /// Clears all accumulated dirty state. Called at the end of each replication tick, after the
    /// dirty slots have been encoded once into scratch.
    /// </summary>
    public void ClearDirty()
    {
        // Sparse clear: zero only the masks that are actually set, then wipe the bitset.
        foreach (int slot in Dirty.EnumerateSetBits())
        {
            DirtyMask[slot] = 0;
        }

        Dirty.Clear();
    }

    /// <summary>First slot of an owner's chain, or -1 when the owner owns nothing.</summary>
    public int GetOwnerHead(uint ownerId) => _ownerHead.TryGetValue(ownerId, out int head) ? head : -1;

    /// <summary>Next slot in an owner chain, or -1 at the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetOwnerNext(int slot) => _ownerNext[slot];

    /// <summary>Copies a slot's fields into a wire-state struct (for record encoding).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void FillWireState(int slot, out EntityWireState state)
    {
        state = default;
        state.X = X[slot];
        state.Y = Y[slot];
        state.Rot = Rot[slot];
        state.Vx = Vx[slot];
        state.Vy = Vy[slot];
        state.Kind = Kind[slot];
        state.OwnerId = OwnerId[slot];
        state.Flags = Flags[slot];
    }
}
