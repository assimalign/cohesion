using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.Configuration.Internal;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("(V) {Key}: {Value}")]
[DebuggerTypeProxy(typeof(DebuggerView))]
internal sealed class ConfigurationValue : ConfigurationEntry, IConfigurationValue
{
    private readonly Lock _lock;
    private readonly bool _isReadOnly;

    private string? _value;

    internal ConfigurationValue(
        Path path,
        string? value,
        string providerName,
        bool isReadOnly = false,
        ConfigurationSection? parent = null)
        : base(path, providerName, parent)
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
            InvalidOperationException.ThrowIf(_isReadOnly, "The configuration value is read-only.");

            SetValue(value);
        }
    }

    internal bool SetValue(string? value)
    {
        lock (_lock)
        {
            if (string.Equals(_value, value, StringComparison.Ordinal))
            {
                return false;
            }

            _value = value;
        }

        NotifyChanged();

        return true;
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

    partial class DebuggerView
    {
        private readonly ConfigurationValue _value;

        public DebuggerView(ConfigurationValue value)
        {
            _value = value;
        }

        public Key Key => _value.Key;
        public Path Path => _value.Path;
        public string? Value => _value.Value;
    }
}
