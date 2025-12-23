using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using System;


namespace Assimalign.Cohesion.Configuration.Internal;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Key} = {Value}")]
internal class ConfigurationValue : ConfigurationEntry, IConfigurationValue
{
    private readonly Lock _lock;

    private string? _value;
    private bool _isReadOnly;


    internal ConfigurationValue(Path path, string? value, string providerName, bool isReadOnly = false)
        : base(path, providerName)
    {
        _lock = new Lock();
        _value = value;
        _isReadOnly = isReadOnly;
    }

    /// <summary>
    /// The raw configuration value.
    /// </summary>
    public string? Value
    {
        get => _value;
        set
        {
            ObjectDisposedException.ThrowIf(_isReadOnly, "The configuration value is read-only.");

            lock (_lock)
            {
                _value = NotifyChanged(_value, value);
            }
        }
    }





    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    public static implicit operator KeyValuePair<Key, string?>(ConfigurationValue value)
    {
        return new KeyValuePair<Key, string?>(value.Key, value.Value);
    }

    /// <summary>
    /// Implicitly converts the configuration value to a string.
    /// </summary>
    /// <param name="value"></param>
    public static implicit operator string?(ConfigurationValue value)
    {
        return value.Value;
    }
}
