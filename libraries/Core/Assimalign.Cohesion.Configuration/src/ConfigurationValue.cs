using System;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Configuration;

using Internal;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Key}: {Value}")]
public class ConfigurationValue : IConfigurationValue
{
    private Key _key;
    private Path _path;
    private string? _value;
    private readonly List<ChangeToken>? _tokens;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="value"></param>
    public ConfigurationValue(Path path, string? value)
    {
        _path = path;
        _key = path.Keys[path.Count - 1];
        _value = value;
        _tokens = new List<ChangeToken>();
    }

    #region Properties

    /// <summary>
    /// The configuration key.
    /// </summary>
    public Key Key => _key;

    /// <summary>
    /// 
    /// </summary>
    public Path Path => _path;

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
    public IChangeToken GetChangeToken()
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
