namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Defines a mutable configuration root that can load additional providers at runtime.
/// </summary>
public interface IConfigurationManager : IConfiguration, IConfigurationBuilder;