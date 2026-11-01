using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// One step of a gateway's deployment: one of its resources, and the steps that must be ready before
// it deploys. Order builds a gateway's steps in dependency order; the gateway then runs each one once
// the steps it waits for are ready, and Completion tells the steps waiting for it whether it became
// ready. A gateway orders only its own resources: a child gateway deploys on its own and joins.
[DebuggerDisplay("{Name,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class GatewayStep
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<GatewayStep> _dependencies = [];
    private readonly List<ApplicationResourceDependency> _reasons = [];
    private int _skipped;

    private GatewayStep(ApplicationResourceManifest resource)
    {
        Resource = resource;
        Name = string.Concat(resource.Provider.ToString(), "/", resource.Name.ToString());
    }

    // The resource the step deploys.
    public ApplicationResourceManifest Resource { get; }

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

    // The steps of a gateway, each after every step it waits for; independent steps keep their
    // declaration order.
    public static GatewayStep[] Order(ApplicationModelManifest gateway)
    {
        var steps = new List<GatewayStep>();
        var owners = new Dictionary<(ProviderName Provider, ResourceName Name), GatewayStep>();
        foreach (ApplicationResourceManifest resource in gateway.Resources)
        {
            var step = new GatewayStep(resource);
            owners.Add((resource.Provider, resource.Name), step);
            steps.Add(step);
        }

        // A step waits for the steps of the resources it depends on.
        foreach (GatewayStep step in steps)
        {
            foreach (ApplicationResourceDependency dependency in step.Resource.Dependencies)
            {
                if (dependency.Gateway == gateway.Name
                    && owners.TryGetValue((dependency.Provider, dependency.Name), out GatewayStep? owner)
                    && owner != step
                    && !step._dependencies.Contains(owner))
                {
                    step._dependencies.Add(owner);
                    step._reasons.Add(dependency);
                }
            }
        }

        return Sort(gateway, steps);
    }

    // Places every step whose dependencies are placed, in declaration order, until none is left. A
    // step that never becomes free waits, through its dependencies, for itself. A model refuses such
    // a cycle, so this guards a model that was not validated.
    private static GatewayStep[] Sort(ApplicationModelManifest gateway, List<GatewayStep> steps)
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
            ApplicationResourceDependency dependency = from._reasons[from._dependencies.IndexOf(to)];
            edges.Add(Format(
                ErrorMessages.GatewayStepWaits,
                from.Name,
                to.Name,
                Describe(gateway.Name, from.Resource.Provider, from.Resource.Name),
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
