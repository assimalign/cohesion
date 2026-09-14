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
        /// <summary>Checks the environment name using ordinal case-insensitive matching.</summary>
        /// <param name="name">The environment name to match.</param>
        /// <returns>Whether the environment has the supplied name.</returns>
        public bool IsEnvironment(string name)
        {
            return string.Equals(environment.Name, name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Checks whether this is the developer-machine environment.</summary>
        /// <returns>Whether the environment is Local.</returns>
        public bool IsLocal() => environment.IsEnvironment(AppEnvironment.Keys.Local);

        /// <summary>Checks whether this is a deployable development environment.</summary>
        /// <returns>Whether the environment is Development.</returns>
        public bool IsDevelopment()
        {
            return environment.IsEnvironment(AppEnvironment.Keys.Development);
        }

        /// <summary>Checks whether this is a staging environment.</summary>
        /// <returns>Whether the environment is Staging.</returns>
        public bool IsStaging()
        {
            return environment.IsEnvironment(AppEnvironment.Keys.Staging);
        }

        /// <summary>Checks whether this is a test environment.</summary>
        /// <returns>Whether the environment is Test.</returns>
        public bool IsTest()
        {
            return environment.IsEnvironment("test");
        }

        /// <summary>Checks whether this is a production environment.</summary>
        /// <returns>Whether the environment is Production.</returns>
        public bool IsProduction()
        {
            return environment.IsEnvironment(AppEnvironment.Keys.Production);
        }

        /// <summary>Checks whether this is a user acceptance testing environment.</summary>
        /// <returns>Whether the environment is UAT.</returns>
        public bool IsUserAcceptanceTesting()
        {
            return environment.IsEnvironment("uat");
        }

        /// <summary>Checks whether this is a quality assurance environment.</summary>
        /// <returns>Whether the environment is QA.</returns>
        public bool IsQualityAssurance()
        {
            return environment.IsEnvironment("qa");
        }
    }
}
