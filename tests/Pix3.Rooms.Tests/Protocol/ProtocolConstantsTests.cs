using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Tests.Protocol;

/// <summary>
/// The parts of the wire contract that are numbers rather than layouts: TypeIds, reject codes and
/// their close codes, the small enums, version negotiation and <c>netId</c> packing. Every expected
/// value is transcribed from the tables in <c>docs/protocol.md</c>.
/// </summary>
/// <remarks>
/// These read like tautologies against the constants they check, and that is the point: a TypeId or a
/// reject code is a number a *client* hardcodes. Renaming a constant is free; renumbering one silently
/// breaks every deployed bundle, so the number needs a second, independent statement of what it is.
/// </remarks>
public class ProtocolConstantsTests
{
    [Fact]
    public void Core_range_TypeIds_match_the_allocation_table()
    {
        Assert.Equal(1, MessageTypeIds.HelloCommand);
        Assert.Equal(2, MessageTypeIds.WelcomeEvent);
        Assert.Equal(3, MessageTypeIds.RejectedEvent);
        Assert.Equal(4, MessageTypeIds.PingCommand);
        Assert.Equal(5, MessageTypeIds.PongEvent);
        Assert.Equal(6, MessageTypeIds.PeerJoinedEvent);
        Assert.Equal(7, MessageTypeIds.PeerLeftEvent);
        Assert.Equal(8, MessageTypeIds.RoomInfoEvent);
        Assert.Equal(9, MessageTypeIds.SendChatCommand);
        Assert.Equal(10, MessageTypeIds.ChatMessageEvent);
        Assert.Equal(11, MessageTypeIds.LeaveCommand);
        Assert.Equal(12, MessageTypeIds.SetRoomVarCommand);
        Assert.Equal(13, MessageTypeIds.RoomVarsChangedEvent);
        Assert.Equal(14, MessageTypeIds.ResyncCommand);
        Assert.Equal(15, MessageTypeIds.SetClientPrefsCommand);
        Assert.Equal(16, MessageTypeIds.HostChangedEvent);
    }

    [Fact]
    public void State_and_signal_range_TypeIds_match_the_allocation_table()
    {
        Assert.Equal(64, MessageTypeIds.SpawnEntityRequest);
        Assert.Equal(65, MessageTypeIds.SpawnEntityResponse);
        Assert.Equal(66, MessageTypeIds.DespawnEntityCommand);
        Assert.Equal(67, MessageTypeIds.EntityUpdatePacket);
        Assert.Equal(68, MessageTypeIds.SnapshotPacket);
        Assert.Equal(69, MessageTypeIds.DeltaPacket);
        Assert.Equal(70, MessageTypeIds.SetEntityPropsCommand);
        Assert.Equal(71, MessageTypeIds.EntityPropsChangedEvent);

        Assert.Equal(128, MessageTypeIds.EmitSignalCommand);
        Assert.Equal(129, MessageTypeIds.SignalEvent);
        Assert.Equal(130, MessageTypeIds.SignalBatchPacket);
    }

    [Fact]
    public void TypeId_ranges_match_the_reservation_table()
    {
        Assert.Equal(0, MessageTypeIds.CoreRangeFirst);
        Assert.Equal(63, MessageTypeIds.CoreRangeLast);
        Assert.Equal(64, MessageTypeIds.StateRangeFirst);
        Assert.Equal(127, MessageTypeIds.StateRangeLast);
        Assert.Equal(128, MessageTypeIds.SignalRangeFirst);
        Assert.Equal(191, MessageTypeIds.SignalRangeLast);
        Assert.Equal(192, MessageTypeIds.AppRangeFirst);
        Assert.Equal(255, MessageTypeIds.AppRangeLast);
    }

    [Fact]
    public void Only_the_four_hand_packed_payloads_are_hot_plane()
    {
        // "Hot plane (TypeId 67/68/69/130, suffix …Packet) — hand-packed fixed layouts."
        for (int id = 0; id <= 255; id++)
        {
            bool expected = id is 67 or 68 or 69 or 130;
            Assert.Equal(expected, MessageTypeIds.IsHotPlane((byte)id));
        }
    }

