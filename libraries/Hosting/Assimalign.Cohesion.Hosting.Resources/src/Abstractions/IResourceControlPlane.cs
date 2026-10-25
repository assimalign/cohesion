using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Defines a resource's local control plane: its runtime surface and configuration status, health,
/// observed endpoints, graceful stop, and declarative commands.
/// </summary>
/// <remarks>
/// The control plane holds no desired state. Configuration comes from the deployment record the platform
/// delivers; the control plane applies it to the registered sections and reports what is in force.
/// </remarks>
public interface IResourceControlPlane
{
    /// <summary>Gets the configuration sections, actions and tools this resource exposes.</summary>
    /// <remarks>The surface is frozen when the resource's host is built.</remarks>
    ResourceRuntimeSurface Surface => ResourceRuntimeSurface.Empty;

    /// <summary>Gets which deployment record this instance applied and the outcome for each section.</summary>
    ResourceConfigurationStatus ConfigurationStatus => ResourceConfigurationStatus.Empty;

    /// <summary>Registers a configuration section the deployment record may set.</summary>
    /// <param name="section">The section.</param>
    /// <exception cref="NotSupportedException">This control plane has no runtime surface.</exception>
    void AddConfiguration(ResourceConfigurationSection section) =>
        throw new NotSupportedException(ErrorMessages.ResourceSurfaceUnsupported);

    /// <summary>Registers an action the gateway may invoke.</summary>
    /// <param name="action">The action.</param>
    /// <exception cref="NotSupportedException">This control plane has no runtime surface.</exception>
    void AddAction(ResourceAction action) =>
        throw new NotSupportedException(ErrorMessages.ResourceSurfaceUnsupported);

    /// <summary>Registers an operator tool a management portal may open.</summary>
    /// <param name="tool">The tool.</param>
    /// <exception cref="NotSupportedException">This control plane has no runtime surface.</exception>
    void AddTool(ResourceTool tool) =>
        throw new NotSupportedException(ErrorMessages.ResourceSurfaceUnsupported);

    /// <summary>Applies a delivered deployment record to the registered configuration sections.</summary>
    /// <param name="record">The deployment record.</param>
    /// <param name="phase">Whether the host has started; while it runs, only live sections apply.</param>
    /// <param name="cancellationToken">Cancels the request before the record is applied.</param>
    /// <returns>The resulting configuration status.</returns>
    /// <exception cref="NotSupportedException">This control plane has no runtime surface.</exception>
    ValueTask<ResourceConfigurationStatus> ApplyConfigurationAsync(
        ResourceConfigurationRecord record,
        ResourceConfigurationPhase phase,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(ErrorMessages.ResourceSurfaceUnsupported);

    /// <summary>Invokes a registered action with a UTF-8 JSON input.</summary>
    /// <param name="name">The action name.</param>
    /// <param name="input">The UTF-8 JSON input.</param>
    /// <param name="cancellationToken">Cancels the action.</param>
    /// <returns>The action's UTF-8 JSON result.</returns>
    /// <exception cref="ArgumentException">The input does not bind to the action's input type.</exception>
    /// <exception cref="NotSupportedException">The resource exposes no action with this name.</exception>
    ValueTask<ReadOnlyMemory<byte>> InvokeActionAsync(
        ResourceActionName name,
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(ErrorMessages.ResourceActionUnknown);

    /// <summary>
    /// Gets the area-defined command kinds accepted by this control plane.
    /// </summary>
    IReadOnlyList<string> AcceptedCommandKinds { get; }

    /// <summary>
    /// Gets accepted declarations, including their owners, without transport state.
    /// </summary>
    IReadOnlyList<ResourceCommand> Commands => Array.Empty<ResourceCommand>();

    /// <summary>
    /// Gets the latest observed endpoint snapshot keyed by endpoint name.
    /// </summary>
    IReadOnlyDictionary<string, Uri> ObservedEndpoints { get; }

    /// <summary>Registers the runtime implementation of an advertised command kind.</summary>
    /// <param name="handler">The area-owned mutation handler.</param>
    /// <exception cref="ArgumentNullException">The handler is null.</exception>
    /// <exception cref="ArgumentException">The handler kind is not advertised.</exception>
    /// <exception cref="InvalidOperationException">A handler already exists for the kind.</exception>
    /// <exception cref="NotSupportedException">This control plane does not support registration.</exception>
    void RegisterCommandHandler(IResourceCommandHandler handler) =>
        throw new NotSupportedException("This resource control plane does not support command handlers.");

    /// <summary>Adds a contributor to health, readiness, and liveness aggregation.</summary>
    /// <param name="contributor">The contributor to add.</param>
    void AddHealthContributor(IHealthContributor contributor);

    /// <summary>Records the realized address of a resource endpoint.</summary>
    /// <param name="name">The endpoint name.</param>
    /// <param name="address">The realized endpoint address.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="address"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty, or <paramref name="address"/> is not an endpoint URI.
    /// </exception>
    void ObserveEndpoint(string name, Uri address);

    /// <summary>Checks the aggregate resource health.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The aggregate health report.</returns>
    ValueTask<ResourceHealthReport> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks whether the resource is ready to receive work.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The aggregate readiness report.</returns>
    ValueTask<ResourceHealthReport> CheckReadinessAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks whether the resource process remains live.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The aggregate liveness report.</returns>
    ValueTask<ResourceHealthReport> CheckLivenessAsync(CancellationToken cancellationToken = default);

    /// <summary>Attaches the built resource host used for graceful stop requests, and freezes the runtime surface.</summary>
    /// <param name="host">The resource host.</param>
    void AttachHost(IHost host);

    /// <summary>Requests a graceful stop from the attached resource host.</summary>
    /// <param name="cancellationToken">Cancels the request before it is accepted.</param>
    /// <returns>A value task that completes when the request has been accepted.</returns>
    ValueTask RequestStopAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes an area-defined declarative command.</summary>
    /// <param name="command">The command envelope.</param>
    /// <param name="cancellationToken">Cancels command execution.</param>
    /// <returns>The area-defined response payload.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="ArgumentException">An envelope identity field is blank.</exception>
    /// <exception cref="NotSupportedException">The command kind has no registered runtime handler.</exception>
    /// <exception cref="ResourceCommandRejectedException">Ownership or the runtime refuses the command.</exception>
    ValueTask<ReadOnlyMemory<byte>> ExecuteCommandAsync(
        ResourceCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a previously applied declaration belonging to the supplied owner.</summary>
    /// <param name="command">The declaration identity to delete.</param>
    /// <param name="cancellationToken">Cancels deletion.</param>
    /// <returns>The area-defined response bytes.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="ArgumentException">An envelope identity field is blank.</exception>
    /// <exception cref="NotSupportedException">The command kind or deletion is unsupported.</exception>
    /// <exception cref="ResourceCommandRejectedException">Ownership or the runtime refuses deletion.</exception>
    ValueTask<ReadOnlyMemory<byte>> DeleteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Resource command kind '{command.Kind}' does not support deletion.");
}
