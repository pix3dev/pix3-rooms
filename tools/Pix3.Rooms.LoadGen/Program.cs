using Pix3.Rooms.LoadGen;

// Load generator entry point. Exit codes: 0 = a valid run, 1 = bad arguments or a run that could not
// start, 2 = the run completed but the clients saw something that invalidates it as a measurement.
if (!LoadGenOptions.TryParse(args, out LoadGenOptions options, out string? error))
{
    Console.Error.WriteLine(error);
    return 1;
}

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;   // tear the run down cleanly, so the rooms it created are still destroyed
    cancellation.Cancel();
};

try
{
    LoadRunner runner = new(options, message => Console.Error.WriteLine($"[loadgen] {message}"));
    LoadGenReport report = await runner.RunAsync(cancellation.Token);

    Console.WriteLine(options.JsonReport ? LoadRunner.RenderJson(report) : LoadRunner.RenderText(report));
    return report.IsValidMeasurement ? 0 : 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("[loadgen] cancelled");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"[loadgen] FAILED: {exception.Message}");
    return 1;
}
