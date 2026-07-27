using System.Diagnostics.CodeAnalysis;

namespace Pix3.Rooms.Server.Auth;

/// <summary>
/// Configuration-driven <see cref="IOriginPolicy"/> over <c>Rooms:Auth:AllowedOrigins</c>.
/// </summary>
/// <remarks>
/// <para>
/// Origins are normalised once, at construction, to <c>scheme://host[:port]</c> with the default port for
/// the scheme elided, and compared case-insensitively. Doing it once means the accept path is a short
/// ordinal scan over a pre-built array with no allocation and no <see cref="Uri"/> construction per
/// request — an upgrade flood must cost as little as possible.
/// </para>
/// <para>
/// <b>An empty allowlist accepts everything</b> and is permitted in development only; the composition
/// root refuses to start a production process with an empty list. That default is deliberate: a developer
/// running the editor from <c>localhost:8123</c>, a file URL or a preview host must not have to configure
/// origins before the first socket works.
/// </para>
/// </remarks>
public sealed class ConfiguredOriginPolicy : IOriginPolicy
{
    /// <summary>Longest <c>Origin</c> header value considered; beyond it no configured origin could match.</summary>
    public const int MaxOriginLength = 512;

    /// <summary>The literal a browser sends for a privacy-sensitive context (file URL, sandboxed frame).</summary>
    public const string OpaqueOrigin = "null";

    /// <summary>Message the composition root should fail startup with when the list is empty in production.</summary>
    public const string EmptyAllowlistRefusalMessage =
        "Rooms:Auth:AllowedOrigins is empty, which accepts any Origin. A WebSocket upgrade is not subject "
        + "to the same-origin policy and carries the browser's cookies, so an empty allowlist lets any page "
        + "on the internet open an authenticated socket in a visitor's browser. Configure the game's origins.";

    /// <summary>Normalised allowlist. Empty means "accept any origin".</summary>
    private readonly string[] _allowed;

    /// <summary>Builds the policy from configuration.</summary>
    /// <param name="options">Auth options carrying <see cref="AuthOptions.AllowedOrigins"/>.</param>
    /// <exception cref="InvalidOperationException">An entry is not a usable absolute origin.</exception>
    public ConfiguredOriginPolicy(AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string[] configured = options.AllowedOrigins;
        List<string> normalised = new(configured.Length);
        for (int i = 0; i < configured.Length; i++)
        {
            string entry = configured[i];
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (!TryNormalize(entry, out string canonical))
            {
                // Fail loudly at startup: a typo here silently widens or closes the whole surface, and a
                // policy that cannot express what the operator meant must not exist at all.
                throw new InvalidOperationException(
                    $"{AuthOptions.SectionName}:{nameof(AuthOptions.AllowedOrigins)} entry '{entry}' is not an "
                    + "absolute origin. Use the form scheme://host[:port], for example https://play.example.com.");
            }

            if (!normalised.Contains(canonical, StringComparer.Ordinal))
            {
                normalised.Add(canonical);
            }
        }

        _allowed = [.. normalised];
    }

    /// <summary>True when the allowlist is empty, i.e. every origin is accepted (development only).</summary>
    public bool AllowsAnyOrigin => _allowed.Length == 0;

    /// <summary>The normalised allowlist, for startup logging and tests.</summary>
    public IReadOnlyList<string> AllowedOrigins => _allowed;

    /// <inheritdoc />
    public bool IsAllowed(string? origin)
    {
        if (_allowed.Length == 0)
        {
            return true;
        }

        // No Origin header at all means a non-browser client (curl, LoadGen, a native game): it carries no
        // ambient cookies, so there is nothing for a third-party page to hijack. Refusing it would break
        // every non-browser client without closing an attack.
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        if (origin.Length > MaxOriginLength)
        {
            return false;
        }

        // "null" is what a browser sends from an opaque origin (file://, a sandboxed iframe). It must be
        // matched literally if allowed at all, never treated as absent.
        if (!TryNormalize(origin, out string canonical))
        {
            return false;
        }

        for (int i = 0; i < _allowed.Length; i++)
        {
            if (string.Equals(_allowed[i], canonical, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reduces an origin to <c>scheme://host[:port]</c>, lower-cased, with the scheme's default port
    /// elided so <c>https://a.example</c> and <c>https://a.example:443</c> are one entry. False when the
    /// value is not an absolute HTTP(S) origin; the literal <c>null</c> normalises to itself.
    /// </summary>
    public static bool TryNormalize(string? value, [NotNullWhen(true)] out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (string.Equals(trimmed, OpaqueOrigin, StringComparison.OrdinalIgnoreCase))
        {
            canonical = OpaqueOrigin;
            return true;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (uri.Host.Length == 0)
        {
            return false;
        }

        // GetLeftPart(Authority) already elides the scheme's default port and lower-cases scheme and host.
        canonical = uri.GetLeftPart(UriPartial.Authority);
        return canonical.Length > 0;
    }
}
