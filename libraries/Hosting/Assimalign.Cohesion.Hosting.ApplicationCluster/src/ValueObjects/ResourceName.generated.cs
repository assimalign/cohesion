using System;

namespace Assimalign.Cohesion.Hosting
{
    partial struct ResourceName :
		global::System.Numerics.IEqualityOperators<ResourceName, ResourceName, bool>
		,global::System.Numerics.IComparisonOperators<ResourceName, ResourceName, bool>
		,global::System.IComparable<ResourceName>
		,global::System.IEquatable<ResourceName>
    {
        public ResourceName(string value)
        {
			Value = value;
        }

		public string Value { get; }
		public int CompareTo(ResourceName other) => Value.CompareTo(other.Value);
		public bool Equals(ResourceName other) => Value.Equals(other.Value);
		public string ToString(
		    [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
		    global::System.IFormatProvider? formatProvider) => Value.ToString(formatProvider);

		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => Value;
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not ResourceName instance ? false : Equals(instance);
		public static bool operator ==(ResourceName a, ResourceName b) => a.Equals(b);
		public static bool operator !=(ResourceName a, ResourceName b) => !a.Equals(b);
		public static bool operator >(ResourceName a, ResourceName b) => a.CompareTo(b) > 0;
		public static bool operator <(ResourceName a, ResourceName b) => a.CompareTo(b) < 0;
		public static bool operator >=(ResourceName a, ResourceName b) => a.CompareTo(b) >= 0;
		public static bool operator <=(ResourceName a, ResourceName b) => a.CompareTo(b) <= 0;
		public static implicit operator string(ResourceName item) => item.Value;
		public static implicit operator ResourceName(string item) => new ResourceName(item);
    }
}
