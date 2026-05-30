using System;
using System.Diagnostics.CodeAnalysis;

using Assimalign.Cohesion.ObjectMapping.Internal;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Collects profiles and options, then builds a <see cref="Mapper"/>.
/// </summary>
public sealed class MapperBuilder : IMapperBuilder
{
    private readonly MapperOptions _options;
    private Mapper? _mapper;

    /// <summary>
    /// Initializes a new <see cref="MapperBuilder"/> with default options.
    /// </summary>
    public MapperBuilder()
    {
        _options = new();
    }

    /// <summary>
    /// Initializes a new <see cref="MapperBuilder"/> with the supplied options.
    /// </summary>
    /// <param name="options">The mapper options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public MapperBuilder(MapperOptions options)
    {
        _options = ArgumentNullException.ThrowIfNull<MapperOptions>(options);
    }

    /// <summary>
    /// Adds a profile instance to the mapper being built.
    /// </summary>
    /// <param name="profile">The profile to add.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="profile"/> is <see langword="null"/>.</exception>
    public MapperBuilder AddProfile(IMapperProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _options.Profiles.Add(profile);

        return this;
    }

    /// <summary>
    /// Adds a profile for the <typeparamref name="TTarget"/>/<typeparamref name="TSource"/>
    /// pair configured inline through a descriptor callback.
    /// </summary>
    /// <typeparam name="TTarget">The target type the profile maps to.</typeparam>
    /// <typeparam name="TSource">The source type the profile maps from.</typeparam>
    /// <param name="configure">A callback that configures the mapping actions.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public MapperBuilder AddProfile<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TTarget,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSource>(Action<MapperProfileDescriptor<TTarget, TSource>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return AddProfile(new DefaultMapperProfile<TTarget, TSource>(configure));
    }

    /// <summary>
    /// Builds the configured <see cref="Mapper"/>. Can only be called once per builder.
    /// </summary>
    /// <returns>The configured mapper.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the mapper has already been built.</exception>
    public Mapper Build()
    {
        InvalidOperationException.ThrowIf(_mapper is not null, "Mapper has already been built.");
        return _mapper = new Mapper(_options);
    }

    IMapperBuilder IMapperBuilder.AddProfile(IMapperProfile profile)
    {
        return AddProfile(profile);
    }

    IMapper IMapperBuilder.Build()
    {
        return Build();
    }
}