    [Theory]
    [InlineData(RejectCode.None, 0)]
    [InlineData(RejectCode.ProtocolVersionMismatch, 1)]
    [InlineData(RejectCode.InvalidToken, 2)]
    [InlineData(RejectCode.TokenExpired, 3)]
    [InlineData(RejectCode.TokenRoomMismatch, 4)]
    [InlineData(RejectCode.RoomNotFound, 5)]
    [InlineData(RejectCode.RoomFull, 6)]
    [InlineData(RejectCode.RoomClosing, 7)]
    [InlineData(RejectCode.RateLimited, 8)]
    [InlineData(RejectCode.PayloadTooLarge, 9)]
    [InlineData(RejectCode.QuotaExceeded, 10)]
    [InlineData(RejectCode.ServerShuttingDown, 11)]
    [InlineData(RejectCode.IdleTimeout, 12)]
    [InlineData(RejectCode.BadRequest, 13)]
    [InlineData(RejectCode.SessionReplaced, 14)]
    [InlineData(RejectCode.EntityLimitReached, 15)]
    [InlineData(RejectCode.NotEntityOwner, 16)]
    [InlineData(RejectCode.InternalError, 17)]
    [InlineData(RejectCode.KindNotAllowed, 18)]
    [InlineData(RejectCode.SendQueueOverflow, 19)]
    public void Reject_codes_match_the_spec_table(RejectCode code, int expected)
    {
        Assert.Equal(expected, (ushort)code);
    }

    [Theory]
    [InlineData(RejectCode.ProtocolVersionMismatch, 4001)]
    [InlineData(RejectCode.InvalidToken, 4002)]
    [InlineData(RejectCode.TokenExpired, 4002)]
    [InlineData(RejectCode.TokenRoomMismatch, 4002)]
    [InlineData(RejectCode.RoomNotFound, 4003)]
    [InlineData(RejectCode.RoomFull, 4003)]
    [InlineData(RejectCode.RoomClosing, 4003)]
    [InlineData(RejectCode.RateLimited, 4004)]
    [InlineData(RejectCode.PayloadTooLarge, 4004)]
    [InlineData(RejectCode.QuotaExceeded, 4004)]
    [InlineData(RejectCode.ServerShuttingDown, 4005)]
    [InlineData(RejectCode.IdleTimeout, 4006)]
    [InlineData(RejectCode.BadRequest, 4007)]
    [InlineData(RejectCode.SessionReplaced, 4008)]
    [InlineData(RejectCode.InternalError, 4000)]
    // Not RateLimited: that one means the client sent too much, this one that it read too little.
    [InlineData(RejectCode.SendQueueOverflow, 4004)]
    public void Close_codes_match_the_spec_table(RejectCode code, int expected)
    {
        Assert.True(code.HasWebSocketCloseStatus());
        Assert.Equal(expected, code.ToCloseCode());
    }

    [Theory]
    [InlineData(RejectCode.None)]
    [InlineData(RejectCode.EntityLimitReached)]
    [InlineData(RejectCode.NotEntityOwner)]
    [InlineData(RejectCode.KindNotAllowed)]
    public void Spawn_and_despawn_only_reject_codes_carry_no_close_code(RejectCode code)
    {
        Assert.False(code.HasWebSocketCloseStatus());
    }

    [Fact]
    public void Leave_reasons_match_the_spec_table()
    {
        Assert.Equal(0, (byte)LeaveReason.Disconnected);
        Assert.Equal(1, (byte)LeaveReason.LeftVoluntarily);
        Assert.Equal(2, (byte)LeaveReason.Kicked);
        Assert.Equal(3, (byte)LeaveReason.Timeout);
        Assert.Equal(4, (byte)LeaveReason.RoomClosed);
        Assert.Equal(5, (byte)LeaveReason.Error);
    }

    [Fact]
    public void Signal_targets_match_the_spec_table()
    {
        Assert.Equal(0, (byte)SignalTarget.Server);
        Assert.Equal(1, (byte)SignalTarget.AllPeers);
        Assert.Equal(2, (byte)SignalTarget.SinglePeer);
        Assert.Equal(3, (byte)SignalTarget.AoiPeers);
    }

