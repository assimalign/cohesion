namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Marker interface implemented by every element of the OpenAPI description object model.
/// </summary>
/// <remarks>
/// The interface carries no members; it exists so that generic constraints, collections, and
/// tooling can refer to "any OpenAPI element" without enumerating every concrete type.
/// </remarks>
public interface IOpenApiElement
{
}
