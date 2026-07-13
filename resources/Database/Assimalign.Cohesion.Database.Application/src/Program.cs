using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Hosting;

namespace Assimalign.Cohesion.Database.Application;

using Assimalign.Cohesion.Database.Application.Internal;

/// <summary>
/// The standalone database host executable — the artifact the
/// <c>DatabaseResource</c> orchestration manifest declares. A thin shim: bind the
/// conventional environment variables, hand them to the bootstrap, and run until
/// the shutdown signal (SIGTERM / Ctrl+C) drains the host gracefully.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        DatabaseHostConfiguration configuration;

        try
        {
            configuration = DatabaseHostConfiguration.FromEnvironment();
        }
        catch (FormatException exception)
        {
            await Console.Error.WriteLineAsync($"cohesion-db: invalid configuration: {exception.Message}");
            return 1;
        }

        DatabaseApplicationComposition composition;

        try
        {
            composition = DatabaseApplicationBootstrap.Compose(configuration);
        }
        catch (FormatException exception)
        {
            await Console.Error.WriteLineAsync($"cohesion-db: invalid configuration: {exception.Message}");
            return 1;
        }

        await using (composition)
        {
            using var shutdown = new CancellationTokenSource();

            // SIGINT (Ctrl+C) and SIGTERM (the orchestrator's stop) both request a
            // graceful drain instead of terminating the process: cancelling the run
            // token is the host's shutdown signal — the endpoint drains first, the
            // worker slots quiesce, and the engines flush durably.
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });

            Console.WriteLine(
                $"cohesion-db: starting (data: {configuration.DataPath ?? "<in-memory>"}, " +
                $"port: {(configuration.Port?.ToString() ?? "<os-assigned>")}, " +
                $"durability: {configuration.Durability ?? "full"})");

            await composition.RunAsync(shutdown.Token);

            Console.WriteLine("cohesion-db: stopped");
        }

        return 0;
    }
}
