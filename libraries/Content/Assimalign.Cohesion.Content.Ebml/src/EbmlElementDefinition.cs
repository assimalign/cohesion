using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Ebml.Internal;

namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>An immutable element definition identified by its encoded ID and permitted parents.</summary>
public sealed class EbmlElementDefinition
{
    private EbmlElementDefinition(VInt identifier, string name, EbmlElementType type, VInt[] parents, bool unknownSizeAllowed, bool recursive, bool global)
    {
        Identifier = identifier;
        Name = name;
        Type = type;
        ParentIds = Array.AsReadOnly(parents);
        UnknownSizeAllowed = unknownSizeAllowed;
        Recursive = recursive;
        IsGlobal = global;
    }

    /// <summary>Creates a definition; no parent IDs means root level.</summary>
    /// <param name="identifier">A valid encoded element ID.</param>
    /// <param name="name">A descriptive name.</param>
    /// <param name="type">The payload type.</param>
    /// <param name="parentIds">Allowed direct parent IDs, copied at creation.</param>
    /// <param name="unknownSizeAllowed">Whether this master can have unknown size.</param>
    /// <param name="recursive">Whether this master can contain itself.</param>
    /// <param name="global">Whether the element is valid in every master and at root level.</param>
    /// <param name="minOccurs">The minimum occurrences in each parent.</param>
    /// <param name="maxOccurs">The maximum occurrences, or null for unbounded.</param>
    /// <param name="minimumValue">An optional typed numeric lower bound.</param>
    /// <param name="maximumValue">An optional typed numeric upper bound.</param>
    /// <param name="defaultValue">An immutable scalar default for empty or omitted mandatory values.</param>
    /// <param name="minimumLength">The minimum wire payload length.</param>
    /// <param name="maximumLength">The maximum wire payload length.</param>
    /// <param name="minimumInclusive">Whether the lower bound is inclusive.</param>
    /// <param name="maximumInclusive">Whether the upper bound is inclusive.</param>
    /// <param name="excludedValue">An optional excluded typed numeric value.</param>
    /// <returns>An immutable definition.</returns>
    /// <exception cref="ArgumentException">An ID, type, name, or flag combination is invalid.</exception>
    public static EbmlElementDefinition Create(VInt identifier, string name, EbmlElementType type,
        IEnumerable<VInt>? parentIds = null, bool unknownSizeAllowed = false, bool recursive = false, bool global = false,
        int minOccurs = 0, int? maxOccurs = null, object? minimumValue = null, object? maximumValue = null,
        object? defaultValue = null, long minimumLength = 0, long maximumLength = long.MaxValue,
        bool minimumInclusive = true, bool maximumInclusive = true, object? excludedValue = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!identifier.IsValidIdentifier || type < EbmlElementType.SignedInteger || type > EbmlElementType.MasterElement ||
            ((unknownSizeAllowed || recursive) && type != EbmlElementType.MasterElement) || (unknownSizeAllowed && recursive))
        {
            throw new ArgumentException(ErrorMessages.SchemaConflict);
        }

        var parents = parentIds is null ? Array.Empty<VInt>() : new List<VInt>(parentIds).ToArray();
        foreach (var parent in parents)
        {
            if (!parent.IsValidIdentifier)
            {
                throw new ArgumentException(ErrorMessages.InvalidVInt, nameof(parentIds));
            }
        }

