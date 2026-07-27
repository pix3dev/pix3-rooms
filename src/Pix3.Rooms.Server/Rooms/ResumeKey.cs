using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Pix3.Rooms.Server.Rooms;

/// <summary>
/// A session's 16-byte resume credential, held as two <see cref="ulong"/>s: it keys the pending-session
/// table without allocating and compares without branching.
/// </summary>
/// <remarks>
/// <para>
/// The key is the <b>only</b> credential a resume presents — a client never names the id it wants — so it
/// is generated from <see cref="RandomNumberGenerator"/> and <b>regenerated on every connect</b>. A key
/// that leaked from an earlier session therefore buys nothing: it names no pending session any more.
/// </para>
/// <para>
/// <see cref="Equals(ResumeKey)"/> accumulates both halves before testing, so it has no early exit and a
/// timing measurement cannot walk the key byte by byte. The dictionary bucket a key hashes into still
/// leaks a few bits of the hash, which is harmless for a 128-bit random secret that is never reused.
/// </para>
/// </remarks>
internal readonly struct ResumeKey : IEquatable<ResumeKey>
{
    /// <summary>Key length in bytes, fixed by the protocol.</summary>
    internal const int Size = 16;

    private readonly ulong _low;
    private readonly ulong _high;

    /// <summary>Wraps the first <see cref="Size"/> bytes of <paramref name="key"/>.</summary>
    /// <param name="key">At least <see cref="Size"/> bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is shorter than <see cref="Size"/>.</exception>
    internal ResumeKey(ReadOnlySpan<byte> key)
    {
        if (key.Length < Size)
        {
            throw new ArgumentException($"A resume key is {Size} bytes.", nameof(key));
        }

        _low = BinaryPrimitives.ReadUInt64LittleEndian(key);
        _high = BinaryPrimitives.ReadUInt64LittleEndian(key.Slice(8));
    }

    /// <summary>Mints a fresh cryptographically random key.</summary>
    internal static ResumeKey Create()
    {
        Span<byte> bytes = stackalloc byte[Size];
        RandomNumberGenerator.Fill(bytes);
        return new ResumeKey(bytes);
    }

    /// <summary>True for the all-zero key, which is never issued and never matches a real session.</summary>
    internal bool IsEmpty => (_low | _high) == 0UL;

    /// <summary>Copies the key into a fresh array for <c>JoinGrant.ResumeKey</c> (control path only).</summary>
    internal byte[] ToArray()
    {
        var bytes = new byte[Size];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, _low);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), _high);
        return bytes;
    }

    /// <inheritdoc />
    public bool Equals(ResumeKey other) => (((_low ^ other._low) | (_high ^ other._high)) == 0UL);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ResumeKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_low, _high);
}
