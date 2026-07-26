using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pix3.Rooms.Server.Replication;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// Builds the replication instance for one room.
/// </summary>
/// <remarks>
/// Replication is <b>per room</b>, never a DI singleton: it owns that room's entity table, spatial hash
/// and per-subscriber known-sets, and it is single-threaded by contract. Sharing one instance across
/// rooms is precisely the mistake (one global game model) this fabric was built to avoid.
/// </remarks>
/// <param name="config">The normalized config of the room being created.</param>
public delegate IRoomReplication RoomReplicationFactory(RoomConfig config);

/// <summary>Creates fully wired <see cref="Room"/> instances.</summary>
public interface IRoomFactory
{
    /// <summary>
    /// Builds a room and its own replication instance. <paramref name="config"/> must already have
    /// passed <see cref="RoomConfigValidator"/>.
    /// </summary>
    Room Create(RoomConfig config);
}

/// <summary>
/// Default <see cref="IRoomFactory"/>: pairs each new room with a freshly constructed
/// <see cref="IRoomReplication"/> from the injected <see cref="RoomReplicationFactory"/>.
/// </summary>
public sealed class RoomFactory : IRoomFactory
{
    private readonly RoomReplicationFactory _replicationFactory;
    private readonly RoomServerOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates the factory.</summary>
    /// <param name="replicationFactory">Produces one replication instance per room.</param>
    /// <param name="options">Server-wide room knobs; normalized here so every room sees sane values.</param>
    /// <param name="loggerFactory">Used to build each room's logger.</param>
    public RoomFactory(
        RoomReplicationFactory replicationFactory,
        IOptions<RoomServerOptions> options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(replicationFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _replicationFactory = replicationFactory;
        _options = options.Value;
        _options.Normalize();
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public Room Create(RoomConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        IRoomReplication replication = _replicationFactory(config)
            ?? throw new InvalidOperationException($"The replication factory returned null for room '{config.RoomId}'.");

        return new Room(config, replication, _options, _loggerFactory.CreateLogger<Room>());
    }
}
