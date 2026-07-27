using System.Net;
using System.Net.WebSockets;
using Pix3.Rooms.LoadGen;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Tests.EndToEnd;

/// <summary>
/// The whole stack over a real socket: handshake, AOI, deltas, resume, and the transport hardening that
/// can only be observed on the wire.
/// </summary>
/// <remarks>
/// These replace a throwaway probe that was run by hand once. The behaviours it checked — a second
/// client seeing the first through AOI, an identical re-send producing zero updates, a resume keeping
/// the client id while peers see nothing — are exactly the ones no unit test can reach, because they
/// span the transport, the room and the replication core at once.
/// </remarks>
[Collection(LiveServerCollection.Name)]
public sealed class LiveSessionTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    private readonly LiveServerFixture _server;

    /// <summary>Receives the shared live server.</summary>
    public LiveSessionTests(LiveServerFixture server) => _server = server;

    [Fact]
    public async Task The_upgrade_response_never_negotiates_permessage_deflate()
    {
        // 64–316 KiB of zlib context per connection, and context takeover would break the move to
        // datagrams later. The client below explicitly asks for it, so this is a real negotiation and not
        // an absence of one.
        await _server.CreateRoomAsync("e2e-transport");

        using ClientWebSocket socket = new();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();

        UriBuilder uri = new(_server.BaseUri) { Scheme = "ws", Path = "/ws", Query = "room=e2e-transport" };
        await socket.ConnectAsync(uri.Uri, CancellationToken.None);

        Assert.NotNull(socket.HttpResponseHeaders);
        Assert.DoesNotContain(
            socket.HttpResponseHeaders!,
            header => header.Key.Equals("Sec-WebSocket-Extensions", StringComparison.OrdinalIgnoreCase));

        socket.Abort();
    }

    [Fact]
    public async Task The_root_endpoint_reveals_only_the_server_identity()
    {
        using HttpClient http = new() { BaseAddress = _server.BaseUri };

        string body = await http.GetStringAsync("/");

        Assert.StartsWith("pix3-rooms ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Rooms:", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_handshake_yields_a_welcome_and_a_final_snapshot()
    {
        await _server.CreateRoomAsync("e2e-welcome", tickHz: 20, aoiRadius: 1200f, maxVisibleEntities: 64);

        await using RoomClient client = await _server.ConnectAsync("e2e-welcome", "ada");

        Assert.Equal(ProtocolVersion.Current, client.NegotiatedVersion);
        Assert.Equal(20, client.TickHz);
        Assert.Equal(64, client.MaxVisibleEntities);
        Assert.Equal(16, client.ResumeKey.Length);
        Assert.False(client.Resumed);
        Assert.NotEqual(0u, client.ClientId);

        // "A snapshot is always sent, even when it is empty": a joiner who can see nothing still needs the
        // Final that tells it its known set is complete.
        Assert.True(
            await client.WaitForAsync(c => c.Metrics.SnapshotsCompleted > 0, Settle),
            "no final snapshot arrived");
        Assert.Equal(0, client.Metrics.SeqGaps);
    }

    [Fact]
    public async Task A_peer_sees_a_spawned_entity_through_AOI_and_then_its_movement_as_a_delta()
    {
        await _server.CreateRoomAsync("e2e-aoi");
        await using RoomClient ada = await _server.ConnectAsync("e2e-aoi", "ada");
        uint avatar = await ada.SpawnAsync(100f, 200f);

        await using RoomClient bob = await _server.ConnectAsync("e2e-aoi", "bob");
        uint bobAvatar = await bob.SpawnAsync(120f, 200f);

        Assert.True(
            await bob.WaitForAsync(c => c.KnownNetIds.Contains(avatar), Settle),
            "bob never learned about ada's entity");
        Assert.Contains(bobAvatar, bob.KnownNetIds);

        int updatesBefore = bob.Metrics.Updates;
        await ada.SendUpdateAsync(avatar, 140f, 205f, 0f);

        Assert.True(
            await bob.WaitForAsync(c => c.Metrics.Updates > updatesBefore, Settle),
            "ada's move never reached bob as an update");

        // Every one of those updates was addressed by slot against a full record bob had already received.
        Assert.Equal(0, bob.Metrics.UpdatesForUnknownSlots);
        Assert.Equal(0, bob.Metrics.SeqGaps);
        Assert.Equal(0, bob.Metrics.MalformedFrames);
    }

    [Fact]
    public async Task Re_sending_an_identical_position_costs_the_room_nothing()
    {
        // Dirty detection compares quantized integers. If it compared floats, an idle entity would stay
        // dirty forever and this test would see a stream of updates.
        await _server.CreateRoomAsync("e2e-noop");
        await using RoomClient ada = await _server.ConnectAsync("e2e-noop", "ada");
        uint avatar = await ada.SpawnAsync(100f, 200f);

        await using RoomClient bob = await _server.ConnectAsync("e2e-noop", "bob");
        Assert.True(await bob.WaitForAsync(c => c.KnownNetIds.Contains(avatar), Settle), "bob never saw ada");

        await ada.SendUpdateAsync(avatar, 140f, 205f, 0f);
        Assert.True(await bob.WaitForAsync(c => c.Metrics.Updates > 0, Settle), "the first move never arrived");
        await Task.Delay(200);

        int updatesAfterMove = bob.Metrics.Updates;
        for (int i = 0; i < 10; i++)
        {
            await ada.SendUpdateAsync(avatar, 140f, 205f, 0f);
            await Task.Delay(30);
        }

        await Task.Delay(300);
        Assert.Equal(updatesAfterMove, bob.Metrics.Updates);
    }

    [Fact]
    public async Task A_resume_inside_the_grace_keeps_the_client_id_and_is_invisible_to_peers()
    {
        await _server.CreateRoomAsync("e2e-resume");
        RoomClient ada = await _server.ConnectAsync("e2e-resume", "ada");
        uint avatar = await ada.SpawnAsync(100f, 200f);
        byte[] resumeKey = ada.ResumeKey;
        uint originalId = ada.ClientId;

        await using RoomClient bob = await _server.ConnectAsync("e2e-resume", "bob");
        Assert.True(await bob.WaitForAsync(c => c.KnownNetIds.Contains(avatar), Settle), "bob never saw ada");

        ada.Abort();   // a dropped socket, not a voluntary leave
        await Task.Delay(300);

        await using RoomClient revived = await _server.ConnectAsync("e2e-resume", "ada", resumeKey);

        Assert.True(revived.Resumed);
        Assert.Equal(originalId, revived.ClientId);
        Assert.False(resumeKey.AsSpan().SequenceEqual(revived.ResumeKey), "the resume key must be regenerated");

        // The client's entities stayed alive and its known set was rebuilt from scratch.
        Assert.True(
            await revived.WaitForAsync(c => c.KnownNetIds.Contains(avatar), Settle),
            "the resumed session never got its entity back");
        Assert.True(revived.Metrics.SnapshotsCompleted > 0);
        Assert.True(revived.Metrics.RoomVarChanges > 0, "a resumed client must be re-sent the full room-var set");

        // From bob's side the member never left.
        Assert.Equal(0, bob.Metrics.PeerLeft);
        Assert.Equal(0, bob.Metrics.SeqGaps);

        await ada.DisposeAsync();
    }

    [Fact]
    public async Task A_stale_resume_key_degrades_to_a_fresh_join_rather_than_an_error()
    {
        // "A failed resume silently degrades to a fresh join. No new error paths."
        await _server.CreateRoomAsync("e2e-badkey");

        await using RoomClient stranger = await _server.ConnectAsync("e2e-badkey", "stranger", new byte[16]);

        Assert.False(stranger.Resumed);
        Assert.Null(stranger.Rejected);
        Assert.NotEqual(0u, stranger.ClientId);
    }

    [Fact]
    public async Task A_client_below_the_minimum_version_is_rejected_with_a_typed_reason()
    {
        // Never a decoder error: the client has to be able to show a real message.
        await _server.CreateRoomAsync("e2e-version");

        await using RoomClient old = new(_server.BaseUri, "e2e-version", "old-client");
        await Assert.ThrowsAnyAsync<Exception>(() => old.ConnectAsync(announcedVersion: 1));

        Assert.Equal(RejectCode.ProtocolVersionMismatch, old.Rejected);
        Assert.True(
            await old.WaitForAsync(c => c.Metrics.CloseStatus == 4001, Settle),
            $"expected close 4001, saw {old.Metrics.CloseStatus}");
    }

    [Fact]
    public async Task A_client_announcing_a_newer_version_is_served_at_the_current_one()
    {
        // Negotiation is by range: min(client, current). This is what lets a bundle published later keep
        // working against an older fabric.
        await _server.CreateRoomAsync("e2e-newer");

        await using RoomClient future = new(_server.BaseUri, "e2e-newer", "future-client");
        await future.ConnectAsync(announcedVersion: 9);

        Assert.Equal(ProtocolVersion.Current, future.NegotiatedVersion);
    }

    [Fact]
    public async Task An_unknown_TypeId_is_ignored_and_the_session_survives_it()
    {
        await _server.CreateRoomAsync("e2e-unknown");
        await using RoomClient client = await _server.ConnectAsync("e2e-unknown", "ada");
        uint first = await client.SpawnAsync(0f, 0f);

        // 192–255 is reserved for app extensions the fabric never interprets.
        await client.SendRawFrameAsync(200, new byte[] { 1, 2, 3 });

        uint second = await client.SpawnAsync(10f, 0f);
        Assert.NotEqual(first, second);
        Assert.True(client.IsOpen);
        Assert.Null(client.Rejected);
    }

    [Fact]
    public async Task A_text_frame_is_refused_with_close_4007()
    {
        // "Text frames are rejected (close 4007)" — the wire is binary only.
        await _server.CreateRoomAsync("e2e-text");
        await using RoomClient client = await _server.ConnectAsync("e2e-text", "ada");

        await client.SendTextFrameAsync("hello");

        Assert.True(
            await client.WaitForAsync(c => c.Metrics.CloseStatus is not null, Settle),
            "the server never closed the socket");
        Assert.Equal(4007, client.Metrics.CloseStatus);
    }

    [Fact]
    public async Task A_hidden_client_receives_no_hot_frames_until_it_un_hides()
    {
        await _server.CreateRoomAsync("e2e-hidden");
        await using RoomClient ada = await _server.ConnectAsync("e2e-hidden", "ada");
        uint avatar = await ada.SpawnAsync(0f, 0f);

        await using RoomClient bob = await _server.ConnectAsync("e2e-hidden", "bob");
        Assert.True(await bob.WaitForAsync(c => c.KnownNetIds.Contains(avatar), Settle), "bob never saw ada");

        await bob.SetPrefsAsync(hidden: true, sendRateDivisor: 1);
        await Task.Delay(200);

        int framesWhileVisible = bob.Metrics.SnapshotFrames + bob.Metrics.DeltaFrames;
        for (int i = 1; i <= 10; i++)
        {
            await ada.SendUpdateAsync(avatar, i * 10f, 0f, 0f);
            await Task.Delay(30);
        }

        await Task.Delay(300);
        Assert.Equal(framesWhileVisible, bob.Metrics.SnapshotFrames + bob.Metrics.DeltaFrames);

        // Un-hiding is a resync by definition — the known set became a fiction while the tab was hidden.
        int snapshotsBefore = bob.Metrics.SnapshotsCompleted;
        await bob.SetPrefsAsync(hidden: false, sendRateDivisor: 1);

        Assert.True(
            await bob.WaitForAsync(c => c.Metrics.SnapshotsCompleted > snapshotsBefore, Settle),
            "un-hiding did not produce a fresh snapshot");
        Assert.Equal(0, bob.Metrics.SeqGaps);
    }

    [Fact]
    public async Task The_admin_API_refuses_a_request_without_the_service_token()
    {
        using HttpClient http = new() { BaseAddress = _server.BaseUri };

        using HttpResponseMessage response = await http.GetAsync("/admin/rooms");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected 401/403, saw {(int)response.StatusCode}");
    }

    [Fact]
    public async Task The_admin_API_reports_a_live_rooms_counters()
    {
        await _server.CreateRoomAsync("e2e-stats");
        await using RoomClient ada = await _server.ConnectAsync("e2e-stats", "ada");
        await ada.SpawnAsync(0f, 0f);

        using AdminApiClient admin = _server.CreateAdminClient();
        RoomStatsSnapshot? stats = null;
        for (int attempt = 0; attempt < 100 && (stats is null || stats.EntityCount == 0); attempt++)
        {
            stats = await admin.GetRoomStatsAsync("e2e-stats");
            if (stats is null || stats.EntityCount == 0)
            {
                await Task.Delay(20);
            }
        }

        Assert.NotNull(stats);
        Assert.Equal(1, stats!.PlayerCount);
        Assert.Equal(1, stats.EntityCount);
        Assert.True(stats.ServerTick > 0);
        Assert.Equal(0, stats.DroppedFrames);
    }

    [Fact]
    public async Task The_metrics_endpoint_exposes_the_room_gauges()
    {
        await _server.CreateRoomAsync("e2e-metrics");
        await using RoomClient ada = await _server.ConnectAsync("e2e-metrics", "ada");
        await ada.SpawnAsync(0f, 0f);
        await Task.Delay(200);

        using AdminApiClient admin = _server.CreateAdminClient();
        string exposition = await admin.GetMetricsAsync();

        Assert.Contains("rooms_active", exposition, StringComparison.Ordinal);
        Assert.Contains("# TYPE", exposition, StringComparison.Ordinal);
    }
}
