using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Admin;

/// <summary>
/// Hard bounds the admin API enforces on room parameters, before a room manager ever sees them.
/// </summary>
/// <remarks>
/// These are sanity rails, not policy: they keep a typo or a hostile caller from asking for a room that
/// cannot be served (a 10 kHz tick, a 4-billion-entity table). Product policy lives in
/// <c>Rooms:Defaults</c>.
/// </remarks>
public static class RoomLimits
{
    /// <summary>Longest accepted project id.</summary>
    public const int ProjectIdMaxLength = 128;

    /// <summary>Longest accepted build id.</summary>
    public const int BuildIdMaxLength = 64;

    /// <summary>Longest accepted destroy reason; longer input is truncated, not rejected.</summary>
    public const int ReasonMaxLength = 200;

    /// <summary>Smallest accepted member cap.</summary>
    public const int MinMaxPlayers = 1;

    /// <summary>Largest accepted member cap. The design target is 600 in one room.</summary>
    public const int MaxMaxPlayers = 2000;

    /// <summary>Slowest accepted tick rate.</summary>
    public const int MinTickHz = 1;

    /// <summary>Fastest accepted tick rate.</summary>
    public const int MaxTickHz = 60;

    /// <summary>Smallest accepted area-of-interest radius, in world units.</summary>
    public const float MinAoiRadius = 1f;

    /// <summary>Largest accepted area-of-interest radius, in world units.</summary>
    public const float MaxAoiRadius = 1_000_000f;

    /// <summary>Smallest accepted empty-room TTL. Zero means "evict on the next sweep".</summary>
    public const int MinIdleTtlSeconds = 0;

    /// <summary>Largest accepted empty-room TTL (24 h).</summary>
    public const int MaxIdleTtlSeconds = 86_400;

    /// <summary>Smallest accepted entity-table capacity.</summary>
    public const int MinMaxEntities = 1;

    /// <summary>Largest accepted entity-table capacity: one netId slot per entity.</summary>
    public const int MaxMaxEntities = NetId.MaxSlot;

    /// <summary>True when the value is inside the accepted member-cap range.</summary>
    public static bool IsValidMaxPlayers(int value) => value is >= MinMaxPlayers and <= MaxMaxPlayers;

    /// <summary>True when the value is inside the accepted tick-rate range.</summary>
    public static bool IsValidTickHz(int value) => value is >= MinTickHz and <= MaxTickHz;

    /// <summary>True when the value is a finite radius inside the accepted range.</summary>
    public static bool IsValidAoiRadius(float value)
        => float.IsFinite(value) && value >= MinAoiRadius && value <= MaxAoiRadius;

    /// <summary>True when the value is inside the accepted TTL range.</summary>
    public static bool IsValidIdleTtlSeconds(int value)
        => value is >= MinIdleTtlSeconds and <= MaxIdleTtlSeconds;

    /// <summary>True when the value is inside the accepted entity-capacity range.</summary>
    public static bool IsValidMaxEntities(int value) => value is >= MinMaxEntities and <= MaxMaxEntities;

    /// <summary>
    /// True when the id is a non-empty, bounded token of <c>[A-Za-z0-9._-]</c>. Used for project and
    /// build ids, which travel into log lines and metrics labels.
    /// </summary>
    public static bool IsValidToken(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0 || value.Length > maxLength)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool allowed = char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' || c == '.';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
