using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

/// <summary>
/// A resource's program, as a gateway program references it: it reads what its platform gave it
/// from its environment, publishes itself to its gateway, and runs its host until it is stopped.
/// Records each run.
/// </summary>
internal sealed class TestProgram
{
    // One sequence for every program, so the order in which programs stopped can be compared.
    private static int _stops;

    private readonly ProviderName _provider;
    private readonly ResourceName _name;
    private readonly string[] _configuration;
    private readonly ConcurrentQueue<TestProgramRun> _runs = new();

    /// <param name="provider">The resource's provider.</param>
    /// <param name="name">The resource's name.</param>
    /// <param name="configuration">The configuration keys the program reads, such as its settings.</param>
    public TestProgram(ProviderName provider, ResourceName name, params string[] configuration)
    {
        _provider = provider;
        _name = name;
        _configuration = configuration;
    }

    public IReadOnlyList<TestProgramRun> Runs => [.. _runs];

    /// <summary>An exception the program throws before it publishes, as a program whose composition fails does.</summary>
    public Exception? Failure { get; set; }

    public async Task MainAsync(string[] args)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string variable in (string[])[
            AppEnvironment.Variables.Application,
            AppEnvironment.Variables.Gateway,
            AppEnvironment.Variables.Resource,
            AppEnvironment.Variables.ManifestEndpoint,
            AppEnvironment.Variables.ControlPlaneEndpoint,
            AppEnvironment.Variables.Deployment,
            AppEnvironment.Variables.Endpoint("http", "PORT")])
        {
            environment[variable] = AppEnvironment.GetValue(variable);
        }

        foreach (string key in _configuration)
        {
            environment[key] = AppEnvironment.GetValue(AppEnvironment.Variables.Configuration(key));
        }

        // The program repeats the deployment that started it, as every managed instance does.
        var resource = new TestResource(_provider, _name, DeploymentId.TryParse(environment[AppEnvironment.Variables.Deployment], out DeploymentId deployment) ? deployment : null);
        ApplicationInstancePublisher publisher = ApplicationInstancePublisher.Create(new ApplicationInstancePublisherOptions
        {
            Endpoint = new Uri(environment[AppEnvironment.Variables.ManifestEndpoint]!),
        });

        var options = new ProgramHostOptions();
        options.HostedServices.Add(ApplicationInstanceService.Create(
            ApplicationInstanceControlPlane.Create(resource, JsonApplicationContentSerializer.Create()),
            publisher));

        var run = new TestProgramRun(resource.Key, environment);
        _runs.Enqueue(run);
        try
        {
            await new ProgramHost(options).RunAsync();
        }
        finally
        {
            run.Stop(Interlocked.Increment(ref _stops));
        }
    }
}

/// <summary>
/// One run of a test program: what it read from its environment, the instance it published, and
/// when it stopped.
/// </summary>
internal sealed class TestProgramRun
{
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestProgramRun(InstanceKey key, IReadOnlyDictionary<string, string?> environment)
    {
        Key = key;
        Environment = environment;
    }

    public InstanceKey Key { get; }

    /// <summary>The variables the program read, by name; configuration keys by key.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    public Task Stopped => _stopped.Task;

    /// <summary>The program's place among every program's stops; 0 while it runs.</summary>
    public int StopOrder { get; private set; }

    public void Stop(int order)
    {
        StopOrder = order;
        _stopped.TrySetResult();
    }
}
