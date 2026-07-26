using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// The room registry: creates, looks up, enumerates and destroys rooms, and owns the task each room's
/// tick loop runs on.
/// </summary>
/// <remarks>
/// <para>
/// Lookups are lock-free (they happen on the socket accept path). Lifecycle changes take a small gate so
/// that the room cap and duplicate-id rejection are exact — the result of <c>TryAdd</c> is never
/// ignored, which is where the reference server silently returned a second room object for an id that
/// was already taken.
/// </para>
/// <para>
/// Every room gets its own <see cref="CancellationTokenSource"/> and its own task, so destroying or
/// crashing one room cannot touch another.
/// </para>
/// </remarks>
public sealed class RoomManager : IRoomManager, IAsyncDisposable, IDisposable
{
    private readonly IRoomFactory _roomFactory;
    private readonly RoomServerOptions _options;
    private readonly RoomDefaultsOptions _defaults;
    private readonly ILogger<RoomManager> _logger;

    private readonly ConcurrentDictionary<string, RoomHandle> _rooms = new(StringComparer.Ordinal);
    private readonly List<Task> _teardown = new();
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Creates the registry.</summary>
    /// <param name="roomFactory">Builds a room plus its own replication instance.</param>
    /// <param name="options">Server-wide room knobs (room cap, queue sizes, shutdown timeout).</param>
    /// <param name="defaults">Defaults filled into configs that left fields unspecified.</param>
    /// <param name="logger">Logger for lifecycle events.</param>
    public RoomManager(
        IRoomFactory roomFactory,
        IOptions<RoomServerOptions> options,
        IOptions<RoomDefaultsOptions> defaults,
        ILogger<RoomManager> logger)
    {
        ArgumentNullException.ThrowIfNull(roomFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(logger);

        _roomFactory = roomFactory;
        _options = options.Value;
        _options.Normalize();
        _defaults = defaults.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public int RoomCount => _rooms.Count;

    /// <inheritdoc />
    public bool TryCreate(RoomConfig config, [MaybeNullWhen(false)] out IRoom room, out RejectCode reject, out string? error)
    {
        room = null;

        if (!RoomConfigValidator.TryNormalize(config, _defaults, out RoomConfig? normalized, out reject, out error))
        {
            _logger.LogWarning("Room creation refused ({Reject}): {Error}", reject, error);
            return false;
        }

        Room created;
        RoomHandle handle;

        lock (_gate)
        {
            if (_disposed)
            {
                reject = RejectCode.ServerShuttingDown;
                error = "the server is shutting down";
                return false;
            }

            if (_rooms.Count >= _options.MaxRooms)
            {
                reject = RejectCode.QuotaExceeded;
                error = $"the server room cap ({_options.MaxRooms}) is reached";
                _logger.LogWarning("Room '{RoomId}' refused: {Error}", normalized.RoomId, error);
                return false;
            }

            try
            {
                created = _roomFactory.Create(normalized);
            }
            catch (Exception ex)
            {
                reject = RejectCode.InternalError;
                error = "the room could not be constructed";
                _logger.LogError(ex, "Room '{RoomId}' could not be constructed", normalized.RoomId);
                return false;
            }

            handle = new RoomHandle(created);

            // The result of TryAdd is authoritative: a duplicate id must fail, not overwrite or leak.
            if (!_rooms.TryAdd(normalized.RoomId, handle))
            {
                handle.Dispose();
                reject = RejectCode.BadRequest;
                error = $"room '{normalized.RoomId}' already exists";
                _logger.LogWarning("Room '{RoomId}' refused: {Error}", normalized.RoomId, error);
                return false;
            }

            handle.Task = StartRoom(handle);
        }

        _logger.LogInformation(
            "Created room {RoomId} (project {ProjectId}, build '{BuildId}'): {TickHz} Hz, {MaxPlayers} players, {MaxEntities} entities, AOI {AoiRadius}, idle TTL {IdleTtlSeconds}s. {RoomCount}/{MaxRooms} rooms alive",
            normalized.RoomId, normalized.ProjectId, normalized.BuildId, normalized.TickHz, normalized.MaxPlayers,
            normalized.MaxEntities, normalized.AoiRadius, normalized.IdleTtlSeconds, _rooms.Count, _options.MaxRooms);

        room = created;
        reject = RejectCode.None;
        error = null;
        return true;
    }

    /// <inheritdoc />
    public bool TryGet(string roomId, [MaybeNullWhen(false)] out IRoom room)
    {
        if (!string.IsNullOrEmpty(roomId) && _rooms.TryGetValue(roomId, out RoomHandle? handle))
        {
            room = handle.Room;
            return true;
        }

        room = null;
        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Non-blocking: the entry is removed first (so nobody can join a dying room), members are closed,
    /// the room's token is cancelled and its loop is observed in the background. Awaiting every loop is
    /// <see cref="DisposeAsync"/>'s job.
    /// </remarks>
    public bool Destroy(string roomId, string reason)
    {
        if (string.IsNullOrEmpty(roomId))
        {
            return false;
        }

        RoomHandle? handle;
        lock (_gate)
        {
            if (!_rooms.TryRemove(roomId, out handle))
            {
                return false;
            }
        }

        _logger.LogInformation(
            "Destroying room {RoomId} ({PlayerCount} members): {Reason}",
            roomId, handle.Room.PlayerCount, reason);

        handle.Room.CloseAll(RejectCode.RoomClosing, LeaveReason.RoomClosed, reason);
        handle.Cancel();
        TrackTeardown(handle);
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<RoomStats> ListStats()
    {
        var stats = new List<RoomStats>(_rooms.Count);
        foreach (KeyValuePair<string, RoomHandle> pair in _rooms)
        {
            stats.Add(pair.Value.Room.SnapshotStats());
        }

        return stats;
    }

    /// <inheritdoc />
    public IReadOnlyList<RoomConfig> ListConfigs()
    {
        var configs = new List<RoomConfig>(_rooms.Count);
        foreach (KeyValuePair<string, RoomHandle> pair in _rooms)
        {
            configs.Add(pair.Value.Room.Config);
        }

        return configs;
    }

    /// <summary>
    /// Destroys every room and waits (bounded by <see cref="RoomServerOptions.ShutdownTimeoutSeconds"/>)
    /// for their loops to stop.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        foreach (KeyValuePair<string, RoomHandle> pair in _rooms)
        {
            Destroy(pair.Key, "the server is shutting down");
        }

        Task[] pending;
        lock (_gate)
        {
            pending = _teardown.ToArray();
            _teardown.Clear();
        }

        if (pending.Length == 0)
        {
            return;
        }

        Task all = Task.WhenAll(pending);
        using var timeout = new CancellationTokenSource();
        Task delay = Task.Delay(TimeSpan.FromSeconds(_options.ShutdownTimeoutSeconds), timeout.Token);
        Task finished = await Task.WhenAny(all, delay).ConfigureAwait(false);

        if (ReferenceEquals(finished, all))
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            return;
        }

        _logger.LogWarning("Timed out after {Timeout}s waiting for {Count} room loops to stop",
            _options.ShutdownTimeoutSeconds, pending.Length);
    }

    /// <summary>
    /// Synchronous teardown for hosts that only dispose synchronously. Prefer
    /// <see cref="DisposeAsync"/>, which does not block a thread while rooms drain.
    /// </summary>
    public void Dispose()
    {
        try
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room manager shutdown failed");
        }
    }

    /// <remarks>
    /// <see cref="TaskCreationOptions.LongRunning"/> gives the first tick a dedicated thread instead of
    /// occupying a pool thread during startup; once the loop awaits its <c>PeriodicTimer</c> the
    /// continuations run on the pool, which is what keeps hundreds of rooms affordable.
    /// </remarks>
    private Task StartRoom(RoomHandle handle)
        => Task.Factory.StartNew(
            () => RunRoomAsync(handle),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();

    /// <summary>
    /// Wraps one room's loop: a loop that stops for any reason other than cancellation is fatal, so its
    /// members are closed with <see cref="RejectCode.InternalError"/> and the entry is removed. A room is
    /// never left half-alive in the registry.
    /// </summary>
    private async Task RunRoomAsync(RoomHandle handle)
    {
        Room roomInstance = handle.Room;
        string roomId = roomInstance.Config.RoomId;

        try
        {
            await roomInstance.RunAsync(handle.Token).ConfigureAwait(false);

            if (!handle.IsCancellationRequested)
            {
                _logger.LogError("Room {RoomId} tick loop stopped on its own; destroying the room", roomId);
                CloseAndEvict(handle, roomId, "room stopped unexpectedly");
            }
        }
        catch (OperationCanceledException) when (handle.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room {RoomId} tick loop faulted; destroying the room", roomId);
            CloseAndEvict(handle, roomId, "room internal error");
        }
        finally
        {
            // Disposes the room and its token source. Idempotent, so the Destroy path observing this
            // task afterwards can dispose again without caring who got here first.
            handle.Dispose();
        }
    }

    private void CloseAndEvict(RoomHandle handle, string roomId, string message)
    {
        try
        {
            handle.Room.CloseAll(RejectCode.InternalError, LeaveReason.Error, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room {RoomId} failed to close its members during eviction", roomId);
        }

        lock (_gate)
        {
            // Value-matching remove: never evict a replacement room that reused this id.
            _rooms.TryRemove(new KeyValuePair<string, RoomHandle>(roomId, handle));
        }

        handle.Cancel();
    }

    private void TrackTeardown(RoomHandle handle)
    {
        Task observer = ObserveShutdownAsync(handle);
        lock (_gate)
        {
            _teardown.RemoveAll(static task => task.IsCompleted);
            if (!observer.IsCompleted)
            {
                _teardown.Add(observer);
            }
        }
    }

    /// <summary>Awaits one destroyed room's loop, never throwing, then releases its token source.</summary>
    private async Task ObserveShutdownAsync(RoomHandle handle)
    {
        try
        {
            await handle.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room {RoomId} loop faulted while shutting down", handle.Room.Config.RoomId);
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>One room plus the cancellation source and task that drive its loop.</summary>
    private sealed class RoomHandle : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        internal RoomHandle(Room room)
        {
            Room = room;
            Task = System.Threading.Tasks.Task.CompletedTask;
        }

        internal Room Room { get; }

        internal Task Task { get; set; }

        internal CancellationToken Token => _cts.Token;

        internal bool IsCancellationRequested => _cts.IsCancellationRequested;

        internal void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down; cancelling twice is not an error.
            }
        }

        public void Dispose()
        {
            Room.Dispose();
            _cts.Dispose();
        }
    }
}
