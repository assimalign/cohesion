using System;

namespace Assimalign.Cohesion.Synthara
{
    partial struct AccountName :
		global::System.Numerics.IEqualityOperators<AccountName, AccountName, bool>
		,global::System.Numerics.IComparisonOperators<AccountName, AccountName, bool>
		,global::System.IComparable<AccountName>
		,global::System.IEquatable<AccountName>
    {
        public AccountName(string value)
        {
			if (!IsValid(value, out string message))
			{
				throw new ArgumentException(message);
			}
			Value = value;
        }

		public string Value { get; }
		public partial bool IsValid(string value, out string message);
		public int CompareTo(AccountName other) => Value.CompareTo(other.Value);
		public bool Equals(AccountName other) => Value.Equals(other.Value);
		public string ToString(
		    [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
		    global::System.IFormatProvider? formatProvider) => Value.ToString(formatProvider);

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value;
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not AccountName instance ? false : Equals(instance);
		public static bool operator ==(AccountName a, AccountName b) => a.Equals(b);
		public static bool operator !=(AccountName a, AccountName b) => !a.Equals(b);
		public static bool operator >(AccountName a, AccountName b) => a.CompareTo(b) > 0;
		public static bool operator <(AccountName a, AccountName b) => a.CompareTo(b) < 0;
		public static bool operator >=(AccountName a, AccountName b) => a.CompareTo(b) >= 0;
		public static bool operator <=(AccountName a, AccountName b) => a.CompareTo(b) <= 0;
		public static implicit operator string(AccountName item) => item.Value;
		public static implicit operator AccountName(string item) => new AccountName(item);
    }
}
