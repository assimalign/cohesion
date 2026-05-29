using System.Collections.Generic;

namespace Assimalign.Cohesion.DependencyInjection;

/// <summary>
/// Specifies the contract for a collection of service descriptors.
/// </summary>
public interface IServiceContainer : IEnumerable<ServiceDescriptor>
{
    /// <summary>
    /// Get's the number of service descriptors in the container.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    ServiceDescriptor this[int index] { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="descriptor"></param>
    void Register(ServiceDescriptor descriptor);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="descriptor"></param>
    /// <returns></returns>
    bool Unregister(ServiceDescriptor descriptor);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    void UnregisterAt(int index);

    /// <summary>
    /// 
    /// </summary>
    void Clear();

    // bool IsRegistered(ServiceDescriptor descriptor);
}