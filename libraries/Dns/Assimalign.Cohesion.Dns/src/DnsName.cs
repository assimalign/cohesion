using System;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A case-insensitive DNS domain name in presentation form (e.g. <c>"www.example.com"</c>).
/// Labels are separated by '<c>.</c>'; the optional trailing dot marks an explicitly
/// fully-qualified name. Comparison is ASCII-case-insensitive per RFC 1035 &#167; 2.3.3.
/// </summary>
/// <remarks>
/// <para>
/// This is the public-API shape used by every Cohesion DNS abstraction. The wire-format
/// encoding (label-length prefixes, root-label terminator, name compression) is the job of the
/// packet serializer and is intentionally NOT exposed here. Callers always work with the
/// dotted presentation form.
/// </para>
/// <para>
/// <see cref="DnsName"/> is a struct and behaves like a value: equal instances compare equal,
/// it has no hidden allocations beyond the underlying <see cref="string"/>, and the default
/// (uninitialized) value represents the root name <c>"."</c>.
/// </para>
/// </remarks>
public readonly struct DnsName : IEquatable<DnsName>
{
    private readonly string? _value;

    /// <summary>
    /// Initializes a <see cref="DnsName"/> from a presentation-form string. The input is
    /// stored as-is; canonicalization happens at comparison time.
    /// </summary>
    /// <param name="value">A non-null presentation-form DNS name (e.g. <c>"example.com"</c>).</param>
    /// <exception cref="ArgumentException">The name is empty or contains a label longer than
    /// the RFC 1035 limit of 63 octets, or the total length exceeds 255 octets.</exception>
    public DnsName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Validate(value);
        _value = value;
    }

    /// <summary>
    /// The DNS root name (<c>"."</c>). All names are descendants of the root.
    /// </summary>
    public static DnsName Root { get; } = new(".");

    /// <summary>
    /// The presentation-form value of this name. Defaults to <c>"."</c> when the struct is
    /// uninitialized.
    /// </summary>
    public string Value => _value ?? ".";

    /// <summary>
    /// True when this name is the DNS root.
    /// </summary>
    public bool IsRoot => Value == "." || Value.Length == 0;

    /// <summary>
    /// Returns the labels of this name (most-specific to least-specific), without the root
    /// label. An empty array is returned for the root.
    /// </summary>
    public string[] GetLabels()
    {
        string text = Value;
        if (text.Length == 0 || text == ".")
        {
            return Array.Empty<string>();
        }

        // Strip trailing dot so split doesn't produce a phantom empty trailing label.
        if (text[^1] == '.')
        {
            text = text[..^1];
        }
        return text.Split('.');
    }

    /// <inheritdoc />
    public bool Equals(DnsName other)
        => string.Equals(Normalize(Value), Normalize(other.Value), StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DnsName other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Normalize(Value).GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => Value;

    public static bool operator ==(DnsName left, DnsName right) => left.Equals(right);

    public static bool operator !=(DnsName left, DnsName right) => !left.Equals(right);

    /// <summary>
    /// Implicit conversion from <see cref="string"/> so callers can write
    /// <c>DnsName name = "example.com";</c>.
    /// </summary>
    public static implicit operator DnsName(string value) => new(value);

    /// <summary>
    /// Implicit conversion to <see cref="string"/> so a <see cref="DnsName"/> drops in wherever
    /// presentation-form is expected.
    /// </summary>
    public static implicit operator string(DnsName name) => name.Value;

    private static string Normalize(string value)
    {
        // Strip the optional trailing dot and lower-case ASCII letters so two presentation
        // forms that differ only in case or trailing-dot are treated as equal.
        if (value.Length > 0 && value[^1] == '.')
        {
            value = value[..^1];
        }
        return value.ToLowerInvariant();
    }

    private static void Validate(string value)
    {
        if (value.Length == 0)
        {
            throw new ArgumentException("A DNS name cannot be empty.", nameof(value));
        }

        // Strip trailing dot for length checks; the root label is implicit.
        string check = value.Length > 0 && value[^1] == '.' ? value[..^1] : value;

        // RFC 1035 caps total octet length at 255 (including the root label and length octets).
        if (check.Length > 253)
        {
            throw new ArgumentException(
                $"DNS name exceeds the RFC 1035 limit of 253 characters in presentation form: '{value}'.",
                nameof(value));
        }

        int labelStart = 0;
        for (int i = 0; i <= check.Length; i++)
        {
            if (i == check.Length || check[i] == '.')
            {
                int labelLength = i - labelStart;
                if (labelLength == 0 && i != check.Length)
                {
                    throw new ArgumentException(
                        $"DNS name contains an empty label (consecutive dots): '{value}'.",
                        nameof(value));
                }
                if (labelLength > 63)
                {
                    throw new ArgumentException(
                        $"DNS label '{check.Substring(labelStart, labelLength)}' exceeds the RFC 1035 limit of 63 octets.",
                        nameof(value));
                }
                labelStart = i + 1;
            }
        }
    }
}
