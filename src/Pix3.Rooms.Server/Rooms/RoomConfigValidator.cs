using System.Diagnostics.CodeAnalysis;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// Turns a caller-supplied <see cref="RoomConfig"/> into the normalized config a <see cref="Room"/>
/// runs on, or refuses it with a wire-facing <see cref="RejectCode"/> and an admin-facing message.
/// </summary>
/// <remarks>
/// <para><b>Normalized silently</b> (the caller said "I don't care"):</para>
/// <list type="bullet">
///   <item><description>
///   A numeric field left at 0 or negative (<c>MaxPlayers</c>, <c>TickHz</c>, <c>AoiRadius</c>,
///   <c>MaxEntities</c>, <c>MaxVisibleEntities</c>, <c>WorldSize</c>) means "unspecified" and takes the
///   value from <see cref="RoomDefaultsOptions"/>. An unspecified <c>WorldSize</c> takes the default
///   origins with it: half a world is not a world.
///   </description></item>
///   <item><description>
///   <c>IdleTtlSeconds</c> below 0 takes the default; exactly 0 is honoured and means "reap as soon as
///   the room is empty" (still subject to the sweeper's creation grace period).
///   </description></item>
///   <item><description><c>BuildId</c> is sanitised: control characters stripped, capped at
///   <see cref="MaxBuildIdLength"/>. It is a cosmetic tag, so losing characters cannot break a room.</description></item>
///   <item><description><c>AllowedKinds</c> is de-duplicated in place: it denotes a set, so collapsing
///   repeats changes nothing a caller could observe.</description></item>
/// </list>
/// <para><b>Rejected</b> (silently changing these would surprise the caller and misprice the room):</para>
/// <list type="bullet">
///   <item><description>Missing/blank/over-long <c>RoomId</c> or <c>ProjectId</c>, or one containing
///   characters outside <c>[A-Za-z0-9._:-]</c> — those ids end up in URLs, log scopes and metric
///   labels.</description></item>
///   <item><description>A positive but out-of-range <c>MaxPlayers</c>, <c>TickHz</c>,
///   <c>MaxEntities</c>, <c>MaxVisibleEntities</c>, <c>AoiRadius</c> or <c>IdleTtlSeconds</c>. Someone
///   asking for 1000 Hz wants an error, not a room quietly running at 120 Hz.</description></item>
///   <item><description>A non-finite <c>AoiRadius</c> (NaN/±∞), which would poison the spatial hash.</description></item>
///   <item><description>World bounds that fail <see cref="WorldQuantizer.IsValidWorld"/> — non-finite, a
///   degenerate size, or a coordinate magnitude beyond
///   <see cref="WorldQuantizer.MaxCoordinateToSizeRatio"/> × size, where float32 round-tripping stops
///   being a fixed point and every position oscillates by a quantum <i>forever</i>. Clamping a world is
///   never right: it would silently move every entity in the game.</description></item>
///   <item><description>An <c>AllowedKinds</c> list longer than <see cref="MaxAllowedKinds"/>.</description></item>
///   <item><description><see cref="RoomMode.Authoritative"/>: this build only implements Relay
///   (Level 1). Silently degrading to Relay would hand the game a server that does not simulate.</description></item>
/// </list>
/// </remarks>
public static class RoomConfigValidator
{
    /// <summary>Maximum characters in a room id.</summary>
    public const int MaxRoomIdLength = 64;

    /// <summary>Maximum characters in a project id.</summary>
    public const int MaxProjectIdLength = 64;

    /// <summary>Maximum characters kept from a build tag.</summary>
    public const int MaxBuildIdLength = 64;

    /// <summary>Slowest room tick rate that still makes sense.</summary>
    public const int MinTickHz = 1;

    /// <summary>Fastest room tick rate this server will run.</summary>
    public const int MaxTickHz = 120;

    /// <summary>A room must admit at least one player.</summary>
    public const int MinMaxPlayers = 1;

    /// <summary>Hard ceiling on room membership (the flagship target is 600).</summary>
    public const int MaxMaxPlayers = 1024;

    /// <summary>A room must be able to hold at least one entity.</summary>
    public const int MinMaxEntities = 1;

    /// <summary>
    /// Entity-table ceiling: server→client records address entities by <c>u16 Slot</c>, so the table can
    /// never exceed the 16-bit slot space of the 16/16 <see cref="NetId"/> split.
    /// </summary>
    public const int MaxMaxEntities = NetId.MaxSlot;

    /// <summary>A client must be allowed to see at least one entity.</summary>
    public const int MinMaxVisibleEntities = 1;

    /// <summary>
    /// Visibility ceiling. <c>WelcomeEvent.MaxVisibleEntities</c> is a <c>u16</c> and a known set is
    /// bounded by the slot space anyway, so the two limits coincide.
    /// </summary>
    public const int MaxMaxVisibleEntities = NetId.MaxSlot;

