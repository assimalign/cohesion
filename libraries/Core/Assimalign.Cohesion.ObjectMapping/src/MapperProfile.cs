using System;

namespace Assimalign.Cohesion.ObjectMapping;


using Assimalign.Cohesion.ObjectMapping.Internal;

/// <summary>
/// 
/// </summary>
/// <typeparam name="TTarget"></typeparam>
/// <typeparam name="TSource"></typeparam>
public abstract class MapperProfile<TTarget, TSource> : IMapperProfile<TTarget, TSource>
{
    private IMapperActionStack mapActions;

    public MapperProfile()
    {
        this.mapActions = new MapperActionStack();
    }

    /// <inheritdoc cref="IMapperProfile.TargetType" />
    public Type TargetType => typeof(TTarget);

    /// <inheritdoc cref="IMapperProfile.SourceType"/>
    public Type SourceType => typeof(TSource);

    /// <inheritdoc cref="IMapperProfile.MapActions"/>
    public IMapperActionStack MapActions => this.mapActions;

    /// <inheritdoc cref="IMapperProfile.Configure(IMapperActionDescriptor)"/>
    public virtual void Configure(IMapperActionDescriptor descriptor)
    {
        if (descriptor is IMapperActionDescriptor<TTarget, TSource>)
        {
            this.Configure((IMapperActionDescriptor<TTarget, TSource>)descriptor);
        }
        else
        {
            throw new NotSupportedException($"The descriptor type: '{descriptor.GetType().Name}' is not supported for this MapperProfile.");
        }       
    }

    /// <inheritdoc cref="IMapperProfile{TTarget, TSource}.Configure(IMapperActionDescriptor{TTarget, TSource})"/>
    public abstract void Configure(IMapperActionDescriptor<TTarget, TSource> descriptor);

    public override bool Equals(object obj) => obj is IMapperProfile profile ? profile.SourceType == this.SourceType && profile.TargetType == this.TargetType : false;
    public override int GetHashCode() => HashCode.Combine(this.SourceType, this.TargetType);
}

//private IDictionary<string, Type> GetPaths(Type type)
//{
//    var paths = new Dictionary<string, Type>();

//    foreach (var property in type.GetProperties().Where(x => x.CanWrite && x.CanRead))
//    {
//        if (property.PropertyType.IsValueType())
//        {
//            paths.Add(property.Name, property.PropertyType);
//        }
//        else if (property.PropertyType.IsComplexType())
//        {
//            foreach (var child in GetPaths(property.PropertyType))
//            {
//                paths.Add($"{property.Name}.{child.Key}", child.Value);
//            }
//        }
//        else if (property.PropertyType.IsEnumerableType(out var implementationType))
//        {

//            if (implementationType.IsComplexType())
//            {
//                foreach (var child in GetPaths(implementationType))
//                {
//                    paths.Add($"{property.Name}.[{child.Key}]", child.Value);
//                }
//            }
//        }
//    }

//    return paths;
//}