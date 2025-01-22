
namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// The <see cref="IManagedHostService"/> is an abstraction to separate the 
/// management layer and the application layer. 
/// 
/// Many services such as databases, messaging systems, etc., are out of the box solutions that simply. Often times there is a 
/// need to run middle extensions between the exucution of these services. The <see cref="IManagedHostService"/>
/// </summary>
/// <remarks>
/// </remarks>
public interface IManagedHostService : IHostService
{
    
}