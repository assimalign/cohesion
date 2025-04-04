using System;
using System.Linq;
using System.Buffers;
using System.Diagnostics;
using System.Web;
using System.Text.Encodings.Web;

namespace Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Http.Internal;

/// <summary>
/// 
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly struct HttpPath : IEquatable<HttpPath>
{
	const int StackAllocationLimit = 128;

	// The allowed characters in an HTTP Path.
	private static readonly SearchValues<char> characters = SearchValues.Create("!$&'()*+,-./0123456789:;=@ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz~");

    // HttpPath is only set internally on requestion creation.
    public HttpPath(string value)
	{
        ReadOnlySpan<char> span = value;

        if (span.ContainsAny(characters))
        {

        }

        // IOGraphGdmValueCollection

		//if (value.Any(c => !characters.Contains(c)))
		//{
		//	ThrowUtility.InvalidHttpPath($"The following path contains an in invalid character: '{value}'.");
		//}
		this.Value = value;
	}

	/// <summary>
	/// The raw path value.
	/// </summary>
	public string Value { get; }


    #region Methods

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
	public bool StartsWith(HttpPath other)
	{
		return false;
	}

    public static HttpPath FromUriComponent(string uriComponent)
    {
        int num = uriComponent.IndexOf('%');
        if (num == -1)
        {
            return new HttpPath(uriComponent);
        }
        Span<char> span = ((uriComponent.Length > 128) ? ((Span<char>)new char[uriComponent.Length]) : stackalloc char[128]);
        Span<char> destination = span;
        uriComponent.CopyTo(destination);
        int num2 = UrlDecoder.DecodeInPlace(destination.Slice(num, uriComponent.Length - num));
        destination = destination.Slice(0, num + num2);
        return new HttpPath(destination.ToString());
    }

    #endregion

    #region Overloads
    public override string ToString()
    {
        return Value;
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

    #region Operators

    public static implicit operator HttpPath(string value) => new HttpPath(value);

    public static implicit operator string(HttpPath route) => route.Value;

	#endregion
}
