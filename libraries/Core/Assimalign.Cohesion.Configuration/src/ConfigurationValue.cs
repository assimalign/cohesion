using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Key}: {Value}")]
public class ConfigurationValue : IConfigurationValue
{
    public ConfigurationValue(Key key, string? value)
    {
        Key = key;
        Value = value;
    }

    #region Properties

    /// <summary>
    /// The configuration key.
    /// </summary>
    public Key Key { get; }

    /// <summary>
    /// The raw configuration value.
    /// </summary>
    public string? Value { get; set; }

    #endregion

    #region Methods 

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public IConfigurationChangeToken GetChangeToken()
    {
        throw new NotImplementedException();
    }

    #endregion

    #region Operators

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    public static implicit operator KeyValuePair<Key, string?>(ConfigurationValue value)
    {
        return new KeyValuePair<Key, string?>(value.Key, value.Value);
    }

    #endregion
}
