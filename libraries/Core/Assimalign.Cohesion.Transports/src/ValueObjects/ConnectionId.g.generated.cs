using System;

namespace Assimalign.Cohesion.Transports
{
    partial struct ConnectionId : 
	#if NET7_0_OR_GREATER
		global::System.Numerics.IEqualityOperators<ConnectionId, ConnectionId, bool>,
		global::System.Numerics.IComparisonOperators<ConnectionId, ConnectionId, bool>,
	#endif
		global::System.IComparable<ConnectionId>,
		global::System.IEquatable<ConnectionId>,
		global::System.IFormattable
    {
        public ConnectionId(Ulid value)
        {
			Value = value;
        }

		public Ulid Value { get; }

		public int CompareTo(ConnectionId other) => Value.CompareTo(other.Value);
		public bool Equals(ConnectionId other) => Value.Equals(other.Value);
		public string ToString(
			#if NET7_0_OR_GREATER
			[global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)]
			#endif
			string? format,
			global::System.IFormatProvider? formatProvider)
			=> Value.ToString(format, formatProvider);
       public static ConnectionId NewConnectionId() => new ConnectionId(Ulid.NewUlid()); 

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value.ToString();
		public override bool Equals(object? obj)
		{
			if (ReferenceEquals(null, obj) || obj is not ConnectionId instance)
			{
				return false;
			}
			return Equals(instance);
		}

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