    /// <summary>Smallest usable area-of-interest radius.</summary>
    public const float MinAoiRadius = 1f;

    /// <summary>Largest area-of-interest radius (beyond this AOI stops filtering anything).</summary>
    public const float MaxAoiRadius = 1_000_000f;

    /// <summary>Longest idle TTL: a day.</summary>
    public const int MaxIdleTtlSeconds = 86_400;

    /// <summary>
    /// Longest entity-kind allowlist. A prefab table larger than this is not an allowlist any more, and
    /// the whole point of the list is that a room states exactly what its build can instantiate.
    /// </summary>
    public const int MaxAllowedKinds = 1024;

    /// <summary>
    /// Validates and normalizes <paramref name="requested"/>.
    /// </summary>
    /// <param name="requested">Config as supplied by the admin API or a test.</param>
    /// <param name="defaults">Server defaults for unspecified numeric fields.</param>
    /// <param name="normalized">The config a room may be built from; null when validation failed.</param>
    /// <param name="reject">Wire-facing reason; <see cref="RejectCode.None"/> on success.</param>
    /// <param name="error">Human-readable detail for the admin API; null on success.</param>
    public static bool TryNormalize(
        RoomConfig requested,
        RoomDefaultsOptions defaults,
        [MaybeNullWhen(false)] out RoomConfig normalized,
        out RejectCode reject,
        out string? error)
    {
        normalized = null;
        reject = RejectCode.None;
        error = null;

        if (requested is null)
        {
            reject = RejectCode.BadRequest;
            error = "room config is required";
            return false;
        }

        ArgumentNullException.ThrowIfNull(defaults);

        string roomId = requested.RoomId;
        if (!IsWellFormedId(roomId, MaxRoomIdLength))
        {
            reject = RejectCode.BadRequest;
            error = $"roomId must be 1..{MaxRoomIdLength} characters from [A-Za-z0-9._:-]";
            return false;
        }

        string projectId = requested.ProjectId;
        if (!IsWellFormedId(projectId, MaxProjectIdLength))
        {
            reject = RejectCode.BadRequest;
            error = $"projectId must be 1..{MaxProjectIdLength} characters from [A-Za-z0-9._:-]";
            return false;
        }

        if (requested.Mode != RoomMode.Relay)
        {
            reject = RejectCode.BadRequest;
            error = $"room mode {requested.Mode} is not supported by this build; only {RoomMode.Relay} is implemented";
            return false;
        }

        int maxPlayers = requested.MaxPlayers <= 0 ? defaults.MaxPlayers : requested.MaxPlayers;
        if (!TryRange(maxPlayers, MinMaxPlayers, MaxMaxPlayers, "maxPlayers", ref reject, ref error))
        {
            return false;
        }

        int tickHz = requested.TickHz <= 0 ? defaults.TickHz : requested.TickHz;
        if (!TryRange(tickHz, MinTickHz, MaxTickHz, "tickHz", ref reject, ref error))
        {
            return false;
        }

        int maxEntities = requested.MaxEntities <= 0 ? defaults.MaxEntities : requested.MaxEntities;
        if (!TryRange(maxEntities, MinMaxEntities, MaxMaxEntities, "maxEntities", ref reject, ref error))
        {
            return false;
        }

        int maxVisibleEntities = requested.MaxVisibleEntities <= 0
            ? defaults.MaxVisibleEntities
            : requested.MaxVisibleEntities;
        if (!TryRange(maxVisibleEntities, MinMaxVisibleEntities, MaxMaxVisibleEntities, "maxVisibleEntities", ref reject, ref error))
        {
            return false;
        }

        float aoiRadius = requested.AoiRadius;
        if (float.IsNaN(aoiRadius) || float.IsInfinity(aoiRadius))
        {
            reject = RejectCode.BadRequest;
            error = "aoiRadius must be a finite number";
            return false;
        }

        if (aoiRadius <= 0f)
        {
            aoiRadius = defaults.AoiRadius;
        }

        if (aoiRadius < MinAoiRadius || aoiRadius > MaxAoiRadius)
        {
            reject = RejectCode.BadRequest;
            error = $"aoiRadius must be within [{MinAoiRadius}, {MaxAoiRadius}]";
            return false;
        }

        int idleTtl = requested.IdleTtlSeconds < 0 ? defaults.IdleTtlSeconds : requested.IdleTtlSeconds;
        if (idleTtl < 0 || idleTtl > MaxIdleTtlSeconds)
        {
            reject = RejectCode.BadRequest;
            error = $"idleTtlSeconds must be within [0, {MaxIdleTtlSeconds}]";
            return false;
        }

        if (!TryResolveWorld(requested, defaults, out float worldOriginX, out float worldOriginY, out float worldSize, out error))
        {
            reject = RejectCode.BadRequest;
            return false;
        }

        if (!TryResolveAllowedKinds(requested.AllowedKinds, out ushort[] allowedKinds, out error))
        {
            reject = RejectCode.BadRequest;
            return false;
        }

        // Defaults themselves can be misconfigured; a bad default must not create an unrunnable room.
        maxPlayers = Math.Clamp(maxPlayers, MinMaxPlayers, MaxMaxPlayers);
        tickHz = Math.Clamp(tickHz, MinTickHz, MaxTickHz);
        maxEntities = Math.Clamp(maxEntities, MinMaxEntities, MaxMaxEntities);
        maxVisibleEntities = Math.Clamp(maxVisibleEntities, MinMaxVisibleEntities, MaxMaxVisibleEntities);
        aoiRadius = Math.Clamp(aoiRadius, MinAoiRadius, MaxAoiRadius);
        idleTtl = Math.Clamp(idleTtl, 0, MaxIdleTtlSeconds);

        normalized = new RoomConfig
        {
            RoomId = roomId,
            ProjectId = projectId,
            BuildId = RoomText.Sanitize(requested.BuildId, MaxBuildIdLength),
            MaxPlayers = maxPlayers,
            TickHz = tickHz,
            AoiRadius = aoiRadius,
            IdleTtlSeconds = idleTtl,
            MaxEntities = maxEntities,
            Mode = RoomMode.Relay,
            MaxVisibleEntities = maxVisibleEntities,
            WorldOriginX = worldOriginX,
            WorldOriginY = worldOriginY,
            WorldSize = worldSize,
            AllowedKinds = allowedKinds,
        };
        return true;
    }

