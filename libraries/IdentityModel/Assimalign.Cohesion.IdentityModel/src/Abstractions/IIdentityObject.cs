namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents a normalized identity model object.
/// </summary>
public interface IIdentityObject
{
    /// <summary>
    /// Gets the kind of identity represented by the object.
    /// </summary>
    IdentityKind Kind { get; }
}
