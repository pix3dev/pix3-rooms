using Microsoft.AspNetCore.Server.Kestrel.Core;
using Pix3.Rooms.Server.Admin;
using Pix3.Rooms.Server.Auth;
using Pix3.Rooms.Server.Net;
using Pix3.Rooms.Server.Observability;
using Pix3.Rooms.Server.Replication;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server;

/// <summary>
/// The replication-owned keys of configuration section <c>Rooms:Server</c>: the three bandwidth caps plus
/// the two speed rails. Bound here rather than in <c>Replication</c> because
/// <see cref="ReplicationOptions"/> is per-room state built by the room factory, while these five are
/// server-wide operator settings — the composition root is the only place that knows both.
/// </summary>
/// <remarks>
/// A plain POCO, bound and validated exactly like <see cref="NetOptions"/> and <see cref="QuotaOptions"/>:
/// a value that would make a room unrunnable fails the process, not the first client. The per-room fields
/// (<c>MaxEntities</c>, <c>MaxPlayers</c>, <c>AoiRadius</c>, <c>MaxVisibleEntities</c>, <c>TickHz</c> and
/// the world bounds) deliberately live in <see cref="RoomConfig"/> instead, because a game tunes those per
/// room through the admin API.
/// </remarks>
public sealed class ReplicationTuningOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Rooms:Server";

    /// <summary>
    /// Hard per-client per-tick hot-frame budget in bytes — one MSS, and one future QUIC datagram.
    /// </summary>
    public int MaxBytesPerClientPerTick { get; set; } = 1100;

    /// <summary>New full records a client may be told about per tick in a <c>DeltaPacket</c>.</summary>
    public int MaxEntersPerTick { get; set; } = 24;

    /// <summary>
    /// AOI hysteresis as a factor of the room's <c>AoiRadius</c>: an entity enters at the radius and
    /// exits only beyond <c>AoiRadius × AoiExitFactor</c>.
    /// </summary>
    public float AoiExitFactor { get; set; } = 1.25f;

    /// <summary>
    /// Plausible entity speed in world units per second for the counted-only Level-1 speed check.
    /// </summary>
    public float MaxEntitySpeed { get; set; } = 2000f;

    /// <summary>Ceiling on free-coordinate (spectator) focus movement, in world units per second.</summary>
    public float MaxSpectatorFocusSpeed { get; set; } = 2000f;

    /// <summary>
    /// Throws when a value would make every room in the process unrunnable. Called by the composition
    /// root right after binding, so a typo here fails startup rather than the first tick.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is outside its supported range.</exception>
    public void Validate()
    {
        // The same floor ReplicationOptions.Validate enforces, checked here so the failure names the
        // configuration key instead of surfacing as an ArgumentOutOfRangeException from a room factory.
        if (MaxBytesPerClientPerTick < ReplicationOptions.MinViableFrameBytes)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxBytesPerClientPerTick)} must be at least "
                + $"{ReplicationOptions.MinViableFrameBytes}: below that no hot frame could carry even one "
                + "full record, so a client could never learn about a single entity.");
        }

        if (MaxEntersPerTick < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxEntersPerTick)} must be at least 1; zero would stop every client "
                + "from ever seeing a new entity.");
        }

        // < 1 puts the exit radius inside the enter radius: an entity would exit while still entering and
        // flap every tick, which is the exact failure hysteresis exists to prevent.
        if (!float.IsFinite(AoiExitFactor) || AoiExitFactor < 1f)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(AoiExitFactor)} must be finite and at least 1: a smaller factor puts "
                + "the AOI exit radius inside the enter radius and makes every boundary entity flap.");
        }

        if (!float.IsFinite(MaxEntitySpeed) || MaxEntitySpeed <= 0f)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxEntitySpeed)} must be finite and greater than 0.");
        }

        if (!float.IsFinite(MaxSpectatorFocusSpeed) || MaxSpectatorFocusSpeed <= 0f)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxSpectatorFocusSpeed)} must be finite and greater than 0.");
        }
    }
}

/// <summary>
/// Composition root of the room fabric: binds every options section, validates it, and registers the
/// Net, Auth, Rooms, Replication and Observability singletons plus their hosted services.
/// </summary>
/// <remarks>
/// <para>
/// Two registration styles live side by side on purpose. The transport and auth types take their POCO
/// options directly (they are library-style classes with no DI awareness), so those are bound to a fresh
/// instance, <c>Validate()</c>d and registered as singleton instances. The Rooms types take
/// <c>IOptions&lt;T&gt;</c> and call <c>Normalize()</c> themselves, so those go through
/// <c>Configure&lt;T&gt;</c>.
/// </para>
/// <para>
/// Everything that could possibly be misconfigured is checked <b>here</b>, at startup: a bad
/// appsettings must fail the process, never the first client.
/// </para>
/// </remarks>
public static class RoomsFabricExtensions
{
    /// <summary>Shortest service token that is worth calling a secret; below it we warn loudly.</summary>
    private const int MinimumComfortableServiceTokenLength = 32;

