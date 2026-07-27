using System.Diagnostics.CodeAnalysis;
using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server.Admin;

/// <summary>
/// Turns a <see cref="CreateRoomRequest"/> into a validated <see cref="RoomConfig"/>, or into a precise
/// list of field errors. Nothing here touches the room manager, so it is directly unit-testable.
/// </summary>
public static class RoomCreateValidator
{
    private static readonly IReadOnlyList<AdminFieldError> NoErrors = Array.Empty<AdminFieldError>();

    /// <summary>
    /// Validates the request and fills omitted fields from <paramref name="defaults"/>.
    /// </summary>
    /// <param name="request">Parsed request body; null when the caller sent no JSON object.</param>
    /// <param name="defaults">Values for omitted fields.</param>
    /// <param name="config">The effective room configuration when validation succeeds.</param>
    /// <param name="errors">One entry per rejected field; empty on success.</param>
    /// <returns>True when <paramref name="config"/> was produced.</returns>
    public static bool TryBuildConfig(
        CreateRoomRequest? request,
        RoomCreationDefaults defaults,
        [MaybeNullWhen(false)] out RoomConfig config,
        out IReadOnlyList<AdminFieldError> errors)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        if (request is null)
        {
            config = null;
            errors = new AdminFieldError[]
            {
                new("body", "A JSON object with at least a projectId is required."),
            };
            return false;
        }

        List<AdminFieldError> problems = new(4);

        string roomId = "";
        string? requestedRoomId = Trim(request.RoomId);
        if (requestedRoomId is null)
        {
            roomId = RoomIdPolicy.Generate();
        }
        else if (!RoomIdPolicy.IsValid(requestedRoomId))
        {
            problems.Add(new AdminFieldError(
                "roomId",
                $"Must match [A-Za-z0-9_-]{{1,{RoomIdPolicy.MaxLength}}}."));
        }
        else
        {
            roomId = requestedRoomId;
        }

        string projectId = "";
        string? requestedProjectId = Trim(request.ProjectId);
        if (requestedProjectId is null)
        {
            problems.Add(new AdminFieldError("projectId", "Required."));
        }
        else if (!RoomLimits.IsValidToken(requestedProjectId, RoomLimits.ProjectIdMaxLength))
        {
            problems.Add(new AdminFieldError(
                "projectId",
                $"Must match [A-Za-z0-9._-]{{1,{RoomLimits.ProjectIdMaxLength}}}."));
        }
        else
        {
            projectId = requestedProjectId;
        }

        string buildId = "";
        string? requestedBuildId = Trim(request.BuildId);
        if (requestedBuildId is not null)
        {
            if (!RoomLimits.IsValidToken(requestedBuildId, RoomLimits.BuildIdMaxLength))
            {
                problems.Add(new AdminFieldError(
                    "buildId",
                    $"Must match [A-Za-z0-9._-]{{1,{RoomLimits.BuildIdMaxLength}}}."));
            }
            else
            {
                buildId = requestedBuildId;
            }
        }

        int maxPlayers = defaults.MaxPlayers;
        if (request.MaxPlayers is int requestedMaxPlayers)
        {
            if (RoomLimits.IsValidMaxPlayers(requestedMaxPlayers))
            {
                maxPlayers = requestedMaxPlayers;
            }
            else
            {
                problems.Add(new AdminFieldError(
                    "maxPlayers",
                    $"Must be between {RoomLimits.MinMaxPlayers} and {RoomLimits.MaxMaxPlayers}."));
            }
        }

        int tickHz = defaults.TickHz;
        if (request.TickHz is int requestedTickHz)
        {
            if (RoomLimits.IsValidTickHz(requestedTickHz))
            {
                tickHz = requestedTickHz;
            }
            else
            {
                problems.Add(new AdminFieldError(
                    "tickHz",
                    $"Must be between {RoomLimits.MinTickHz} and {RoomLimits.MaxTickHz}."));
            }
        }

