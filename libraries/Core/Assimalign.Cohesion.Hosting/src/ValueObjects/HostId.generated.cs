using System;

namespace Assimalign.Cohesion.Hosting
{
    partial struct HostId :
		global::System.Numerics.IEqualityOperators<HostId, HostId, bool>
		,global::System.Numerics.IComparisonOperators<HostId, HostId, bool>
		,global::System.IComparable<HostId>
		,global::System.IEquatable<HostId>
		,global::System.IFormattable
    {
        public HostId(Ulid value)
        {
			Value = value;
        }

		public Ulid Value { get; }
		public int CompareTo(HostId other) => Value.CompareTo(other.Value);
		public bool Equals(HostId other) => Value.Equals(other.Value);
		public string ToString(
		    [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
		    global::System.IFormatProvider? formatProvider) =>  Value.ToString(format, formatProvider);
		public static HostId Parse(string value) => Ulid.Parse(value);
		public static HostId Parse(string value, IFormatProvider? provider) => Parse(value.AsSpan());
		public static bool TryParse([global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out HostId result) => TryParse(value.AsSpan(), provider, out result);
		public static HostId Parse(ReadOnlySpan<char> span) => Parse(span, null);
		public static HostId Parse(ReadOnlySpan<char> span, IFormatProvider? provider) => new HostId(Ulid.Parse(span, provider));
		public static bool TryParse(ReadOnlySpan<char> span, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out HostId result)
		{
			result = default;
			
			if (Ulid.TryParse(span, provider, out Ulid value))
			{
				result = new HostId(value);
				return true;
			}
			
			return false;
		}
		public static HostId New() => new HostId(Ulid.NewUlid());

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value.ToString();
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not HostId instance ? false : Equals(instance);
		public static bool operator ==(HostId a, HostId b) => a.Equals(b);
		public static bool operator !=(HostId a, HostId b) => !a.Equals(b);
		public static bool operator >(HostId a, HostId b) => a.CompareTo(b) > 0;
		public static bool operator <(HostId a, HostId b) => a.CompareTo(b) < 0;
		public static bool operator >=(HostId a, HostId b) => a.CompareTo(b) >= 0;
		public static bool operator <=(HostId a, HostId b) => a.CompareTo(b) <= 0;
		public static implicit operator Ulid(HostId item) => item.Value;
		public static implicit operator HostId(Ulid item) => new HostId(item);
    }
}
