namespace Pix3.Rooms.LoadGen;

/// <summary>
/// What one <see cref="RoomClient"/> session observed. Plain mutable fields updated from the receive
/// loop and read after the run: a load generator that measured itself with locks would be measuring its
/// own locks.
/// </summary>
/// <remarks>
/// The three counters that decide whether a run is a valid measurement at all are
/// <see cref="SeqGaps"/>, <see cref="UpdatesForUnknownSlots"/> and <see cref="MalformedFrames"/>. A run
/// with any of them non-zero produced numbers for a stream the client could not actually follow, and the
/// report says so instead of quoting a throughput.
/// </remarks>
public sealed class ClientMetrics
{
    private long _bytesReceived;
    private long _bytesSent;
    private long _framesReceived;
    private long _framesSent;
    private readonly List<double> _roundTripMs = [];
    private readonly Lock _latencyGate = new();

    /// <summary>Advisory tick stamped into outbound <c>EntityUpdatePacket</c>s.</summary>
    public uint ClientTick { get; private set; }

    /// <summary>Bytes of WebSocket payload received (framing excluded — the transport's own overhead).</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>Bytes of WebSocket payload sent.</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>Frames received.</summary>
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>Frames sent.</summary>
    public long FramesSent => Interlocked.Read(ref _framesSent);

    /// <summary><c>SnapshotPacket</c> frames, split frames included.</summary>
    public int SnapshotFrames;

    /// <summary>Snapshots whose final frame carried <c>FrameFlags.Final</c>.</summary>
    public int SnapshotsCompleted;

    /// <summary><c>DeltaPacket</c> frames.</summary>
    public int DeltaFrames;

    /// <summary>Full records received, from snapshots and AOI enters together.</summary>
    public int FullRecords;

    /// <summary>AOI enter records.</summary>
    public int Enters;

    /// <summary>Removal records.</summary>
    public int Removals;

    /// <summary>Update records.</summary>
    public int Updates;

    /// <summary>Bytes those update records occupied, for the "8 B per moving entity" claim.</summary>
    public long UpdateBytes;

    /// <summary><c>SignalBatchPacket</c> frames.</summary>
    public int SignalBatches;

    /// <summary>Entries inside those batches.</summary>
    public int SignalEntries;

    /// <summary>Control-plane <c>SignalEvent</c>s.</summary>
    public int SignalEvents;

    /// <summary>Largest known set this client ever held — what <c>MaxVisibleEntities</c> must bound.</summary>
    public int PeakKnownCount;

    /// <summary><c>Seq</c> discontinuities. Non-zero means this client lost frames.</summary>
    public int SeqGaps;

    /// <summary>Resyncs this client asked for, which is what it must do on a gap.</summary>
    public int ResyncsRequested;

    /// <summary>Updates for a slot no full record had introduced — a protocol violation, not a hiccup.</summary>
    public int UpdatesForUnknownSlots;

    /// <summary>Removals for a slot this client did not know.</summary>
    public int RemovalsForUnknownSlots;

    /// <summary>A slot entered twice without a removal in between.</summary>
    public int DuplicateEnters;

    /// <summary>Frames that did not decode.</summary>
    public int MalformedFrames;

    /// <summary>TypeIds this client does not know — ignored and counted, never fatal.</summary>
    public int UnknownTypeIds;

    /// <summary><c>PeerJoinedEvent</c>s.</summary>
    public int PeerJoined;

    /// <summary><c>PeerLeftEvent</c>s.</summary>
    public int PeerLeft;

    /// <summary><c>ChatMessageEvent</c>s.</summary>
    public int ChatMessages;

    /// <summary><c>RoomVarsChangedEvent</c>s.</summary>
    public int RoomVarChanges;

    /// <summary><c>HostChangedEvent</c>s.</summary>
    public int HostChanges;

    /// <summary><c>RoomRosterEvent</c> chunks, of which only the last of a roster carries <c>Final</c>.</summary>
    public int RosterChunks;

    /// <summary>Rosters completed: chunks that carried <c>Final</c>.</summary>
    public int RostersCompleted;

    /// <summary><c>RoomInfoEvent</c>s (~1 Hz).</summary>
    public int RoomInfoEvents;

    /// <summary>WebSocket close status, once the socket closed.</summary>
    public int? CloseStatus;

    /// <summary>The message from a <c>RejectedEvent</c>, if one arrived.</summary>
    public string? RejectMessage;

    /// <summary>Round-trip samples from ping/pong, in milliseconds.</summary>
    public double[] RoundTripSamples
    {
        get
        {
            lock (_latencyGate)
            {
                return _roundTripMs.ToArray();
            }
        }
    }

    /// <summary>True when nothing this client saw invalidates the run as a measurement.</summary>
    public bool IsClean =>
        SeqGaps == 0
        && UpdatesForUnknownSlots == 0
        && RemovalsForUnknownSlots == 0
        && DuplicateEnters == 0
        && MalformedFrames == 0;

    internal void RecordReceived(int bytes)
    {
        Interlocked.Add(ref _bytesReceived, bytes);
        Interlocked.Increment(ref _framesReceived);
    }

    internal void RecordSent(int bytes)
    {
        Interlocked.Add(ref _bytesSent, bytes);
        Interlocked.Increment(ref _framesSent);
        ClientTick++;
    }

    internal void RecordRoundTrip(double milliseconds)
    {
        lock (_latencyGate)
        {
            _roundTripMs.Add(milliseconds);
        }
    }
}
