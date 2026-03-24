using System;

namespace Assimalign.Cohesion.Scheduler
{
    partial struct ScheduleId :
		global::System.Numerics.IEqualityOperators<ScheduleId, ScheduleId, bool>
		,global::System.Numerics.IComparisonOperators<ScheduleId, ScheduleId, bool>
		,global::System.IComparable<ScheduleId>
		,global::System.IEquatable<ScheduleId>
		,global::System.IFormattable
    {
        public ScheduleId(Ulid value)
        {
			Value = value;
        }

		public Ulid Value { get; }
		public int CompareTo(ScheduleId other) => Value.CompareTo(other.Value);
		public bool Equals(ScheduleId other) => Value.Equals(other.Value);
		public string ToString(
		    [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
		    global::System.IFormatProvider? formatProvider) =>  Value.ToString(format, formatProvider);
		public static ScheduleId Parse(string value) => Ulid.Parse(value);
		public static ScheduleId Parse(string value, IFormatProvider? provider) => Parse(value.AsSpan());
		public static bool TryParse([global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ScheduleId result) => TryParse(value.AsSpan(), provider, out result);
		public static ScheduleId Parse(ReadOnlySpan<char> span) => Parse(span, null);
		public static ScheduleId Parse(ReadOnlySpan<char> span, IFormatProvider? provider) => new ScheduleId(Ulid.Parse(span, provider));
		public static bool TryParse(ReadOnlySpan<char> span, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ScheduleId result)
		{
			result = default;
			
			if (Ulid.TryParse(span, provider, out Ulid value))
			{
				result = new ScheduleId(value);
				return true;
			}
			
			return false;
		}
		public static ScheduleId New() => new ScheduleId(Ulid.NewUlid());

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value.ToString();
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not ScheduleId instance ? false : Equals(instance);
		public static bool operator ==(ScheduleId a, ScheduleId b) => a.Equals(b);
		public static bool operator !=(ScheduleId a, ScheduleId b) => !a.Equals(b);
		public static bool operator >(ScheduleId a, ScheduleId b) => a.CompareTo(b) > 0;
		public static bool operator <(ScheduleId a, ScheduleId b) => a.CompareTo(b) < 0;
		public static bool operator >=(ScheduleId a, ScheduleId b) => a.CompareTo(b) >= 0;
		public static bool operator <=(ScheduleId a, ScheduleId b) => a.CompareTo(b) <= 0;
		public static implicit operator Ulid(ScheduleId item) => item.Value;
		public static implicit operator ScheduleId(Ulid item) => new ScheduleId(item);
    }
}
