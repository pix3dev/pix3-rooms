using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Pix3.Rooms.Server.Admin;

/// <summary>
/// Room id rules for the admin API: what an operator may ask for, what the server invents when they
/// don't, and the WebSocket path a client uses to reach the room.
/// </summary>
/// <remarks>
/// Ids end up in log lines, URLs and metrics labels, so the charset is deliberately narrow:
/// <c>[A-Za-z0-9_-]{1,64}</c>. Nothing needs escaping and nothing can be confused for a path segment.
/// </remarks>
public static class RoomIdPolicy
{
    /// <summary>Longest accepted room id.</summary>
    public const int MaxLength = 64;

    /// <summary>Length of a generated id (96 bits of entropy, base64url).</summary>
    public const int GeneratedLength = 16;

    /// <summary>Route the WebSocket endpoint listens on.</summary>
    public const string WebSocketRoute = "/ws";

    private const int GeneratedEntropyBytes = 12;

    /// <summary>True when <paramref name="roomId"/> is a well-formed room id.</summary>
    public static bool IsValid([NotNullWhen(true)] string? roomId)
    {
        if (roomId is null || roomId.Length == 0 || roomId.Length > MaxLength)
        {
            return false;
        }

        for (int i = 0; i < roomId.Length; i++)
        {
            char c = roomId[i];
            bool allowed = char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Generates a URL-safe random room id.</summary>
    public static string Generate()
    {
        Span<byte> entropy = stackalloc byte[GeneratedEntropyBytes];
        RandomNumberGenerator.Fill(entropy);

        Span<char> chars = stackalloc char[GeneratedLength];
        if (!Convert.TryToBase64Chars(entropy, chars, out int written))
        {
            throw new InvalidOperationException("Failed to encode a generated room id.");
        }

        for (int i = 0; i < written; i++)
        {
            chars[i] = chars[i] switch
            {
                '+' => '-',
                '/' => '_',
                char c => c,
            };
        }

        return new string(chars[..written]);
    }

    /// <summary>The path (with query) a client opens to join the room.</summary>
    public static string WebSocketPath(string roomId)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        return $"{WebSocketRoute}?room={Uri.EscapeDataString(roomId)}";
    }
}
