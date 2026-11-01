namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Declares the application one gateway runs, the parent gateway it joins, and builds its model.
/// </summary>
/// <remarks>
/// <see cref="ApplicationModel.CreateBuilder(GatewayName)"/> creates one. A gateway's builder
/// implements this interface beside its own, so a gateway program declares its model and configures
/// its gateway on one object, and an area's registration verbs extend either.
/// </remarks>
public interface IApplicationModelBuilder : IApplicationSet
{
    /// <summary>
    /// Gets the parent gateway this gateway joins, or <see langword="null"/> for a root gateway.
    /// </summary>
    IApplicationModelParent? Parent { get; }

    /// <summary>
    /// Names the parent gateway this gateway joins.
    /// </summary>
    /// <param name="name">The parent gateway's name.</param>
    /// <returns>The parent, for the outputs of its resources this gateway's settings use.</returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="name"/> is <see langword="default"/> or names this gateway.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// The gateway already names a parent, or the model is built.
    /// </exception>
    IApplicationModelParent UseParent(GatewayName name);

    /// <summary>
    /// Validates the declarations and builds the model. The builder accepts no changes afterward.
    /// </summary>
    /// <returns>The model.</returns>
    /// <exception cref="System.ArgumentException">
    /// A declaration breaks a rule of <see cref="ApplicationModel.Create(ApplicationModelManifest)"/>, such
    /// as a setting that uses an output of a gateway that is neither this one nor its parent.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">The model is already built.</exception>
    IApplicationModel Build();
}
