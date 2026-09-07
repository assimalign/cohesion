using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Hosting.TestResource;

/// <summary>
/// Provides an executable fixture whose host fails during an in-process entry invocation.
/// </summary>
public sealed class Program
{
    /// <summary>
    /// Runs the fixture until its ambient resource context requests shutdown.
    /// </summary>
    /// <param name="args">The resource arguments.</param>
    /// <returns>A task that represents the fixture host lifetime.</returns>
    public static async Task Main(string[] args)
    {
        _ = args;

        if (!ResourceRuntime.TryCreateControlPlane(
                typeof(Program).Assembly,
                out IResourceControlPlane? controlPlane) ||
            controlPlane is null)
        {
            throw new InvalidOperationException("The fixture control plane was not registered.");
        }

        var options = new FixtureHostOptions();
        options.HostedServices.Add(new StopFailureService());

        await using var host = new FixtureHost(options);
        ResourceRuntime.HostBuilt(host, controlPlane);
        await host.RunAsync().ConfigureAwait(false);
    }
}

internal static class FixtureRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        ResourceRuntime.RegisterEntry(typeof(Program).Assembly);
        ResourceRuntime.RegisterControlPlane(
            typeof(Program).Assembly,
            static () => ResourceControlPlane.Create());
    }
}

internal sealed class FixtureHostOptions : HostOptions<FixtureHostContext>
{
    internal List<IHostService> HostedServices { get; } = [];
}

internal sealed class FixtureHostContext : HostContext
{
    private readonly FixtureHostOptions _options;

    internal FixtureHostContext(FixtureHostOptions options)
    {
        _options = options;
        Environment = new HostEnvironment("Test");
    }

    public override IHostEnvironment Environment { get; }

    public override IEnumerable<IHostService> HostedServices => _options.HostedServices;
}

internal sealed class FixtureHost : Host<FixtureHostContext>
{
    internal FixtureHost(FixtureHostOptions options)
        : base(options)
    {
        Context = new FixtureHostContext(options);
    }

    public override FixtureHostContext Context { get; }
}

internal sealed class StopFailureService : IHostService
{
    public ServiceId Id { get; } = ServiceId.New();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromException(new InvalidOperationException("fixture stop failure"));
    }
}
