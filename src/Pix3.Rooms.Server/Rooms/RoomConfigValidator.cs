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
///   <c>MaxEntities</c>) means "unspecified" and takes the value from <see cref="RoomDefaultsOptions"/>.
///   </description></item>
///   <item><description>
///   <c>IdleTtlSeconds</c> below 0 takes the default; exactly 0 is honoured and means "reap as soon as
///   the room is empty" (still subject to the sweeper's creation grace period).
///   </description></item>
///   <item><description><c>BuildId</c> is sanitised: control characters stripped, capped at
///   <see cref="MaxBuildIdLength"/>. It is a cosmetic tag, so losing characters cannot break a room.</description></item>
/// </list>
/// <para><b>Rejected</b> (silently changing these would surprise the caller and misprice the room):</para>
/// <list type="bullet">
///   <item><description>Missing/blank/over-long <c>RoomId</c> or <c>ProjectId</c>, or one containing
///   characters outside <c>[A-Za-z0-9._:-]</c> — those ids end up in URLs, log scopes and metric
///   labels.</description></item>
///   <item><description>A positive but out-of-range <c>MaxPlayers</c>, <c>TickHz</c>,
///   <c>MaxEntities</c>, <c>AoiRadius</c> or <c>IdleTtlSeconds</c>. Someone asking for 1000 Hz wants an
///   error, not a room quietly running at 120 Hz.</description></item>
///   <item><description>A non-finite <c>AoiRadius</c> (NaN/±∞), which would poison the spatial hash.</description></item>
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

    /// <summary>Entity-table ceiling: the <c>netId</c> slot field is 20 bits wide.</summary>
    public const int MaxMaxEntities = NetId.MaxSlot;

    /// <summary>Smallest usable area-of-interest radius.</summary>
    public const float MinAoiRadius = 1f;

    /// <summary>Largest area-of-interest radius (beyond this AOI stops filtering anything).</summary>
    public const float MaxAoiRadius = 1_000_000f;

    /// <summary>Longest idle TTL: a day.</summary>
    public const int MaxIdleTtlSeconds = 86_400;

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

        // Defaults themselves can be misconfigured; a bad default must not create an unrunnable room.
        maxPlayers = Math.Clamp(maxPlayers, MinMaxPlayers, MaxMaxPlayers);
        tickHz = Math.Clamp(tickHz, MinTickHz, MaxTickHz);
        maxEntities = Math.Clamp(maxEntities, MinMaxEntities, MaxMaxEntities);
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
