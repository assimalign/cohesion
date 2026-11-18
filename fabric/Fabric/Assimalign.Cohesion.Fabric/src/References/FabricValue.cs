using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// A spec value that is either a literal or a <see cref="FabricRef"/> the ResourceManager
/// resolves at reconcile.
/// </summary>
/// <typeparam name="T">The literal's type.</typeparam>
public sealed record FabricValue<T> where T : notnull
{
    private FabricValue(T? literal, FabricRef? reference)
    {
        Literal = literal;
        Ref = reference;
    }

    /// <summary>
    /// Gets the literal, or <see langword="default"/> when the value is a reference.
    /// </summary>
    public T? Literal { get; }

    /// <summary>
    /// Gets the reference, or <see langword="null"/> when the value is a literal.
    /// </summary>
    public FabricRef? Ref { get; }

    /// <summary>
    /// Gets a value indicating whether the value is a reference.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Ref))]
    public bool IsRef => Ref is not null;

    /// <summary>
    /// Creates a literal value.
    /// </summary>
    /// <param name="literal">The literal.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="literal"/> is <see langword="null"/>.</exception>
    public static FabricValue<T> FromLiteral(T literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        return new FabricValue<T>(literal, null);
    }

    /// <summary>
    /// Creates a reference value.
    /// </summary>
    /// <param name="reference">The reference.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reference"/> is <see langword="null"/>.</exception>
    public static FabricValue<T> FromRef(FabricRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new FabricValue<T>(default, reference);
    }

    /// <summary>
    /// Converts a literal to a value.
    /// </summary>
    /// <param name="literal">The literal.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="literal"/> is <see langword="null"/>.</exception>
    public static implicit operator FabricValue<T>(T literal) => FromLiteral(literal);

    /// <summary>
    /// Converts a reference to a value.
    /// </summary>
    /// <param name="reference">The reference.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reference"/> is <see langword="null"/>.</exception>
    public static implicit operator FabricValue<T>(FabricRef reference) => FromRef(reference);
}
