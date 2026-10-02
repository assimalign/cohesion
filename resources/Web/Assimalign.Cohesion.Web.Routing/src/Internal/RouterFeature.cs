namespace Assimalign.Cohesion.Web.Routing.Internal;

/// <summary>
/// Default per-application <see cref="IRouterFeature"/>. Each web application owns exactly one
/// instance, so its <see cref="Builder"/> and the <see cref="Router"/> built from it are isolated
/// from every other application in the process.
/// </summary>
/// <remarks>
/// The router is the builder's single router (<see cref="RouterBuilder.Build"/>): built once and
/// thread-safely on first access — by <c>UseRouting</c> when the application's request pipeline is
/// built — after which the builder rejects further routes.
/// </remarks>
internal sealed class RouterFeature : IRouterFeature
{
    private readonly RouterBuilder _builder = new();

    /// <inheritdoc />
    public string Name => nameof(IRouterFeature);

    /// <inheritdoc />
    public IRouterBuilder Builder => _builder;

    /// <inheritdoc />
    public IRouter Router => _builder.Build();
}
