using System.Buffers.Binary;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Replication;

namespace Pix3.Rooms.Tests.Replication;

/// <summary>
/// The recovery half of the contract: two-phase known-set commit, <c>Seq</c>, <c>ResyncCommand</c> and
/// hidden clients. From <c>docs/architecture.md</c> → Tests: "a failed hot-lane enqueue rolls the known
/// set back and leaves <c>Seq</c> unchanged; <c>ResyncCommand</c> produces a complete snapshot ending
/// with <c>Final</c>; a hidden client receives no hot frames and re-snapshots on un-hide".
/// </summary>
/// <remarks>
/// A rolled-back frame is what a hot-lane send-queue overflow looks like from here — the lossy link is
/// our own bounded queue, not the network. These are the tests that would have caught v1's actual bug:
/// it flipped known-set bits while composing a frame the queue could then drop, leaving a permanent
/// ghost entity (removal dropped) or a permanently invisible one (enter dropped).
/// </remarks>
public class RecoveryTests
{
    /// <summary>Every hot frame carries its <c>u16 Seq</c> at offset 1, whichever kind it is.</summary>
    private static ushort SeqOf(byte[] frame) => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1));

    [Fact]
    public void A_rolled_back_snapshot_leaves_Seq_and_the_snapshot_cursor_untouched()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        h.JoinWithAvatar(1, 0f, 0f);
        h.Tick();

        byte[] scratch = new byte[1100];
        int written = h.Replication.WriteSnapshot(1, scratch, out PendingKnownSetCommit handle);
        Assert.True(written > 0);
        Assert.Equal(0, handle.Seq);
        Assert.True(handle.IsFinalSnapshotFrame);

        h.Replication.Rollback(handle);

        // The client never learns the frame existed: no Seq gap to detect, and the snapshot is still owed.
        Assert.Equal(0, h.Replication.PeekSeq(1));
        Assert.True(h.Replication.IsSnapshotPending(1));

        h.Tick();
        SnapshotView resent = h.PumpSnapshot(1);

        Assert.Equal(0, resent.Seq);
        Assert.Single(resent.Records);
        Assert.Equal(1, h.Replication.PeekSeq(1));
    }

    [Fact]
    public void A_rolled_back_delta_re_sends_its_enter_on_the_next_tick()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        h.JoinWithAvatar(1, 0f, 0f);
        h.TickAndEstablish(1);
        uint stranger = h.Spawn(2, 20f, 0f);

        h.Tick();
        byte[]? dropped = h.PumpHot(1, commit: false);
        Assert.NotNull(dropped);
        Assert.Single(Wire.ReadDelta(dropped).Enters);

        // Seq did not advance, and the entity is still un-known — an enter does not self-heal, so this is
        // exactly the case the two-phase commit exists for.
        Assert.Equal(1, h.Replication.PeekSeq(1));

        h.Tick();
        DeltaView retry = h.PumpDelta(1);

        Assert.Equal(1, retry.Seq);
        (uint netId, _) = Assert.Single(retry.Enters);
        Assert.Equal(stranger, netId);
        Assert.Equal(2, h.Replication.PeekSeq(1));
    }

    [Fact]
    public void A_rolled_back_delta_re_sends_its_removal_on_the_next_tick()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        h.JoinWithAvatar(1, 0f, 0f);
        uint stranger = h.Spawn(2, 20f, 0f);
        h.TickAndEstablish(1);

        Assert.True(h.Replication.TryDespawn(stranger, 2, out _));
        h.Tick();
        byte[]? dropped = h.PumpHot(1, commit: false);
        Assert.NotNull(dropped);
        Assert.Single(Wire.ReadDelta(dropped).Removed);

        h.Tick();
        DeltaView retry = h.PumpDelta(1);

        // A dropped removal would otherwise leave a permanent ghost entity on the client.
        Assert.Equal(NetId.Slot(stranger), Assert.Single(retry.Removed));
    }

    [Fact]
    public void A_rolled_back_delta_re_offers_its_update_from_current_state()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        uint avatar = h.JoinWithAvatar(1, 0f, 0f);
        h.TickAndEstablish(1);

        h.MoveTo(avatar, 1, 10f, 0f);
        h.Tick();
        Assert.NotNull(h.PumpHot(1, commit: false));

        // The entity then stops moving. A dropped absolute value only self-heals if the entity keeps
        // changing, so the owed update has to be re-offered from CURRENT state.
        h.Tick();
        DeltaView retry = h.PumpDelta(1);

        (ushort slot, _, EntityWireState state) = Assert.Single(retry.Updates);
        Assert.Equal(NetId.Slot(avatar), slot);
        Assert.True(h.Quantizer.TryQuantizePosition(10f, 0f, out ushort expectedQX, out _));
        Assert.Equal(expectedQX, state.QX);

        h.Tick();
        Assert.Null(h.PumpHot(1));   // debt settled: nothing further is owed
    }

    [Fact]
    public void A_resync_clears_the_known_set_and_produces_a_complete_snapshot_ending_with_Final()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        h.JoinWithAvatar(1, 0f, 0f);
        for (uint i = 0; i < 5; i++)
        {
            h.Spawn(2, 10f + i, 0f);
        }

        h.TickAndEstablish(1);
        ushort seqBefore = h.Replication.PeekSeq(1);

        h.Replication.RequestResync(1);
        Assert.True(h.Replication.IsSnapshotPending(1));

        h.Tick();
        IReadOnlyList<SnapshotView> frames = h.DrainSnapshot(1);

        // Six entities re-introduced from scratch — the known set is rebuilt, never assumed.
        Assert.Equal(6, frames.Sum(f => f.Records.Count));
        Assert.True(frames[^1].Final);
        Assert.Equal(seqBefore, frames[0].Seq);   // Seq continues from where it was: no gap, no reset
        Assert.Equal(1, h.Replication.ResyncCount);
    }

    [Fact]
    public void A_hidden_client_receives_no_hot_frames_and_its_Seq_stands_still()
    {
        // A backgrounded tab cannot drain a 20 Hz stream, it buffers it.
        ReplicationHarness h = ReplicationHarness.Create();
        uint avatar = h.JoinWithAvatar(1, 0f, 0f);
        h.TickAndEstablish(1);
        ushort seqBefore = h.Replication.PeekSeq(1);

        h.Replication.SetSubscriberHidden(1, true);
        for (int i = 1; i <= 5; i++)
        {
            h.MoveTo(avatar, 1, i * 5f, 0f);
            h.Tick();
            Assert.Null(h.PumpHot(1));
        }

        Assert.Equal(seqBefore, h.Replication.PeekSeq(1));
        Assert.True(h.Replication.HiddenSuppressedFrameCount >= 5);

        // Un-hiding is a resync by definition: while hidden the known set became a fiction.
        h.Replication.SetSubscriberHidden(1, false);
        Assert.True(h.Replication.IsSnapshotPending(1));

        h.Tick();
        SnapshotView snapshot = h.PumpSnapshot(1);

        Assert.Equal(seqBefore, snapshot.Seq);
        Assert.Single(snapshot.Records);
    }

    [Fact]
    public void Re_hiding_an_already_hidden_client_does_not_force_a_resync()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        h.JoinWithAvatar(1, 0f, 0f);
        h.TickAndEstablish(1);

        h.Replication.SetSubscriberHidden(1, false);   // it was never hidden
        h.Replication.SetSubscriberHidden(1, false);

        Assert.False(h.Replication.IsSnapshotPending(1));
        Assert.Equal(0, h.Replication.ResyncCount);
    }

    [Fact]
    public void Seq_advances_by_exactly_one_per_emitted_frame_and_never_for_a_quiet_tick()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        uint avatar = h.JoinWithAvatar(1, 0f, 0f);
        h.TickAndEstablish(1);

        List<ushort> seqs = [];
        for (int tick = 0; tick < 20; tick++)
        {
            // Move on every third tick only, so most ticks produce no frame at all.
            if (tick % 3 == 0)
            {
                h.MoveTo(avatar, 1, tick + 1f, 0f);
            }

            h.Tick();
            byte[]? frame = h.PumpHot(1);
            if (frame is not null)
            {
                seqs.Add(SeqOf(frame));
            }
        }

        Assert.NotEmpty(seqs);
        // Contiguous from the snapshot's successor: a gap is the client's desync detector, so an emitted
        // frame must be the only thing that advances it.
        Assert.Equal(Enumerable.Range(1, seqs.Count).Select(i => (ushort)i).ToArray(), seqs.ToArray());
    }

    [Fact]
    public void A_send_divisor_serves_the_client_on_every_nth_tick_only()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        uint avatar = h.JoinWithAvatar(1, 0f, 0f);
        h.TickAndEstablish(1);
        h.Replication.SetSubscriberSendDivisor(1, 4);

        int frames = 0;
        for (int tick = 0; tick < 16; tick++)
        {
            h.MoveTo(avatar, 1, tick + 1f, 0f);
            h.Tick();
            if (h.PumpHot(1) is not null)
            {
                frames++;
            }
        }

        Assert.Equal(4, frames);   // 16 ticks ÷ 4
        Assert.True(h.Replication.DivisorSkippedFrameCount >= 12);
    }

    [Theory]
    [InlineData(0, 1)]     // 0 and 1 both mean "every tick" on the wire
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(200, 8)]   // clamped to [1, 8]
    public void A_send_divisor_is_clamped_to_the_documented_range(int requested, int effective)
    {
        ReplicationHarness h = ReplicationHarness.Create();
        uint avatar = h.JoinWithAvatar(1, 0f, 0f);
        h.TickAndEstablish(1);
        h.Replication.SetSubscriberSendDivisor(1, (byte)requested);

        int frames = 0;
        const int ticks = 32;
        for (int tick = 0; tick < ticks; tick++)
        {
            h.MoveTo(avatar, 1, tick + 1f, 0f);
            h.Tick();
            if (h.PumpHot(1) is not null)
            {
                frames++;
            }
        }

        Assert.Equal(ticks / effective, frames);
    }

    [Fact]
    public void An_update_section_cut_short_by_the_byte_budget_is_a_debt_not_a_loss()
    {
        // The load-bearing truncation rule: an entity that moves once and then stops would otherwise leave
        // a client that got truncated on exactly that tick permanently stale.
        ReplicationOptions options = ReplicationHarness.DefaultOptions() with { MaxBytesPerClientPerTick = 60 };
        ReplicationHarness h = new(options);
        h.JoinWithAvatar(1, 0f, 0f);

        uint[] crowd = new uint[20];
        for (int i = 0; i < crowd.Length; i++)
        {
            crowd[i] = h.Spawn(2, 10f + i, 0f);
        }

        h.Tick();
        IReadOnlyList<SnapshotView> snapshot = h.DrainSnapshot(1);
        Assert.True(snapshot.Count > 1, "the byte budget should have split this snapshot");
        Assert.Equal(21, snapshot.Sum(f => f.Records.Count));

        // One move each, then everything goes still.
        for (int i = 0; i < crowd.Length; i++)
        {
            h.MoveTo(crowd[i], 2, 10f + i, 5f);
        }

        Dictionary<ushort, ushort> deliveredQY = [];
        for (int tick = 0; tick < 20; tick++)
        {
            h.Tick();
            if (h.PumpHot(1) is not { } frame)
            {
                break;
            }

            foreach ((ushort slot, byte mask, EntityWireState state) in Wire.ReadDelta(frame).Updates)
            {
                Assert.Equal(DeltaMask.Y, mask & DeltaMask.Y);
                deliveredQY[slot] = state.QY;
            }
        }

        Assert.True(h.Replication.TruncatedUpdateSectionCount > 0, "the budget should have cut a section short");
        Assert.True(h.Quantizer.TryQuantizePosition(0f, 5f, out _, out ushort expectedQY));

        // Every entity's new position eventually arrived, from current state, and then the stream went quiet.
        Assert.Equal(crowd.Length, deliveredQY.Count);
        Assert.All(deliveredQY.Values, qy => Assert.Equal(expectedQY, qy));

        h.Tick();
        Assert.Null(h.PumpHot(1));
    }

    [Fact]
    public void A_removal_section_cut_short_by_the_byte_budget_is_re_detected_next_tick()
    {
        ReplicationOptions options = ReplicationHarness.DefaultOptions() with { MaxBytesPerClientPerTick = 40 };
        ReplicationHarness h = new(options);
        h.JoinWithAvatar(1, 0f, 0f);

        uint[] crowd = new uint[20];
        for (int i = 0; i < crowd.Length; i++)
        {
            crowd[i] = h.Spawn(2, 10f + i, 0f);
        }

        h.Tick();
        h.DrainSnapshot(1);

        foreach (uint netId in crowd)
        {
            Assert.True(h.Replication.TryDespawn(netId, 2, out _));
        }

        HashSet<ushort> removed = [];
        for (int tick = 0; tick < 30; tick++)
        {
            h.Tick();
            if (h.PumpHot(1) is not { } frame)
            {
                break;
            }

            foreach (ushort slot in Wire.ReadDelta(frame).Removed)
            {
                Assert.True(removed.Add(slot), $"slot {slot} was removed twice");
            }
        }

        Assert.True(h.Replication.TruncatedRemovalSectionCount > 0, "the budget should have cut a section short");
        Assert.Equal(crowd.Length, removed.Count);
    }
}
