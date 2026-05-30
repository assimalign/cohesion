namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Resolves configured <see cref="IMapper"/> instances by name.
/// </summary>
public interface IMapperFactory
{
    /// <summary>
    /// Resolves the mapper registered under the given name.
    /// </summary>
    /// <param name="mapperName">The name of the mapper to resolve.</param>
    /// <returns>The registered <see cref="IMapper"/>.</returns>
    /// <exception cref="MapperException">Thrown when no mapper is registered with <paramref name="mapperName"/>.</exception>
    IMapper Create(string mapperName);
}
