namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// 
/// </summary>
public interface IHostEnvironment
{
    /// <summary>
    /// The name of the environment.
    /// </summary>
    string? Name { get; }
    /// <summary>
    /// Checks whether the environment name.
    /// </summary>
    /// <param name="environment"></param>
    /// <returns></returns>
    bool IsEnvironment(string? environment);
}