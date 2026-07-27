using System.Diagnostics;
using MemoryPack;
using Microsoft.Extensions.Logging.Abstractions;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Net;
using Pix3.Rooms.Server.Replication;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Tests.Rooms;

/// <summary>
/// Two live rooms, each with its own tick thread, and zero cross-talk between them.
/// </summary>
/// <remarks>
/// This one is a permanent test "because that is precisely what the predecessor server got wrong"
/// (<c>docs/architecture.md</c> → Tests). It runs real <see cref="Room"/> instances with real tick
/// loops and fake connections, because a room-isolation bug lives in exactly the wiring a replication
/// unit test replaces with a stub.
/// </remarks>
public sealed class RoomIsolationTests : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<(Room Room, Task Loop)> _rooms = [];

    public void Dispose()
    {
        _cts.Cancel();
        foreach ((Room _, Task loop) in _rooms)
        {
            try
            {
                loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // A cancelled tick loop faulting on shutdown is not what these tests are about.
            }
        }

        _cts.Dispose();
    }

    private Room StartRoom(string roomId, int maxPlayers = 8)
    {
        RoomConfig config = new()
        {
            RoomId = roomId,
            ProjectId = "tests",
            MaxPlayers = maxPlayers,
            TickHz = 20,
            AoiRadius = 1200f,
            MaxEntities = 256,
        };

        ReplicationOptions replicationOptions = new()
        {
            MaxEntities = config.MaxEntities,
            MaxPlayers = config.MaxPlayers,
            AoiRadius = config.AoiRadius,
            MaxVisibleEntities = config.MaxVisibleEntities,
            TickHz = config.TickHz,
            WorldOriginX = config.WorldOriginX,
            WorldOriginY = config.WorldOriginY,
            WorldSize = config.WorldSize,
        };

        RoomServerOptions options = new();
        options.Normalize();

        Room room = new(config, new RoomReplication(replicationOptions), options, NullLogger<Room>.Instance);
        _rooms.Add((room, room.RunAsync(_cts.Token)));
        return room;
    }

    private static FakeClientConnection Join(Room room, uint clientId, string name)
    {
        FakeClientConnection connection = new(clientId, name);
        Assert.True(room.TryJoin(connection, out JoinGrant grant, out RejectCode reject), $"join refused: {reject}");
        Assert.Equal(clientId, grant.ClientId);
        Assert.Equal(16, grant.ResumeKey.Length);
        return connection;
    }

    private static void Send<T>(Room room, uint clientId, byte typeId, T message)
    {
        OutboundFrame encoded = FramePool.EncodeControl(typeId, message);
        Assert.True(room.TryEnqueueInbound(new InboundMessage(clientId, typeId, encoded.Buffer, encoded.Length)));
    }

    private static void Spawn(Room room, uint clientId, ushort qx, ushort qy)
        => Send(room, clientId, MessageTypeIds.SpawnEntityRequest, new SpawnEntityRequest
        {
            RequestId = 1,
            Kind = 1,
            QX = qx,
            QY = qy,
        });

    private static void WaitFor(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"timed out after {timeoutMs} ms waiting for: {what}");
    }

    /// <summary>Distinct entity slots this connection was ever told about, across snapshots and deltas.</summary>
    private static HashSet<ushort> SlotsSeen(FakeClientConnection connection)
    {
        HashSet<ushort> slots = [];
        foreach (byte[] frame in connection.Frames)
        {
            if (frame[0] == MessageTypeIds.SnapshotPacket
                && HotWire.TryReadSnapshotPacket(frame, out _, out _, out _, out int count, out ReadOnlySpan<byte> records))
            {
                for (int i = 0; i < count; i++)
                {
                    if (HotWire.TryReadFullRecord(records.Slice(i * HotWire.FullRecordSize), out uint netId, out _))
                    {
                        slots.Add((ushort)NetId.Slot(netId));
                    }
                }
            }
            else if (frame[0] == MessageTypeIds.DeltaPacket && HotWire.TryReadDeltaPacket(frame, out DeltaPacketSections sections))
            {
                for (int i = 0; i < sections.EnterCount; i++)
                {
                    if (sections.TryGetEnterRecord(i, out uint netId, out _))
                    {
                        slots.Add((ushort)NetId.Slot(netId));
                    }
                }
            }
        }

        return slots;
    }

    [Fact]
    public void Two_rooms_share_no_entities_no_peers_and_no_chat()
    {
        Room alpha = StartRoom("alpha");
        Room beta = StartRoom("beta");

        FakeClientConnection a1 = Join(alpha, 1, "a1");
        FakeClientConnection a2 = Join(alpha, 2, "a2");
        FakeClientConnection b1 = Join(beta, 3, "b1");

        Spawn(alpha, 1, 32768, 32768);
        Spawn(alpha, 2, 32768, 32768);
        Spawn(beta, 3, 32768, 32768);

        WaitFor(() => alpha.SnapshotStats().EntityCount == 2, "alpha to hold both of its entities");
        WaitFor(() => beta.SnapshotStats().EntityCount == 1, "beta to hold its entity");
        WaitFor(() => SlotsSeen(a1).Count == 2, "a1 to be told about both alpha entities");
        WaitFor(() => SlotsSeen(b1).Count == 1, "b1 to be told about its own entity");

        Send(alpha, 1, MessageTypeIds.SendChatCommand, new SendChatCommand { Text = "hello alpha" });
        WaitFor(() => a2.CountOfType(MessageTypeIds.ChatMessageEvent) == 1, "a2 to receive the chat message");

        // Give beta several more ticks to prove nothing leaks across; a cross-talk bug shows up as an extra
        // entity slot, an extra peer or a chat message from a room this client was never in.
        Thread.Sleep(150);

        Assert.Single(SlotsSeen(b1));
        Assert.Equal(0, b1.CountOfType(MessageTypeIds.ChatMessageEvent));
        Assert.Equal(0, b1.CountOfType(MessageTypeIds.PeerJoinedEvent));
        Assert.Equal(1, a1.CountOfType(MessageTypeIds.PeerJoinedEvent));   // a2 joining, and nobody else

        Assert.Equal(2, alpha.SnapshotStats().PlayerCount);
        Assert.Equal(1, beta.SnapshotStats().PlayerCount);
        Assert.Equal(2, alpha.SnapshotStats().EntityCount);
        Assert.Equal(1, beta.SnapshotStats().EntityCount);
    }

    [Fact]
    public void A_joiner_is_announced_to_the_room_and_its_departure_is_too()
    {
        Room room = StartRoom("peers");
        FakeClientConnection first = Join(room, 1, "first");

        WaitFor(() => first.Frames.Count > 0, "the first member's initial frames");
        FakeClientConnection second = Join(room, 2, "second");

        WaitFor(() => first.CountOfType(MessageTypeIds.PeerJoinedEvent) == 1, "a PeerJoinedEvent for the second member");
        PeerJoinedEvent? joined = MemoryPackSerializer.Deserialize<PeerJoinedEvent>(
            first.FramesOfType(MessageTypeIds.PeerJoinedEvent)[0].AsSpan(1));
        Assert.NotNull(joined);
        Assert.Equal(2u, joined.ClientId);
        Assert.Equal("second", joined.DisplayName);
        Assert.Equal(0, second.CountOfType(MessageTypeIds.PeerJoinedEvent));   // never announced to itself

        room.Leave(2, LeaveReason.LeftVoluntarily);

        WaitFor(() => first.CountOfType(MessageTypeIds.PeerLeftEvent) == 1, "a PeerLeftEvent for the second member");
        PeerLeftEvent? left = MemoryPackSerializer.Deserialize<PeerLeftEvent>(
            first.FramesOfType(MessageTypeIds.PeerLeftEvent)[0].AsSpan(1));
        Assert.NotNull(left);
        Assert.Equal(2u, left.ClientId);
        Assert.Equal((byte)LeaveReason.LeftVoluntarily, left.Reason);
        Assert.Equal(1, room.SnapshotStats().PlayerCount);
    }

    [Fact]
    public void A_disconnect_inside_the_resume_grace_announces_nothing_and_freezes_the_entities()
    {
        // "A drop inside the resume grace emits no PeerLeftEvent at all" — peers must not be told about a blip.
        Room room = StartRoom("resume");
        FakeClientConnection first = Join(room, 1, "first");
        Join(room, 2, "second");
        Spawn(room, 2, 32768, 32768);

        WaitFor(() => room.SnapshotStats().EntityCount == 1, "the second member's entity");

        room.Leave(2, LeaveReason.Disconnected);
        Thread.Sleep(150);

        Assert.Equal(0, first.CountOfType(MessageTypeIds.PeerLeftEvent));
        Assert.Equal(1, room.SnapshotStats().EntityCount);   // entities stay alive and frozen
        Assert.Equal(2, room.SnapshotStats().PlayerCount);   // the slot is still reserved
    }

    [Fact]
    public void The_host_is_the_longest_present_member_and_migrates_when_it_leaves()
    {
        // Without this a departing host's pickups vanish and every public "play with friends" session dies
        // when its creator backgrounds their phone.
        Room room = StartRoom("host");
        Join(room, 1, "first");
        FakeClientConnection second = Join(room, 2, "second");

        WaitFor(() => room.HostClientId == 1, "the first member to become host");

        room.Leave(1, LeaveReason.LeftVoluntarily);

        WaitFor(() => room.HostClientId == 2, "the host to migrate to the second member");
        WaitFor(() => second.CountOfType(MessageTypeIds.HostChangedEvent) >= 1, "a HostChangedEvent");

        HostChangedEvent? changed = MemoryPackSerializer.Deserialize<HostChangedEvent>(
            second.FramesOfType(MessageTypeIds.HostChangedEvent)[^1].AsSpan(1));
        Assert.NotNull(changed);
        Assert.Equal(2u, changed.HostClientId);
        Assert.Equal(1u, changed.PreviousHostClientId);
    }

    [Fact]
    public void An_unknown_TypeId_is_ignored_rather_than_fatal()
    {
        // This is what lets a game published six months ago keep working when the fabric adds messages.
        Room room = StartRoom("unknown");
        FakeClientConnection connection = Join(room, 1, "first");
        Spawn(room, 1, 32768, 32768);
        WaitFor(() => room.SnapshotStats().EntityCount == 1, "the spawn to land");

        byte[] payload = FramePool.Rent(4);
        payload[0] = 200;   // reserved for app/game-specific extensions: the fabric never interprets these
        Assert.True(room.TryEnqueueInbound(new InboundMessage(1, 200, payload, 1)));

        Spawn(room, 1, 40000, 40000);
        WaitFor(() => room.SnapshotStats().EntityCount == 2, "the room to keep serving after an unknown TypeId");
        Assert.True(connection.IsOpen);
        Assert.Null(connection.CloseCode);
    }
}
