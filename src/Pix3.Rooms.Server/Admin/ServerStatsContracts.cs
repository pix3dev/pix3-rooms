namespace Pix3.Rooms.Server.Admin;

/// <summary>
/// Body of <c>GET /admin/stats</c> — everything an operations dashboard needs about this fabric in one
/// round trip.
/// </summary>
/// <remarks>
/// <c>GET /health</c> stays deliberately tiny (it is the unauthenticated liveness probe); this is its
/// service-token-authenticated counterpart and the only place that reports resource use.
/// </remarks>
/// <param name="Status">Always <c>ok</c>: the process answered, which is the only claim it can make.</param>
/// <param name="Version">Informational assembly version, without build metadata.</param>
/// <param name="Commit">
/// Git sha this binary was published from, or empty when it cannot be established. Compared against the
/// repository's HEAD, this — not the version string — is what tells an operator whether a deploy is due.
/// </param>
/// <param name="Environment">ASP.NET environment name (<c>Production</c>, <c>Development</c>).</param>
/// <param name="StartedAt">When the process started.</param>
/// <param name="UptimeSeconds">Seconds since process start.</param>
/// <param name="Process">This process's resource use.</param>
/// <param name="Host">The host's load and headroom.</param>
/// <param name="Connections">Transport-level connection counters.</param>
/// <param name="Rooms">Room totals plus one entry per live room.</param>
public sealed record ServerStatsResponse(
    string Status,
    string Version,
    string Commit,
    string Environment,
    DateTimeOffset StartedAt,
    double UptimeSeconds,
    ProcessStatsResponse Process,
    HostStatsResponse Host,
    ConnectionStatsResponse Connections,
    RoomsStatsResponse Rooms);

/// <summary>Process resource use, as <see cref="Observability.ProcessResources"/> reports it.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="CpuPercent">CPU use since the previous sample, 100 = every core saturated.</param>
/// <param name="CpuSecondsTotal">CPU seconds burned since start.</param>
/// <param name="WorkingSetBytes">Resident set size.</param>
/// <param name="PrivateMemoryBytes">Committed private bytes.</param>
/// <param name="ManagedHeapBytes">Bytes the GC considers allocated.</param>
/// <param name="HeapLimitBytes">The GC's hard heap limit, derived from the unit's <c>MemoryMax</c>.</param>
/// <param name="ThreadCount">OS threads.</param>
/// <param name="Gen2Collections">Full GCs since start.</param>
public sealed record ProcessStatsResponse(
    int Pid,
    double? CpuPercent,
    double CpuSecondsTotal,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    long? HeapLimitBytes,
    int ThreadCount,
    int Gen2Collections);

/// <summary>
/// Host load and headroom. Nullable throughout: the numbers come from <c>/proc</c>, and a platform
/// without it answers "unknown" rather than zero.
/// </summary>
/// <param name="Hostname">Machine name — also how a caller tells whether two services share a host.</param>
/// <param name="Os">OS description.</param>
/// <param name="CpuCount">Logical processors.</param>
/// <param name="CpuPercent">Host-wide CPU busy percentage since the previous sample.</param>
/// <param name="Load1">1-minute load average.</param>
/// <param name="Load5">5-minute load average.</param>
/// <param name="Load15">15-minute load average.</param>
/// <param name="MemoryTotalBytes">Physical RAM.</param>
/// <param name="MemoryAvailableBytes">RAM available without swapping.</param>
/// <param name="DiskTotalBytes">Size of the filesystem the app runs from.</param>
/// <param name="DiskFreeBytes">Free space on it.</param>
/// <param name="UptimeSeconds">Seconds since boot.</param>
public sealed record HostStatsResponse(
    string Hostname,
    string Os,
    int CpuCount,
    double? CpuPercent,
    double? Load1,
    double? Load5,
    double? Load15,
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    long? DiskTotalBytes,
    long? DiskFreeBytes,
    double? UptimeSeconds);

