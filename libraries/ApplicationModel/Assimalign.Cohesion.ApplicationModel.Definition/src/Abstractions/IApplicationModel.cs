using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The immutable model of an application: what its gateway deploys, as validated manifest data.
/// </summary>
/// <remarks>
/// <para>
/// A model is built from code, by <see cref="IApplicationBuilder.Build"/>, or from manifest data a
/// gateway received, by
/// <see cref="ApplicationModel.Create(ApplicationManifest, GatewayName, IEnumerable{ApplicationResourceOutputValue})"/>.
/// Both paths enforce the same rules, so a gateway treats the two alike.
/// </para>
/// <para>
/// <see cref="Manifest"/> is always the whole hierarchy, so a dependency that points outside this
/// gateway's part still resolves. <see cref="Name"/> says which gateway the model is for, and
/// <see cref="Deployment"/> is that gateway's part.
/// </para>
/// </remarks>
public interface IApplicationModel
{
    /// <summary>
    /// Gets the id of the gateway the model is for.
    /// </summary>
    GatewayId Id => GatewayId.FromName(Name);

    /// <summary>
    /// Gets the name of the gateway the model is for: the root gateway for a model built in code, or
    /// the child gateway a deployment manifest names.
    /// </summary>
    GatewayName Name { get; }

    /// <summary>
    /// Gets the gateway's position in the hierarchy.
    /// </summary>
    ApplicationGatewayRole Role => Name == Manifest.Name ? ApplicationGatewayRole.Root : ApplicationGatewayRole.Composite;

    /// <summary>
    /// Gets the manifest of the whole hierarchy, from the root gateway down.
    /// </summary>
    ApplicationManifest Manifest { get; }

    /// <summary>
    /// Gets the part of <see cref="Manifest"/> this gateway deploys: its own resources and its child
    /// gateways.
    /// </summary>
    ApplicationManifest Deployment { get; }

    /// <summary>
    /// Gets the values of outputs of resources in the gateways above this one that settings in its
    /// part use, as the gateway above passed them; empty for the root gateway's model.
    /// </summary>
    IReadOnlyList<ApplicationResourceOutputValue> Outputs { get; }
}
