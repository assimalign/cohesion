using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Rezolvr.ApplicationModel;

/// <summary>
/// Describes a Rezolvr resource together with its application-graph dependencies.
/// </summary>
public interface IRezolvrResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed Rezolvr resource wrapped by this descriptor.
    /// </summary>
    new RezolvrResource Resource { get; }
}