    /// <summary>
    /// Extra grace added to <see cref="RoomServerOptions.ShutdownTimeoutSeconds"/> for the host's own
    /// shutdown budget, so <c>RoomManager.DisposeAsync</c> gets to finish draining rooms instead of being
    /// cut off by the host timeout it is nested inside.
    /// </summary>
    private const int HostShutdownGraceSeconds = 5;

    /// <summary>
    /// Registers the whole room fabric on <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The web application builder being composed.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// The configuration cannot work: an options section failed validation, <c>Auth:Mode=Insecure</c> was
    /// requested in an environment that forbids it, or Production was started without a service token.
    /// </exception>
    public static WebApplicationBuilder AddRoomsFabric(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        IConfiguration configuration = builder.Configuration;
        IServiceCollection services = builder.Services;

        // ── Options ───────────────────────────────────────────────────────────────────────────────
        NetOptions netOptions = Bind<NetOptions>(configuration, NetOptions.SectionName);
        netOptions.Validate();
        services.AddSingleton(netOptions);

        QuotaOptions quotaOptions = Bind<QuotaOptions>(configuration, QuotaOptions.SectionName);
        quotaOptions.Validate();
        services.AddSingleton(quotaOptions);

        // The five replication-owned keys of Rooms:Server. Bound like the transport's, because the room
        // factory needs concrete numbers the moment a room is created.
        ReplicationTuningOptions replicationTuning =
            Bind<ReplicationTuningOptions>(configuration, ReplicationTuningOptions.SectionName);
        replicationTuning.Validate();
        services.AddSingleton(replicationTuning);

        AuthOptions authOptions = Bind<AuthOptions>(configuration, AuthOptions.SectionName);
        authOptions.Validate();
        services.AddSingleton(authOptions);

        services.Configure<RoomServerOptions>(configuration.GetSection(RoomServerOptions.SectionName));
        services.Configure<RoomDefaultsOptions>(configuration.GetSection(RoomDefaultsOptions.SectionName));

        // A local, already-normalized copy: the replication factory and the host shutdown budget need
        // concrete numbers now, before any IOptions consumer has had a chance to normalize the shared one.
        RoomServerOptions roomServerOptions = Bind<RoomServerOptions>(configuration, RoomServerOptions.SectionName);
        roomServerOptions.Normalize();

        // Registered as an instance so MetricsOptions.Resolve (used by MapMetricsEndpoint) and the
        // registry's cardinality cap are guaranteed to come from one and the same object.
        MetricsOptions metricsOptions = MetricsOptions.FromConfiguration(configuration);
        services.AddSingleton(metricsOptions);

        // ── Observability ─────────────────────────────────────────────────────────────────────────
        MetricsRegistry metricsRegistry = new(metricsOptions.MaxSeriesPerMetric);
        services.AddSingleton(metricsRegistry);
        services.AddSingleton(new RoomsMetrics(metricsRegistry));

        // ── Auth ──────────────────────────────────────────────────────────────────────────────────
        ValidateEnvironmentPolicy(builder, authOptions, configuration);

        services.AddSingleton<IServiceTokenValidator>(_ => new ServiceTokenValidator(authOptions));

        // Built here rather than by the container: the constructor rejects a malformed entry, and that
        // must fail startup instead of the first upgrade. Registered under both its concrete type (the
        // startup summary reports the list) and the seam the endpoint depends on.
        ConfiguredOriginPolicy originPolicy = new(authOptions);
        services.AddSingleton(originPolicy);
        services.AddSingleton<IOriginPolicy>(originPolicy);

        // Both validators take the transport's counter surface as their IAuthFailureSink: it is what
        // finally feeds auth_failures_total{reason}, and Net -> Auth is a declared dependency arrow, so
        // the counters may live in Net while the knowledge of *why* a token failed lives in Auth.
        if (authOptions.Mode == AuthMode.Insecure)
        {
            services.AddSingleton<IRoomTokenValidator>(sp =>
                new InsecureRoomTokenValidator(
                    sp.GetRequiredService<ILogger<InsecureRoomTokenValidator>>(),
                    sp.GetRequiredService<NetMetrics>()));
        }
        else
        {
            services.AddSingleton<IRoomTokenValidator>(sp =>
                new JwtRoomTokenValidator(
                    authOptions,
                    sp.GetRequiredService<ILogger<JwtRoomTokenValidator>>(),
                    sp.GetRequiredService<NetMetrics>()));
        }

        // ── Net ───────────────────────────────────────────────────────────────────────────────────
        services.AddSingleton<NetMetrics>();
        services.AddSingleton<IpConnectionLimiter>();
        services.AddSingleton<HandshakeProcessor>();
        services.AddSingleton<WebSocketEndpoint>();

        // One supervisor, two roles: the endpoint resolves it as a singleton and the host starts/stops
        // the same object. Registering AddHostedService<ConnectionSupervisor>() would build a second one.
        services.AddSingleton<ConnectionSupervisor>();
        services.AddHostedService(sp => sp.GetRequiredService<ConnectionSupervisor>());

        // ── Rooms + Replication ───────────────────────────────────────────────────────────────────
        // Replication is per room, never a singleton: each room owns its entity table, spatial hash and
        // known-sets. Per-room shape comes from RoomConfig (a game tunes those through the admin API);
        // the server-wide rails come from Rooms:Server. CellSize is left at 0 so it derives from
        // AoiRadius, which keeps every AOI query inside a 3x3 cell neighbourhood.
        RoomReplicationFactory replicationFactory = config => new RoomReplication(new ReplicationOptions
        {
            MaxEntities = config.MaxEntities,
            MaxPlayers = config.MaxPlayers,
            AoiRadius = config.AoiRadius,
            MaxVisibleEntities = config.MaxVisibleEntities,
            TickHz = config.TickHz,
            WorldOriginX = config.WorldOriginX,
            WorldOriginY = config.WorldOriginY,
            WorldSize = config.WorldSize,
            MaxBytesPerClientPerTick = replicationTuning.MaxBytesPerClientPerTick,
            MaxEntersPerTick = replicationTuning.MaxEntersPerTick,
            AoiExitFactor = replicationTuning.AoiExitFactor,
            MaxEntitySpeed = replicationTuning.MaxEntitySpeed,
            MaxSpectatorFocusSpeed = replicationTuning.MaxSpectatorFocusSpeed,
        });
        services.AddSingleton(replicationFactory);

        services.AddSingleton<IRoomFactory, RoomFactory>();

        // Registered concretely once and aliased, so the container owns exactly one instance and
        // disposes it exactly once (RoomManager is IAsyncDisposable and drains rooms on disposal).
        services.AddSingleton<RoomManager>();
        services.AddSingleton<IRoomManager>(sp => sp.GetRequiredService<RoomManager>());

        services.AddHostedService<RoomIdleSweeper>();

        // ── Bridge ────────────────────────────────────────────────────────────────────────────────
        services.AddHostedService<MetricsBridge>();

        // ── Kestrel ───────────────────────────────────────────────────────────────────────────────
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Kestrel's own default is unlimited, which is not a policy. This is the backstop behind the
            // endpoint's own MaxTotalConnections accounting: it is enforced by the web server before our
            // code runs, so it still holds if that accounting is ever wrong.
            kestrel.Limits.MaxConcurrentUpgradedConnections = netOptions.MaxConcurrentUpgradedConnections;

            // Pin the listener to HTTP/1.1. Browsers negotiate RFC 8441 WebSockets-over-HTTP/2 whenever a
            // server advertises the extended CONNECT protocol, and that is pure overhead for one
            // long-lived binary socket: HPACK state, stream flow control and a multiplexing layer we
            // never use. This process serves exactly one WebSocket route plus a handful of admin/scrape
            // requests, all of which are perfectly happy on HTTP/1.1, so the whole listener is pinned
            // rather than /ws alone — protocol selection in Kestrel is per listener, not per route.
            kestrel.ConfigureEndpointDefaults(listen => listen.Protocols = HttpProtocols.Http1);
        });

        // ── Host ──────────────────────────────────────────────────────────────────────────────────
        services.Configure<HostOptions>(host =>
            host.ShutdownTimeout = TimeSpan.FromSeconds(roomServerOptions.ShutdownTimeoutSeconds + HostShutdownGraceSeconds));

        return builder;
    }

    /// <summary>
    /// Counts the origin entries that would actually reach the allowlist. Blank entries are dropped by
    /// <see cref="ConfiguredOriginPolicy"/>, so a file containing only <c>[""]</c> is an empty allowlist
    /// and must be refused like one.
    /// </summary>
    private static int CountConfiguredOrigins(AuthOptions authOptions)
    {
        int count = 0;
        string[] configured = authOptions.AllowedOrigins;
        for (int i = 0; i < configured.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(configured[i]))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Binds one section onto a fresh instance, leaving absent keys at their code defaults.</summary>
    private static TOptions Bind<TOptions>(IConfiguration configuration, string sectionName)
        where TOptions : class, new()
    {
        TOptions options = new();
        configuration.GetSection(sectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// Enforces the rules that depend on the hosting environment rather than on a single value: insecure
    /// auth is refused outside development, and Production must carry a service token, a non-empty origin
    /// allowlist and a non-empty default entity-kind allowlist. Outside Production each of the three
    /// "empty means wide open" cases is a loud warning instead.
    /// </summary>
    private static void ValidateEnvironmentPolicy(
        WebApplicationBuilder builder,
        AuthOptions authOptions,
        IConfiguration configuration)
    {
        string environmentName = builder.Environment.EnvironmentName;
        bool isProduction = builder.Environment.IsProduction();

        if (authOptions.Mode == AuthMode.Insecure
            && !InsecureRoomTokenValidator.IsPermittedInEnvironment(environmentName))
        {
            throw new InvalidOperationException(InsecureRoomTokenValidator.RefusalMessage);
        }

        bool serviceTokenMissing = string.IsNullOrWhiteSpace(authOptions.ServiceToken);
        if (serviceTokenMissing && isProduction)
        {
            throw new InvalidOperationException(
                $"{AuthOptions.SectionName}:{nameof(AuthOptions.ServiceToken)} is required in Production: "
                + "an empty service token makes the admin API deny every request, so rooms could never be "
                + "created. Supply it via the Rooms__Auth__ServiceToken environment variable or a secret store.");
        }

        bool originsMissing = CountConfiguredOrigins(authOptions) == 0;
        if (originsMissing && isProduction)
        {
            throw new InvalidOperationException(
                $"{AuthOptions.SectionName}:{nameof(AuthOptions.AllowedOrigins)} is required in Production. "
                + ConfiguredOriginPolicy.EmptyAllowlistRefusalMessage);
        }

        // The kind allowlist is a room-creation *default*, not a transport setting, so the refusal is
        // about Rooms:Defaults:AllowedKinds rather than about any single create request. Reasoning: an
        // unknown kind faults every observer's scene code, and a create request that omits allowedKinds
        // inherits this default — so a non-empty default is exactly the condition that makes it
        // impossible to end up with a wide-open production room. (A request that explicitly sends an
        // empty list is treated as "omitted" by RoomCreateValidator and inherits the default too, so
        // there is no second hole to close at request time.)
        RoomCreationDefaults roomDefaults = RoomCreationDefaults.FromConfiguration(configuration);
        bool allowedKindsMissing = roomDefaults.AllowedKinds.Count == 0;
        if (allowedKindsMissing && isProduction)
        {
            throw new InvalidOperationException(
                $"{RoomCreationDefaults.DefaultsSection}:{nameof(RoomCreationDefaults.AllowedKinds)} is required "
                + "in Production: an empty entity-kind allowlist lets a client spawn any kind, and an unknown "
                + "kind indexes past the build's prefab table and faults every observer's scene code. List the "
                + "kinds this build can instantiate, or run the server outside Production while the exporter "
                + "does not yet emit a prefab table.");
        }

        // A boot-time logger: warnings here must reach the operator, and the host's own logging is not
        // available until Build(). Disposed immediately — nothing keeps a reference to it.
        using ILoggerFactory bootLoggerFactory = LoggerFactory.Create(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddConsole();
        });
        ILogger logger = bootLoggerFactory.CreateLogger(typeof(RoomsFabricExtensions).FullName ?? nameof(RoomsFabricExtensions));

        if (originsMissing)
        {
            logger.LogWarning(
                "{Section}:{Key} is empty: every Origin is accepted, so any page on the internet can open an "
                + "authenticated socket in a visitor's browser. Acceptable in the {Environment} environment only.",
                AuthOptions.SectionName,
                nameof(AuthOptions.AllowedOrigins),
                environmentName);
        }

        if (allowedKindsMissing)
        {
            logger.LogWarning(
                "{Section}:{Key} is empty: rooms created without an explicit list accept any entity kind. "
                + "Acceptable in the {Environment} environment only.",
                RoomCreationDefaults.DefaultsSection,
                nameof(RoomCreationDefaults.AllowedKinds),
                environmentName);
        }

        if (serviceTokenMissing)
        {
            logger.LogWarning(
                "{Section}:{Key} is empty: the admin API and the room lifecycle it drives will deny every request. "
                + "Acceptable in the {Environment} environment only.",
                AuthOptions.SectionName,
                nameof(AuthOptions.ServiceToken),
                environmentName);
        }
        else if (authOptions.ServiceToken.Length < MinimumComfortableServiceTokenLength)
        {
            logger.LogWarning(
                "{Section}:{Key} is only {Length} characters; use at least {Minimum} of unguessable entropy — "
                + "this single secret is the whole authorisation story for room creation and destruction.",
                AuthOptions.SectionName,
                nameof(AuthOptions.ServiceToken),
                authOptions.ServiceToken.Length,
                MinimumComfortableServiceTokenLength);
        }
    }
}
