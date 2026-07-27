namespace Pix3.Rooms.LoadGen;

/// <summary>How the synthetic players move. The pattern decides which cap the run actually exercises.</summary>
public enum MovementPattern
{
    /// <summary>
    /// Each client orbits its own centre, centres spread over the world. The realistic case: AOI keeps
    /// each client's visible set small, so this is the run that produces the "steady state" numbers.
    /// </summary>
    Orbit,

    /// <summary>
    /// Every client crowds one point, so everyone is inside everyone's AOI. This is the case an AOI radius
    /// does not bound and the case <c>MaxVisibleEntities</c>, <c>MaxEntersPerTick</c> and the byte budget
    /// exist for — the worst-case numbers come from here.
    /// </summary>
    Dogpile,

    /// <summary>Clients drift on straight lines across the world, entering and leaving AOIs constantly.</summary>
    Drift,
}

/// <summary>Command-line configuration for a load run.</summary>
public sealed record LoadGenOptions
{
    /// <summary>Server base address.</summary>
    public Uri BaseUri { get; init; } = new("http://127.0.0.1:5011");

    /// <summary>Service token for the admin API. Required unless <see cref="CreateRooms"/> is false.</summary>
    public string ServiceToken { get; init; } = "";

    /// <summary>Rooms to drive concurrently.</summary>
    public int Rooms { get; init; } = 1;

    /// <summary>Clients per room.</summary>
    public int ClientsPerRoom { get; init; } = 50;

    /// <summary>How long to hold the load, in seconds, after every client has joined.</summary>
    public int DurationSeconds { get; init; } = 30;

    /// <summary>Entity-update rate per client. The room's own tick rate is separate.</summary>
    public int SendHz { get; init; } = 20;

    /// <summary>Room tick rate to create rooms with.</summary>
    public int TickHz { get; init; } = 20;

    /// <summary>AOI enter radius for created rooms.</summary>
    public float AoiRadius { get; init; } = 1200f;

    /// <summary>Member cap for created rooms; must be at least <see cref="ClientsPerRoom"/>.</summary>
    public int MaxPlayers { get; init; } = 600;

    /// <summary>Entity-table capacity for created rooms.</summary>
    public int MaxEntities { get; init; } = 4096;

    /// <summary>Per-client visibility cap for created rooms.</summary>
    public int MaxVisibleEntities { get; init; } = 64;

    /// <summary>Movement pattern.</summary>
    public MovementPattern Pattern { get; init; } = MovementPattern.Orbit;

    /// <summary>Room id prefix; the run appends an index.</summary>
    public string RoomPrefix { get; init; } = "loadgen";

    /// <summary>Project id recorded on created rooms.</summary>
    public string ProjectId { get; init; } = "loadgen";

    /// <summary>Create the rooms through the admin API, and destroy them afterwards.</summary>
    public bool CreateRooms { get; init; } = true;

    /// <summary>Milliseconds between client joins, so 600 sockets do not arrive as one burst.</summary>
    public int JoinStaggerMs { get; init; } = 5;

    /// <summary>Print the report as JSON instead of text.</summary>
    public bool JsonReport { get; init; }

    /// <summary>Usage text, printed for <c>--help</c> and for a bad argument.</summary>
    public static string Usage =>
        """
        pix3-rooms load generator — drives real v2 clients against a running server and reports what it measured.

        Usage: Pix3.Rooms.LoadGen [options]

          --url <uri>              server base address            (default http://127.0.0.1:5011)
          --service-token <token>  admin API token, required to create rooms
          --rooms <n>              rooms to drive                 (default 1)
          --clients <n>            clients per room               (default 50)
          --duration <seconds>     hold time after ramp-up        (default 30)
          --send-hz <n>            entity updates per client/s    (default 20)
          --tick-hz <n>            tick rate of created rooms     (default 20)
          --aoi <units>            AOI enter radius               (default 1200)
          --max-players <n>        member cap of created rooms    (default 600)
          --max-entities <n>       entity capacity                (default 4096)
          --max-visible <n>        per-client visibility cap      (default 64)
          --pattern <name>         orbit | dogpile | drift        (default orbit)
          --room-prefix <text>     room id prefix                 (default loadgen)
          --project <id>           project id on created rooms    (default loadgen)
          --no-create              join existing rooms instead of creating them
          --join-stagger <ms>      delay between client joins     (default 5)
          --json                   emit the report as JSON
          --help                   this text

        The server must allow this many connections from one address. The shipped defaults are
        Rooms:Quotas:MaxConnectionsPerIp = 8 and Rooms:Server:MaxPreAuthConnectionsPerIp = 4, so a run of
        any size needs both raised (and MaxTotalConnections above rooms x clients) or the joins are
        refused with RateLimited. The run says so explicitly rather than reporting a small number.
        """;

