using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

public abstract partial class ApplicationGateway
{
    /// <summary>Renders local execution units using the shared local plan-set document format.</summary>
    /// <param name="processKind">The execution-unit kind, such as a process or an ambient host.</param>
    /// <param name="endpointAllocation">The endpoint allocation strategy shown in the document.</param>
    /// <param name="models">The application models to render in order.</param>
    /// <param name="resolveArtifact">Resolves each local resource's artifact identity and optional content root without realizing it.</param>
    /// <param name="output">The destination for the rendered document.</param>
    /// <param name="cancellationToken">A token that cancels rendering.</param>
    /// <returns>A task that completes after the document has been written.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">An execution setting is empty or a model is null.</exception>
    /// <exception cref="InvalidOperationException">A model's descriptors and plans are inconsistent.</exception>
    /// <exception cref="IOException">The output cannot be written.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    protected Task RenderLocalPlanSetAsync(
        string processKind,
        string endpointAllocation,
        IReadOnlyList<IApplicationModel> models,
        Func<IApplicationResource, (string Identity, string? ContentRoot)> resolveArtifact,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolveArtifact);
        return LocalPlanSetWriter.WriteAsync(
            Name, processKind, endpointAllocation, models,
            resource =>
            {
                (string identity, string? contentRoot) = resolveArtifact(resource);
                return new LocalRenderArtifact(identity, contentRoot);
            },
            output, cancellationToken);
    }
}
