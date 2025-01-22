using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// 
/// </summary>
public interface IMapper
{
    /// <summary>
    /// 
    /// </summary>
    IEnumerable<IMapperProfile> Profiles { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TTarget"></typeparam>
    /// <typeparam name="TSource"></typeparam>
    /// <param name="source"></param>
    /// <returns></returns>
    TTarget Map<TTarget, TSource>(TSource source)
        where TTarget : new();

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TSource"></typeparam>
    /// <typeparam name="TTarget"></typeparam>
    /// <param name="source"></param>
    /// <param name="target"></param>
    /// <returns></returns>
    TTarget Map<TTarget, TSource>(TTarget target, TSource source);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="source"></param>
    /// <param name="sourceType"></param>
    /// <param name="targetType"></param>
    /// <returns></returns>
    object Map(object source, Type targetType, Type sourceType);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="source"></param>
    /// <param name="target"></param>
    /// <param name="sourceType"></param>
    /// <param name="targetType"></param>
    /// <returns></returns>
    object Map(object target, object source, Type targetType, Type sourceType);
}