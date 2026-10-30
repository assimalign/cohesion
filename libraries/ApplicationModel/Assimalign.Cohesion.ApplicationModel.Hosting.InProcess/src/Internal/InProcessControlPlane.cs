using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.InProcess.Internal;

/// <summary>
/// An instance's control plane as its in-process gateway calls it: the control plane itself, with
/// every call run in the execution context the instance attached it from.
/// </summary>
/// <remarks>
/// The gateway calls from its own flow. Without this, an action would run in the gateway's
/// environment rather than the instance's, which a request to the instance's own listener never
/// does: the instance reads its variables and ambient state through that context.
/// </remarks>
internal sealed class InProcessControlPlane : IApplicationInstanceControlPlane
{
    private readonly IApplicationInstanceControlPlane _controlPlane;
    private readonly ExecutionContext? _context;

    public InProcessControlPlane(IApplicationInstanceControlPlane controlPlane, ExecutionContext? context)
    {
        _controlPlane = controlPlane;
        _context = context;
    }

    public IApplicationResourceInstance Resource => _controlPlane.Resource;

    public ApplicationInstanceManifest Manifest => _controlPlane.Manifest;

    public IApplicationContentSerializer Serializer => _controlPlane.Serializer;

    public ValueTask<ApplicationActionResult> ExecuteAsync(ActionName action, ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Run(() => _controlPlane.ExecuteAsync(action, context, cancellationToken));
    }

    public ValueTask<ApplicationActionResult> RestoreAsync(ApplicationActionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return Run(() => _controlPlane.RestoreAsync(snapshot, cancellationToken));
    }

    // Starts the call inside the instance's context; its continuations keep that context, and the
    // caller's own is restored once the call first yields.
    private ValueTask<ApplicationActionResult> Run(Func<ValueTask<ApplicationActionResult>> call)
    {
        if (_context is null)
        {
            return call.Invoke();
        }

        ValueTask<ApplicationActionResult> result = default;
        ExecutionContext.Run(_context, _ => result = call.Invoke(), null);
        return result;
    }
}
