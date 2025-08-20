using System;

namespace Assimalign.Cohesion.Transports
{
    partial struct TransportProtocol :
		global::System.Numerics.IEqualityOperators<TransportProtocol, TransportProtocol, bool>
		,global::System.Numerics.IComparisonOperators<TransportProtocol, TransportProtocol, bool>
		,global::System.IComparable<TransportProtocol>
		,global::System.IEquatable<TransportProtocol>
    {
        public TransportProtocol(string value)
        {
			Value = value;
        }

		public string Value { get; }
		public int CompareTo(TransportProtocol other) => Value.CompareTo(other.Value);
		public bool Equals(TransportProtocol other) => Value.Equals(other.Value);
		public string ToString(
		    [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
		    global::System.IFormatProvider? formatProvider) => Value.ToString(formatProvider);

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value;
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not TransportProtocol instance ? false : Equals(instance);
		public static bool operator ==(TransportProtocol a, TransportProtocol b) => a.Equals(b);
		public static bool operator !=(TransportProtocol a, TransportProtocol b) => !a.Equals(b);
		public static bool operator >(TransportProtocol a, TransportProtocol b) => a.CompareTo(b) > 0;
		public static bool operator <(TransportProtocol a, TransportProtocol b) => a.CompareTo(b) < 0;
		public static bool operator >=(TransportProtocol a, TransportProtocol b) => a.CompareTo(b) >= 0;
		public static bool operator <=(TransportProtocol a, TransportProtocol b) => a.CompareTo(b) <= 0;
		public static implicit operator string(TransportProtocol item) => item.Value;
		public static implicit operator TransportProtocol(string item) => new TransportProtocol(item);
    }
}
