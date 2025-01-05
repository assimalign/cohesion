using System;
using System.Collections.Generic;

namespace Assimalign.Extensions.Mapping;

/// <summary>
/// 
/// </summary>
public interface IMapperProfile
{
    /// <summary>
    /// Represents the Target Type in which values will be copied to.
    /// </summary>
    Type TargetType { get; }
    /// <summary>
    /// Represents the Source Type to be used to copy values to the target type.
    /// </summary>
    Type SourceType { get; }
    /// <summary>
    /// A collection of actions to be invoked on mapping.
    /// </summary>
    IMapperActionStack MapActions { get; }
    /// <summary>
    /// Invokes the Profile descriptor which should add <see cref="IMapperAction"/>'s 
    /// into the <see cref="IMapperProfile.MapActions"/> stack.
    /// </summary>
    /// <param name="descriptor"></param>
    void Configure(IMapperActionDescriptor descriptor);
}

/// <summary>
/// A generic typed mapper profile
/// </summary>
/// <typeparam name="TTarget"></typeparam>
/// <typeparam name="TSource"></typeparam>
public interface IMapperProfile<TTarget, TSource> : IMapperProfile
{
    /// <inheritdoc cref="IMapperProfile.Configure(IMapperActionDescriptor)"/>
    /// <param name="descriptor">A generic descriptor that wraps the Target and Source objects.</param>
    void Configure(IMapperActionDescriptor<TTarget, TSource> descriptor);
}