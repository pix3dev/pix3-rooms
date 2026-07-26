using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// Destroys rooms that have been empty longer than their <see cref="RoomConfig.IdleTtlSeconds"/>, so an
/// abandoned room stops costing a tick loop.
/// </summary>
/// <remarks>
/// <para>
/// Two guards keep the sweeper from reaping something alive: a room younger than
/// <see cref="RoomServerOptions.RoomCreationGraceSeconds"/> is never touched (a room is usually created
/// moments <i>before</i> its first player connects), and a room is only considered when it currently has
/// no members <b>and</b> its last activity is older than its TTL.
/// </para>
/// <para>
/// A sweep that throws must not take the background service down with it, so each pass is isolated and
/// logged.
/// </para>
/// </remarks>
public sealed class RoomIdleSweeper : BackgroundService
{
    private readonly IRoomManager _rooms;
    private readonly RoomServerOptions _options;
    private readonly ILogger<RoomIdleSweeper> _logger;

    /// <summary>Creates the sweeper.</summary>
    /// <param name="rooms">The registry to sweep.</param>
    /// <param name="options">Sweep interval and creation grace period.</param>
    /// <param name="logger">Logger for evictions and failures.</param>
    public RoomIdleSweeper(IRoomManager rooms, IOptions<RoomServerOptions> options, ILogger<RoomIdleSweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _rooms = rooms;
        _options = options.Value;
        _options.Normalize();
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.IdleSweepIntervalSeconds);
        _logger.LogInformation(
            "Room idle sweeper started: every {IntervalSeconds}s, {GraceSeconds}s creation grace",
            _options.IdleSweepIntervalSeconds, _options.RoomCreationGraceSeconds);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Sweep();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Runs one pass. Public so the behaviour is directly testable without a host.</summary>
    /// <returns>How many rooms were destroyed.</returns>
    public int Sweep()
    {
        int destroyed = 0;

        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan grace = TimeSpan.FromSeconds(_options.RoomCreationGraceSeconds);
            IReadOnlyList<RoomConfig> configs = _rooms.ListConfigs();

            for (int i = 0; i < configs.Count; i++)
            {
                RoomConfig config = configs[i];
                if (!_rooms.TryGet(config.RoomId, out IRoom? room))
                {
                    continue;
                }

                if (room.PlayerCount > 0)
                {
                    continue;
                }

                if (now - room.CreatedAt < grace)
                {
                    continue;
                }

                TimeSpan idle = now - room.LastActivityAt;
                if (idle < TimeSpan.FromSeconds(config.IdleTtlSeconds))
                {
                    continue;
                }

                if (!_rooms.Destroy(config.RoomId, $"idle for {idle.TotalSeconds:F0}s (TTL {config.IdleTtlSeconds}s)"))
                {
                    continue;
                }

                destroyed++;
                _logger.LogInformation(
                    "Swept idle room {RoomId} after {IdleSeconds:F0}s empty (TTL {IdleTtlSeconds}s)",
                    config.RoomId, idle.TotalSeconds, config.IdleTtlSeconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room idle sweep failed; retrying on the next interval");
        }

        return destroyed;
    }
}
