using System.Diagnostics.CodeAnalysis;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Auth;

/// <summary>
/// Development-only room-token validator: it accepts unsigned <c>dev:&lt;subject&gt;:&lt;roomId&gt;</c>
/// strings (or <c>dev:&lt;subject&gt;</c> for any room) and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// There is no signature here, so anyone who can reach the port can be anyone. It exists so a developer
/// can run the editor against a local server without standing up a token minter.
/// </para>
/// <para>
/// <b>It must never run in production.</b> The composition root calls
/// <see cref="IsPermittedInEnvironment"/> before registering it and refuses to start otherwise;
/// constructing it also logs a warning banner every time.
/// </para>
/// </remarks>
public sealed class InsecureRoomTokenValidator : IRoomTokenValidator
{
    /// <summary>Every accepted token starts with this.</summary>
    public const string TokenPrefix = "dev:";

    /// <summary>Environment name this validator refuses to be used in.</summary>
    public const string ProductionEnvironmentName = "Production";

    /// <summary>Subjects starting with this are treated as guests.</summary>
    public const string GuestSubjectPrefix = "guest";

    /// <summary>Role every dev identity gets.</summary>
    public const string DefaultRole = "player";

    /// <summary>Longest dev token accepted.</summary>
    public const int MaxTokenLength = 512;

    /// <summary>Nominal lifetime reported for a dev token; they never actually expire.</summary>
    public const int TokenLifetimeMinutes = 60;

    /// <summary>Logged once at construction so an accidentally-shipped insecure build is obvious.</summary>
    public const string StartupWarning =
        "Auth:Mode=Insecure — room tokens are NOT verified. Any client can claim any identity. "
        + "Local development only.";

    /// <summary>Message the composition root should fail startup with when this validator is not permitted.</summary>
    public const string RefusalMessage =
        "Auth:Mode=Insecure is refused outside development: it accepts unsigned room tokens. "
        + "Configure Auth:Mode=Jwt and Auth:JwtSecret.";

    /// <summary>
    /// Handed back when validation fails; callers must honour the <c>false</c> return and never read it.
    /// </summary>
    private static readonly RoomTokenClaims RejectedClaims =
        new("", "", "", true, null, DateTimeOffset.MinValue);

    private readonly ILogger<InsecureRoomTokenValidator> _logger;
    private readonly IAuthFailureSink _failures;

    /// <summary>Creates the validator and logs the insecure-mode banner.</summary>
    /// <param name="logger">Logger for the startup banner.</param>
    /// <param name="failures">
    /// Where the fine-grained refusal reason is counted. Optional so an existing composition root keeps
    /// compiling; pass the transport's counter surface so <c>auth_failures_total{reason}</c> is populated.
    /// </param>
    public InsecureRoomTokenValidator(
        ILogger<InsecureRoomTokenValidator> logger,
        IAuthFailureSink? failures = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _failures = failures ?? NullAuthFailureSink.Instance;
        _logger.LogWarning(StartupWarning);
    }

    /// <summary>
    /// False for the Production environment, which is what the composition root checks before it will
    /// register this validator. Treats an unknown/empty environment name as non-production, matching
    /// ASP.NET Core's own default of <c>Production</c> only when explicitly set.
    /// </summary>
    public static bool IsPermittedInEnvironment(string? environmentName)
        => !string.Equals(environmentName, ProductionEnvironmentName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool TryValidate(
        string token,
        string requestedRoomId,
        [MaybeNullWhen(false)] out RoomTokenClaims claims,
        out RejectCode reject)
    {
        claims = RejectedClaims;
        reject = RejectCode.InvalidToken;

        if (string.IsNullOrEmpty(token))
        {
            _failures.OnAuthFailure(AuthFailureCause.MissingToken);
            return false;
        }

        if (string.IsNullOrEmpty(requestedRoomId))
        {
            _failures.OnAuthFailure(AuthFailureCause.Other);
            return false;
        }

        if (token.Length > MaxTokenLength || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            _failures.OnAuthFailure(AuthFailureCause.MalformedToken);
            return false;
        }

        ReadOnlySpan<char> body = token.AsSpan(TokenPrefix.Length);
        int separator = body.IndexOf(':');
        ReadOnlySpan<char> subject = separator < 0 ? body : body.Slice(0, separator);
        ReadOnlySpan<char> roomId = separator < 0 ? default : body.Slice(separator + 1);

        subject = subject.Trim();
        roomId = roomId.Trim();

        if (subject.IsEmpty)
        {
            _failures.OnAuthFailure(AuthFailureCause.MalformedToken);
            return false;
        }

        // Exactly two or three segments; anything else is a typo, not an identity.
        if (roomId.IndexOf(':') >= 0)
        {
            _failures.OnAuthFailure(AuthFailureCause.MalformedToken);
            return false;
        }

        if (!roomId.IsEmpty && !roomId.SequenceEqual(requestedRoomId.AsSpan()))
        {
            reject = RejectCode.TokenRoomMismatch;
            _failures.OnAuthFailure(AuthFailureCause.RoomMismatch);
            return false;
        }

        claims = new RoomTokenClaims(
            new string(subject),
            requestedRoomId,
            DefaultRole,
            subject.StartsWith(GuestSubjectPrefix, StringComparison.Ordinal),
            null,
            DateTimeOffset.UtcNow.AddMinutes(TokenLifetimeMinutes));
        reject = RejectCode.None;
        return true;
    }
}
