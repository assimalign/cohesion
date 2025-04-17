using System;

namespace Assimalign.Cohesion.Synthara
{
    partial struct AccountId : 
	#if NET7_0_OR_GREATER
		global::System.Numerics.IEqualityOperators<AccountId, AccountId, bool>,
		global::System.Numerics.IComparisonOperators<AccountId, AccountId, bool>,
	#endif
		global::System.IComparable<AccountId>,
		global::System.IEquatable<AccountId>,
		global::System.IFormattable
    {
        public AccountId(string value)
        {
			Value = value;
        }

		public string Value { get; }

		public int CompareTo(AccountId other) => Value.CompareTo(other.Value);
		public bool Equals(AccountId other) => Value.Equals(other.Value);
		public string ToString(
			#if NET7_0_OR_GREATER
			[global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)]
			#endif
			string? format,
			global::System.IFormatProvider? formatProvider)
			=> Value.ToString(formatProvider);

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value;
		public override bool Equals(object? obj)
		{
			if (ReferenceEquals(null, obj) || obj is not AccountId instance)
			{
				return false;
			}
			return Equals(instance);
		}

		public static bool operator ==(AccountId a, AccountId b) => a.Equals(b);
		public static bool operator !=(AccountId a, AccountId b) => !a.Equals(b);
		public static bool operator >(AccountId a, AccountId b) => a.CompareTo(b) > 0;
		public static bool operator <(AccountId a, AccountId b) => a.CompareTo(b) < 0;
		public static bool operator >=(AccountId a, AccountId b) => a.CompareTo(b) >= 0;
		public static bool operator <=(AccountId a, AccountId b) => a.CompareTo(b) <= 0;
		public static implicit operator string(AccountId item) => item.Value;
		public static implicit operator AccountId(string item) => new AccountId(item);
    }
}
