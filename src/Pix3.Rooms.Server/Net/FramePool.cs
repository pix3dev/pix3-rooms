using System.Buffers;
using MemoryPack;

namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Pool + encode helpers shared by Net and Rooms. Every outbound frame buffer in the process comes
/// from here and goes back here, so the tick path never allocates a byte array.
/// </summary>
/// <remarks>
/// Backed by <see cref="ArrayPool{T}.Shared"/>. Rented buffers are <b>not</b> cleared, so a caller must
/// only ever read the first <c>Length</c> bytes of an <see cref="OutboundFrame"/>.
/// </remarks>
public static class FramePool
{
    /// <summary>Smallest buffer handed out; avoids churning the pool's tiny buckets.</summary>
    public const int MinimumFrameSize = 256;

    /// <summary>Initial capacity of a per-thread control-message staging writer.</summary>
    private const int ControlWriterInitialCapacity = 1024;

    /// <summary>
    /// A staging writer larger than this is released instead of being retained, so one oversized
    /// control message cannot pin memory on a thread forever.
    /// </summary>
    private const int ControlWriterRetainLimit = 64 * 1024;

    /// <summary>
    /// Per-thread staging writer for <see cref="EncodeControl{T}"/>. MemoryPack needs a growable
    /// <see cref="IBufferWriter{T}"/>; reusing one per thread keeps control-path encoding
    /// allocation-free apart from the frame buffer itself.
    /// </summary>
    [ThreadStatic]
    private static PooledBufferWriter? _controlWriter;

    /// <summary>
    /// Rents a buffer of at least <paramref name="minimumLength"/> bytes. The returned array is
    /// usually larger than requested; carry the real length in <see cref="OutboundFrame.Length"/>.
    /// </summary>
    public static byte[] Rent(int minimumLength)
    {
        int size = minimumLength < MinimumFrameSize ? MinimumFrameSize : minimumLength;
        return ArrayPool<byte>.Shared.Rent(size);
    }

    /// <summary>
    /// Returns a buffer previously obtained from <see cref="Rent"/> or from an
    /// <see cref="OutboundFrame"/> the caller still owns. Returning the same buffer twice corrupts the
    /// pool, so return it exactly once, at the single point that owns it.
    /// </summary>
    public static void Return(byte[] buffer)
    {
        if (buffer.Length == 0)
        {
            // Array.Empty-style zero-length arrays never came from the pool.
            return;
        }

        ArrayPool<byte>.Shared.Return(buffer);
    }

    /// <summary>
    /// MemoryPack-encodes a control message into a rented buffer, prefixed with its TypeId, producing
    /// a complete <c>[TypeId][payload]</c> frame. The caller owns the returned frame until it hands it
    /// to <see cref="IClientConnection.TryEnqueue"/>.
    /// </summary>
    /// <typeparam name="T">A <c>[MemoryPackable]</c> message type.</typeparam>
    /// <param name="typeId">The id from <c>MessageTypeIds</c> that matches <typeparamref name="T"/>.</param>
    /// <param name="message">The message to serialize.</param>
    /// <remarks>
    /// Only for the control plane. The hot frames (67/68/69) are hand-packed straight into a rented
    /// buffer via <c>HotWire</c> and must never pass through MemoryPack.
    /// </remarks>
    public static OutboundFrame EncodeControl<T>(byte typeId, T message)
    {
        PooledBufferWriter writer = _controlWriter ??= new PooledBufferWriter(ControlWriterInitialCapacity);
        try
        {
            MemoryPackSerializer.Serialize(writer, message);

            int payloadLength = writer.WrittenCount;
            int frameLength = payloadLength + 1;
            byte[] buffer = Rent(frameLength);
            buffer[0] = typeId;
            writer.WrittenSpan.CopyTo(buffer.AsSpan(1, payloadLength));
            return new OutboundFrame(buffer, frameLength);
        }
        finally
        {
            writer.Reset(ControlWriterRetainLimit, ControlWriterInitialCapacity);
        }
    }

    /// <summary>
    /// A growable <see cref="IBufferWriter{T}"/> over pooled arrays, reused across calls on one thread.
    /// </summary>
    private sealed class PooledBufferWriter : IBufferWriter<byte>
    {
        private byte[] _buffer;
        private int _written;

        internal PooledBufferWriter(int initialCapacity)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        }

        internal int WrittenCount => _written;

        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count)
        {
            if (count < 0 || _written + count > _buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Advance past the end of the staging buffer.");
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        /// <summary>
        /// Rewinds for reuse, releasing the backing array when it grew past
        /// <paramref name="retainLimit"/>.
        /// </summary>
        internal void Reset(int retainLimit, int initialCapacity)
        {
            _written = 0;
            if (_buffer.Length <= retainLimit)
            {
                return;
            }

            byte[] oversized = _buffer;
            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
            ArrayPool<byte>.Shared.Return(oversized);
        }

        private void EnsureCapacity(int sizeHint)
        {
            int hint = sizeHint <= 0 ? 1 : sizeHint;
            int required = _written + hint;
            if (required <= _buffer.Length)
            {
                return;
            }

            int doubled = _buffer.Length * 2;
            byte[] grown = ArrayPool<byte>.Shared.Rent(required > doubled ? required : doubled);
            _buffer.AsSpan(0, _written).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
        }
    }
}
