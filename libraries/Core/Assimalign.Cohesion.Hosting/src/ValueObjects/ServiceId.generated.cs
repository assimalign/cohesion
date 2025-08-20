using System;

namespace Assimalign.Cohesion.Hosting
{
    partial struct ServiceId :
		global::System.Numerics.IEqualityOperators<ServiceId, ServiceId, bool>
		,global::System.Numerics.IComparisonOperators<ServiceId, ServiceId, bool>
		,global::System.IComparable<ServiceId>
		,global::System.IEquatable<ServiceId>
		,global::System.IFormattable
    {
        public ServiceId(Ulid value)
        {
			Value = value;
        }

		public Ulid Value { get; }
		public int CompareTo(ServiceId other) => Value.CompareTo(other.Value);
		public bool Equals(ServiceId other) => Value.Equals(other.Value);
		public string ToString(
		    [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
		    global::System.IFormatProvider? formatProvider) =>  Value.ToString(format, formatProvider);
		public static ServiceId Parse(string value) => Ulid.Parse(value);
		public static ServiceId Parse(string value, IFormatProvider? provider) => Parse(value.AsSpan());
		public static bool TryParse([global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ServiceId result) => TryParse(value.AsSpan(), provider, out result);
		public static ServiceId Parse(ReadOnlySpan<char> span) => Parse(span, null);
		public static ServiceId Parse(ReadOnlySpan<char> span, IFormatProvider? provider) => new ServiceId(Ulid.Parse(span, provider));
		public static bool TryParse(ReadOnlySpan<char> span, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ServiceId result)
		{
			result = default;
			
			if (Ulid.TryParse(span, provider, out Ulid value))
			{
				result = new ServiceId(value);
				return true;
			}
			
			return false;
		}
		public static ServiceId New() => new ServiceId(Ulid.NewUlid());

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value.ToString();
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not ServiceId instance ? false : Equals(instance);
		public static bool operator ==(ServiceId a, ServiceId b) => a.Equals(b);
		public static bool operator !=(ServiceId a, ServiceId b) => !a.Equals(b);
		public static bool operator >(ServiceId a, ServiceId b) => a.CompareTo(b) > 0;
		public static bool operator <(ServiceId a, ServiceId b) => a.CompareTo(b) < 0;
		public static bool operator >=(ServiceId a, ServiceId b) => a.CompareTo(b) >= 0;
		public static bool operator <=(ServiceId a, ServiceId b) => a.CompareTo(b) <= 0;
		public static implicit operator Ulid(ServiceId item) => item.Value;
		public static implicit operator ServiceId(Ulid item) => new ServiceId(item);
    }
}
