namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Collects profiles and builds an <see cref="IMapper"/>.
/// </summary>
public interface IMapperBuilder
{
    /// <summary>
    /// Adds a profile to the mapper being built.
    /// </summary>
    /// <param name="profile">The profile to add.</param>
    /// <returns>The same builder instance for chaining.</returns>
    IMapperBuilder AddProfile(IMapperProfile profile);

    /// <summary>
    /// Builds the configured <see cref="IMapper"/>.
    /// </summary>
    /// <returns>The configured mapper.</returns>
    IMapper Build();
}
