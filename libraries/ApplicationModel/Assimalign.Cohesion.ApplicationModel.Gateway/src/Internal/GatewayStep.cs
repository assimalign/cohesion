using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// One step of a gateway's deployment: one of its own resources, or one of its child gateways with
// everything below it, and the steps that must be ready before it deploys. Order builds a gateway's
// steps in dependency order; the gateway then runs each one once the steps it waits for are ready,
// and Completion tells the steps waiting for it whether it became ready.
//
// A child gateway is one step: the gateway cannot see the readiness of what the child deploys, so
// everything below the child is ordered as a whole.
[DebuggerDisplay("{Name,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class GatewayStep
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<GatewayStep> _dependencies = [];
    private readonly List<(ApplicationResourceDependency Dependency, string Dependent)> _reasons = [];
    private readonly List<(GatewayName Gateway, ApplicationResourceManifest Resource)> _members = [];
    private int _skipped;

    private GatewayStep(ApplicationResourceManifest? resource, ApplicationManifest? gateway)
    {
        Resource = resource;
        Gateway = gateway;
        Name = resource is null
            ? Format(ErrorMessages.GatewayStepGateway, gateway!.Name)
            : string.Concat(resource.Provider.ToString(), "/", resource.Name.ToString());
    }

    // The resource the step deploys, or null for a child gateway.
    public ApplicationResourceManifest? Resource { get; }

    // The child gateway the step deploys, or null for a resource.
    public ApplicationManifest? Gateway { get; }

    public string Name { get; }

    public IReadOnlyList<GatewayStep> Dependencies => _dependencies;

    // Completes when the step ends: true when it became ready, false when it failed or was skipped.
    public Task<bool> Completion => _completion.Task;

    public bool IsSkipped => Volatile.Read(ref _skipped) != 0;

    public void Skip()
    {
        Volatile.Write(ref _skipped, 1);
    }

    public void Complete(bool ready)
    {
        _completion.TrySetResult(ready);
    }

    // The steps of one gateway of a hierarchy, each after every step it waits for; independent
    // steps keep their declaration order, own resources first, then child gateways.
    public static GatewayStep[] Order(ApplicationManifest gateway)
    {
        var steps = new List<GatewayStep>();
        var owners = new Dictionary<(GatewayName Gateway, ProviderName Provider, ResourceName Name), GatewayStep>();
        foreach (ApplicationResourceManifest resource in gateway.Resources)
        {
            var step = new GatewayStep(resource, null);
            step._members.Add((gateway.Name, resource));
            owners.Add((gateway.Name, resource.Provider, resource.Name), step);
            steps.Add(step);
        }

        foreach (ApplicationManifest child in gateway.Gateways)
        {
            var step = new GatewayStep(null, child);
            AddMembers(step, child);
            foreach ((GatewayName Gateway, ApplicationResourceManifest Resource) member in step._members)
            {
                owners.Add((member.Gateway, member.Resource.Provider, member.Resource.Name), step);
            }

            steps.Add(step);
        }

        // A step waits for the steps that own what its resources depend on. A dependency outside the
        // gateway's part is an ancestor gateway's to order, which deploys this gateway after it.
        foreach (GatewayStep step in steps)
        {
            foreach ((GatewayName Gateway, ApplicationResourceManifest Resource) member in step._members)
            {
                foreach (ApplicationResourceDependency dependency in member.Resource.Dependencies)
                {
                    if (owners.TryGetValue((dependency.Gateway, dependency.Provider, dependency.Name), out GatewayStep? owner)
                        && owner != step
                        && !step._dependencies.Contains(owner))
                    {
                        step._dependencies.Add(owner);
                        step._reasons.Add((dependency, Describe(member.Gateway, member.Resource.Provider, member.Resource.Name)));
                    }
                }
            }
        }

        return Sort(gateway, steps);
    }

    private static void AddMembers(GatewayStep step, ApplicationManifest gateway)
    {
        foreach (ApplicationResourceManifest resource in gateway.Resources)
        {
            step._members.Add((gateway.Name, resource));
        }

        foreach (ApplicationManifest child in gateway.Gateways)
        {
            AddMembers(step, child);
        }
    }

    // Places every step whose dependencies are placed, in declaration order, until none is left. A
    // step that never becomes free waits, through its dependencies, for itself.
    private static GatewayStep[] Sort(ApplicationManifest gateway, List<GatewayStep> steps)
    {
        var placed = new HashSet<GatewayStep>();
        var order = new List<GatewayStep>(steps.Count);
        bool progress = true;
        while (order.Count < steps.Count && progress)
        {
            progress = false;
            foreach (GatewayStep step in steps)
            {
                if (!placed.Contains(step) && step._dependencies.TrueForAll(placed.Contains))
                {
                    placed.Add(step);
                    order.Add(step);
                    progress = true;
                }
            }
        }

        if (order.Count == steps.Count)
        {
            return [.. order];
        }

        // Every unplaced step waits for an unplaced one, so following them from any unplaced step
        // returns to a step already on the path.
        GatewayStep current = steps.Find(step => !placed.Contains(step))!;
        var path = new List<GatewayStep>();
        while (!path.Contains(current))
        {
            path.Add(current);
            current = current._dependencies.Find(dependency => !placed.Contains(dependency))!;
        }

        var edges = new List<string>();
        for (int index = path.IndexOf(current); index < path.Count; index++)
        {
            GatewayStep from = path[index];
            GatewayStep to = index + 1 < path.Count ? path[index + 1] : current;
            (ApplicationResourceDependency dependency, string dependent) = from._reasons[from._dependencies.IndexOf(to)];
            edges.Add(Format(
                ErrorMessages.GatewayStepWaits,
                from.Name,
                to.Name,
                dependent,
                Describe(dependency.Gateway, dependency.Provider, dependency.Name)));
        }

        throw new ArgumentException(
            Format(ErrorMessages.GatewayDeploymentCycle, gateway.Name, string.Join("; ", edges)),
            "model");
    }

    private static string Describe(GatewayName gateway, ProviderName provider, ResourceName name)
    {
        return string.Concat(gateway.ToString(), "/", provider.ToString(), "/", name.ToString());
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly GatewayStep _step;

        public DebugView(GatewayStep step)
        {
            _step = step;
        }

        public string Name => _step.Name;

        public IReadOnlyList<GatewayStep> Dependencies => _step.Dependencies;

        public bool IsSkipped => _step.IsSkipped;
    }
}
