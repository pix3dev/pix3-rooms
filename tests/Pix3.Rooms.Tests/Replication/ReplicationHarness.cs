using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Replication;

namespace Pix3.Rooms.Tests.Replication;

/// <summary>One decoded <c>SnapshotPacket</c>, in a form assertions can hold onto.</summary>
internal sealed record SnapshotView(
    ushort Seq,
    uint ServerTick,
    bool Final,
    IReadOnlyList<(uint NetId, EntityWireState State)> Records);

/// <summary>One decoded <c>DeltaPacket</c>, in a form assertions can hold onto.</summary>
internal sealed record DeltaView(
    ushort Seq,
    uint ServerTick,
    IReadOnlyList<ushort> Removed,
    IReadOnlyList<(uint NetId, EntityWireState State)> Enters,
    IReadOnlyList<(ushort Slot, byte Mask, EntityWireState State)> Updates);

/// <summary>One decoded <c>SignalBatchPacket</c> entry.</summary>
internal sealed record SignalView(uint SenderClientId, string Name, byte[] Payload);

/// <summary>
/// Decodes hot frames into ordinary objects. The decoders are the ones in
/// <see cref="HotWire"/> — deliberately, because they are pinned byte for byte by the golden vectors,
/// so a behaviour test that reads through them is still reading the published wire format.
/// </summary>
internal static class Wire
{
    public static SnapshotView ReadSnapshot(ReadOnlySpan<byte> frame)
    {
        Assert.True(
            HotWire.TryReadSnapshotPacket(frame, out ushort seq, out uint tick, out byte flags, out int count, out ReadOnlySpan<byte> records),
            "frame is not a well-formed SnapshotPacket");

        List<(uint, EntityWireState)> parsed = [];
        for (int i = 0; i < count; i++)
        {
            Assert.True(HotWire.TryReadFullRecord(records.Slice(i * HotWire.FullRecordSize), out uint netId, out EntityWireState state));
            parsed.Add((netId, state));
        }

        return new SnapshotView(seq, tick, FrameFlags.IsFinal(flags), parsed);
    }

    public static DeltaView ReadDelta(ReadOnlySpan<byte> frame)
    {
        Assert.True(HotWire.TryReadDeltaPacket(frame, out DeltaPacketSections sections), "frame is not a well-formed DeltaPacket");

        List<ushort> removed = [];
        for (int i = 0; i < sections.RemovedCount; i++)
        {
            Assert.True(sections.TryGetRemovedSlot(i, out ushort slot));
            removed.Add(slot);
        }

        List<(uint, EntityWireState)> enters = [];
        for (int i = 0; i < sections.EnterCount; i++)
        {
            Assert.True(sections.TryGetEnterRecord(i, out uint netId, out EntityWireState state));
            enters.Add((netId, state));
        }

        List<(ushort, byte, EntityWireState)> updates = [];
        int cursor = 0;
        for (int i = 0; i < sections.UpdateCount; i++)
        {
            Assert.True(sections.TryReadNextUpdate(ref cursor, out ushort slot, out byte mask, out EntityWireState state));
            updates.Add((slot, mask, state));
        }

        return new DeltaView(sections.Seq, sections.ServerTick, removed, enters, updates);
    }

    public static IReadOnlyList<SignalView> ReadSignalBatch(ReadOnlySpan<byte> frame)
    {
        Assert.True(HotWire.TryReadSignalBatchPacket(frame, out SignalBatchSections sections));

        List<SignalView> parsed = [];
        int cursor = 0;
        for (int i = 0; i < sections.Count; i++)
        {
            Assert.True(sections.TryReadNextEntry(ref cursor, out uint sender, out ReadOnlySpan<byte> name, out ReadOnlySpan<byte> payload));
            parsed.Add(new SignalView(sender, System.Text.Encoding.UTF8.GetString(name), payload.ToArray()));
        }

        return parsed;
    }
}

/// <summary>
/// Drives one <see cref="RoomReplication"/> the way a room's tick loop does: mutate, <c>Tick</c>, then
/// per client write a frame and commit or roll it back.
/// </summary>
/// <remarks>
/// The harness deliberately owns the commit discipline, because getting it wrong is the failure mode
/// the two-phase design exists to prevent, and a test that quietly skipped a <c>Commit</c> would report
/// a fabricated bug. Everything it exposes goes through <see cref="IRoomReplication"/> plus the
/// diagnostics the core publishes for exactly this purpose.
/// </remarks>
internal sealed class ReplicationHarness
{
    private readonly byte[] _buffer;
    private uint _serverTick;

    public ReplicationHarness(ReplicationOptions options)
    {
        Options = options;
        Replication = new RoomReplication(options);
        _buffer = new byte[Math.Max(options.MaxBytesPerClientPerTick, 64)];
    }

    public ReplicationOptions Options { get; }

    public RoomReplication Replication { get; }

    public WorldQuantizer Quantizer => Replication.Quantizer;

    public uint ServerTick => _serverTick;

