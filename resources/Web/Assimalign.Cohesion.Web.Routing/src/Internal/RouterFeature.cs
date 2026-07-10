namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Default per-application <see cref="IRouterFeature"/>. Each web application owns exactly one
/// instance, so its <see cref="Builder"/> and the <see cref="Router"/> built from it are isolated
/// from every other application in the process.
/// </summary>
internal sealed class RouterFeature : IRouterFeature
{
    private IRouter? _router;

    /// <inheritdoc />
    public string Name => nameof(IRouterFeature);

    /// <inheritdoc />
    public IRouterBuilder Builder { get; } = new RouterBuilder();

    /// <inheritdoc />
    public IRouter Router => _router ??= Builder.Build();
}
