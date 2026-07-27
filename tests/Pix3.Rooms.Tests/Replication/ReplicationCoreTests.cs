using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Replication;

namespace Pix3.Rooms.Tests.Replication;

/// <summary>
/// The replication core's permanent regression properties, from <c>docs/architecture.md</c> → Tests:
/// no delta without a prior full record; generation reuse rejected; enter+dirty in one tick sends a
/// full record only; encode count equals dirty-entity count rather than dirty × subscribers; removals
/// precede reuse within a frame.
/// </summary>
/// <remarks>
/// These are behaviour tests against <see cref="IRoomReplication"/> and the decoded wire bytes — no
/// sockets, no rooms, no internals. That is the seam's own promise ("must stay unit-testable with no
/// sockets"), and it is also what keeps the tests from being a mirror of the implementation.
/// </remarks>
public class ReplicationCoreTests
{
    [Fact]
    public void A_joiner_is_refused_a_delta_until_its_snapshot_has_been_committed()
    {
        // Update records are slot-addressed, so a delta that arrived before the snapshot establishing
        // slot → netId would be undecodable.
        ReplicationHarness h = ReplicationHarness.Create();
        h.JoinWithAvatar(1, 0f, 0f);
        h.Tick();

        Assert.True(h.Replication.IsSnapshotPending(1));
        byte[] scratch = new byte[1100];
        Assert.Equal(0, h.Replication.WriteDelta(1, scratch, out PendingKnownSetCommit refused));
        Assert.True(refused.IsEmpty);

        SnapshotView snapshot = h.PumpSnapshot(1);

        Assert.True(snapshot.Final);
        Assert.Single(snapshot.Records);
        Assert.False(h.Replication.IsSnapshotPending(1));
    }

    [Fact]
    public void An_entity_the_client_knows_is_delivered_as_an_update_not_a_second_full_record()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        uint avatar = h.JoinWithAvatar(1, 0f, 0f);
        h.TickAndEstablish(1);

        h.MoveTo(avatar, 1, 10f, 0f);
        h.Tick();
        DeltaView delta = h.PumpDelta(1);

