
namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A placeholder for one output of a resource: a value the resource has only once it is deployed,
/// such as the endpoint its platform gives it.
/// </summary>
/// <remarks>
/// A descriptor creates it (<see cref="IApplicationResourceDefinition.Output"/>,
/// <see cref="IApplicationResourceDefinition.Endpoint"/>). The gateway that runs the resource records
/// the value its infrastructure provider reports, and fills it in where a dependent resource refers to
/// it.
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
