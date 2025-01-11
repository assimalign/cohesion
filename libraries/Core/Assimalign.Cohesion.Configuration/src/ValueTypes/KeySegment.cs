
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

[DebuggerDisplay("{ToString()}")]
public readonly struct KeySegment : IEquatable<KeySegment>
{
    private readonly int? index;
    private readonly string? label;

    public KeySegment(string value)
    {
        if (value.ContainsAny([.. Key.Separators]))
        {
            ThrowHelper.ThrowArgumentException("A key segment cannot contain any separators.");
        }

        var name = new char[value.Length];

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i].Equals('['))
            {
                Array.Resize(ref name, i);

                var num = new char[value.Length - 2 - i];

                for (; value[i] != ']'; i++)
                {
                    if (value.Length == i)
                    {
                        ThrowHelper.ThrowArgumentException("");
                    }
                }
                if (!int.TryParse(num, out var index))
                {
                    // TODO: Invalid index
                }
                this.index = index;
            }
            else
            {
                name[i] = value[i];
            }
        }

        Value = new string(name);
    }

    /// <summary>
    /// 
    /// </summary>
    public string Value { get; }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    public bool HasIndex(out int? index)
    {
        index = this.index;
        return index.HasValue;
    }
    /// <summary>
    ///
    /// </summary>
    /// <remarks>
    /// Labels represent a way to implement polymorphic/dynamic configuration
    /// </remarks>
    /// <param name="label"></param>
    /// <returns></returns>
    public bool HasLabel(out string? label)
    {
        label = this.label;
        return false;
    }

    public bool Equals(KeySegment other)
    {
        return Equals(other, KeyComparison.Ordinal);
    }

    public bool Equals(KeySegment other, KeyComparison comparison)
    {
        if (!other.Value.Equals(Value, (StringComparison)comparison))
        {
            return false;
        }

        return true;
    }

    #region Overloads
    public override string ToString()
    {
        if (HasIndex(out var index))
        {
            return $"{Value}[{index}]";
        }
        return Value;
    }
    #endregion
}