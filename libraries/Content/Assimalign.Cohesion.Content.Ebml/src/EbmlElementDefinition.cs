using System;
using System.Collections.Generic;

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
    /// <returns>An immutable definition.</returns>
    /// <exception cref="ArgumentException">An ID, type, name, or flag combination is invalid.</exception>
    public static EbmlElementDefinition Create(VInt identifier, string name, EbmlElementType type,
        IEnumerable<VInt>? parentIds = null, bool unknownSizeAllowed = false, bool recursive = false, bool global = false)
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

        return new EbmlElementDefinition(identifier, name, type, parents, unknownSizeAllowed, recursive, global);
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

    internal bool AllowsParent(ulong? parent)
    {
        if (IsGlobal)
        {
            return true;
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
