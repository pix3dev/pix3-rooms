using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Auth;

/// <summary>
/// Production room-token validator: HS256 JWTs minted by pix3 cloud, bound to a single room.
/// </summary>
/// <remarks>
/// <para>
/// Everything that can be pinned is pinned: the algorithm list is explicitly <c>HS256</c> (so a token
/// cannot talk the validator into <c>none</c> or into an asymmetric algorithm), issuer and audience are
/// required, an expiry is required, and lifetime is checked with a configured clock skew.
/// </para>
/// <para>
/// The <c>roomId</c> claim must equal the room the client asked for. Without that check a token for a
/// cheap public room would open every room on the server.
/// </para>
/// </remarks>
public sealed class JwtRoomTokenValidator : IRoomTokenValidator
{
    /// <summary>Claim naming the one room this token authorises.</summary>
    public const string RoomIdClaim = "roomId";

    /// <summary>Claim carrying an application-defined role.</summary>
    public const string RoleClaim = "role";

    /// <summary>Claim marking an anonymous identity.</summary>
    public const string GuestClaim = "guest";

    /// <summary>Claim carrying an issuer-asserted display name.</summary>
    public const string NameClaim = "name";

    /// <summary>Subjects starting with this are guests even without an explicit <see cref="GuestClaim"/>.</summary>
    public const string GuestSubjectPrefix = "guest:";

    /// <summary>Role assumed when the token does not name one.</summary>
    public const string DefaultRole = "player";

    /// <summary>Longest token string accepted. Anything larger is refused without being parsed.</summary>
    public const int MaxTokenLength = 8_192;

    /// <summary>
    /// Handed back when validation fails. Callers must honour the <c>false</c> return and never read it;
    /// it exists so the out parameter is always a real object instead of a null the contract forbids.
    /// </summary>
    private static readonly RoomTokenClaims RejectedClaims =
        new("", "", "", true, null, DateTimeOffset.MinValue);

    private readonly JsonWebTokenHandler _handler = new();
    private readonly TokenValidationParameters _parameters;
    private readonly ILogger<JwtRoomTokenValidator> _logger;

    /// <summary>Creates the validator. Throws when the configuration could never validate a token.</summary>
    /// <exception cref="InvalidOperationException">The signing secret, issuer or audience is unusable.</exception>
    public JwtRoomTokenValidator(AuthOptions options, ILogger<JwtRoomTokenValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        // Fail fast and loudly: a validator that cannot verify a signature must not exist at all.
        if (string.IsNullOrWhiteSpace(options.JwtSecret))
        {
            throw new InvalidOperationException($"{AuthOptions.SectionName}:{nameof(AuthOptions.JwtSecret)} is required for JWT room tokens.");
        }

        byte[] secret = Encoding.UTF8.GetBytes(options.JwtSecret);
        if (secret.Length < AuthOptions.MinimumJwtSecretBytes)
        {
            throw new InvalidOperationException(
                $"{AuthOptions.SectionName}:{nameof(AuthOptions.JwtSecret)} must be at least {AuthOptions.MinimumJwtSecretBytes} bytes for HS256.");
        }

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            throw new InvalidOperationException($"{AuthOptions.SectionName}:{nameof(AuthOptions.Issuer)} is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new InvalidOperationException($"{AuthOptions.SectionName}:{nameof(AuthOptions.Audience)} is required.");
        }

        _logger = logger;
        _parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(secret),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds),
            // Pinned: without this an attacker chooses the algorithm, and "none" is an algorithm.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            NameClaimType = NameClaim,
            RoleClaimType = RoleClaim,
        };
    }

    /// <inheritdoc />
    public bool TryValidate(
        string token,
        string requestedRoomId,
        [MaybeNullWhen(false)] out RoomTokenClaims claims,
        out RejectCode reject)
    {
        claims = RejectedClaims;
        reject = RejectCode.InvalidToken;

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrEmpty(requestedRoomId))
        {
            return false;
        }

        if (token.Length > MaxTokenLength)
        {
            _logger.LogDebug("Refused a room token of {Length} characters", token.Length);
            return false;
        }

        TokenValidationResult result;
        try
        {
            // HS256 with a static in-memory key is pure CPU work: the task is already complete, so this
            // never blocks a thread. The seam is synchronous because the handshake runs inline on the
            // socket's receive loop.
            result = _handler.ValidateTokenAsync(token, _parameters).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Room-token validation threw");
            return false;
        }

        if (!result.IsValid)
        {
            reject = MapFailure(result.Exception);
            _logger.LogDebug("Rejected a room token: {RejectCode} ({Detail})", reject, result.Exception?.GetType().Name ?? "unspecified");
            return false;
        }

        if (result.SecurityToken is not JsonWebToken jwt)
        {
            _logger.LogWarning("Room-token validation produced an unexpected token type {TokenType}", result.SecurityToken?.GetType().Name ?? "null");
            return false;
        }

        if (!jwt.TryGetPayloadValue(RoomIdClaim, out string tokenRoomId) || string.IsNullOrEmpty(tokenRoomId))
        {
            _logger.LogDebug("Rejected a room token with no {Claim} claim", RoomIdClaim);
            return false;
        }

        if (!string.Equals(tokenRoomId, requestedRoomId, StringComparison.Ordinal))
        {
            reject = RejectCode.TokenRoomMismatch;
            return false;
        }

        string subject = jwt.Subject;
        if (string.IsNullOrEmpty(subject))
        {
            _logger.LogDebug("Rejected a room token with no sub claim");
            return false;
        }

        claims = new RoomTokenClaims(
            subject,
            tokenRoomId,
            ReadRole(jwt),
            ReadGuestFlag(jwt, subject),
            ReadDisplayName(jwt),
            new DateTimeOffset(DateTime.SpecifyKind(jwt.ValidTo, DateTimeKind.Utc)));
        reject = RejectCode.None;
        return true;
    }

    private static string ReadRole(JsonWebToken jwt)
        => jwt.TryGetPayloadValue(RoleClaim, out string role) && !string.IsNullOrWhiteSpace(role)
            ? role
            : DefaultRole;

    /// <summary>
    /// A guest is either explicitly flagged or identified by a <c>guest:</c> subject. Both are honoured
    /// because different minters express it differently, and the flag must never be inferred as "false"
    /// just because the claim uses the other form.
    /// </summary>
    private static bool ReadGuestFlag(JsonWebToken jwt, string subject)
    {
        if (jwt.TryGetPayloadValue(GuestClaim, out bool flagged) && flagged)
        {
            return true;
        }

        if (jwt.TryGetPayloadValue(GuestClaim, out string flaggedText)
            && bool.TryParse(flaggedText, out bool parsed)
            && parsed)
        {
            return true;
        }

        return subject.StartsWith(GuestSubjectPrefix, StringComparison.Ordinal);
    }

    private static string? ReadDisplayName(JsonWebToken jwt)
        => jwt.TryGetPayloadValue(NameClaim, out string name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;

    /// <summary>
    /// Expiry is reported separately from "invalid" so a client can distinguish "get a new token" from
    /// "your token is wrong", which are very different fixes.
    /// </summary>
    private static RejectCode MapFailure(Exception? exception) => exception switch
    {
        SecurityTokenExpiredException => RejectCode.TokenExpired,
        _ => RejectCode.InvalidToken,
    };
}
