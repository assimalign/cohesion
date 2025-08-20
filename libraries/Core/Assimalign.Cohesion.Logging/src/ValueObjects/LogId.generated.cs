using System;

namespace Assimalign.Cohesion.Logging
{
    partial struct LogId :
		global::System.Numerics.IEqualityOperators<LogId, LogId, bool>
		,global::System.Numerics.IComparisonOperators<LogId, LogId, bool>
		,global::System.IComparable<LogId>
		,global::System.IEquatable<LogId>
		,global::System.IFormattable
    {
        public LogId(Ulid value)
        {
			Value = value;
        }

		public Ulid Value { get; }
		public int CompareTo(LogId other) => Value.CompareTo(other.Value);
		public bool Equals(LogId other) => Value.Equals(other.Value);
		public string ToString(
		    [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
		    global::System.IFormatProvider? formatProvider) =>  Value.ToString(format, formatProvider);
		public static LogId Parse(string value) => Ulid.Parse(value);
		public static LogId Parse(string value, IFormatProvider? provider) => Parse(value.AsSpan());
		public static bool TryParse([global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out LogId result) => TryParse(value.AsSpan(), provider, out result);
		public static LogId Parse(ReadOnlySpan<char> span) => Parse(span, null);
		public static LogId Parse(ReadOnlySpan<char> span, IFormatProvider? provider) => new LogId(Ulid.Parse(span, provider));
		public static bool TryParse(ReadOnlySpan<char> span, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out LogId result)
		{
			result = default;
			
			if (Ulid.TryParse(span, provider, out Ulid value))
			{
				result = new LogId(value);
				return true;
			}
			
			return false;
		}
		public static LogId New() => new LogId(Ulid.NewUlid());

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value.ToString();
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not LogId instance ? false : Equals(instance);
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
