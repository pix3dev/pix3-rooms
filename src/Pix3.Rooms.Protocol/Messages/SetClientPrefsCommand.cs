using MemoryPack;

namespace Pix3.Rooms.Protocol;

/// <summary>
/// C→S, TypeId <see cref="MessageTypeIds.SetClientPrefsCommand"/>. Per-client delivery preferences.
/// Neither of them affects the control plane.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class SetClientPrefsCommand
{
    /// <summary>
    /// True suspends this client's hot plane <b>entirely</b> (no deltas, no snapshots, no signal
    /// batches, and <c>Seq</c> stops advancing); un-hiding implies a resync. Chrome throttles timers to
    /// once per second in a hidden tab and once per minute after five minutes, and
    /// <c>requestAnimationFrame</c> stops outright: a backgrounded tab cannot drain a 20 Hz stream, it
    /// buffers it.
    /// </summary>
    [MemoryPackOrder(0)]
    public bool Hidden { get; set; }

    /// <summary>
    /// Serve this client every <c>n</c>th tick. <c>0</c> and <c>1</c> both mean every tick; the server
    /// clamps to <c>[1, 8]</c>.
    /// </summary>
    [MemoryPackOrder(1)]
    public byte SendRateDivisor { get; set; }

    /// <summary>MemoryPack requires a public parameterless constructor.</summary>
    public SetClientPrefsCommand()
    {
    }
}
