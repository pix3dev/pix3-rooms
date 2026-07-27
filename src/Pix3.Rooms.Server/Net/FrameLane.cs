namespace Pix3.Rooms.Server.Net;

/// <summary>
/// Which send queue a frame belongs to. The two lanes have different failure policies, and that
/// difference is a correctness contract, not tuning.
/// </summary>
/// <remarks>
/// <para>
/// The lossy link in this system is our own bounded send queue, not the network — WSS is TCP. Position
/// updates carry absolute values and self-heal, but a <c>RejectedEvent</c>, a spawn response or a chat
/// message has no later frame that repairs it. One queue cannot serve both: a policy that is right for a
/// stale delta (drop it, resync later) silently destroys a control message, and a policy that is right for
/// a control message (never drop) buffers deltas until the client is minutes behind.
/// </para>
/// <para>
/// The send loop drains <see cref="Control"/> fully before each <see cref="Hot"/> frame, so a hot backlog
/// can never starve a rejection.
/// </para>
/// </remarks>
public enum FrameLane : byte
{
    /// <summary>
    /// Handshake, chat, room vars, spawn responses, signals, rejections. A full control lane means the
    /// client is unrecoverably behind: close the connection. Control frames are never dropped silently.
    /// </summary>
    Control = 0,

    /// <summary>
    /// Snapshots, deltas, signal batches. A full hot lane returns the buffer and marks the client for
    /// resync; the known-set changes that frame carried are rolled back.
    /// </summary>
    Hot = 1,
}
