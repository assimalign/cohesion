using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.DependencyInjection;

/// <summary>
/// Specifies the contract for a collection of service descriptors.
/// </summary>
public interface IServiceCollection : IList<ServiceDescriptor>
{

}
