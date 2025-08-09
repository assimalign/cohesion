using System;

namespace Assimalign.Cohesion.Transports
{
    partial struct TransportId : 
	#if NET7_0_OR_GREATER
		global::System.Numerics.IEqualityOperators<TransportId, TransportId, bool>,
		global::System.Numerics.IComparisonOperators<TransportId, TransportId, bool>,
	#endif
		global::System.IComparable<TransportId>,
		global::System.IEquatable<TransportId>,
		global::System.IFormattable
    {
        public TransportId(Ulid value)
        {
			Value = value;
        }

		public Ulid Value { get; }

		public int CompareTo(TransportId other) => Value.CompareTo(other.Value);
		public bool Equals(TransportId other) => Value.Equals(other.Value);
		public string ToString(
			#if NET7_0_OR_GREATER
			[global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)]
			#endif
			string? format,
			global::System.IFormatProvider? formatProvider)
			=> Value.ToString(format, formatProvider);
       public static TransportId NewTransportId() => new TransportId(Ulid.NewUlid()); 

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value.ToString();
		public override bool Equals(object? obj)
		{
			if (ReferenceEquals(null, obj) || obj is not TransportId instance)
			{
				return false;
			}
			return Equals(instance);
		}

		public static bool operator ==(TransportId a, TransportId b) => a.Equals(b);
		public static bool operator !=(TransportId a, TransportId b) => !a.Equals(b);
		public static bool operator >(TransportId a, TransportId b) => a.CompareTo(b) > 0;
		public static bool operator <(TransportId a, TransportId b) => a.CompareTo(b) < 0;
		public static bool operator >=(TransportId a, TransportId b) => a.CompareTo(b) >= 0;
		public static bool operator <=(TransportId a, TransportId b) => a.CompareTo(b) <= 0;
		public static implicit operator Ulid(TransportId item) => item.Value;
		public static implicit operator TransportId(Ulid item) => new TransportId(item);
    }
}
