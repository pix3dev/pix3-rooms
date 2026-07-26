using Pix3.Rooms.Server.Auth;
using Pix3.Rooms.Server.Net;
using Pix3.Rooms.Server.Observability;
using Pix3.Rooms.Server.Replication;
using Pix3.Rooms.Server.Rooms;

namespace Pix3.Rooms.Server;

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
        ValidateAuthEnvironment(builder, authOptions, configuration);

        services.AddSingleton<IServiceTokenValidator>(_ => new ServiceTokenValidator(authOptions));
        if (authOptions.Mode == AuthMode.Insecure)
        {
            services.AddSingleton<IRoomTokenValidator>(sp =>
                new InsecureRoomTokenValidator(sp.GetRequiredService<ILogger<InsecureRoomTokenValidator>>()));
        }
        else
        {
            services.AddSingleton<IRoomTokenValidator>(sp =>
                new JwtRoomTokenValidator(authOptions, sp.GetRequiredService<ILogger<JwtRoomTokenValidator>>()));
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
        // known-sets. CellSize and AoiHysteresis are left at 0 so they derive from AoiRadius.
        RoomReplicationFactory replicationFactory = config => new RoomReplication(new ReplicationOptions
        {
            MaxEntities = config.MaxEntities,
            MaxPlayers = config.MaxPlayers,
            AoiRadius = config.AoiRadius,
            MaxPayloadBytes = roomServerOptions.MaxFrameBytes,
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

        // ── Host ──────────────────────────────────────────────────────────────────────────────────
        services.Configure<HostOptions>(host =>
            host.ShutdownTimeout = TimeSpan.FromSeconds(roomServerOptions.ShutdownTimeoutSeconds + HostShutdownGraceSeconds));

        return builder;
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
    /// Enforces the two rules that depend on the hosting environment rather than on a single value:
    /// insecure auth is refused outside development, and Production must carry a service token.
    /// </summary>
    private static void ValidateAuthEnvironment(
        WebApplicationBuilder builder,
        AuthOptions authOptions,
        IConfiguration configuration)
    {
        string environmentName = builder.Environment.EnvironmentName;

        if (authOptions.Mode == AuthMode.Insecure
            && !InsecureRoomTokenValidator.IsPermittedInEnvironment(environmentName))
        {
            throw new InvalidOperationException(InsecureRoomTokenValidator.RefusalMessage);
        }

        bool serviceTokenMissing = string.IsNullOrWhiteSpace(authOptions.ServiceToken);
        if (serviceTokenMissing && builder.Environment.IsProduction())
        {
            throw new InvalidOperationException(
                $"{AuthOptions.SectionName}:{nameof(AuthOptions.ServiceToken)} is required in Production: "
                + "an empty service token makes the admin API deny every request, so rooms could never be "
                + "created. Supply it via the Rooms__Auth__ServiceToken environment variable or a secret store.");
        }

        // A boot-time logger: warnings here must reach the operator, and the host's own logging is not
        // available until Build(). Disposed immediately — nothing keeps a reference to it.
        using ILoggerFactory bootLoggerFactory = LoggerFactory.Create(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddConsole();
        });
        ILogger logger = bootLoggerFactory.CreateLogger(typeof(RoomsFabricExtensions).FullName ?? nameof(RoomsFabricExtensions));

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