    /// <summary>
    /// Default options for a behaviour test: a small world of entities around the origin, an AOI a test
    /// can straddle by hand, and a speed cap high enough that a deliberate 500-unit jump is not also a
    /// speed violation (the tests that care about the speed counter lower it themselves).
    /// </summary>
    public static ReplicationOptions DefaultOptions(int maxEntities = 256, int maxPlayers = 8) => new()
    {
        MaxEntities = maxEntities,
        MaxPlayers = maxPlayers,
        AoiRadius = 100f,
        MaxEntitySpeed = 1_000_000f,
    };

    public static ReplicationHarness Create(int maxEntities = 256, int maxPlayers = 8)
        => new(DefaultOptions(maxEntities, maxPlayers));

    // ── Mutation ──────────────────────────────────────────────────────────────

    public uint Spawn(uint ownerId, float x, float y, ushort kind = 1, byte flags = 0)
    {
        EntityWireState state = default;
        Assert.True(Quantizer.TryQuantizePosition(x, y, out state.QX, out state.QY));
        state.Flags = flags;

        Assert.True(Replication.TrySpawn(ownerId, kind, state, out uint netId, out RejectCode reject), $"spawn refused: {reject}");
        return netId;
    }

    public void MoveTo(uint netId, uint ownerId, float x, float y)
    {
        EntityWireState state = default;
        Assert.True(Quantizer.TryQuantizePosition(x, y, out state.QX, out state.QY));

        Assert.True(Replication.TryApplyOwnedUpdate(netId, ownerId, DeltaMask.X | DeltaMask.Y, state));
    }

    /// <summary>Adds a subscriber and binds its AOI focus to an entity it owns, the normal arrangement.</summary>
    public uint JoinWithAvatar(uint clientId, float x, float y)
    {
        Replication.AddSubscriber(clientId);
        uint netId = Spawn(clientId, x, y);
        Replication.BindSubscriberFocus(clientId, netId);
        return netId;
    }

    public uint Tick()
    {
        _serverTick++;
        Replication.Tick(_serverTick);
        return _serverTick;
    }

    // ── Frame assembly ────────────────────────────────────────────────────────

    /// <summary>
    /// Writes this client's snapshot or delta for the current tick and commits it, returning the frame
    /// bytes — or <c>null</c> when the client had nothing to receive.
    /// </summary>
    public byte[]? PumpHot(uint clientId, bool commit = true)
    {
        int written = Replication.IsSnapshotPending(clientId)
            ? Replication.WriteSnapshot(clientId, _buffer, out PendingKnownSetCommit handle)
            : Replication.WriteDelta(clientId, _buffer, out handle);

        if (written == 0)
        {
            Replication.Rollback(handle);   // no-op for an empty handle; keeps the discipline honest
            return null;
        }

        if (commit)
        {
            Replication.Commit(handle);
        }
        else
        {
            Replication.Rollback(handle);
        }

        return _buffer.AsSpan(0, written).ToArray();
    }

    public byte[]? PumpSignals(uint clientId, bool commit = true)
    {
        int written = Replication.WriteSignalBatch(clientId, _buffer, out PendingKnownSetCommit handle);
        if (written == 0)
        {
            Replication.Rollback(handle);
            return null;
        }

        if (commit)
        {
            Replication.Commit(handle);
        }
        else
        {
            Replication.Rollback(handle);
        }

        return _buffer.AsSpan(0, written).ToArray();
    }

    public SnapshotView PumpSnapshot(uint clientId)
    {
        byte[]? frame = PumpHot(clientId);
        Assert.NotNull(frame);
        return Wire.ReadSnapshot(frame);
    }

    public DeltaView PumpDelta(uint clientId)
    {
        byte[]? frame = PumpHot(clientId);
        Assert.NotNull(frame);
        return Wire.ReadDelta(frame);
    }

    /// <summary>
    /// Runs snapshot frames until the client's known set is complete, asserting that exactly the last one
    /// carries <c>Final</c>. Returns every frame, so a split snapshot can be inspected.
    /// </summary>
    public IReadOnlyList<SnapshotView> DrainSnapshot(uint clientId, int maxFrames = 64)
    {
        List<SnapshotView> frames = [];
        while (Replication.IsSnapshotPending(clientId))
        {
            Assert.True(frames.Count < maxFrames, "snapshot never completed");
            SnapshotView view = PumpSnapshot(clientId);
            frames.Add(view);
            Assert.Equal(view.Final, !Replication.IsSnapshotPending(clientId));
        }

        Assert.NotEmpty(frames);
        Assert.True(frames[^1].Final);
        Assert.All(frames.Take(frames.Count - 1), f => Assert.False(f.Final));
        return frames;
    }

    /// <summary>Ticks once and drains this client's snapshot, the usual "get a joiner established" step.</summary>
    public IReadOnlyList<SnapshotView> TickAndEstablish(uint clientId)
    {
        Tick();
        return DrainSnapshot(clientId);
    }
}
