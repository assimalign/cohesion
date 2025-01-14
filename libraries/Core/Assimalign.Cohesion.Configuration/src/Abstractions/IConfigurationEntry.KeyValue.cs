namespace Assimalign.Cohesion.Configuration;


/// <summary>
/// Represents a leaf entry in the configuration tree.
/// </summary>
public interface IConfigurationValue : IConfigurationEntry
{
    /// <summary>
    /// The raw configuration value.
    /// </summary>
    object? Value { get; }
}

/// <summary>
/// 
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IConfigurationValue<T> : IConfigurationValue
{
    /// <summary>
    /// 
    /// </summary>
    new T Value { get; }
}
