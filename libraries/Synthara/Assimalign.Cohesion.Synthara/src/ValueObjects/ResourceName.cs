using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Synthara;

[DebuggerDisplay("")]
public readonly struct ResourceName :
    IEquatable<ResourceName>
{
    private readonly string _value = string.Empty;
    private static readonly StringComparison _comparison = StringComparison.InvariantCultureIgnoreCase;

    public ResourceName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {

        }

        _value = value;
    }



    public bool Equals(ResourceName other)
    {
        return _value.Equals(other._value, _comparison);
    }


    #region Overloads

    public override bool Equals(object? obj)
    {
        if (obj is ResourceName name)
        {
            return Equals(name);
        }

        return false;
    }

    public override int GetHashCode()
    {
        return _value.GetHashCode(_comparison);
    }

    public override string ToString()
    {
        return _value;
    }

    #endregion

    #region Operators

    public static implicit operator ResourceName(string value)
    {
        return new ResourceName(value);
    }

    public static bool operator ==(ResourceName left, ResourceName right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(ResourceName left, ResourceName right)
    {
        return !left.Equals(right);
    }

    #endregion
}
