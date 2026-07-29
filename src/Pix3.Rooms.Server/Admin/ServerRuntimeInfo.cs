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
    private static readonly string ResolvedCommit = ResolveCommit();

    /// <summary>When this process started.</summary>
    public static DateTimeOffset StartedAt => ProcessStartedAt;

    /// <summary>Informational assembly version, without any build metadata suffix.</summary>
    public static string Version => ResolvedVersion;

    /// <summary>
    /// Git sha this binary was published from, or empty when neither source knows it.
    /// </summary>
    /// <remarks>
    /// The version number cannot answer "is production current?" — it is hand-maintained and identical
    /// across a dozen deploys. The sha can, so it is reported alongside it.
    /// </remarks>
    public static string Commit => ResolvedCommit;

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

    /// <summary>
    /// Establishes the commit from the build first and the deploy layout second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Build:</b> <c>dotnet publish -p:SourceRevisionId=&lt;sha&gt;</c> makes the SDK append <c>+&lt;sha&gt;</c>
    /// to <see cref="AssemblyInformationalVersionAttribute"/>, so a published binary carries its own
    /// provenance and needs no help from the host.
    /// </para>
    /// <para>
    /// <b>Deploy:</b> the shipped unit runs <c>&lt;root&gt;/current/Pix3.Rooms.Server</c> where <c>current</c>
    /// is a symlink to <c>releases/&lt;sha&gt;</c>. Resolving that link recovers the sha for binaries published
    /// before the build stamp existed, which is the only reason this fallback is here.
    /// </para>
    /// </remarks>
    private static string ResolveCommit()
    {
        string? informational = typeof(ServerRuntimeInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0 && plus + 1 < informational.Length)
            {
                string suffix = informational[(plus + 1)..];
                if (LooksLikeCommitSha(suffix))
                {
                    return suffix;
                }
            }
        }

        return ResolveCommitFromReleaseDirectory();
    }

    private static string ResolveCommitFromReleaseDirectory()
    {
        try
        {
            string baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (baseDirectory.Length == 0)
            {
                return string.Empty;
            }

            DirectoryInfo directory = new(baseDirectory);
            // ResolveLinkTarget returns null for a real directory, which is the local-build case.
            FileSystemInfo resolved = directory.ResolveLinkTarget(returnFinalTarget: true) ?? directory;
            string name = Path.GetFileName(resolved.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return LooksLikeCommitSha(name) ? name : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Provenance is a nicety; failing to read it must never affect the process.
            return string.Empty;
        }
    }

    /// <summary>True for a hex string long enough to be a git object name and not a version folder.</summary>
    private static bool LooksLikeCommitSha(string value)
    {
        if (value.Length is < 7 or > 40)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
            {
                return false;
            }
        }

        return true;
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
