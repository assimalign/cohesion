using System;

namespace Assimalign.Cohesion.Logging
{
    partial struct LogId : 
	#if NET7_0_OR_GREATER
		global::System.Numerics.IEqualityOperators<LogId, LogId, bool>,
		global::System.Numerics.IComparisonOperators<LogId, LogId, bool>,
	#endif
		global::System.IComparable<LogId>,
		global::System.IEquatable<LogId>,
		global::System.IFormattable
    {
        public LogId(Ulid value)
        {
			Value = value;
        }

		public Ulid Value { get; }

		public int CompareTo(LogId other) => Value.CompareTo(other.Value);
		public bool Equals(LogId other) => Value.Equals(other.Value);
		public string ToString(
			#if NET7_0_OR_GREATER
			[global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)]
			#endif
			string? format,
			global::System.IFormatProvider? formatProvider)
			=> Value.ToString(format, formatProvider);
       public static LogId NewLogId() => new LogId(Ulid.NewUlid()); 

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value.ToString();
		public override bool Equals(object? obj)
		{
			if (ReferenceEquals(null, obj) || obj is not LogId instance)
			{
				return false;
			}
			return Equals(instance);
		}

		public static bool operator ==(LogId a, LogId b) => a.Equals(b);
		public static bool operator !=(LogId a, LogId b) => !a.Equals(b);
		public static bool operator >(LogId a, LogId b) => a.CompareTo(b) > 0;
		public static bool operator <(LogId a, LogId b) => a.CompareTo(b) < 0;
		public static bool operator >=(LogId a, LogId b) => a.CompareTo(b) >= 0;
		public static bool operator <=(LogId a, LogId b) => a.CompareTo(b) <= 0;
		public static implicit operator Ulid(LogId item) => item.Value;
		public static implicit operator LogId(Ulid item) => new LogId(item);
    }
}
