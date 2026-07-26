using System.Net;
using Microsoft.Extensions.Primitives;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Works out which address a connection should be billed to for per-IP quotas and logs.
/// </summary>
/// <remarks>
/// The result is a dictionary key in <see cref="IpConnectionLimiter"/>, so it must be canonical (one
/// address, one key) and it must never be raw attacker-controlled text: a header value that does not
/// parse as an IP address is discarded rather than trusted.
/// </remarks>
public static class RemoteIpResolver
{
    /// <summary>The proxy header consulted when <see cref="NetOptions.TrustForwardedHeaders"/> is on.</summary>
    public const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>Key used when no address can be determined at all (Unix socket, test host).</summary>
    public const string UnknownAddress = "unknown";

    /// <summary>
    /// The client address for this request. With <paramref name="trustForwardedHeaders"/> on, the
    /// <b>last</b> hop of <c>X-Forwarded-For</c> wins — that is the entry appended by the proxy closest
    /// to this server, and therefore the only one a client cannot forge. Falls back to the transport
    /// address when the header is absent or unparseable.
    /// </summary>
    public static string Resolve(HttpContext context, bool trustForwardedHeaders)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (trustForwardedHeaders)
        {
            StringValues header = context.Request.Headers[ForwardedForHeader];
            for (int i = header.Count - 1; i >= 0; i--)
            {
                if (TryResolveLastHop(header[i], out string forwarded))
                {
                    return forwarded;
                }
            }
        }

        IPAddress? transport = context.Connection.RemoteIpAddress;
        return transport is null ? UnknownAddress : Canonicalize(transport);
    }

    /// <summary>
    /// Reads the last hop out of one <c>X-Forwarded-For</c> header value. False when the value is empty
    /// or its last entry is not an IP address (with or without a port).
    /// </summary>
    public static bool TryResolveLastHop(string? headerValue, out string address)
    {
        address = UnknownAddress;
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        ReadOnlySpan<char> value = headerValue.AsSpan();
        int comma = value.LastIndexOf(',');
        ReadOnlySpan<char> lastHop = (comma >= 0 ? value.Slice(comma + 1) : value).Trim();
        return TryParseAddress(lastHop, out address);
    }

    /// <summary>
    /// Parses <c>1.2.3.4</c>, <c>1.2.3.4:5678</c>, <c>::1</c>, <c>[::1]:5678</c> into a canonical
    /// address string. False for anything else, so junk never becomes a quota key.
    /// </summary>
    public static bool TryParseAddress(ReadOnlySpan<char> candidate, out string address)
    {
        address = UnknownAddress;
        candidate = candidate.Trim();
        if (candidate.IsEmpty)
        {
            return false;
        }

        // [ipv6] or [ipv6]:port
        if (candidate[0] == '[')
        {
            int closing = candidate.IndexOf(']');
            if (closing <= 1)
            {
                return false;
            }

            candidate = candidate.Slice(1, closing - 1);
        }
        else
        {
            int lastColon = candidate.LastIndexOf(':');
            // A single colon means host:port; several mean a bare IPv6 literal.
            if (lastColon > 0 && candidate.IndexOf(':') == lastColon)
            {
                candidate = candidate.Slice(0, lastColon);
            }
        }

        if (!IPAddress.TryParse(candidate, out IPAddress? parsed))
        {
            return false;
        }

        address = Canonicalize(parsed);
        return true;
    }

    /// <summary>
    /// One address, one string: IPv4-mapped IPv6 addresses collapse to their IPv4 form so a dual-stack
    /// listener does not give the same client two quota buckets.
    /// </summary>
    public static string Canonicalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : address.ToString();
    }
}