    /// <summary>
    /// Parses arguments. Returns false with <paramref name="error"/> set on anything unusable — a load
    /// run that silently used a default the operator did not intend reports a number about the wrong thing.
    /// </summary>
    public static bool TryParse(string[] args, out LoadGenOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = new LoadGenOptions();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--no-create":
                    options = options with { CreateRooms = false };
                    continue;
                case "--json":
                    options = options with { JsonReport = true };
                    continue;
                case "--help" or "-h":
                    error = Usage;
                    return false;
            }

            if (i + 1 >= args.Length)
            {
                error = $"missing value for {argument}\n\n{Usage}";
                return false;
            }

            string value = args[++i];
            try
            {
                options = argument switch
                {
                    "--url" => options with { BaseUri = new Uri(value) },
                    "--service-token" => options with { ServiceToken = value },
                    "--rooms" => options with { Rooms = int.Parse(value) },
                    "--clients" => options with { ClientsPerRoom = int.Parse(value) },
                    "--duration" => options with { DurationSeconds = int.Parse(value) },
                    "--send-hz" => options with { SendHz = int.Parse(value) },
                    "--tick-hz" => options with { TickHz = int.Parse(value) },
                    "--aoi" => options with { AoiRadius = float.Parse(value) },
                    "--max-players" => options with { MaxPlayers = int.Parse(value) },
                    "--max-entities" => options with { MaxEntities = int.Parse(value) },
                    "--max-visible" => options with { MaxVisibleEntities = int.Parse(value) },
                    "--pattern" => options with { Pattern = ParsePattern(value) },
                    "--room-prefix" => options with { RoomPrefix = value },
                    "--project" => options with { ProjectId = value },
                    "--join-stagger" => options with { JoinStaggerMs = int.Parse(value) },
                    _ => throw new FormatException($"unknown option {argument}"),
                };
            }
            catch (Exception exception) when (exception is FormatException or OverflowException or UriFormatException or ArgumentException)
            {
                error = $"{argument}: {exception.Message}\n\n{Usage}";
                return false;
            }
        }

        return options.Validate(out error);
    }

    private static MovementPattern ParsePattern(string value) => value.ToLowerInvariant() switch
    {
        "orbit" => MovementPattern.Orbit,
        "dogpile" => MovementPattern.Dogpile,
        "drift" => MovementPattern.Drift,
        _ => throw new FormatException($"unknown pattern '{value}' (orbit | dogpile | drift)"),
    };

    private bool Validate(out string? error)
    {
        error = null;

        if (Rooms < 1 || ClientsPerRoom < 1)
        {
            error = "--rooms and --clients must both be at least 1";
            return false;
        }

        if (DurationSeconds < 1)
        {
            error = "--duration must be at least 1 second";
            return false;
        }

        if (SendHz is < 1 or > 120)
        {
            error = "--send-hz must be 1..120";
            return false;
        }

        if (CreateRooms && string.IsNullOrWhiteSpace(ServiceToken))
        {
            error = "--service-token is required to create rooms (or pass --no-create to join existing ones)";
            return false;
        }

        if (CreateRooms && MaxPlayers < ClientsPerRoom)
        {
            error = $"--max-players ({MaxPlayers}) is below --clients ({ClientsPerRoom}); the room would refuse joins";
            return false;
        }

        return true;
    }
}
