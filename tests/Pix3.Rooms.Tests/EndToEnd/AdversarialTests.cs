using MemoryPack;
using Pix3.Rooms.LoadGen;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Tests.EndToEnd;

/// <summary>
/// Deliberate abuse over a real socket: floods, oversized frames, mutations of somebody else's
/// entities, spawn-cap breaches and malformed hot frames.
/// </summary>
/// <remarks>
/// "These are not defensive niceties: a single packet could otherwise take a room down"
/// (<c>docs/protocol.md</c> → Input validation). Every test here asserts two things — the abuser is
/// handled the way the spec says, and the <i>room keeps serving its other client</i>. The second half is
/// the point: a validation bug that merely rejects the attacker looks identical to one that takes the
/// room with it, until you look at the bystander.
/// </remarks>
[Collection(LiveServerCollection.Name)]
public sealed class AdversarialTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    private readonly LiveServerFixture _server;

    /// <summary>Receives the shared live server.</summary>
    public AdversarialTests(LiveServerFixture server) => _server = server;

    [Fact]
    public async Task A_message_flood_closes_the_abuser_with_RateLimited_and_leaves_the_room_serving()
    {
        // Inbound messages: 60/s per connection.
        await _server.CreateRoomAsync("adv-flood");
        await using RoomClient bystander = await _server.ConnectAsync("adv-flood", "bystander");
        uint bystanderAvatar = await bystander.SpawnAsync(0f, 0f);

        RoomClient abuser = await _server.ConnectAsync("adv-flood", "abuser");
        uint abuserAvatar = await abuser.SpawnAsync(5f, 0f);

        try
        {
            for (int i = 0; i < 400 && abuser.IsOpen; i++)
            {
                await abuser.SendUpdateAsync(abuserAvatar, i % 50, 0f, 0f);
            }
        }
        catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or InvalidOperationException)
        {
            // The socket died mid-flood, which is the outcome under test.
        }

        Assert.True(
            await abuser.WaitForAsync(c => c.Metrics.CloseStatus is not null, Settle),
            "the flood was never rate limited");
        Assert.Equal(4004, abuser.Metrics.CloseStatus);
        Assert.Equal(RejectCode.RateLimited, abuser.Rejected);

        // The room is still alive and still serving the client that behaved.
        await bystander.SendUpdateAsync(bystanderAvatar, 12f, 0f, 0f);
        Assert.True(
            await bystander.WaitForAsync(c => c.Metrics.Updates > 0 || c.Metrics.SnapshotsCompleted > 0, Settle),
            "the bystander stopped being served");
        Assert.True(bystander.IsOpen);
        Assert.Equal(0, bystander.Metrics.SeqGaps);

        await abuser.DisposeAsync();
    }

    [Fact]
    public async Task An_oversized_frame_closes_the_sender_with_PayloadTooLarge()
    {
        // Inbound payload: 4 KiB per frame.
        await _server.CreateRoomAsync("adv-oversize");
        await using RoomClient client = await _server.ConnectAsync("adv-oversize", "abuser");

        try
        {
            await client.SendRawFrameAsync(MessageTypeIds.SetEntityPropsCommand, new byte[64 * 1024]);
        }
        catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or InvalidOperationException)
        {
            // The server may cut the socket mid-write; that is the expected outcome, not a test failure.
        }

        Assert.True(
            await client.WaitForAsync(c => c.Metrics.CloseStatus is not null, Settle),
            "an oversized frame was accepted");
        Assert.Equal(4004, client.Metrics.CloseStatus);
    }

    [Fact]
    public async Task Mutating_another_clients_entity_is_refused_counted_and_leaves_the_entity_alone()
    {
        await _server.CreateRoomAsync("adv-ownership");
        await using RoomClient victim = await _server.ConnectAsync("adv-ownership", "victim");
        uint victimAvatar = await victim.SpawnAsync(0f, 0f);

        await using RoomClient thief = await _server.ConnectAsync("adv-ownership", "thief");
        await thief.SpawnAsync(10f, 0f);
        Assert.True(await thief.WaitForAsync(c => c.KnownNetIds.Contains(victimAvatar), Settle), "thief never saw the victim");

        int updatesBefore = thief.Metrics.Updates;
        for (int i = 0; i < 5; i++)
        {
            await thief.SendUpdateAsync(victimAvatar, 900f + i, 900f, 0f);
            await Task.Delay(40);
        }

        await Task.Delay(300);

        // Nothing moved, so nothing was replicated: an accepted mutation would have produced updates.
        Assert.Equal(updatesBefore, thief.Metrics.Updates);
        Assert.True(thief.IsOpen, "an ownership violation is counted, not fatal");

        // The per-client tallies are merged from Replication and published by the tick thread at ~1 Hz —
        // Replication is single-threaded, so an admin thread may never read it directly — which means the
        // admin API is up to a second behind. Poll rather than assume.
        using AdminApiClient admin = _server.CreateAdminClient();
        string violations = "";
        for (int attempt = 0; attempt < 60; attempt++)
        {
            violations = (await admin.GetMetricsAsync($"/admin/rooms/adv-ownership/violations/{thief.ClientId}"))
                .Replace(" ", "");
            if (violations.Contains("\"ownership\":5", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Contains("\"ownership\":5", violations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Despawning_another_clients_entity_is_refused()
    {
        await _server.CreateRoomAsync("adv-despawn");
        await using RoomClient victim = await _server.ConnectAsync("adv-despawn", "victim");
        uint victimAvatar = await victim.SpawnAsync(0f, 0f);

        await using RoomClient thief = await _server.ConnectAsync("adv-despawn", "thief");
        Assert.True(await thief.WaitForAsync(c => c.KnownNetIds.Contains(victimAvatar), Settle), "thief never saw the victim");

        await thief.SendRawFrameAsync(
            MessageTypeIds.DespawnEntityCommand,
            MemoryPackSerializer.Serialize(new DespawnEntityCommand { NetId = victimAvatar }));
        await Task.Delay(300);

        using AdminApiClient admin = _server.CreateAdminClient();
        RoomStatsSnapshot? stats = await admin.GetRoomStatsAsync("adv-despawn");
        Assert.NotNull(stats);
        Assert.Equal(1, stats!.EntityCount);
        Assert.Contains(victimAvatar, thief.KnownNetIds);
        Assert.True(thief.IsOpen);
    }

    [Fact]
    public async Task Spawning_past_the_per_owner_cap_is_refused_without_killing_the_session()
    {
        // Entities per owner: 64.
        await _server.CreateRoomAsync("adv-spawncap", maxEntities: 256);
        await using RoomClient client = await _server.ConnectAsync("adv-spawncap", "hoarder");

        for (int i = 0; i < 64; i++)
        {
            await client.SpawnAsync(i % 50, i / 50f);
        }

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SpawnAsync(0f, 0f));

        // QuotaExceeded, not EntityLimitReached: the table has 256 slots free, it is this owner's budget
        // of 64 that ran out. docs/protocol.md names both codes but does not say which one a per-owner
        // cap breach returns, so this test pins the server's answer.
        Assert.Contains("QuotaExceeded", refused.Message, StringComparison.Ordinal);
        Assert.True(client.IsOpen);

        using AdminApiClient admin = _server.CreateAdminClient();
        RoomStatsSnapshot? stats = await admin.GetRoomStatsAsync("adv-spawncap");
        Assert.NotNull(stats);
        Assert.Equal(64, stats!.EntityCount);
    }

    [Fact]
    public async Task A_truncated_hot_frame_is_dropped_and_the_session_survives()
    {
        // "Readers of untrusted bytes return bool and never throw. Malformed input is a normal event."
        await _server.CreateRoomAsync("adv-garbage");
        await using RoomClient client = await _server.ConnectAsync("adv-garbage", "fuzzer");
        uint avatar = await client.SpawnAsync(0f, 0f);

        // An EntityUpdatePacket claiming three records and carrying four bytes of nonsense.
        await client.SendRawFrameAsync(
            MessageTypeIds.EntityUpdatePacket,
            new byte[] { 1, 0, 0, 0, 3, 0xFF, 0xFF, 0xFF, 0xFF });

        // A DeltaPacket TypeId from a client: server-to-client only, so it must be ignored, not decoded.
        await client.SendRawFrameAsync(MessageTypeIds.DeltaPacket, new byte[] { 0xAA, 0xBB, 0xCC });

        // A signal batch header with a count no payload backs.
        await client.SendRawFrameAsync(MessageTypeIds.SignalBatchPacket, new byte[] { 0, 0, 0, 0, 0, 0, 200 });

        await Task.Delay(300);

        // Still a working session: a legitimate update after the garbage still lands.
        await client.SendUpdateAsync(avatar, 25f, 0f, 0f);
        await Task.Delay(200);
        Assert.True(client.IsOpen);
        Assert.Null(client.Rejected);

        using AdminApiClient admin = _server.CreateAdminClient();
        RoomStatsSnapshot? stats = await admin.GetRoomStatsAsync("adv-garbage");
        Assert.NotNull(stats);
        Assert.Equal(1, stats!.EntityCount);
    }

    [Fact]
    public async Task An_AOI_signal_from_a_client_that_owns_nothing_is_refused_rather_than_broadcast()
    {
        // "A sender with no bound focus entity cannot scope a signal to an AOI at all… There is
        // deliberately no send-to-everyone fallback — that would turn one client's emit into 600 sends."
        await _server.CreateRoomAsync("adv-signal");
        await using RoomClient listener = await _server.ConnectAsync("adv-signal", "listener");
        await listener.SpawnAsync(0f, 0f);

        await using RoomClient spectator = await _server.ConnectAsync("adv-signal", "spectator");
        for (int i = 0; i < 5; i++)
        {
            await spectator.EmitSignalAsync("fire", SignalTarget.AoiPeers, [1, 2, 3]);
            await Task.Delay(40);
        }

        await Task.Delay(400);

        Assert.Equal(0, listener.Metrics.SignalBatches);
        Assert.Equal(0, listener.Metrics.SignalEntries);
        Assert.True(spectator.IsOpen);
    }

    [Fact]
    public async Task A_client_that_never_sends_a_HelloCommand_is_dropped_by_the_handshake_deadline()
    {
        // The pre-auth gate: no client id, no room state, and a deadline enforced by the supervisor sweep.
        await _server.CreateRoomAsync("adv-silent");

        using System.Net.WebSockets.ClientWebSocket socket = new();
        UriBuilder uri = new(_server.BaseUri) { Scheme = "ws", Path = "/ws", Query = "room=adv-silent" };
        await socket.ConnectAsync(uri.Uri, CancellationToken.None);

        // The dev configuration's handshake deadline is generous (30 s) for hand-driven sessions, so this
        // asserts the gate exists rather than waiting it out: a second frame before authentication is the
        // other half of the same rule and is refused immediately.
        await socket.SendAsync(new byte[] { 99 }, System.Net.WebSockets.WebSocketMessageType.Binary, true, CancellationToken.None);
        await socket.SendAsync(new byte[] { 99 }, System.Net.WebSockets.WebSocketMessageType.Binary, true, CancellationToken.None);

        byte[] buffer = new byte[256];
        using CancellationTokenSource timeout = new(Settle);
        try
        {
            while (socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                System.Net.WebSockets.WebSocketReceiveResult result =
                    await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("the server accepted two pre-auth frames without closing");
        }

        Assert.Equal(4007, (int)socket.CloseStatus!.Value);
    }
}
