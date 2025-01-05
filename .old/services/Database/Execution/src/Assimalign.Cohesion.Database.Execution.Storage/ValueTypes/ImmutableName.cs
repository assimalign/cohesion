using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;

namespace Assimalign.Cohesion.Database.Execution.Storage.ValueTypes;

/// <summary>
/// represents a case-insensitive name. When set all 
/// </summary>
[Serializable]
[CLSCompliant(true)]
public readonly struct ImmutableName : IComparable, IComparable<ImmutableName>, IEquatable<ImmutableName>
{
	public const int MaxLength = 255;
	private readonly string segmentName;

	public ImmutableName(string segmentName)
	{
		if (segmentName is null)
		{
			throw new ArgumentNullException(nameof(segmentName));
		}
		if (segmentName.Length > MaxLength)
		{
			throw new ArgumentOutOfRangeException(nameof(segmentName));
		}

		this.segmentName = segmentName;
		this.Name = segmentName.ToLowerInvariant().Trim().ToCharArray();
	}

	public char[] Name { get; }
	public int Length => this.Name.Length;
	public int CompareTo(ImmutableName other)
	{
		var min = Math.Min(this.Length, other.Length);

		for (int i = 0; i < min; i++)
		{
			var comparison = this.Name[i].CompareTo(other.Name[i]);

			if (comparison != 0)
			{
				return comparison;
			}
		}

		return 0;
	}
	public int CompareTo(object? other)
	{
		if (other is ImmutableName name)
		{
			return CompareTo(name);
		}
		if (other is null)
		{
			return 1;
		}
		else
		{
			throw new ArgumentException("");
		}
	}
	public bool Equals(ImmutableName other)
	{
		if (this.Name.Length != other.Name.Length)
		{
			return false;
		}
		else
		{
			for (int i = 0; i < this.Name.Length; i++)
			{
				if (this.Name[i] != other.Name[i])
				{
					return false;
				}
			}
		}

		return true;
	}
	public override bool Equals(object? segmentName)
	{
		if (segmentName is ImmutableName name)
		{
			return this.Equals(name);
		}
		return false;
	}
	public override int GetHashCode() => HashCode.Combine(typeof(ImmutableName), segmentName);
	public override string ToString() => new string(this.Name);
	public static bool operator ==(ImmutableName left, ImmutableName right) => left.Equals(right);
	public static bool operator !=(ImmutableName left, ImmutableName right) => !left.Equals(right);
	public static bool operator <(ImmutableName left, ImmutableName right) => left.CompareTo(right) < 0;
	public static bool operator <=(ImmutableName left, ImmutableName right) => left.CompareTo(right) <= 0;
	public static bool operator >(ImmutableName left, ImmutableName right) => left.CompareTo(right) > 0;
	public static bool operator >=(ImmutableName left, ImmutableName right) => left.CompareTo(right) >= 0;


	//public static implicit operator SegmentName(Span<byte> name) => new SegmentName(Envo name.)
	public static implicit operator ImmutableName(string name) => new ImmutableName(name);
	public static implicit operator string(ImmutableName name) => new string(name.Name);
}