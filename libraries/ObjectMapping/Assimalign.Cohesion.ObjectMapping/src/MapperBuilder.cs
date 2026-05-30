using System;
using System.Linq;

namespace Assimalign.Cohesion.ObjectMapping;

using Assimalign.Cohesion.ObjectMapping.Internal;
using System.Diagnostics.CodeAnalysis;

public sealed class MapperBuilder : IMapperBuilder
{
    private readonly MapperOptions _options;
    private Mapper? _mapper;

    public MapperBuilder()
    {
        _options = new();
    }

    public MapperBuilder(MapperOptions options)
    {
        _options = ArgumentNullException.ThrowIfNull<MapperOptions>(options);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TTarget"></typeparam>
    /// <typeparam name="TSource"></typeparam>
    /// <param name="profile"></param>
    /// <exception cref="ArgumentException"></exception>
    public MapperBuilder AddProfile(IMapperProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _options.Profiles.Add(profile);

        return this;
    }
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TTarget"></typeparam>
    /// <typeparam name="TSource"></typeparam>
    /// <param name="configure"></param>
    /// <returns></returns>
    public MapperBuilder AddProfile<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TTarget,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSource>(Action<MapperProfileDescriptor<TTarget, TSource>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return AddProfile(new DefaultMapperProfile<TTarget, TSource>(configure));
    }


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
