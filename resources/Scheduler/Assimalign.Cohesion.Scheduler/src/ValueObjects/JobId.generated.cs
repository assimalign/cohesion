using System;

namespace Assimalign.Cohesion.Scheduler
{
    partial struct JobId :
		global::System.Numerics.IEqualityOperators<JobId, JobId, bool>
		,global::System.Numerics.IComparisonOperators<JobId, JobId, bool>
		,global::System.IComparable<JobId>
		,global::System.IEquatable<JobId>
		,global::System.IFormattable
    {
        public JobId(Ulid value)
        {
			Value = value;
        }

		public Ulid Value { get; }
		public int CompareTo(JobId other) => Value.CompareTo(other.Value);
		public bool Equals(JobId other) => Value.Equals(other.Value);
		public string ToString(
		    [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
		    global::System.IFormatProvider? formatProvider) =>  Value.ToString(format, formatProvider);
		public static JobId Parse(string value) => Ulid.Parse(value);
		public static JobId Parse(string value, IFormatProvider? provider) => Parse(value.AsSpan());
		public static bool TryParse([global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out JobId result) => TryParse(value.AsSpan(), provider, out result);
		public static JobId Parse(ReadOnlySpan<char> span) => Parse(span, null);
		public static JobId Parse(ReadOnlySpan<char> span, IFormatProvider? provider) => new JobId(Ulid.Parse(span, provider));
		public static bool TryParse(ReadOnlySpan<char> span, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out JobId result)
		{
			result = default;
			
			if (Ulid.TryParse(span, provider, out Ulid value))
			{
				result = new JobId(value);
				return true;
			}
			
			return false;
		}
		public static JobId New() => new JobId(Ulid.NewUlid());

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value.ToString();
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not JobId instance ? false : Equals(instance);
		public static bool operator ==(JobId a, JobId b) => a.Equals(b);
		public static bool operator !=(JobId a, JobId b) => !a.Equals(b);
		public static bool operator >(JobId a, JobId b) => a.CompareTo(b) > 0;
		public static bool operator <(JobId a, JobId b) => a.CompareTo(b) < 0;
		public static bool operator >=(JobId a, JobId b) => a.CompareTo(b) >= 0;
		public static bool operator <=(JobId a, JobId b) => a.CompareTo(b) <= 0;
		public static implicit operator Ulid(JobId item) => item.Value;
		public static implicit operator JobId(Ulid item) => new JobId(item);
    }
}
