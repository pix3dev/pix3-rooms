namespace Pix3.Rooms.Server.Net;

/// <summary>
/// A rented buffer holding one complete frame (<c>[TypeId][payload]</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> The buffer belongs to <see cref="FramePool"/>. Ownership transfers to the
/// connection on a successful <see cref="IClientConnection.TryEnqueue"/> and the send loop returns it
/// to the pool once written. When <c>TryEnqueue</c> returns false the caller still owns the buffer and
/// must call <see cref="FramePool.Return"/> itself, or it leaks out of the pool.
/// </para>
/// <para>
/// <b>No refcounting.</b> One frame belongs to exactly one connection. Broadcast encodes once into a
/// scratch buffer and copies the bytes per recipient — never hand the same
/// <see cref="OutboundFrame"/> to two connections.
/// </para>
/// </remarks>
public readonly struct OutboundFrame
{
    /// <summary>The rented backing array. Only the first <see cref="Length"/> bytes are meaningful.</summary>
    public readonly byte[] Buffer;

    /// <summary>Number of valid bytes, including the leading TypeId.</summary>
    public readonly int Length;

    /// <summary>Wraps a rented buffer and the number of bytes written into it.</summary>
    public OutboundFrame(byte[] buffer, int length)
    {
        Buffer = buffer;
        Length = length;
    }

    /// <summary>True when there is nothing to send.</summary>
    public bool IsEmpty => Length <= 0;

    /// <summary>The frame bytes.</summary>
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    /// <summary>The frame bytes as <see cref="Memory{T}"/>, ready for <c>WebSocket.SendAsync</c>.</summary>
    public ReadOnlyMemory<byte> Memory => Buffer.AsMemory(0, Length);
}
