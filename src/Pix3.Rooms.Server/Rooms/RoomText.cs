using System.Buffers;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// Sanitises free text that arrived from a client (chat, room-var keys, remote-event names) before it
/// is stored, logged or fanned out to other players.
/// </summary>
/// <remarks>
/// Control characters are removed rather than escaped: they defuse log injection, break client-side
/// text rendering, and no legitimate chat message needs them. Control path only — chat and room vars
/// are rate limited, so a single allocation for the cleaned string is acceptable.
/// </remarks>
public static class RoomText
{
    /// <summary>
    /// Trims, strips control characters and caps the length. Returns <see cref="string.Empty"/> for
    /// null/blank input, and returns the input instance unchanged when it needs no cleaning.
    /// </summary>
    /// <param name="value">Raw client text; may be null.</param>
    /// <param name="maxLength">Maximum characters to keep. Values below 1 yield an empty string.</param>
    public static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength < 1)
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = value.AsSpan().Trim();
        if (span.Length > maxLength)
        {
            span = span.Slice(0, maxLength);
        }

        bool clean = true;
        for (int i = 0; i < span.Length; i++)
        {
            if (char.IsControl(span[i]))
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return span.Length == value.Length ? value : new string(span);
        }

        char[] scratch = ArrayPool<char>.Shared.Rent(span.Length);
        try
        {
            int written = 0;
            for (int i = 0; i < span.Length; i++)
            {
                char c = span[i];
                if (!char.IsControl(c))
                {
                    scratch[written++] = c;
                }
            }

            // Stripping can expose new leading/trailing whitespace ("a " -> "a "), so trim again.
            return new string(scratch, 0, written).Trim();
        }
        finally
        {
            ArrayPool<char>.Shared.Return(scratch);
        }
    }
}
