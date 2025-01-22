namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Represents a single mapping 
/// </summary>
public interface IMapperAction 
{
	/// <summary>
	/// An arbitrary number used as a unique identifier. This helps to 
	/// prevent duplicate mappings of the same member within a <see cref="IMapperProfile"/>.
	/// </summary>
	int Id { get; }
	/// <summary>
	/// 
	/// </summary>
	/// <param name="context"></param>
	void Invoke(IMapperContext context);
}