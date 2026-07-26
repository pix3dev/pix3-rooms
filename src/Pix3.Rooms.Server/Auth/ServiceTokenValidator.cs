using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Pix3.Rooms.Server.Auth;

/// <summary>
/// Guards the admin REST surface with a shared service token, compared in fixed time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unconfigured means deny.</b> An empty <see cref="AuthOptions.ServiceToken"/> refuses every request.
/// The alternative — treating "no token configured" as "no auth required" — turns a forgotten setting
/// into an open room-lifecycle API.
/// </para>
/// <para>
/// <b>Fixed time.</b> The comparison runs through
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte},ReadOnlySpan{byte})"/>, and a
/// length mismatch still performs an equal-cost comparison so response timing does not leak the token's
/// length.
/// </para>
/// </remarks>
public sealed class ServiceTokenValidator : IServiceTokenValidator
{
    /// <summary>Longest presented token accepted; beyond this it cannot be the configured secret.</summary>
    public const int MaxTokenLength = 4_096;

    /// <summary>Presented tokens up to this many bytes are compared on the stack.</summary>
    private const int StackBufferBytes = 512;

    private readonly byte[] _expected;

    /// <summary>Creates the validator from configuration.</summary>
    public ServiceTokenValidator(AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _expected = string.IsNullOrEmpty(options.ServiceToken)
            ? []
            : Encoding.UTF8.GetBytes(options.ServiceToken);
    }

    /// <summary>False when no service token is configured, in which case every request is denied.</summary>
    public bool IsConfigured => _expected.Length > 0;

    /// <inheritdoc />
    public bool IsValid(string? presentedToken)
    {
        if (_expected.Length == 0)
        {
            return false;
        }

        if (string.IsNullOrEmpty(presentedToken) || presentedToken.Length > MaxTokenLength)
        {
            return false;
        }

        int byteCount = Encoding.UTF8.GetByteCount(presentedToken);
        if (byteCount <= StackBufferBytes)
        {
            Span<byte> buffer = stackalloc byte[StackBufferBytes];
            int written = Encoding.UTF8.GetBytes(presentedToken, buffer);
            bool result = Compare(buffer.Slice(0, written));
            CryptographicOperations.ZeroMemory(buffer);
            return result;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            int written = Encoding.UTF8.GetBytes(presentedToken, rented);
            return Compare(rented.AsSpan(0, written));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented.AsSpan(0, byteCount));
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private bool Compare(ReadOnlySpan<byte> presented)
    {
        if (presented.Length == _expected.Length)
        {
            return CryptographicOperations.FixedTimeEquals(presented, _expected);
        }

        // Burn an equivalent comparison so a wrong length is indistinguishable from a wrong value.
        CryptographicOperations.FixedTimeEquals(_expected, _expected);
        return false;
    }
}
