using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Adapts actual feature state to a resource's declared management contract.</summary>
public interface IResourceStateHandler
{
    /// <summary>Gets the runtime contract implemented by this handler.</summary>
    ResourceStateDefinition Definition { get; }
    /// <summary>Gets the immutable baseline captured after startup configuration has been applied.</summary>
    /// <remarks>Values identify reproducible configuration, never secrets. Management overrides do not change this baseline.</remarks>
    IReadOnlyList<ResourceStateDeclaration> InitialState => Array.Empty<ResourceStateDeclaration>();
    /// <summary>Validates a desired value without changing feature state.</summary>
    /// <param name="key">The state slot key.</param>
    /// <param name="desired">The desired JSON value.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The domain validation result.</returns>
    ValueTask<ResourceStateValidation> ValidateAsync(ResourceStateKey key, ResourceStateValue desired, CancellationToken cancellationToken = default);
    /// <summary>Reads an atomic snapshot of actual state and its revision.</summary>
    /// <param name="key">The state slot key.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The applied state snapshot.</returns>
    ValueTask<ResourceStateObservation> ReadAsync(ResourceStateKey key, CancellationToken cancellationToken = default);
    /// <summary>Atomically reconciles state and revision; repeated delivery must be safe.</summary>
    /// <param name="context">The verified target and revision context.</param>
    /// <param name="key">The state slot key.</param>
    /// <param name="desired">The desired value.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The actual application outcome.</returns>
    ValueTask<ResourceStateApplyResult> ApplyAsync(ResourceStateApplyContext context, ResourceStateKey key, ResourceStateValue desired, CancellationToken cancellationToken = default);
}
