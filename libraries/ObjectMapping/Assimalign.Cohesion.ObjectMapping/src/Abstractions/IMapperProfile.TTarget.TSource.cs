namespace Assimalign.Cohesion.ObjectMapping;

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