        Assert.Empty(delta.Enters);
        Assert.Empty(delta.Removed);
        (ushort slot, byte mask, EntityWireState state) = Assert.Single(delta.Updates);
        Assert.Equal(NetId.Slot(avatar), slot);
        Assert.Equal(DeltaMask.X, mask & DeltaMask.X);
        Assert.True(h.Quantizer.TryQuantizePosition(10f, 0f, out ushort expectedQX, out _));
        Assert.Equal(expectedQX, state.QX);
    }

    [Fact]
    public void An_entity_that_enters_AOI_while_dirty_is_sent_as_a_full_record_only()
    {
        // Full + delta for the same entity in one frame would apply the delta on top of a record that
        // already carried it — and, worse, would imply the client can decode a slot it is only learning
        // about in this very frame.
        ReplicationHarness h = ReplicationHarness.Create();
        h.JoinWithAvatar(1, 0f, 0f);
        uint stranger = h.Spawn(2, 500f, 0f);   // far outside the 100-unit AOI
        h.TickAndEstablish(1);

        h.MoveTo(stranger, 2, 10f, 0f);         // dirty AND entering, in the same tick
        h.Tick();
        DeltaView delta = h.PumpDelta(1);

        (uint enteredNetId, EntityWireState entered) = Assert.Single(delta.Enters);
        Assert.Equal(stranger, enteredNetId);
        Assert.Empty(delta.Updates);
        Assert.True(h.Quantizer.TryQuantizePosition(10f, 0f, out ushort expectedQX, out _));
        Assert.Equal(expectedQX, entered.QX);   // the full record carries the post-move position
    }

    [Fact]
    public void An_entity_that_leaves_the_exit_radius_is_removed_by_slot()
    {
        // Hysteresis: enter at AoiRadius, exit only beyond 1.25 × it. 110 is inside the band, 200 is out.
        ReplicationHarness h = ReplicationHarness.Create();
        h.JoinWithAvatar(1, 0f, 0f);
        uint stranger = h.Spawn(2, 50f, 0f);
        h.TickAndEstablish(1);

        h.MoveTo(stranger, 2, 110f, 0f);
        h.Tick();
        DeltaView inBand = h.PumpDelta(1);
        Assert.Empty(inBand.Removed);           // still known: past the enter radius, inside the exit radius

        h.MoveTo(stranger, 2, 200f, 0f);
        h.Tick();
        DeltaView gone = h.PumpDelta(1);

        Assert.Equal(NetId.Slot(stranger), Assert.Single(gone.Removed));
        Assert.Empty(gone.Enters);
    }

    [Fact]
    public void A_stale_netId_is_rejected_after_its_slot_has_been_reused()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        h.Replication.AddSubscriber(2);
        uint first = h.Spawn(2, 0f, 0f);

        Assert.True(h.Replication.TryDespawn(first, 2, out _));
        uint second = h.Spawn(2, 0f, 0f);

        // Same slot, next generation — and never the same pair twice within the room's lifetime.
        Assert.Equal(NetId.Slot(first), NetId.Slot(second));
        Assert.Equal(NetId.Generation(first) + 1, NetId.Generation(second));
        Assert.NotEqual(first, second);

        EntityWireState state = default;
        long unknownBefore = h.Replication.UnknownEntityCount;

        Assert.False(h.Replication.TryApplyOwnedUpdate(first, 2, DeltaMask.X, state));
        Assert.False(h.Replication.TryDespawn(first, 2, out RejectCode reject));

        Assert.Equal(RejectCode.BadRequest, reject);
        Assert.Equal(unknownBefore + 2, h.Replication.UnknownEntityCount);
        Assert.Equal(1, h.Replication.EntityCount);
    }

    [Fact]
    public void A_reused_slot_is_removed_before_it_is_re_entered_in_the_same_frame()
    {
        // The rule that makes u16 slot addressing safe on an ordered stream: within a frame, removals are
        // applied before enters, so a slot's removal always precedes any reuse of it.
        ReplicationHarness h = ReplicationHarness.Create();
        h.JoinWithAvatar(1, 0f, 0f);
        uint victim = h.Spawn(2, 20f, 0f);
        h.TickAndEstablish(1);

        Assert.True(h.Replication.TryDespawn(victim, 2, out _));
        uint successor = h.Spawn(3, 20f, 0f);
        Assert.Equal(NetId.Slot(victim), NetId.Slot(successor));

        h.Tick();
        DeltaView delta = h.PumpDelta(1);

        Assert.Equal(NetId.Slot(victim), Assert.Single(delta.Removed));
        (uint enteredNetId, EntityWireState entered) = Assert.Single(delta.Enters);
        Assert.Equal(successor, enteredNetId);
        Assert.Equal(3u, entered.OwnerId);
        // Wire order is removals → enters → updates, so the removal physically precedes the enter that
        // reuses the slot; the decoded sections above only confirm both are in the same frame.
    }

    [Fact]
    public void One_record_is_encoded_per_dirty_entity_no_matter_how_many_clients_receive_it()
    {
        // Encode once, memcpy many. The failure this guards is the obvious implementation: serialize per
        // recipient, which at 600 clients is 600× the work for identical bytes.
        ReplicationHarness h = ReplicationHarness.Create();
        uint[] avatars = new uint[5];
        for (uint client = 1; client <= 5; client++)
        {
            avatars[client - 1] = h.JoinWithAvatar(client, client * 2f, 0f);
        }

        h.Tick();
        for (uint client = 1; client <= 5; client++)
        {
            h.DrainSnapshot(client);
        }

        // Five distinct entities encoded as full records, not five per client.
        Assert.Equal(5, h.Replication.LastTickFullRecordsEncoded);

        for (uint client = 1; client <= 5; client++)
        {
            h.MoveTo(avatars[client - 1], client, (client * 2f) + 1f, 0f);
        }

        h.Tick();
        Assert.Equal(5, h.Replication.LastTickDirtyCount);

        int deliveredRecords = 0;
        for (uint client = 1; client <= 5; client++)
        {
            deliveredRecords += h.PumpDelta(client).Updates.Count;
        }

        // 25 records delivered, 5 encoded: the difference is exactly the point.
        Assert.Equal(25, deliveredRecords);
        Assert.Equal(5, h.Replication.LastTickDirtyCount);
    }

    [Fact]
    public void Re_sending_an_identical_position_marks_nothing_dirty()
    {
        // Dirty detection compares the quantized integers. Comparing floats would keep an idle entity
        // dirty forever on sub-quantum noise — and an idle entity is most of a room.
        ReplicationHarness h = ReplicationHarness.Create();
        uint avatar = h.JoinWithAvatar(1, 3.5f, -7.25f);
        h.TickAndEstablish(1);

        h.MoveTo(avatar, 1, 3.5f, -7.25f);
        Assert.Equal(1, h.Replication.NoOpUpdateCount);

        h.Tick();

        Assert.Equal(0, h.Replication.LastTickDirtyCount);
        Assert.Null(h.PumpHot(1));   // a tick with nothing for a client produces no frame at all
    }

    [Fact]
    public void Sub_quantum_movement_produces_no_frame()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        uint avatar = h.JoinWithAvatar(1, 0f, 0f);
        h.TickAndEstablish(1);

        // A hundredth of a quantum, twenty times: still the same integer, so still nothing to send.
        const float nudge = 4096f / 65535f / 100f;
        for (int i = 1; i <= 20; i++)
        {
            h.MoveTo(avatar, 1, i * nudge, 0f);
        }

        h.Tick();

        Assert.Equal(0, h.Replication.LastTickDirtyCount);
        Assert.Null(h.PumpHot(1));
    }

    [Fact]
    public void An_update_for_an_entity_the_sender_does_not_own_is_refused_and_counted()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        h.Replication.AddSubscriber(2);
        uint victim = h.Spawn(1, 0f, 0f);

        EntityWireState state = default;
        Assert.False(h.Replication.TryApplyOwnedUpdate(victim, ownerId: 2, DeltaMask.X, state));
        Assert.False(h.Replication.TryDespawn(victim, requesterId: 2, out RejectCode reject));

        Assert.Equal(RejectCode.NotEntityOwner, reject);
        Assert.Equal(2, h.Replication.OwnershipViolationCount);
        Assert.Equal(2u, h.Replication.SnapshotViolations(2).Ownership);
    }

    [Fact]
    public void A_client_mask_carrying_the_server_authored_ColdDirty_bit_is_refused_and_counted()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        h.Replication.AddSubscriber(1);
        uint avatar = h.Spawn(1, 0f, 0f);

        EntityWireState state = default;
        Assert.False(h.Replication.TryApplyOwnedUpdate(avatar, 1, DeltaMask.X | DeltaMask.ColdDirty, state));

        Assert.Equal(1, h.Replication.IllegalMaskCount);
        Assert.Equal(1u, h.Replication.SnapshotViolations(1).Mask);
    }

    [Fact]
    public void The_teleport_bit_is_counted_but_still_applied_at_level_one()
    {
        // Legitimate under client authority (respawns), so it is quotaed and counted rather than refused.
        ReplicationHarness h = ReplicationHarness.Create();
        uint avatar = h.JoinWithAvatar(1, 0f, 0f);
        h.TickAndEstablish(1);

        EntityWireState state = default;
        Assert.True(h.Quantizer.TryQuantizePosition(40f, 0f, out state.QX, out state.QY));
        Assert.True(h.Replication.TryApplyOwnedUpdate(avatar, 1, DeltaMask.X | DeltaMask.Y | DeltaMask.Teleport, state));

        Assert.Equal(1, h.Replication.TeleportBitCount);
        Assert.Equal(1u, h.Replication.SnapshotViolations(1).Teleport);

        h.Tick();
        (_, _, EntityWireState delivered) = Assert.Single(h.PumpDelta(1).Updates);
        Assert.Equal(state.QX, delivered.QX);
    }

    [Fact]
    public void An_implausible_move_is_counted_and_still_applied()
    {
        // "|Δpos| ≤ MaxEntitySpeed × Δt × 1.25 — counted, not enforced, at Level 1."
        ReplicationOptions options = ReplicationHarness.DefaultOptions() with { MaxEntitySpeed = 100f, TickHz = 20 };
        ReplicationHarness h = new(options);
        uint avatar = h.JoinWithAvatar(1, 0f, 0f);
        h.TickAndEstablish(1);

        // Allowance is 100 / 20 × 1.25 = 6.25 units for one elapsed tick; 300 is forty-eight times that.
        h.MoveTo(avatar, 1, 300f, 0f);

        Assert.Equal(1, h.Replication.SpeedViolationCount);
        Assert.Equal(1u, h.Replication.SnapshotViolations(1).Speed);

        h.Tick();
        Assert.Single(h.PumpDelta(1).Updates);
    }

    [Fact]
    public void A_non_finite_spectator_focus_is_refused_and_counted_as_nan()
    {
        // After quantization, spectator focus is the only inbound float left on the entity path — and one
        // NaN poisons the spatial hash.
        ReplicationHarness h = ReplicationHarness.Create();
        h.Replication.AddSubscriber(9);

        h.Replication.SetSpectatorFocus(9, float.NaN, 0f);
        h.Replication.SetSpectatorFocus(9, 0f, float.PositiveInfinity);

        Assert.Equal(2, h.Replication.NanFocusCount);
        Assert.Equal(2u, h.Replication.SnapshotViolations(9).Nan);
    }

    [Fact]
    public void Spectator_focus_movement_is_speed_clamped_but_its_first_placement_is_not()
    {
        // Clamping the first placement against the initial (0, 0) would strand a joining spectator at the
        // world origin; clamping thereafter is what deletes focus-teleport AOI amplification.
        ReplicationOptions options = ReplicationHarness.DefaultOptions() with { MaxSpectatorFocusSpeed = 200f, TickHz = 20 };
        ReplicationHarness h = new(options);
        h.Replication.AddSubscriber(9);

        h.Replication.SetSpectatorFocus(9, 900f, 0f);   // first placement: accepted verbatim
        Assert.Equal(0, h.Replication.FocusClampCount);

        h.Replication.SetSpectatorFocus(9, 1900f, 0f);  // 1000 units in one tick; the cap is 200/20 = 10
        Assert.Equal(1, h.Replication.FocusClampCount);
        Assert.Equal(1u, h.Replication.SnapshotViolations(9).FocusClamp);
    }

    [Fact]
    public void A_departing_owner_takes_its_Owned_entities_and_leaves_its_Shared_ones()
    {
        // Without the policy bits a departing host's pickups, spawners and props vanished with it.
        ReplicationHarness h = ReplicationHarness.Create();
        h.Replication.AddSubscriber(1);
        uint avatar = h.Spawn(1, 0f, 0f, flags: (byte)OwnershipPolicy.Owned);
        uint pickup = h.Spawn(1, 5f, 0f, flags: (byte)OwnershipPolicy.Shared);
        uint prop = h.Spawn(1, 6f, 0f, flags: (byte)OwnershipPolicy.Transferable);

        List<uint> despawned = [];
        h.Replication.RemoveOwner(1, despawned);

        Assert.Equal(new[] { avatar }, despawned);
        Assert.Equal(2, h.Replication.EntityCount);
        Assert.Equal(2, h.Replication.PolicyPreservedEntityCount);

        List<uint> reassigned = [];
        h.Replication.ReassignOwner(1, 2, reassigned);

        Assert.Equal(2, reassigned.Count);
        Assert.Contains(pickup, reassigned);
        Assert.Contains(prop, reassigned);
    }

    [Fact]
    public void A_reassigned_entity_is_re_introduced_so_observers_learn_its_new_owner()
    {
        // OwnerId travels only in a FullRecord, so an update record cannot carry it: the entity has to be
        // removed and re-entered, or an observer would hold a stale owner and accept authority from the
        // wrong peer.
        ReplicationHarness h = ReplicationHarness.Create();
        h.JoinWithAvatar(1, 0f, 0f);
        uint pickup = h.Spawn(2, 10f, 0f, flags: (byte)OwnershipPolicy.Shared);
        h.TickAndEstablish(1);

        List<uint> reassigned = [];
        h.Replication.ReassignOwner(2, 3, reassigned);
        Assert.Equal(new[] { pickup }, reassigned);

        h.Tick();
        DeltaView delta = h.PumpDelta(1);

        Assert.Contains((ushort)NetId.Slot(pickup), delta.Removed);
        (uint netId, EntityWireState state) = Assert.Single(delta.Enters);
        Assert.Equal(pickup, netId);
        Assert.Equal(3u, state.OwnerId);
    }

    [Fact]
    public void Cold_props_are_announced_with_the_ColdDirty_bit_on_the_next_update()
    {
        ReplicationHarness h = ReplicationHarness.Create();
        h.JoinWithAvatar(1, 0f, 0f);
        uint prop = h.Spawn(2, 10f, 0f);
        h.TickAndEstablish(1);

        Assert.True(h.Replication.TryMarkColdDirty(prop));
        h.Tick();
        DeltaView delta = h.PumpDelta(1);

        (ushort slot, byte mask, _) = Assert.Single(delta.Updates);
        Assert.Equal(NetId.Slot(prop), slot);
        // ColdDirty carries no payload bytes: the record is a bare 3-byte header promising a
        // control-plane EntityPropsChangedEvent.
        Assert.Equal(DeltaMask.ColdDirty, mask);
        Assert.Equal(HotWire.MinUpdateRecordSize, HotWire.UpdateRecordSize(mask));
    }

    [Fact]
    public void The_entity_table_refuses_to_grow_past_its_capacity()
    {
        ReplicationHarness h = new(ReplicationHarness.DefaultOptions(maxEntities: 4));
        EntityWireState state = default;

        for (int i = 0; i < 4; i++)
        {
            Assert.True(h.Replication.TrySpawn(1, 1, state, out _, out _));
        }

        Assert.False(h.Replication.TrySpawn(1, 1, state, out uint netId, out RejectCode reject));
        Assert.Equal(RejectCode.EntityLimitReached, reject);
        Assert.Equal(NetId.None, netId);
        Assert.Equal(4, h.Replication.EntityCount);
    }
}