        float aoiRadius = defaults.AoiRadius;
        if (request.AoiRadius is float requestedAoiRadius)
        {
            if (RoomLimits.IsValidAoiRadius(requestedAoiRadius))
            {
                aoiRadius = requestedAoiRadius;
            }
            else
            {
                problems.Add(new AdminFieldError(
                    "aoiRadius",
                    $"Must be a finite value between {RoomLimits.MinAoiRadius} and {RoomLimits.MaxAoiRadius}."));
            }
        }

        int idleTtlSeconds = defaults.IdleTtlSeconds;
        if (request.IdleTtlSeconds is int requestedIdleTtl)
        {
            if (RoomLimits.IsValidIdleTtlSeconds(requestedIdleTtl))
            {
                idleTtlSeconds = requestedIdleTtl;
            }
            else
            {
                problems.Add(new AdminFieldError(
                    "idleTtlSeconds",
                    $"Must be between {RoomLimits.MinIdleTtlSeconds} and {RoomLimits.MaxIdleTtlSeconds}."));
            }
        }

        int maxEntities = defaults.MaxEntities;
        if (request.MaxEntities is int requestedMaxEntities)
        {
            if (RoomLimits.IsValidMaxEntities(requestedMaxEntities))
            {
                maxEntities = requestedMaxEntities;
            }
            else
            {
                problems.Add(new AdminFieldError(
                    "maxEntities",
                    $"Must be between {RoomLimits.MinMaxEntities} and {RoomLimits.MaxMaxEntities}."));
            }
        }

        int maxVisibleEntities = defaults.MaxVisibleEntities;
        if (request.MaxVisibleEntities is int requestedMaxVisible)
        {
            if (RoomLimits.IsValidMaxVisibleEntities(requestedMaxVisible))
            {
                maxVisibleEntities = requestedMaxVisible;
            }
            else
            {
                problems.Add(new AdminFieldError(
                    "maxVisibleEntities",
                    $"Must be between {RoomLimits.MinMaxVisibleEntities} and {RoomLimits.MaxMaxVisibleEntities}."));
            }
        }

        TryResolveWorld(request, defaults, problems, out float worldOriginX, out float worldOriginY, out float worldSize);

        ushort[] allowedKinds = ResolveAllowedKinds(request.AllowedKinds, defaults, problems);

        RoomMode mode = defaults.Mode;
        string? requestedMode = Trim(request.Mode);
        if (requestedMode is not null)
        {
            if (RoomCreationDefaults.TryParseMode(requestedMode, out RoomMode parsedMode))
            {
                mode = parsedMode;
            }
            else
            {
                problems.Add(new AdminFieldError(
                    "mode",
                    $"Must be '{nameof(RoomMode.Relay)}' or '{nameof(RoomMode.Authoritative)}'."));
            }
        }

        if (problems.Count > 0)
        {
            config = null;
            errors = problems;
            return false;
        }