/// <summary>
/// Transport counters. Sourced from the Prometheus families rather than from <c>NetMetrics</c> directly,
/// so <c>Admin</c> keeps its single dependency on <c>Observability</c> and the dashboard cannot disagree
/// with a scrape by more than the bridge's one-second pump.
/// </summary>
/// <param name="Active">Sockets currently open.</param>
/// <param name="MaxTotal">The configured ceiling on concurrent connections.</param>
/// <param name="AcceptedTotal">Connections accepted since start.</param>
/// <param name="RejectedTotal">Connections refused since start, summed over every reject code.</param>
/// <param name="AuthFailuresTotal">Token refusals since start, summed over every reason.</param>
/// <param name="ProtocolErrorsTotal">Malformed or forbidden inbound frames since start.</param>
/// <param name="MessagesInTotal">Inbound frames since start, summed over every message type.</param>
/// <param name="MessagesOutTotal">Outbound frames written since start.</param>
/// <param name="BytesOutTotal">Outbound bytes written since start.</param>
/// <param name="FramesDroppedTotal">Frames dropped since start, summed over every drop reason.</param>
public sealed record ConnectionStatsResponse(
    int Active,
    int MaxTotal,
    long AcceptedTotal,
    long RejectedTotal,
    long AuthFailuresTotal,
    long ProtocolErrorsTotal,
    long MessagesInTotal,
    long MessagesOutTotal,
    long BytesOutTotal,
    long FramesDroppedTotal);

/// <summary>Room totals across the server, plus a line per live room.</summary>
/// <param name="Count">Live rooms.</param>
/// <param name="MaxRooms">Configured room cap.</param>
/// <param name="Players">Members joined across all rooms.</param>
/// <param name="Entities">Live entities across all rooms.</param>
/// <param name="BytesOutPerSecond">Recent outbound throughput, summed over all rooms.</param>
/// <param name="TickP50MsMax">Worst per-room median tick duration.</param>
/// <param name="TickP99MsMax">Worst per-room 99th-percentile tick duration.</param>
/// <param name="TickJitterP99MsMax">Worst per-room 99th-percentile tick start jitter.</param>
/// <param name="DroppedFrames">Frames dropped by rooms since their creation.</param>
/// <param name="Items">One entry per live room.</param>
public sealed record RoomsStatsResponse(
    int Count,
    int MaxRooms,
    int Players,
    int Entities,
    long BytesOutPerSecond,
    double TickP50MsMax,
    double TickP99MsMax,
    double TickJitterP99MsMax,
    long DroppedFrames,
    IReadOnlyList<RoomSummaryResponse> Items);

/// <summary>One room, flattened to the fields a dashboard row shows.</summary>
/// <param name="RoomId">Room id.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="BuildId">Build tag, empty when unset.</param>
/// <param name="Mode">Authority model name.</param>
/// <param name="Players">Members joined.</param>
/// <param name="MaxPlayers">Member cap.</param>
/// <param name="Entities">Live entities.</param>
/// <param name="TickHz">Configured tick rate.</param>
/// <param name="TickP50Ms">Median tick duration.</param>
/// <param name="TickP99Ms">99th-percentile tick duration.</param>
/// <param name="BytesOutPerSecond">Recent outbound throughput.</param>
/// <param name="DroppedFrames">Frames dropped because a queue was full.</param>
/// <param name="Resyncs">Known-set rebuilds.</param>
/// <param name="Violations">Sum of member violation counters at the last publish.</param>
/// <param name="CreatedAt">When the room was created.</param>
/// <param name="LastActivityAt">Last join, leave or inbound message.</param>
public sealed record RoomSummaryResponse(
    string RoomId,
    string ProjectId,
    string BuildId,
    string Mode,
    int Players,
    int MaxPlayers,
    int Entities,
    int TickHz,
    double TickP50Ms,
    double TickP99Ms,
    long BytesOutPerSecond,
    long DroppedFrames,
    long Resyncs,
    long Violations,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);
