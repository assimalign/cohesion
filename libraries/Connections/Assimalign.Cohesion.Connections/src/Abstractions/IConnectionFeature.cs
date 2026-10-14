namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Describes an optional capability or fact attached to a connection.
/// </summary>
public interface IConnectionFeature
{
    /// <summary>
    /// Gets the non-empty, stable name identifying this feature's slot in a connection's collection.
    /// </summary>
    string Name { get; }
}