    /// <summary>
    /// True when <paramref name="value"/> is a non-empty id of at most <paramref name="maxLength"/>
    /// characters drawn from <c>[A-Za-z0-9._:-]</c>.
    /// </summary>
    public static bool IsWellFormedId(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool ok = (c >= 'a' && c <= 'z')
                   || (c >= 'A' && c <= 'Z')
                   || (c >= '0' && c <= '9')
                   || c == '.' || c == '_' || c == '-' || c == ':';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves the room's world bounds and refuses anything the quantizer could not round-trip.
    /// </summary>
    /// <remarks>
    /// <see cref="WorldQuantizer.IsValidWorld"/> is the authority, precision ratio included: outside it
    /// the encode→decode→encode fixed point silently stops holding, so the failure mode is not a bad
    /// number but an entire room whose entities jitter by a quantum and never settle.
    /// </remarks>
    private static bool TryResolveWorld(
        RoomConfig requested,
        RoomDefaultsOptions defaults,
        out float originX,
        out float originY,
        out float size,
        out string? error)
    {
        originX = requested.WorldOriginX;
        originY = requested.WorldOriginY;
        size = requested.WorldSize;
        error = null;

        if (!float.IsFinite(originX) || !float.IsFinite(originY) || !float.IsFinite(size))
        {
            error = "worldOriginX, worldOriginY and worldSize must all be finite numbers";
            return false;
        }

        if (size <= 0f)
        {
            // No size means no world was specified, so the origins come from the defaults too — mixing a
            // caller's origin with a default size is how you get a world nobody asked for.
            originX = defaults.WorldOriginX;
            originY = defaults.WorldOriginY;
            size = defaults.WorldSize;
        }

        if (!WorldQuantizer.IsValidWorld(originX, originY, size))
        {
            error = $"world bounds ({originX}, {originY}, size {size}) are unusable: all three must be finite, "
                  + $"size at least {WorldQuantizer.MinWorldSize}, and every coordinate magnitude below "
                  + $"{WorldQuantizer.MaxCoordinateToSizeRatio} × size — beyond that ratio float32 round-tripping "
                  + "stops being a fixed point and positions oscillate by a quantum forever";
            return false;
        }

        return true;
    }

    /// <summary>Copies the allowlist into an immutable array, collapsing duplicates.</summary>
    private static bool TryResolveAllowedKinds(
        IReadOnlyList<ushort>? requested,
        out ushort[] kinds,
        out string? error)
    {
        error = null;

        if (requested is null || requested.Count == 0)
        {
            kinds = [];
            return true;
        }

        if (requested.Count > MaxAllowedKinds)
        {
            kinds = [];
            error = $"allowedKinds must hold at most {MaxAllowedKinds} entries";
            return false;
        }

        var seen = new HashSet<ushort>(requested.Count);
        var accepted = new List<ushort>(requested.Count);
        for (int i = 0; i < requested.Count; i++)
        {
            ushort kind = requested[i];
            if (seen.Add(kind))
            {
                accepted.Add(kind);
            }
        }

        kinds = accepted.ToArray();
        return true;
    }

    private static bool TryRange(int value, int min, int max, string name, ref RejectCode reject, ref string? error)
    {
        if (value >= min && value <= max)
        {
            return true;
        }

        reject = RejectCode.BadRequest;
        error = $"{name} must be within [{min}, {max}]";
        return false;
    }
}
