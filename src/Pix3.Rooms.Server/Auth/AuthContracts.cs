using System.Diagnostics.CodeAnalysis;
using Pix3.Rooms.Protocol;

namespace Pix3.Rooms.Server.Auth;

/// <summary>The validated contents of a room token. Only ever produced by <see cref="IRoomTokenValidator"/>.</summary>
/// <param name="Subject">Stable user/identity id the token was minted for.</param>
/// <param name="RoomId">The one room this token authorises.</param>
/// <param name="Role">Application-defined role string (for example <c>player</c> or <c>spectator</c>).</param>
/// <param name="IsGuest">True for anonymous/guest identities.</param>
/// <param name="DisplayName">Name asserted by the issuer; overrides the client-supplied one when present.</param>
/// <param name="ExpiresAt">Token expiry, already checked against the configured clock skew.</param>
public sealed record RoomTokenClaims(string Subject, string RoomId, string Role,
                                     bool IsGuest, string? DisplayName, DateTimeOffset ExpiresAt);

/// <summary>
/// Why a room token was refused, at a finer grain than <see cref="RejectCode"/> expresses.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <c>auth_failures_total{reason}</c> needs a reason only the validators can know:
/// <see cref="RejectCode.InvalidToken"/> covers "no token", "not parseable" and "signature wrong", which
/// are three very different operational stories.
/// </para>
/// <para>
/// It is deliberately a <b>duplicate</b> of the Observability module's label enum rather than a reference
/// to it: the dependency arrows are <c>Protocol ← {Net, Auth, …}</c> and <c>Net → Auth</c>, so neither
/// <c>Auth</c> nor <c>Net</c> may reach into <c>Observability</c>. The composition root's metrics bridge
/// maps this onto the label enum.
/// </para>
/// <para>
/// Contiguous from zero and closed with <see cref="Other"/>, so a sink can index it with a fixed-size
/// array and keep cardinality bounded.
/// </para>
/// </remarks>
public enum AuthFailureCause : byte
{
    /// <summary>No token was presented at all.</summary>
    MissingToken = 0,

    /// <summary>The token was present but not parseable as a token of the configured kind.</summary>
    MalformedToken = 1,

    /// <summary>The token parsed but its signature did not verify against the configured key.</summary>
    InvalidSignature = 2,

    /// <summary>The token was well-formed and correctly signed but past its expiry.</summary>
    Expired = 3,

    /// <summary>The token was valid but minted for a different room than the one requested.</summary>
    RoomMismatch = 4,

    /// <summary>Anything else, and the collapse target for causes a future validator might add.</summary>
    Other = 5,
}

/// <summary>
/// Where a validator reports <i>why</i> it refused a token, so the reason can be counted without the
/// Auth module learning about metrics.
/// </summary>
/// <remarks>
/// The transport's counter surface implements this; <c>Net → Auth</c> is a declared dependency arrow, so
/// the counters can live in <c>Net</c> while the knowledge lives here. Implementations must be safe to
/// call from any thread (handshakes run on socket threads) and must never throw — a metrics hiccup must
/// not change an authentication outcome.
/// </remarks>
public interface IAuthFailureSink
{
    /// <summary>Records one refused token.</summary>
    /// <param name="cause">Why it was refused.</param>
    void OnAuthFailure(AuthFailureCause cause);
}

/// <summary>A sink that counts nothing, for tests and for a validator constructed without one.</summary>
public sealed class NullAuthFailureSink : IAuthFailureSink
{
    /// <summary>The shared instance; it holds no state.</summary>
    public static readonly NullAuthFailureSink Instance = new();

    private NullAuthFailureSink()
    {
    }

    /// <inheritdoc />
    public void OnAuthFailure(AuthFailureCause cause)
    {
    }
}

/// <summary>Validates the per-session room token presented in <see cref="HelloCommand.Token"/>.</summary>
public interface IRoomTokenValidator
{
    /// <summary>
    /// Validates a token against the room the client asked for. On false,
    /// <paramref name="reject"/> is one of <see cref="RejectCode.InvalidToken"/>,
    /// <see cref="RejectCode.TokenExpired"/> or <see cref="RejectCode.TokenRoomMismatch"/> — precise
    /// enough for the client to act on, vague enough to leak nothing. The finer-grained reason goes to
    /// the validator's <see cref="IAuthFailureSink"/>, never to the client.
    /// </summary>
    bool TryValidate(string token, string requestedRoomId, [MaybeNullWhen(false)] out RoomTokenClaims claims, out RejectCode reject);
}

/// <summary>Guards the admin REST surface with a shared service token.</summary>
public interface IServiceTokenValidator
{
    /// <summary>
    /// True when the presented token matches the configured one. Compares in constant time and treats
    /// null, empty and length-mismatched input as invalid.
    /// </summary>
    bool IsValid(string? presentedToken);
}

/// <summary>
/// Cross-site WebSocket hijacking defence: the upgrade's <c>Origin</c> must be on the allowlist.
/// </summary>
/// <remarks>
/// <para>
/// A WebSocket upgrade is <b>not</b> subject to the same-origin policy and carries the browser's cookies,
/// so without this check any page on the internet can open an authenticated socket to this server in a
/// visitor's browser. CORS does not apply; the <c>Origin</c> header is the only signal, and it is one the
/// browser sets and script cannot forge.
/// </para>
/// <para>
/// An empty allowlist accepts any origin and is permitted in <b>development only</b>. Checked before the
/// upgrade is accepted, so a rejected page costs one HTTP response and no socket.
/// </para>
/// </remarks>
public interface IOriginPolicy
{
    /// <summary>
    /// True when a client presenting <paramref name="origin"/> may open a socket. A null or empty origin
    /// means a non-browser client (the header is browser-set), which is accepted: it has no ambient
    /// credentials to hijack.
    /// </summary>
    bool IsAllowed(string? origin);
}
