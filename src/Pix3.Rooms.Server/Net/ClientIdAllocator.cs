namespace Pix3.Rooms.Server.Net;

/// <summary>
/// The process-wide source of client ids. <c>Net</c> owns the allocator, and nothing else may mint an id.
/// </summary>
/// <remarks>
/// <para>
/// <b>An id is allocated only after a socket authenticates.</b> That is the whole point of this type
/// existing separately from <see cref="ClientConnection"/>: an unauthenticated socket must consume no id,
/// so an unauthenticated flood cannot advance the counter, cannot appear in a log line as a client, and
/// cannot occupy a slot in any id-keyed table. See the pre-auth gate in <see cref="ClientConnection"/>.
/// </para>
/// <para>
/// <b>Ids start at 1</b>: zero is a reserved "no client" sentinel everywhere else in the server (an
/// entity with <c>OwnerId == 0</c> is server-owned, and <c>HostClientId == 0</c> means "no host"), so a
/// pre-auth connection reporting <c>ClientId == 0</c> reads as "not a client yet" by construction.
/// </para>
/// <para>
/// <b>Monotonic, never recycled.</b> A failed join burns its id rather than returning it: reuse would let
/// a late frame from an abandoned session be attributed to a new one. At one connection per millisecond
/// the 32-bit counter lasts ~50 days of continuous churn before wrapping, and wrapping only risks a
/// collision with a session that has been open that entire time — which the connection registry detects
/// and refuses rather than silently aliasing.
/// </para>
/// <para>
/// Static rather than injected on purpose: the counter must be unique per <i>process</i>, and a
/// DI-scoped second instance would hand out duplicate ids. It is the one piece of mutable static state in
/// the transport.
/// </para>
/// </remarks>
public static class ClientIdAllocator
{
    private static uint _next;

    /// <summary>
    /// Takes the next client id. Thread-safe and allocation-free; called exactly once per authenticated
    /// session (a resumed session adopts its original id instead of calling this).
    /// </summary>
    public static uint Next()
    {
        // Unchecked so a 2^32 wraparound produces a duplicate the registry can refuse, rather than an
        // OverflowException that would take down an otherwise healthy accept loop.
        uint id = unchecked(Interlocked.Increment(ref _next));
        return id == 0u ? unchecked(Interlocked.Increment(ref _next)) : id;
    }

    /// <summary>Ids handed out so far. Diagnostics only.</summary>
    public static uint Allocated => Volatile.Read(ref _next);
}
