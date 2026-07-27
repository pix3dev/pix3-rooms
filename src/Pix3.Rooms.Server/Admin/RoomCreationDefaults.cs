using System.Globalization;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server.Admin;

/// <summary>
/// Values the admin API substitutes for omitted <see cref="CreateRoomRequest"/> fields, read from the
/// <c>Rooms:Defaults</c> configuration section, plus the <c>Rooms:Server:MaxRooms</c> capacity rail.
/// </summary>
/// <remarks>
/// The built-in fallbacks mirror <see cref="RoomConfig"/>'s own defaults, so an empty configuration file
/// behaves the same as no admin API at all. A configured value that fails
/// <see cref="RoomLimits"/> validation is ignored in favour of the built-in default rather than turning
/// every create request into a 400.
/// </remarks>
public sealed record RoomCreationDefaults
{
    /// <summary>Section holding per-room defaults.</summary>
    public const string DefaultsSection = "Rooms:Defaults";

    /// <summary>Section holding server-wide rails.</summary>
    public const string ServerSection = "Rooms:Server";

    /// <summary>Default member cap.</summary>
    public int MaxPlayers { get; init; } = 64;

    /// <summary>Default tick rate.</summary>
    public int TickHz { get; init; } = 20;

    /// <summary>Default area-of-interest radius.</summary>
    public float AoiRadius { get; init; } = 1200f;

    /// <summary>Default empty-room TTL.</summary>
    public int IdleTtlSeconds { get; init; } = 300;

    /// <summary>Default entity-table capacity.</summary>
    public int MaxEntities { get; init; } = 4096;

    /// <summary>Default k-nearest visibility cap.</summary>
    public int MaxVisibleEntities { get; init; } = 64;

    /// <summary>Default world-space X of the low corner of a room's quantization range.</summary>
    public float WorldOriginX { get; init; } = -2048f;

    /// <summary>Default world-space Y of the low corner of a room's quantization range.</summary>
    public float WorldOriginY { get; init; } = -2048f;

    /// <summary>Default side length of the square world a room quantizes against.</summary>
    public float WorldSize { get; init; } = 4096f;

    /// <summary>
    /// Default entity-kind allowlist. Empty means "allow any kind", which the composition root refuses
    /// to start with in Production — an unknown kind faults every observer's scene code.
    /// </summary>
    public IReadOnlyList<ushort> AllowedKinds { get; init; } = [];

    /// <summary>Default authority model.</summary>
    public RoomMode Mode { get; init; } = RoomMode.Relay;

    /// <summary>
    /// Server-wide room cap, or null when unconfigured. When set, the admin API refuses creation with
    /// 503 before calling the room manager; the manager stays the authority either way.
    /// </summary>
    public int? MaxRooms { get; init; }

    /// <summary>Reads <c>Rooms:Defaults</c> and <c>Rooms:Server:MaxRooms</c>.</summary>
    public static RoomCreationDefaults FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        RoomCreationDefaults fallback = new();
        IConfigurationSection defaults = configuration.GetSection(DefaultsSection);

        int maxPlayers = defaults.GetValue<int?>("MaxPlayers") is int players && RoomLimits.IsValidMaxPlayers(players)
            ? players
            : fallback.MaxPlayers;

        int tickHz = defaults.GetValue<int?>("TickHz") is int hz && RoomLimits.IsValidTickHz(hz)
            ? hz
            : fallback.TickHz;

        float aoiRadius = defaults.GetValue<float?>("AoiRadius") is float radius && RoomLimits.IsValidAoiRadius(radius)
            ? radius
            : fallback.AoiRadius;

        int idleTtlSeconds = defaults.GetValue<int?>("IdleTtlSeconds") is int ttl && RoomLimits.IsValidIdleTtlSeconds(ttl)
            ? ttl
            : fallback.IdleTtlSeconds;

