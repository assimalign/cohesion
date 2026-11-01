
namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A placeholder for one output of a resource: a value the resource has only once it is deployed,
/// such as the endpoint its platform gives it.
/// </summary>
/// <remarks>
/// A definition creates it for its own resource (<see cref="IApplicationResourceDefinition.Output"/>,
/// <see cref="IApplicationResourceDefinition.Endpoint"/>), and a parent reference for one of the
/// parent's resources (<see cref="IApplicationModelParent.Output"/>, <see cref="IApplicationModelParent.Endpoint"/>).
/// The gateway that runs the resource records the value its infrastructure provider reports, fills
/// it in where one of its resources refers to it, and hands it to a child gateway that joins.
/// </remarks>
/// <param name="Gateway">The gateway whose set declares the resource.</param>
/// <param name="Provider">The resource's provider.</param>
/// <param name="Name">The resource's name.</param>
/// <param name="Output">The output's name.</param>
public sealed record ApplicationResourceOutput(
    GatewayName Gateway,
    ProviderName Provider,
    ResourceName Name,
    OutputName Output);
