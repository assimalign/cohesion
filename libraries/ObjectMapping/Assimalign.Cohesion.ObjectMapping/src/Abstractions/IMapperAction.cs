namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Represents a single mapping action.
/// </summary>
public interface IMapperAction
{
    /// <summary>
    /// Applies this action against the supplied mapping context, copying the
    /// relevant value(s) from the context source onto the context target.
    /// </summary>
    /// <param name="context">The current mapping context.</param>
    void Invoke(IMapperContext context);
}