        int maxEntities = defaults.GetValue<int?>("MaxEntities") is int entities && RoomLimits.IsValidMaxEntities(entities)
            ? entities
            : fallback.MaxEntities;

        int maxVisibleEntities =
            defaults.GetValue<int?>("MaxVisibleEntities") is int visible && RoomLimits.IsValidMaxVisibleEntities(visible)
                ? visible
                : fallback.MaxVisibleEntities;

        // The world is resolved as one value, never field by field: a configured origin combined with a
        // defaulted size is a world nobody asked for, and every quantized value in the room is expressed
        // against it. An unusable trio falls back to the built-in world whole.
        float worldOriginX = defaults.GetValue<float?>("WorldOriginX") ?? fallback.WorldOriginX;
        float worldOriginY = defaults.GetValue<float?>("WorldOriginY") ?? fallback.WorldOriginY;
        float worldSize = defaults.GetValue<float?>("WorldSize") ?? fallback.WorldSize;
        if (!WorldQuantizer.IsValidWorld(worldOriginX, worldOriginY, worldSize))
        {
            worldOriginX = fallback.WorldOriginX;
            worldOriginY = fallback.WorldOriginY;
            worldSize = fallback.WorldSize;
        }

        RoomMode mode = TryParseMode(defaults.GetValue<string?>("Mode"), out RoomMode parsed) ? parsed : fallback.Mode;

        int? maxRooms = configuration.GetSection(ServerSection).GetValue<int?>("MaxRooms") is int rooms && rooms > 0
            ? rooms
            : null;

        return new RoomCreationDefaults
        {
            MaxPlayers = maxPlayers,
            TickHz = tickHz,
            AoiRadius = aoiRadius,
            IdleTtlSeconds = idleTtlSeconds,
            MaxEntities = maxEntities,
            MaxVisibleEntities = maxVisibleEntities,
            WorldOriginX = worldOriginX,
            WorldOriginY = worldOriginY,
            WorldSize = worldSize,
            AllowedKinds = ReadAllowedKinds(defaults, fallback.AllowedKinds),
            Mode = mode,
            MaxRooms = maxRooms,
        };
    }

    /// <summary>
    /// Reads <c>Rooms:Defaults:AllowedKinds</c> as an array of entity kinds, dropping entries outside the
    /// <c>u16</c> kind space and collapsing duplicates.
    /// </summary>
    /// <remarks>
    /// A bad entry is dropped rather than fatal, matching the rest of this type: a misconfigured default
    /// must not turn every create request into a 400. An allowlist that ends up empty is caught by the
    /// composition root's production refusal instead.
    /// </remarks>
    private static IReadOnlyList<ushort> ReadAllowedKinds(IConfigurationSection defaults, IReadOnlyList<ushort> fallback)
    {
        IConfigurationSection section = defaults.GetSection("AllowedKinds");
        if (!section.Exists())
        {
            return fallback;
        }

        List<ushort> kinds = [];
        HashSet<ushort> seen = [];
        foreach (IConfigurationSection entry in section.GetChildren())
        {
            if (!int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                continue;
            }

            if (value is < ushort.MinValue or > ushort.MaxValue)
            {
                continue;
            }

            ushort kind = (ushort)value;
            if (seen.Add(kind))
            {
                kinds.Add(kind);
            }
        }

        return kinds.Count == 0 ? fallback : kinds;
    }

    /// <summary>
    /// Parses a <see cref="RoomMode"/> name or numeric value, rejecting values outside the enum.
    /// </summary>
    public static bool TryParseMode(string? text, out RoomMode mode)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            mode = RoomMode.Relay;
            return false;
        }

        // Enum.TryParse accepts out-of-range numbers ("7"), so the result still has to be a defined member.
        if (Enum.TryParse(text.Trim(), ignoreCase: true, out RoomMode candidate) && Enum.IsDefined(candidate))
        {
            mode = candidate;
            return true;
        }

        mode = RoomMode.Relay;
        return false;
    }
}
