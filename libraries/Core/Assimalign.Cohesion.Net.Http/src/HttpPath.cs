using System;
using System.Linq;
using System.Buffers;
using System.Diagnostics;
using System.Web;

namespace Assimalign.Cohesion.Net.Http;

using Assimalign.Cohesion.Net.Http.Internal;
using System.Text.Encodings.Web;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpPath : IEquatable<HttpPath>
{
#if NET8_0_OR_GREATER
	// The allowed characters in an HTTP Path.
	private static readonly SearchValues<char> characters = SearchValues.Create("!$&'()*+,-./0123456789:;=@ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz~");
#else

#endif
    // HttpPath is only set internally on requestion creation.
    public HttpPath(string value)
	{
		
		//if (value.Any(c => !characters.Contains(c)))
		//{
		//	ThrowUtility.InvalidHttpPath($"The following path contains an in invalid character: '{value}'.");
		//}
		this.Value = value;
		this.Segments = new string[0];
	}

	/// <summary>
	/// The raw path value.
	/// </summary>
	public string Value { get; }
	/// <summary>
	/// A collection of path segments.
	/// </summary>
	public string[] Segments { get; }


	


	public HttpPath Concat(HttpPath path)
	{
		throw new NotImplementedException();
	}

    

    public bool Equals(HttpPath other)
    {
		return Equals(other, StringComparison.OrdinalIgnoreCase);
    }
	public bool Equals(HttpPath other, StringComparison comparison)
	{
		return string.Equals(Value, other.Value, comparison);
	}

    #region Overloads
    public override string ToString()
    {
        return base.ToString();
    }

    public override int GetHashCode()
    {
        return base.GetHashCode();
    }

    public override bool Equals(object? obj)
    {
        if (obj is HttpPath path)
        {
            return Equals(path);
        }
        return false;
    }
    #endregion


    #region Operatos
    public static implicit operator HttpPath(string value) => new HttpPath(value);

    public static implicit operator string(HttpPath route) => route.Value;
	#endregion
}
