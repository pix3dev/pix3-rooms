namespace Pix3.Rooms.Server.Auth;

/// <summary>How room tokens are checked.</summary>
public enum AuthMode : byte
{
    /// <summary>Production: HS256-signed JWTs minted by pix3 cloud.</summary>
    Jwt = 0,

    /// <summary>
    /// Local development only: unsigned <c>dev:&lt;sub&gt;:&lt;roomId&gt;</c> strings. The process must
    /// refuse to start in Production — see <see cref="InsecureRoomTokenValidator"/>.
    /// </summary>
    Insecure = 1,
}

/// <summary>
/// Identity configuration, bound from section <c>Rooms:Auth</c>. A plain POCO; the composition root binds
/// it, validates it and registers the matching validator.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Rooms:Auth";

    /// <summary>Shortest accepted HS256 secret. Below the hash's 256-bit block size a signature is weak.</summary>
    public const int MinimumJwtSecretBytes = 32;

    /// <summary>Default audience claim required in room tokens.</summary>
    public const string DefaultAudience = "pix3-rooms";

    /// <summary>Default issuer claim required in room tokens.</summary>
    public const string DefaultIssuer = "pix3-cloud";

    /// <summary>Which validator to use.</summary>
    public AuthMode Mode { get; set; } = AuthMode.Jwt;

    /// <summary>HS256 signing secret, shared with whoever mints room tokens. Required when <see cref="Mode"/> is <see cref="AuthMode.Jwt"/>.</summary>
    public string JwtSecret { get; set; } = "";

    /// <summary>Required <c>iss</c> claim.</summary>
    public string Issuer { get; set; } = DefaultIssuer;

    /// <summary>Required <c>aud</c> claim.</summary>
    public string Audience { get; set; } = DefaultAudience;

    /// <summary>Tolerance applied to <c>exp</c>/<c>nbf</c> for clock drift between minter and server.</summary>
    public int ClockSkewSeconds { get; set; } = 60;

    /// <summary>
    /// Shared secret guarding the admin REST surface. Empty means the admin API denies everything —
    /// never "allows everything".
    /// </summary>
    public string ServiceToken { get; set; } = "";

    /// <summary>
    /// Throws when the configuration could not possibly work. Call this right after binding so a missing
    /// secret fails startup instead of every handshake.
    /// </summary>
    /// <exception cref="InvalidOperationException">A required value is missing or unusable.</exception>
    public void Validate()
    {
        if (ClockSkewSeconds < 0)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(ClockSkewSeconds)} must not be negative.");
        }

        if (Mode != AuthMode.Jwt)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(JwtSecret))
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(JwtSecret)} is required when {nameof(Mode)} is {nameof(AuthMode.Jwt)}.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(JwtSecret) < MinimumJwtSecretBytes)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(JwtSecret)} must be at least {MinimumJwtSecretBytes} bytes for HS256.");
        }

        if (string.IsNullOrWhiteSpace(Issuer))
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(Issuer)} is required.");
        }

        if (string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(Audience)} is required.");
        }
    }
}
