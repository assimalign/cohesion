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
    private Key _key;
    private string? _value;
    private IConfigurationProvider _provider;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    public ConfigurationValue(Key key, string? value)
    {
        _key = key;
        _value = value;
    }

    #region Properties

    /// <summary>
    /// The configuration key.
    /// </summary>
    public Key Key => _key;

    /// <summary>
    /// The raw configuration value.
    /// </summary>
    public string? Value
    {
        get => _value;
        set
        {
            _value = value;
        }
    }
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
