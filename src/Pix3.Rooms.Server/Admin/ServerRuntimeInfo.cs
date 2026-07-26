using System.Diagnostics;
using System.Reflection;

namespace Pix3.Rooms.Server.Admin;

/// <summary>Process identity and uptime, as reported by <c>GET /health</c>.</summary>
public static class ServerRuntimeInfo
{
    /// <summary>Reported when no version attribute is present (unversioned local build).</summary>
    public const string UnknownVersion = "0.0.0";

    private static readonly DateTimeOffset ProcessStartedAt = ResolveProcessStart();
    private static readonly string ResolvedVersion = ResolveVersion();

    /// <summary>When this process started.</summary>
    public static DateTimeOffset StartedAt => ProcessStartedAt;

    /// <summary>Informational assembly version, without any build metadata suffix.</summary>
    public static string Version => ResolvedVersion;

    /// <summary>Seconds since process start, never negative.</summary>
    public static double UptimeSeconds
    {
        get
        {
            double seconds = (DateTimeOffset.UtcNow - ProcessStartedAt).TotalSeconds;
            return seconds < 0d ? 0d : Math.Round(seconds, 3);
        }
    }

    private static DateTimeOffset ResolveProcessStart()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
        {
            // Some hardened hosts hide process start time; fall back to "first asked" rather than fail liveness.
            return DateTimeOffset.UtcNow;
        }
    }

    private static string ResolveVersion()
    {
        Assembly assembly = typeof(ServerRuntimeInfo).Assembly;

        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        string? fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion;
        }

        return assembly.GetName().Version?.ToString() ?? UnknownVersion;
    }
}
