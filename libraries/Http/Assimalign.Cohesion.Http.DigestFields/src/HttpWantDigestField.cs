using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The parsed value of an RFC 9530 <c>Want-Content-Digest</c> or <c>Want-Repr-Digest</c> field: an
/// ordered map from <see cref="HttpDigestAlgorithm"/> to the integer preference the peer expressed
/// for it. Both fields share the identical RFC 9651 Structured Field Dictionary syntax
/// (<c>algorithm=preference</c>), so one value model serves both.
/// </summary>
/// <remarks>
/// The primary consumer is response generation: a server that received <c>Want-Content-Digest</c>
/// calls <see cref="TrySelectPreferred"/> to pick the supported algorithm the client most prefers,
/// then stamps <c>Content-Digest</c> with it. Deprecated and unregistered algorithms are preserved
/// on parse but are never selected, because this library cannot compute them.
/// </remarks>
public readonly struct HttpWantDigestField
{
    private readonly StructuredFieldDictionary _dictionary;
    private readonly HttpWantDigestPreference[]? _preferences;

    private HttpWantDigestField(StructuredFieldDictionary dictionary, HttpWantDigestPreference[] preferences)
    {
        _dictionary = dictionary;
        _preferences = preferences;
    }

    /// <summary>
    /// Gets the recognized preferences carried by the field, in field order. Members whose
    /// algorithm key is not in the registry are preserved for round-tripping but not surfaced here.
    /// </summary>
    public IReadOnlyList<HttpWantDigestPreference> Preferences => _preferences ?? Array.Empty<HttpWantDigestPreference>();

    #region Parse

    /// <summary>
    /// Parses a <c>Want-Content-Digest</c> / <c>Want-Repr-Digest</c> field value.
    /// </summary>
    /// <param name="value">The header field value; repeated field lines are combined by comma per RFC 9651 &#167; 4.2.</param>
    /// <param name="field">When this method returns <see langword="true"/>, the parsed field.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(HttpHeaderValue value, out HttpWantDigestField field)
        => TryParse(value.Value.AsSpan(), out field, out _);

    /// <summary>
    /// Parses a <c>Want-Content-Digest</c> / <c>Want-Repr-Digest</c> field value.
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <param name="field">When this method returns <see langword="true"/>, the parsed field.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> input, out HttpWantDigestField field)
        => TryParse(input, out field, out _);

    /// <summary>
    /// Parses a <c>Want-Content-Digest</c> / <c>Want-Repr-Digest</c> field value. On failure,
    /// <paramref name="error"/> carries a human-readable explanation (malformed dictionary syntax,
    /// a member that is not an Integer, or an empty field).
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <param name="field">When this method returns <see langword="true"/>, the parsed field.</param>
    /// <param name="error">When this method returns <see langword="false"/>, the reason parsing failed.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(ReadOnlySpan<char> input, out HttpWantDigestField field, out string? error)
    {
        field = default;

        if (!StructuredFieldDictionary.TryParse(input, out StructuredFieldDictionary dictionary, out error))
        {
            return false;
        }

        if (dictionary.Count == 0)
        {
            error = "A want-digest field must carry at least one algorithm preference.";
            return false;
        }

        var preferences = new List<HttpWantDigestPreference>(dictionary.Count);
        foreach (KeyValuePair<string, StructuredFieldMember> member in dictionary)
        {
            StructuredFieldMember value = member.Value;
            if (value.IsInnerList || value.Item.Value.Type != StructuredFieldType.Integer)
            {
                error = $"The want-digest field member '{member.Key}' must be an Integer preference (RFC 9530 §4).";
                return false;
            }

            if (HttpDigestAlgorithm.TryParse(member.Key, out HttpDigestAlgorithm algorithm))
            {
                preferences.Add(new HttpWantDigestPreference(algorithm, value.Item.Value.AsInteger()));
            }
        }

        field = new HttpWantDigestField(dictionary, preferences.ToArray());
        return true;
    }

    /// <summary>
    /// Parses a <c>Want-Content-Digest</c> / <c>Want-Repr-Digest</c> field value, throwing on failure.
    /// </summary>
    /// <param name="input">The field value to parse.</param>
    /// <returns>The parsed field.</returns>
    /// <exception cref="HttpDigestException">The value is not a well-formed want-digest field.</exception>
    public static HttpWantDigestField Parse(ReadOnlySpan<char> input)
    {
        if (!TryParse(input, out HttpWantDigestField field, out string? error))
        {
            throw new HttpDigestException(error ?? "Malformed want-digest field.");
        }
        return field;
    }

    #endregion

    /// <summary>
    /// Builds a want-digest field from an explicit set of preferences (for a server or client that
    /// wants to emit <c>Want-Content-Digest</c> / <c>Want-Repr-Digest</c>).
    /// </summary>
    /// <param name="preferences">The preferences, in the order they should appear.</param>
    /// <returns>The want-digest field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="preferences"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="preferences"/> is empty or references an unregistered algorithm.</exception>
    public static HttpWantDigestField Create(params HttpWantDigestPreference[] preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.Length == 0)
        {
            throw new ArgumentException("At least one preference is required.", nameof(preferences));
        }

        var members = new KeyValuePair<string, StructuredFieldMember>[preferences.Length];
        for (int i = 0; i < preferences.Length; i++)
        {
            HttpWantDigestPreference preference = preferences[i];
            if (!preference.Algorithm.IsRegistered)
            {
                throw new ArgumentException("A want-digest preference must reference a registered algorithm.", nameof(preferences));
            }
            StructuredFieldBareItem integer = StructuredFieldBareItem.FromInteger(preference.Preference);
            members[i] = new KeyValuePair<string, StructuredFieldMember>(
                preference.Algorithm.Key!,
                StructuredFieldMember.FromItem(new StructuredFieldItem(integer)));
        }

        return new HttpWantDigestField(new StructuredFieldDictionary(members), (HttpWantDigestPreference[])preferences.Clone());
    }

    /// <summary>
    /// Selects the supported algorithm the peer most prefers — the acceptable
    /// (<see cref="HttpWantDigestPreference.IsAcceptable"/>) preference with the highest value,
    /// breaking ties toward the stronger algorithm. Deprecated and unregistered algorithms are
    /// never selected.
    /// </summary>
    /// <param name="algorithm">When this method returns <see langword="true"/>, the selected algorithm.</param>
    /// <returns><see langword="true"/> if a supported, acceptable algorithm was found; otherwise <see langword="false"/>.</returns>
    public bool TrySelectPreferred(out HttpDigestAlgorithm algorithm)
    {
        algorithm = default;
        if (_preferences is null)
        {
            return false;
        }

        long bestPreference = 0;
        int bestStrength = -1;
        bool found = false;
        foreach (HttpWantDigestPreference preference in _preferences)
        {
            if (!preference.Algorithm.IsSupported || !preference.IsAcceptable)
            {
                continue;
            }

            int strength = preference.Algorithm.HashLengthInBytes;
            if (!found
                || preference.Preference > bestPreference
                || (preference.Preference == bestPreference && strength > bestStrength))
            {
                found = true;
                bestPreference = preference.Preference;
                bestStrength = strength;
                algorithm = preference.Algorithm;
            }
        }

        return found;
    }

    /// <summary>
    /// Serializes the field to its RFC 9651 &#167; 4.1.2 canonical dictionary form
    /// (<c>algorithm=preference</c>, comma-separated).
    /// </summary>
    /// <returns>The canonical field value, or the empty string for the default instance.</returns>
    public string Serialize() => _dictionary.Serialize();

    /// <inheritdoc />
    public override string ToString() => Serialize();
}
