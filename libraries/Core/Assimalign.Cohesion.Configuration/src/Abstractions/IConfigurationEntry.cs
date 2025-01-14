namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// 
/// </summary>
public interface IConfigurationEntry
{
    /// <summary>
    /// 
    /// </summary>
    Key Key { get; }

    /// <summary>
    /// The composite path
    /// </summary>
    KeyPath Path { get; }

    /// <summary>
    /// The provider in which the entry belongs to.
    /// </summary>
    IConfigurationProvider Provider { get;  }
}