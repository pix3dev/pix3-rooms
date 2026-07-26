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

/// <summary>Validates the per-session room token presented in <see cref="HelloRequest.Token"/>.</summary>
public interface IRoomTokenValidator
{
    /// <summary>
    /// Validates a token against the room the client asked for. On false,
    /// <paramref name="reject"/> is one of <see cref="RejectCode.InvalidToken"/>,
    /// <see cref="RejectCode.TokenExpired"/> or <see cref="RejectCode.TokenRoomMismatch"/> — precise
    /// enough for the client to act on, vague enough to leak nothing.
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