        config = new RoomConfig
        {
            RoomId = roomId,
            ProjectId = projectId,
            BuildId = buildId,
            MaxPlayers = maxPlayers,
            TickHz = tickHz,
            AoiRadius = aoiRadius,
            IdleTtlSeconds = idleTtlSeconds,
            MaxEntities = maxEntities,
            Mode = mode,
            MaxVisibleEntities = maxVisibleEntities,
            WorldOriginX = worldOriginX,
            WorldOriginY = worldOriginY,
            WorldSize = worldSize,
            AllowedKinds = allowedKinds,
        };
        errors = NoErrors;
        return true;
    }

    /// <summary>
    /// Resolves the room's world bounds, adding a field error instead of clamping anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A world is resolved as one value: supplying an origin without a size is refused rather than
    /// silently paired with the default size, because mixing a caller's origin with a default extent
    /// produces a world nobody asked for and every quantized value in the room is expressed against it.
    /// </para>
    /// <para>
    /// The final check is <see cref="RoomLimits.IsValidWorld"/> — the quantizer's own predicate, float32
    /// precision ratio included. Surfacing it here turns "the room could not be created" into a 400 with
    /// the offending field, instead of an <c>ArgumentOutOfRangeException</c> from a room factory and a 500.
    /// </para>
    /// </remarks>
    private static void TryResolveWorld(
        CreateRoomRequest request,
        RoomCreationDefaults defaults,
        List<AdminFieldError> problems,
        out float originX,
        out float originY,
        out float size)
    {
        originX = defaults.WorldOriginX;
        originY = defaults.WorldOriginY;
        size = defaults.WorldSize;

        bool originSupplied = request.WorldOriginX is not null || request.WorldOriginY is not null;
        if (request.WorldSize is not float requestedSize)
        {
            if (originSupplied)
            {
                problems.Add(new AdminFieldError(
                    "worldSize",
                    "Required when worldOriginX or worldOriginY is supplied: a world is set as a whole, "
                    + "never corner-by-corner."));
            }

            return;
        }

        float requestedOriginX = request.WorldOriginX ?? defaults.WorldOriginX;
        float requestedOriginY = request.WorldOriginY ?? defaults.WorldOriginY;

        bool finite = true;
        if (!float.IsFinite(requestedOriginX))
        {
            problems.Add(new AdminFieldError("worldOriginX", "Must be a finite number."));
            finite = false;
        }

        if (!float.IsFinite(requestedOriginY))
        {
            problems.Add(new AdminFieldError("worldOriginY", "Must be a finite number."));
            finite = false;
        }

        if (!float.IsFinite(requestedSize))
        {
            problems.Add(new AdminFieldError("worldSize", "Must be a finite number."));
            finite = false;
        }

        if (!finite)
        {
            return;
        }

        if (!RoomLimits.IsValidWorld(requestedOriginX, requestedOriginY, requestedSize))
        {
            problems.Add(new AdminFieldError(
                "worldSize",
                $"World bounds ({requestedOriginX}, {requestedOriginY}, size {requestedSize}) are unusable: "
                + $"size must be at least {WorldQuantizer.MinWorldSize} and every coordinate magnitude must "
                + $"stay below {WorldQuantizer.MaxCoordinateToSizeRatio} x size, or float32 round-tripping "
                + "stops being a fixed point and positions oscillate by a quantum forever."));
            return;
        }

        originX = requestedOriginX;
        originY = requestedOriginY;
        size = requestedSize;
    }

    /// <summary>
    /// Resolves the entity-kind allowlist, rejecting values outside the wire's <c>u16</c> kind space and
    /// lists longer than <see cref="RoomLimits.MaxAllowedKinds"/>.
    /// </summary>
    /// <remarks>
    /// An omitted <b>or explicitly empty</b> list inherits <c>Rooms:Defaults:AllowedKinds</c>. Treating
    /// <c>[]</c> as "omitted" is what makes the composition root's production refusal complete: with a
    /// non-empty configured default there is no request shape that yields a room accepting any kind.
    /// </remarks>
    private static ushort[] ResolveAllowedKinds(
        IReadOnlyList<int>? requested,
        RoomCreationDefaults defaults,
        List<AdminFieldError> problems)
    {
        if (requested is null || requested.Count == 0)
        {
            return [.. defaults.AllowedKinds];
        }

        if (requested.Count > RoomLimits.MaxAllowedKinds)
        {
            problems.Add(new AdminFieldError(
                "allowedKinds",
                $"Must hold at most {RoomLimits.MaxAllowedKinds} entries."));
            return [];
        }

        HashSet<ushort> seen = new(requested.Count);
        List<ushort> accepted = new(requested.Count);
        for (int i = 0; i < requested.Count; i++)
        {
            int kind = requested[i];
            if (!RoomLimits.IsValidKind(kind))
            {
                problems.Add(new AdminFieldError(
                    "allowedKinds",
                    $"Entry {kind} is not an entity kind: kinds are u16 indexes into the build's prefab "
                    + $"table, so every entry must be between {ushort.MinValue} and {ushort.MaxValue}."));
                return [];
            }

            ushort value = (ushort)kind;
            if (seen.Add(value))
            {
                accepted.Add(value);
            }
        }

        return [.. accepted];
    }

    /// <summary>Trims a caller-supplied string, mapping blank input to null ("field omitted").</summary>
    private static string? Trim(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
