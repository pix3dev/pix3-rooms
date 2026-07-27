using Pix3.Rooms.Protocol;
using Pix3.Rooms.Server.Net;

namespace Pix3.Rooms.Tests.Rooms;

/// <summary>
/// An <see cref="IClientConnection"/> that keeps the frames a room sent it instead of writing them to a
/// socket. Frames arrive on the room's tick thread and are read from the test thread, so every access
/// is under one lock.
/// </summary>
/// <remarks>
/// It honours the buffer-ownership rule the interface states: a successful <see cref="TryEnqueue"/>
/// takes ownership, so the copy is made and the rented buffer is returned to <see cref="FramePool"/>
/// here. Getting that wrong in a test would mask exactly the leak the rule exists to prevent.
/// </remarks>
internal sealed class FakeClientConnection : IClientConnection
{
    private readonly Lock _gate = new();
    private readonly List<byte[]> _frames = [];

    public FakeClientConnection(uint clientId, string displayName = "tester", string remoteIp = "203.0.113.1")
    {
        ClientId = clientId;
        DisplayName = displayName;
        RemoteIp = remoteIp;
    }

    public uint ClientId { get; }

    public string RemoteIp { get; }

    public string DisplayName { get; }

    public bool IsOpen { get; private set; } = true;

    /// <summary>Set when the room asked for a close; the last reason wins.</summary>
    public RejectCode? CloseCode { get; private set; }

    /// <summary>Refuse every enqueue on this lane, simulating a full send queue.</summary>
    public FrameLane? FailingLane { get; set; }

    public IReadOnlyList<byte[]> Frames
    {
        get
        {
            lock (_gate)
            {
                return _frames.ToArray();
            }
        }
    }

    public IReadOnlyList<byte[]> FramesOfType(byte typeId)
    {
        lock (_gate)
        {
            return _frames.Where(f => f.Length > 0 && f[0] == typeId).ToArray();
        }
    }

    public int CountOfType(byte typeId)
    {
        lock (_gate)
        {
            return _frames.Count(f => f.Length > 0 && f[0] == typeId);
        }
    }

    public bool TryEnqueue(in OutboundFrame frame, FrameLane lane)
    {
        if (!IsOpen || lane == FailingLane)
        {
            return false;   // the caller still owns the buffer and must return it
        }

        byte[] copy = frame.Span.ToArray();
        lock (_gate)
        {
            _frames.Add(copy);
        }

        FramePool.Return(frame.Buffer);   // ownership transferred on success
        return true;
    }

    public void RequestClose(RejectCode code, string reason)
    {
        CloseCode = code;
        IsOpen = false;
    }
}