        if (minOccurs < 0 || maxOccurs < minOccurs || minimumLength < 0 || maximumLength < minimumLength ||
            (defaultValue is not null && (minOccurs > 1 || type is EbmlElementType.MasterElement or EbmlElementType.Binary ||
                !EbmlValueCodec.Matches(type, defaultValue))))
        {
            throw new ArgumentException(ErrorMessages.SchemaConflict);
        }
        foreach (var bound in new[] { minimumValue, maximumValue, excludedValue })
        {
            if (bound is not null && (type is not (EbmlElementType.UnsignedInteger or EbmlElementType.SignedInteger or EbmlElementType.Date or EbmlElementType.Float) ||
                !EbmlValueCodec.Matches(type, bound) || bound is double floating && double.IsNaN(floating)))
            {
                throw new ArgumentException(ErrorMessages.SchemaConflict);
            }
        }
        if (minimumValue is not null && maximumValue is not null && Compare(minimumValue, maximumValue) > 0)
        {
            throw new ArgumentException(ErrorMessages.SchemaConflict);
        }
        var definition = new EbmlElementDefinition(identifier, name, type, parents, unknownSizeAllowed, recursive, global)
        {
            MinOccurs = minOccurs, MaxOccurs = maxOccurs, MinimumValue = minimumValue, MaximumValue = maximumValue,
            DefaultValue = defaultValue, MinimumLength = minimumLength, MaximumLength = maximumLength,
            MinimumInclusive = minimumInclusive, MaximumInclusive = maximumInclusive, ExcludedValue = excludedValue
        };
        if (defaultValue is not null)
        {
            EbmlValueCodec.GetLength(type, defaultValue);
            if (!definition.Contains(defaultValue))
            {
                throw new ArgumentException(ErrorMessages.SchemaConflict);
            }
        }
        return definition;
    }

    /// <summary>Gets the element ID.</summary>
    public VInt Identifier { get; }
    /// <summary>Gets the descriptive name.</summary>
    public string Name { get; }
    /// <summary>Gets the data type.</summary>
    public EbmlElementType Type { get; }
    /// <summary>Gets the immutable list of permitted parent IDs.</summary>
    public IReadOnlyList<VInt> ParentIds { get; }
    /// <summary>Gets whether this master may have unknown size.</summary>
    public bool UnknownSizeAllowed { get; }
    /// <summary>Gets whether this master may contain itself.</summary>
    public bool Recursive { get; }
    /// <summary>Gets whether the element is global.</summary>
    public bool IsGlobal { get; }
    /// <summary>Gets the minimum occurrences in each permitted parent.</summary>
    public int MinOccurs { get; private init; }
    /// <summary>Gets the maximum occurrences, or null for unbounded.</summary>
    public int? MaxOccurs { get; private init; }
    /// <summary>Gets the typed numeric lower bound.</summary>
    public object? MinimumValue { get; private init; }
    /// <summary>Gets the typed numeric upper bound.</summary>
    public object? MaximumValue { get; private init; }
    /// <summary>Gets whether the lower bound is inclusive.</summary>
    public bool MinimumInclusive { get; private init; }
    /// <summary>Gets whether the upper bound is inclusive.</summary>
    public bool MaximumInclusive { get; private init; }
    /// <summary>Gets the excluded typed numeric value.</summary>
    public object? ExcludedValue { get; private init; }
    /// <summary>Gets the immutable scalar default.</summary>
    public object? DefaultValue { get; private init; }
    /// <summary>Gets the minimum wire payload length.</summary>
    public long MinimumLength { get; private init; }
    /// <summary>Gets the maximum wire payload length.</summary>
    public long MaximumLength { get; private init; }

    internal bool Contains(object value)
    {
        if (!EbmlValueCodec.Matches(Type, value))
        {
            return false;
        }

        if ((MinimumValue is not null || MaximumValue is not null || ExcludedValue is not null) && value is double floating && double.IsNaN(floating))
        {
            return false;
        }

        if (MinimumValue is not null && Compare(value, MinimumValue) < (MinimumInclusive ? 0 : 1))
        {
            return false;
        }

        if (MaximumValue is not null && Compare(value, MaximumValue) > (MaximumInclusive ? 0 : -1))
        {
            return false;
        }

        return ExcludedValue is null || Compare(value, ExcludedValue) != 0;
    }

    private static int Compare(object left, object right) => left switch
    {
        ulong number => number.CompareTo((ulong)right),
        long number => number.CompareTo((long)right),
        double number => number.CompareTo((double)right),
        _ => 0
    };

    internal bool AllowsParent(ulong? parent)
    {
        if (IsGlobal)
        {
            if (parent is 0x1A45DFA3 or 0x4281 && Identifier.EncodedValue is not (0xEC or 0xBF))
            {
                return false; // RFC 8794 section 11.3: derived globals apply only to the body.
            }
            return Identifier.EncodedValue != 0xBF || parent is not null;
        }

        if (parent is null)
        {
            return ParentIds.Count == 0;
        }

        if (Recursive && parent == Identifier.EncodedValue)
        {
            return true;
        }

        foreach (var id in ParentIds)
        {
            if (id.EncodedValue == parent)
            {
                return true;
            }
        }

        return false;
    }
}
