using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

using Internal;

public static partial class HostExtensions
{
    extension(IHost host)
    {
        /// <summary>
        /// Runs the <see cref="IHost"/> synchronously.
        /// </summary>
        public void Run()
        {
            ArgumentNullException.ThrowIfNull(host);

            host.RunAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Starts the host, waits for shutdown, and drains it through its complete-run pipeline.
        /// </summary>
        /// <param name="cancellationToken">Signals a shutdown request for this run.</param>
        /// <returns>A task that represents the complete host lifetime.</returns>
        public Task RunAsync(CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(host);

            return host is IHostRunDispatcher dispatcher
                ? dispatcher.RunAsync(cancellationToken)
                : RunWithoutDispatcherAsync(host, cancellationToken);
        }

        /// <summary>
        /// Converts a <see cref="IHost"/> to run as a <see cref="IHostService"/>.
        /// </summary>
        /// <returns>A service that starts the host and stops it when the service is stopped.</returns>
        public IHostService AsService()
        {
            ArgumentNullException.ThrowIfNull(host);

            return new HostToServiceWrapper(host);
        }
    }

    private static async Task RunWithoutDispatcherAsync(
        IHost host,
        CancellationToken cancellationToken)
    {
        await host.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await host.Context.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Run cancellation is a graceful shutdown request after readiness.
        }

        if (host.Context.State is HostState.Started or HostState.Stopping)
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    extension(IHostEnvironment environment)
    {
        public bool IsEnvironment(string name)
        {
            return environment.Name == name;
        }

        public bool IsDevelopment()
        {
            return environment.IsEnvironment("development");
        }

        public bool IsStaging()
        {
            return environment.IsEnvironment("staging");
        }

        public bool IsTest()
        {
            return environment.IsEnvironment("test");
        }

        public bool IsProduction()
        {
            return environment.IsEnvironment("production");
        }

        public bool IsUserAcceptanceTesting()
        {
            return environment.IsEnvironment("uat");
        }

        public bool IsQualityAssurance()
        {
            return environment.IsEnvironment("qa");
        }
    }
}
