namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Represents a single mapping action.
/// </summary>
public interface IMapperAction 
{
	/// <summary>
	/// 
	/// </summary>
	/// <param name="context"></param>
	void Invoke(IMapperContext context);
}