using System.Diagnostics.CodeAnalysis;
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
        };
        errors = NoErrors;
        return true;
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