    [Fact]
    public void Ownership_policies_occupy_bits_zero_and_one_of_the_flags_byte()
    {
        Assert.Equal(0, (byte)OwnershipPolicy.Owned);
        Assert.Equal(1, (byte)OwnershipPolicy.Shared);
        Assert.Equal(2, (byte)OwnershipPolicy.Transferable);
        Assert.Equal(3, (byte)OwnershipPolicy.Reserved);

        Assert.Equal(0b0000_0011, EntityFlags.PolicyMask);
        Assert.Equal(0b0000_0100, EntityFlags.ReservedMask);
        Assert.Equal(0b1111_1000, EntityFlags.AppMask);
    }

    [Fact]
    public void Setting_a_policy_preserves_the_app_bits_and_the_reserved_bit()
    {
        // App bits 3–7 are replicated verbatim; the fabric never interprets them.
        const byte appBits = 0b1010_1000;

        byte flags = EntityFlags.WithPolicy(appBits, OwnershipPolicy.Transferable);

        Assert.Equal(0b1010_1010, flags);
        Assert.Equal(OwnershipPolicy.Transferable, EntityFlags.GetPolicy(flags));
        Assert.Equal(appBits, EntityFlags.AppBits(flags));
        Assert.True(EntityFlags.IsReservedBitClear(flags));
        Assert.False(EntityFlags.IsReservedBitClear(0b0000_0100));
    }

    [Fact]
    public void Frame_flags_reserve_everything_above_bit_zero()
    {
        Assert.Equal(0x00, FrameFlags.None);
        Assert.Equal(0x01, FrameFlags.Final);
        Assert.Equal(0xFE, FrameFlags.ReservedBits);
        Assert.True(FrameFlags.IsFinal(0x01));
        Assert.False(FrameFlags.IsFinal(0x00));
        // A receiver ignores reserved bits rather than treating them as fatal.
        Assert.True(FrameFlags.IsFinal(0xFF));
    }

    // ── Version negotiation ───────────────────────────────────────────────────

    [Fact]
    public void v2_is_both_the_current_and_the_minimum_supported_version()
    {
        Assert.Equal(2, ProtocolVersion.Current);
        Assert.Equal(2, ProtocolVersion.MinSupported);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]   // v1 was never spoken by a client and is deleted outright.
    [InlineData(2, true)]
    [InlineData(3, true)]    // A newer client is served, at v2.
    [InlineData(65535, true)]
    public void Negotiation_is_by_range_not_equality(int clientVersion, bool supported)
    {
        Assert.Equal(supported, ProtocolVersion.IsSupported((ushort)clientVersion));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 2)]       // min(client, current)
    [InlineData(65535, 2)]
    public void A_session_runs_at_the_minimum_of_client_and_current(int clientVersion, int expected)
    {
        Assert.Equal(expected, ProtocolVersion.Negotiate((ushort)clientVersion));
    }

    // ── NetId ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NetId_packs_sixteen_bits_of_slot_and_sixteen_of_generation()
    {
        Assert.Equal(16, NetId.SlotBits);
        Assert.Equal(16, NetId.GenerationBits);
        Assert.Equal(0x0000_FFFFu, NetId.SlotMask);
        Assert.Equal(0xFFFF_0000u, NetId.GenerationMask);
        Assert.Equal(65_535, NetId.MaxSlot);
        Assert.Equal(65_535, NetId.MaxGeneration);

        Assert.Equal(0x0002_0007u, NetId.Pack(7, 2));
        Assert.Equal(7, NetId.Slot(0x0002_0007u));
        Assert.Equal(2, NetId.Generation(0x0002_0007u));

        Assert.Equal(0xFFFF_FFFFu, NetId.Pack(NetId.MaxSlot, NetId.MaxGeneration));
        Assert.Equal(NetId.MaxSlot, NetId.Slot(0xFFFF_FFFFu));
        Assert.Equal(NetId.MaxGeneration, NetId.Generation(0xFFFF_FFFFu));
    }

    [Fact]
    public void Zero_is_permanently_a_safe_no_entity_sentinel()
    {
        // Generations start at 1, so no live entity can ever pack to 0.
        Assert.Equal(0u, NetId.None);
        Assert.False(NetId.IsValid(NetId.None));
        Assert.False(NetId.IsValid(0x0000_1234u));   // slot 0x1234, generation 0 — impossible
        Assert.True(NetId.IsValid(NetId.Pack(0, 1)));
    }
}
