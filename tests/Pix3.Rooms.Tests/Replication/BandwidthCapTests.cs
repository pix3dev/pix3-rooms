using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Replication;

namespace Pix3.Rooms.Tests.Replication;

/// <summary>
/// The three caps that turn the bandwidth ceiling from a hope into a guarantee, exercised under the
/// dogpile they exist for: 600 players stacked on one point, where an AOI <i>radius</i> bounds nothing.
/// </summary>
/// <remarks>
/// From <c>docs/architecture.md</c> → Tests: "<c>MaxVisibleEntities</c>, <c>MaxEntersPerTick</c> (with
/// carry) and <c>MaxBytesPerClientPerTick</c> all hold under a 600-entity dogpile", and the frame-size
/// cap "neither loses nor duplicates entities".
/// </remarks>
public class BandwidthCapTests
{
    /// <summary>The shipped defaults, from the bandwidth-caps table in <c>docs/protocol.md</c>.</summary>
    private static ReplicationOptions ProductionOptions() => new()
    {
        MaxEntities = 4096,
        MaxPlayers = 64,
        AoiRadius = 1200f,
        MaxVisibleEntities = 64,
        MaxEntersPerTick = 24,
        MaxBytesPerClientPerTick = 1100,
        MaxEntitySpeed = 1_000_000f,
    };

    /// <summary>Replays a client's frames into the known set the client itself would be holding.</summary>
    private sealed class KnownSet
    {
        private readonly Dictionary<ushort, uint> _slotToNetId = [];

        public int Count => _slotToNetId.Count;

        public int PeakCount { get; private set; }

        public int TotalEnters { get; private set; }

        public void ApplySnapshot(SnapshotView snapshot)
        {
            foreach ((uint netId, _) in snapshot.Records)
            {
                Enter(netId);
            }
        }

        public void ApplyDelta(DeltaView delta)
        {
            // Removals first, exactly as the client must apply them — that is what makes slot reuse safe.
            foreach (ushort slot in delta.Removed)
            {
                Assert.True(_slotToNetId.Remove(slot), $"removal for slot {slot}, which was not known");
            }

            foreach ((uint netId, _) in delta.Enters)
            {
                Enter(netId);
            }

            foreach ((ushort slot, _, _) in delta.Updates)
            {
                Assert.True(_slotToNetId.ContainsKey(slot), $"update for slot {slot} without a prior full record");
            }
        }

        private void Enter(uint netId)
        {
            ushort slot = (ushort)NetId.Slot(netId);
            Assert.False(_slotToNetId.ContainsKey(slot), $"slot {slot} entered twice without a removal in between");
            _slotToNetId.Add(slot, netId);
            TotalEnters++;
            PeakCount = Math.Max(PeakCount, _slotToNetId.Count);
        }
    }

    [Fact]
    public void A_six_hundred_entity_dogpile_never_exceeds_any_of_the_three_caps()
    {
        ReplicationHarness h = new(ProductionOptions());
        uint avatar = h.JoinWithAvatar(1, 0f, 0f);

        // 599 more entities inside a 40-unit blob: everything is inside everything's enter radius, which
        // is precisely the case an AOI radius does not bound.
        uint[] crowd = new uint[599];
        for (int i = 0; i < crowd.Length; i++)
        {
            float angle = i * 0.37f;
            crowd[i] = h.Spawn(2, MathF.Cos(angle) * (i % 40), MathF.Sin(angle) * (i % 40));
        }

        KnownSet known = new();
        h.Tick();
        foreach (SnapshotView frame in h.DrainSnapshot(1))
        {
            known.ApplySnapshot(frame);
        }

        for (int tick = 0; tick < 30; tick++)
        {
            // Keep the pile churning so enters, exits and updates all stay in play.
            for (int i = 0; i < crowd.Length; i += 7)
            {
                float angle = (i * 0.37f) + (tick * 0.05f);
                h.MoveTo(crowd[i], 2, MathF.Cos(angle) * (i % 40), MathF.Sin(angle) * (i % 40));
            }

            h.MoveTo(avatar, 1, tick % 5, 0f);
            h.Tick();

            if (h.PumpHot(1) is not { } frame)
            {
                continue;
            }

            Assert.True(
                frame.Length <= h.Options.MaxBytesPerClientPerTick,
                $"frame of {frame.Length} B exceeds the {h.Options.MaxBytesPerClientPerTick} B budget");

            if (frame[0] == MessageTypeIds.SnapshotPacket)
            {
                known.ApplySnapshot(Wire.ReadSnapshot(frame));
                continue;
            }

            DeltaView delta = Wire.ReadDelta(frame);
            Assert.True(
                delta.Enters.Count <= h.Options.MaxEntersPerTick,
                $"{delta.Enters.Count} enters exceeds MaxEntersPerTick");
            known.ApplyDelta(delta);
        }

        // k-nearest is applied to the EXIT set, so it bounds what the client RETAINS, not just what it
        // may enter. Capping only the enter radius would let the known set creep up to the whole crowd.
        Assert.True(
            known.PeakCount <= h.Options.MaxVisibleEntities,
            $"known set peaked at {known.PeakCount}, above MaxVisibleEntities");
        Assert.True(known.Count > 0);
        Assert.True(h.Replication.CappedVisibilityCount > 0, "the k-nearest cap never engaged");
    }

