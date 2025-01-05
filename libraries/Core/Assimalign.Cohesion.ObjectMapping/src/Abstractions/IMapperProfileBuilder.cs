using System;
using System.Collections.Generic;

namespace Assimalign.Extensions.Mapping;

/// <summary>
/// A fluent builder pattern for creating in-line mapper profiles.
/// </summary>
public interface IMapperProfileBuilder
{
    /// <summary>
    /// Creates a profile from the delegate descriptor and returns 
    /// the current instance of the <see cref="IMapperProfileBuilder"/>.
    /// </summary>
    /// <typeparam name="TTarget"></typeparam>
    /// <typeparam name="TSource"></typeparam>
    /// <param name="configure"></param>
    /// <returns></returns>
    IMapperProfileBuilder CreateProfile<TTarget, TSource>(Action<IMapperActionDescriptor<TTarget, TSource>> configure);
    /// <summary>
    /// Builds the collection of configured profiles.
    /// </summary>
    /// <returns>A collection of <see cref="IMapperProfile"/></returns>
    IEnumerable<IMapperProfile> Build();
}
