using System;

namespace Assimalign.Cohesion.Hosting
{
    partial struct ResourceId :
		global::System.Numerics.IEqualityOperators<ResourceId, ResourceId, bool>
		,global::System.Numerics.IComparisonOperators<ResourceId, ResourceId, bool>
		,global::System.IComparable<ResourceId>
		,global::System.IEquatable<ResourceId>
		,global::System.IFormattable
    {
        public ResourceId(Ulid value)
        {
			Value = value;
        }

		public Ulid Value { get; }
		public int CompareTo(ResourceId other) => Value.CompareTo(other.Value);
		public bool Equals(ResourceId other) => Value.Equals(other.Value);
		public string ToString(
		    [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
		    global::System.IFormatProvider? formatProvider) =>  Value.ToString(format, formatProvider);
		public static ResourceId Parse(string value) => Ulid.Parse(value);
		public static ResourceId Parse(string value, IFormatProvider? provider) => Parse(value.AsSpan());
		public static bool TryParse([global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ResourceId result) => TryParse(value.AsSpan(), provider, out result);
		public static ResourceId Parse(ReadOnlySpan<char> span) => Parse(span, null);
		public static ResourceId Parse(ReadOnlySpan<char> span, IFormatProvider? provider) => new ResourceId(Ulid.Parse(span, provider));
		public static bool TryParse(ReadOnlySpan<char> span, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ResourceId result)
		{
			result = default;
			
			if (Ulid.TryParse(span, provider, out Ulid value))
			{
				result = new ResourceId(value);
				return true;
			}
			
			return false;
		}
		public static ResourceId New() => new ResourceId(Ulid.NewUlid());

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value.ToString();
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not ResourceId instance ? false : Equals(instance);
		public static bool operator ==(ResourceId a, ResourceId b) => a.Equals(b);
		public static bool operator !=(ResourceId a, ResourceId b) => !a.Equals(b);
		public static bool operator >(ResourceId a, ResourceId b) => a.CompareTo(b) > 0;
		public static bool operator <(ResourceId a, ResourceId b) => a.CompareTo(b) < 0;
		public static bool operator >=(ResourceId a, ResourceId b) => a.CompareTo(b) >= 0;
		public static bool operator <=(ResourceId a, ResourceId b) => a.CompareTo(b) <= 0;
		public static implicit operator Ulid(ResourceId item) => item.Value;
		public static implicit operator ResourceId(Ulid item) => new ResourceId(item);
    }
}
