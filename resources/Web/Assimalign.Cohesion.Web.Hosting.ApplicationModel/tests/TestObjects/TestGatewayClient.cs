using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Tests;

/// <summary>
/// A throwaway platform for one Web resource instance: its client records the manifests the
/// instance publishes, answers with the snapshots and lease a test scripts, counts renewals, and
/// calls the instance's control plane as a gateway would once the instance opens it.
/// </summary>
internal sealed class TestGatewayClient : IApplicationGatewayClient
{
    private readonly ConcurrentQueue<ApplicationInstanceManifest> _manifests = new();
    private ApplicationActionSnapshot[] _answer = [];
    private TimeSpan? _lease;
    private Exception? _refusal;
    private int _renewals;
    private volatile bool _forgetful;
    private volatile IApplicationInstanceControlPlane? _serving;

    public PlatformName Platform => "test";

    /// <summary>
    /// Gets the manifests received so far, in arrival order.
    /// </summary>
    public IReadOnlyCollection<ApplicationInstanceManifest> Manifests => _manifests;

    /// <summary>
    /// Gets how many renewals the instance has sent.
    /// </summary>
    public int Renewals => Volatile.Read(ref _renewals);

    /// <summary>
    /// Gets the control plane the gateway's calls reach, or <see langword="null"/> while the instance
    /// has not opened it or has closed it.
    /// </summary>
    public IApplicationInstanceControlPlane? Serving => _serving;

    /// <summary>
    /// Answers later publications with these snapshots.
    /// </summary>
    public void AnswerWith(params ApplicationActionSnapshot[] snapshots)
    {
        Volatile.Write(ref _answer, snapshots);
    }

    /// <summary>
    /// Answers later publications with a snapshot of one action, given as its JSON value.
    /// </summary>
    public void AnswerWith(ActionName action, string value)
    {
        AnswerWith(ApplicationActionSnapshot.Create(action, Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>
    /// Grants later publications a lease, so the instance renews.
    /// </summary>
    public void LeaseWith(TimeSpan lease)
    {
        _lease = lease;
    }

    /// <summary>
    /// Refuses later publications with this exception, as a gateway that refuses the manifest does.
    /// </summary>
    public void RefuseWith(Exception refusal)
    {
        Volatile.Write(ref _refusal, refusal);
    }

    /// <summary>
    /// Answers later renewals as a gateway that does not know the instance, such as one restarted
    /// without its record.
    /// </summary>
    public void ForgetInstances()
    {
        _forgetful = true;
    }

    /// <summary>
    /// Runs an action on the instance as the gateway would: the input crosses as bytes, which the
    /// control plane reads with its own serializer.
    /// </summary>
    /// <exception cref="InvalidOperationException">The instance's control plane is not open to the gateway.</exception>
    public ValueTask<ApplicationActionResult> ExecuteAsync(ActionName action, ApplicationActionKind operation, string? input = null)
    {
        IApplicationInstanceControlPlane controlPlane = _serving
            ?? throw new InvalidOperationException("The instance's control plane is not open to the gateway.");
        ApplicationActionContext context = input is null
            ? ApplicationActionContext.Create(operation, controlPlane.Serializer)
            : ApplicationActionContext.Create(operation, controlPlane.Serializer, Encoding.UTF8.GetBytes(input));

        return controlPlane.ExecuteAsync(action, context);
    }

    public ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        _manifests.Enqueue(manifest);
        if (Volatile.Read(ref _refusal) is { } refusal)
        {
            throw refusal;
        }

        return ValueTask.FromResult(new ApplicationInstancePublication(Volatile.Read(ref _answer), _lease));
    }

    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _renewals);
        return ValueTask.FromResult(!_forgetful);
    }

    public ValueTask StartAsync(IApplicationInstanceControlPlane controlPlane, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);

        _serving = controlPlane;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _serving = null;
        return ValueTask.CompletedTask;
    }
}
