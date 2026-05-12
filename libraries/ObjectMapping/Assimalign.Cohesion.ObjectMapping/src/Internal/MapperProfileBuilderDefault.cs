using System;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

internal class MapperProfileBuilderDefault : MapperProfileBuilder
{
    private readonly Action<IMapperProfileBuilder> configure;

    public MapperProfileBuilderDefault(Action<IMapperProfileBuilder> configure)
    {
        this.configure = configure;
    }


    protected override void OnBuild(IMapperProfileBuilder builder)
    {
        configure.Invoke(builder);
    }
}
