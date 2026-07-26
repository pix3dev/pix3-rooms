namespace Pix3.Rooms.Server.Replication;

/// <summary>
/// Per-client AOI bookkeeping, owned by <see cref="RoomReplication"/> and pooled across joins so a
/// churnful room never reallocates its ~10 KB of bitsets.
/// </summary>
/// <remarks>
/// <para><b>Data layout.</b> <see cref="Known"/> is the set of slots this client currently has a
/// full record for; <see cref="KnownGeneration"/> remembers <i>which generation</i> of each slot the
/// client knows. The pair is what keeps slot reuse honest: a client is deemed to know an entity only
/// when the known bit is set <i>and</i> the generation matches, so an entity that despawns and a
/// different entity that reuses its slot are never confused — the client first gets a removed id
/// packed with the generation it knew, then a fresh full record.</para>
/// <para><b>Scratch.</b> <see cref="VisibleInner"/>/<see cref="VisibleOuter"/> receive the grid query
/// each frame; <see cref="KnownBeforeEnters"/> snapshots the known-set between the exit and enter
/// passes so an entity that both enters AOI and is dirty in the same tick is sent as a full record
/// only, never full + delta.</para>
/// <para>The snapshot continuation cursor deliberately lives with the caller
/// (<c>IRoomReplication.WriteSnapshot</c>'s <c>ref int</c>), not here — one source of truth.</para>
/// </remarks>
public sealed class SubscriberState
{
    /// <summary>Client this state is currently bound to; meaningful only while checked out of the pool.</summary>
    public uint ClientId;

    /// <summary>AOI focus X (normally the client's avatar). (0,0) until the room sets it.</summary>
    public float FocusX;

    /// <summary>AOI focus Y.</summary>
    public float FocusY;

    /// <summary>Slots the client has been sent a full record for (subject to generation match).</summary>
    public readonly SlotBitset Known;

    /// <summary>Generation of each known slot at the time its full record was sent; 0 = not known.</summary>
    public readonly ushort[] KnownGeneration;

    /// <summary>Scratch: slots within the AOI enter radius this frame.</summary>
    public readonly SlotBitset VisibleInner;

    /// <summary>Scratch: slots within the AOI exit radius (enter + hysteresis) this frame.</summary>
    public readonly SlotBitset VisibleOuter;

    /// <summary>Scratch: <see cref="Known"/> as of after the exit pass, before enters mark it.</summary>
    public readonly SlotBitset KnownBeforeEnters;

    /// <summary>Allocates all per-client storage for a table of <paramref name="maxEntities"/> slots.</summary>
    public SubscriberState(int maxEntities)
    {
        Known = new SlotBitset(maxEntities);
        KnownGeneration = new ushort[maxEntities];
        VisibleInner = new SlotBitset(maxEntities);
        VisibleOuter = new SlotBitset(maxEntities);
        KnownBeforeEnters = new SlotBitset(maxEntities);
    }

    /// <summary>Rebinds a pooled instance to a new client with a clean slate.</summary>
    public void Reset(uint clientId)
    {
        ClientId = clientId;
        FocusX = 0f;
        FocusY = 0f;
        Known.Clear();
        Array.Clear(KnownGeneration, 0, KnownGeneration.Length);
        // Visible/scratch sets are cleared by every query/frame pass; no need to touch them here.
    }
}
