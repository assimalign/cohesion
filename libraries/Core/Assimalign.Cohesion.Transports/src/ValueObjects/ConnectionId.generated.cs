using System;

namespace Assimalign.Cohesion.Transports
{
    partial struct ConnectionId :
		global::System.Numerics.IEqualityOperators<ConnectionId, ConnectionId, bool>
		,global::System.Numerics.IComparisonOperators<ConnectionId, ConnectionId, bool>
		,global::System.IComparable<ConnectionId>
		,global::System.IEquatable<ConnectionId>
		,global::System.IFormattable
    {
        public ConnectionId(Ulid value)
        {
			Value = value;
        }

		public Ulid Value { get; }
		public int CompareTo(ConnectionId other) => Value.CompareTo(other.Value);
		public bool Equals(ConnectionId other) => Value.Equals(other.Value);
		public string ToString(
		    [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
		    global::System.IFormatProvider? formatProvider) =>  Value.ToString(format, formatProvider);
		public static ConnectionId Parse(string value) => Ulid.Parse(value);
		public static ConnectionId Parse(string value, IFormatProvider? provider) => Parse(value.AsSpan());
		public static bool TryParse([global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ConnectionId result) => TryParse(value.AsSpan(), provider, out result);
		public static ConnectionId Parse(ReadOnlySpan<char> span) => Parse(span, null);
		public static ConnectionId Parse(ReadOnlySpan<char> span, IFormatProvider? provider) => new ConnectionId(Ulid.Parse(span, provider));
		public static bool TryParse(ReadOnlySpan<char> span, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ConnectionId result)
		{
			result = default;
			
			if (Ulid.TryParse(span, provider, out Ulid value))
			{
				result = new ConnectionId(value);
				return true;
			}
			
			return false;
		}
		public static ConnectionId New() => new ConnectionId(Ulid.NewUlid());

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value.ToString();
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not ConnectionId instance ? false : Equals(instance);
		public static bool operator ==(ConnectionId a, ConnectionId b) => a.Equals(b);
		public static bool operator !=(ConnectionId a, ConnectionId b) => !a.Equals(b);
		public static bool operator >(ConnectionId a, ConnectionId b) => a.CompareTo(b) > 0;
		public static bool operator <(ConnectionId a, ConnectionId b) => a.CompareTo(b) < 0;
		public static bool operator >=(ConnectionId a, ConnectionId b) => a.CompareTo(b) >= 0;
		public static bool operator <=(ConnectionId a, ConnectionId b) => a.CompareTo(b) <= 0;
		public static implicit operator Ulid(ConnectionId item) => item.Value;
		public static implicit operator ConnectionId(Ulid item) => new ConnectionId(item);
    }
}
