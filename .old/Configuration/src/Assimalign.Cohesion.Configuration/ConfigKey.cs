using System;

namespace Assimalign.Cohesion.Configuration;

public readonly struct ConfigKey
{
    private static ReadOnlySpan<char> invalidChars => ['\\', ':', '.'];

    /*
        {key1}:{key2}
        {key1}/{key2}[i]/
     */

    public ConfigKey(string value)
    {
        
    }
    /// <summary>
    /// 
    /// </summary>
    public bool HasIndex { get; }


    #region Overloads



    #endregion

    public static implicit operator ConfigKey(string value)
    {
        return new ConfigKey(value);
    }
    public static bool operator ==(ConfigKey left, ConfigKey right)
    {
        return true;
    }
    public static bool operator !=(ConfigKey left, ConfigKey right)
    {
        return true;
    }


}
