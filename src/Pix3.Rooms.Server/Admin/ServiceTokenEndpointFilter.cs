using System.Diagnostics.CodeAnalysis;
using Pix3.Rooms.Server.Auth;
using Pix3.Rooms.Server.Observability;

namespace Pix3.Rooms.Server.Admin;

/// <summary>
/// Endpoint filter guarding the whole <c>/admin</c> group (and optionally <c>/metrics</c>) with the
/// shared service token.
/// </summary>
/// <remarks>
/// <para>
/// The token is read from <c>X-Service-Token</c>, or from <c>Authorization: Bearer &lt;token&gt;</c>, and
/// checked by <see cref="IServiceTokenValidator"/> in constant time.
/// </para>
/// <para>
/// A failure answers a bare <c>401</c>: no body, no hint about whether the header was missing, malformed
/// or simply wrong. The distinction is recorded in <c>auth_failures_total{reason}</c> and in a warning
/// log line instead, where only operators can see it. The token itself is never logged.
/// </para>
/// </remarks>
public sealed class ServiceTokenEndpointFilter : IEndpointFilter
{
    /// <summary>Preferred header carrying the service token.</summary>
    public const string HeaderName = "X-Service-Token";

    /// <summary>Fallback header, for clients that only speak <c>Authorization</c>.</summary>
    public const string AuthorizationHeaderName = "Authorization";

    private const string BearerPrefix = "Bearer ";

    private readonly IServiceTokenValidator _validator;
    private readonly ILogger<ServiceTokenEndpointFilter> _logger;
    private readonly RoomsMetrics? _metrics;

    /// <summary>Creates the filter. Constructed per endpoint by the framework from DI.</summary>
    /// <param name="validator">Service-token validator.</param>
    /// <param name="logger">Logger for refusal diagnostics.</param>
    /// <param name="metrics">Optional metrics facade; failures are counted when it is registered.</param>
    public ServiceTokenEndpointFilter(
        IServiceTokenValidator validator,
        ILogger<ServiceTokenEndpointFilter> logger,
        RoomsMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(logger);

        _validator = validator;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>Reads the presented service token, if any.</summary>
    /// <param name="request">Request to inspect.</param>
    /// <param name="token">The non-empty token that was presented.</param>
    /// <returns>False when no token was presented at all.</returns>
    public static bool TryReadToken(HttpRequest request, [NotNullWhen(true)] out string? token)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? direct = request.Headers[HeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(direct))
        {
            token = direct.Trim();
            return true;
        }

        string authorization = request.Headers[AuthorizationHeaderName].ToString();
        if (authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string bearer = authorization[BearerPrefix.Length..].Trim();
            if (bearer.Length > 0)
            {
                token = bearer;
                return true;
            }
        }

        token = null;
        return false;
    }

    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        HttpContext http = context.HttpContext;
        if (!TryReadToken(http.Request, out string? token))
        {
            return Refuse(http, AuthFailureReason.MissingToken, "no service token presented");
        }

        if (!_validator.IsValid(token))
        {
            return Refuse(http, AuthFailureReason.ServiceTokenInvalid, "service token rejected");
        }

        return next(context);
    }

    private ValueTask<object?> Refuse(HttpContext http, AuthFailureReason reason, string detail)
    {
        _metrics?.AuthFailures(reason).Inc();

        _logger.LogWarning(
            "Admin request refused ({Detail}): {Method} {Path} from {RemoteIp}.",
            detail,
            http.Request.Method,
            http.Request.Path.Value ?? "/",
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        return ValueTask.FromResult<object?>(TypedResults.Unauthorized());
    }
}