    [Fact]
    public void Enters_beyond_the_per_tick_cap_are_carried_rather_than_lost()
    {
        ReplicationHarness h = new(ProductionOptions());
        h.JoinWithAvatar(1, 0f, 0f);

        // Establish the client against an empty world first: a snapshot is bounded by bytes only, so the
        // enter cap is a delta-path rule and needs a client that is already past its snapshot.
        h.Tick();
        SnapshotView empty = h.PumpSnapshot(1);
        Assert.True(empty.Final);
        Assert.Single(empty.Records);   // its own avatar

        for (int i = 0; i < 63; i++)
        {
            h.Spawn(2, 10f + (i * 0.5f), 0f);
        }

        KnownSet known = new();
        List<int> enterCounts = [];
        for (int tick = 0; tick < 10 && known.TotalEnters < 63; tick++)
        {
            h.Tick();
            if (h.PumpHot(1) is not { } frame)
            {
                continue;
            }

            DeltaView delta = Wire.ReadDelta(frame);
            enterCounts.Add(delta.Enters.Count);
            Assert.True(delta.Enters.Count <= 24);
            known.ApplyDelta(delta);
        }

        // 63 entities at 24 per tick: three ticks, nothing lost, nothing entered twice.
        Assert.Equal(63, known.TotalEnters);
        Assert.Equal(new[] { 24, 24, 15 }, enterCounts);
        Assert.True(h.Replication.EnterCarryCount > 0, "the carry cursor never engaged");
    }

    [Fact]
    public void The_enter_carry_cursor_rotates_so_low_slots_do_not_win_every_tick()
    {
        // Without a carry cursor the same low slots would win the budget every tick and the high ones
        // would starve forever.
        ReplicationHarness h = new(ProductionOptions() with { MaxEntersPerTick = 4 });
        h.JoinWithAvatar(1, 0f, 0f);
        h.Tick();
        h.PumpSnapshot(1);

        for (int i = 0; i < 20; i++)
        {
            h.Spawn(2, 10f + (i * 0.5f), 0f);
        }

        List<ushort> firstFrameSlots = [];
        List<ushort> secondFrameSlots = [];
        h.Tick();
        foreach ((uint netId, _) in h.PumpDelta(1).Enters)
        {
            firstFrameSlots.Add((ushort)NetId.Slot(netId));
        }

        h.Tick();
        foreach ((uint netId, _) in h.PumpDelta(1).Enters)
        {
            secondFrameSlots.Add((ushort)NetId.Slot(netId));
        }

        Assert.Equal(4, firstFrameSlots.Count);
        Assert.Equal(4, secondFrameSlots.Count);
        Assert.Empty(firstFrameSlots.Intersect(secondFrameSlots));
        Assert.True(secondFrameSlots.Min() > firstFrameSlots.Max(), "the second frame did not resume past the first");
    }

    [Fact]
    public void A_snapshot_is_split_across_self_contained_frames_and_only_the_last_is_Final()
    {
        ReplicationHarness h = new(ProductionOptions());
        h.JoinWithAvatar(1, 0f, 0f);
        for (int i = 0; i < 63; i++)
        {
            h.Spawn(2, 10f + (i * 0.5f), 0f);
        }

        h.Tick();
        IReadOnlyList<SnapshotView> frames = h.DrainSnapshot(1);

        // 64 records × 20 B + 10 B header does not fit in 1100 B, so this must split.
        Assert.True(frames.Count > 1);
        Assert.Equal(64, frames.Sum(f => f.Records.Count));
        Assert.All(frames.Take(frames.Count - 1), f => Assert.False(f.Final));
        Assert.True(frames[^1].Final);

        // Seq advances once per emitted frame, including the intermediate ones.
        Assert.Equal(Enumerable.Range(0, frames.Count).Select(i => (ushort)i).ToArray(), frames.Select(f => f.Seq).ToArray());
        Assert.True(h.Replication.SplitSnapshotFrameCount > 0);
    }

    [Fact]
    public void The_k_nearest_cap_keeps_the_nearest_entities_not_an_arbitrary_subset()
    {
        ReplicationHarness h = new(ProductionOptions() with { MaxVisibleEntities = 8 });
        h.JoinWithAvatar(1, 0f, 0f);

        // Spawned far-to-near, so a cap that just took the first k would keep exactly the wrong ones.
        uint[] byDistance = new uint[30];
        for (int i = 0; i < byDistance.Length; i++)
        {
            byDistance[i] = h.Spawn(2, 1000f - (i * 30f), 0f);
        }

        h.Tick();
        IReadOnlyList<SnapshotView> frames = h.DrainSnapshot(1);
        uint[] delivered = frames.SelectMany(f => f.Records).Select(r => r.NetId).ToArray();

        Assert.Equal(8, delivered.Length);
        // The avatar sits at the origin, so the eight nearest are the last seven spawned plus the avatar.
        Assert.Contains(byDistance[^1], delivered);
        Assert.DoesNotContain(byDistance[0], delivered);
        Assert.True(h.Replication.CappedVisibilityCount > 0);
    }

    [Fact]
    public void Frames_stay_within_the_byte_budget_even_when_the_destination_is_larger()
    {
        // limit = min(destination, budget): a caller handing over a bigger buffer must not widen a frame
        // past the one-MSS / one-datagram guarantee.
        ReplicationHarness h = new(ProductionOptions() with { MaxBytesPerClientPerTick = 200 });
        h.JoinWithAvatar(1, 0f, 0f);
        for (int i = 0; i < 40; i++)
        {
            h.Spawn(2, 10f + (i * 0.5f), 0f);
        }

        h.Tick();
        byte[] oversized = new byte[4096];
        int written = h.Replication.WriteSnapshot(1, oversized, out PendingKnownSetCommit handle);
        h.Replication.Commit(handle);

        Assert.True(written > 0);
        Assert.True(written <= 200, $"frame of {written} B ignored the 200 B budget");
    }
}
