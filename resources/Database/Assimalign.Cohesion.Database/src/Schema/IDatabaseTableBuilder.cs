using System;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.Database;

/// <summary>Configures a table from a CLR row type.</summary>
/// <typeparam name="TRow">The table row type.</typeparam>
public interface IDatabaseTableBuilder<TRow>
{
    /// <summary>Declares a column explicitly.</summary>
    /// <param name="selector">Selects a direct row member stored in the column.</param>
    /// <exception cref="ArgumentException"><paramref name="selector"/> does not select a direct row member.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is null.</exception>
    void Column(Expression<Func<TRow, object?>> selector);

    /// <summary>Declares the table's primary key.</summary>
    /// <param name="selector">Selects the direct primary-key member.</param>
    /// <exception cref="ArgumentException"><paramref name="selector"/> does not select a direct row member.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is null.</exception>
    void PrimaryKey(Expression<Func<TRow, object?>> selector);

    /// <summary>Declares the table's primary key.</summary>
    /// <param name="selector">Selects the direct primary-key member.</param>
    /// <exception cref="ArgumentException"><paramref name="selector"/> does not select a direct row member.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is null.</exception>
    void Key(Expression<Func<TRow, object?>> selector);

    /// <summary>Declares an index.</summary>
    /// <param name="selector">Selects the direct indexed member.</param>
    /// <exception cref="ArgumentException"><paramref name="selector"/> does not select a direct row member.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is null.</exception>
    void Index(Expression<Func<TRow, object?>> selector);

    /// <summary>Declares a reference from this table to <typeparamref name="TTarget"/>.</summary>
    /// <typeparam name="TTarget">The referenced table row type.</typeparam>
    /// <param name="selector">Selects this table's direct foreign-key member.</param>
    /// <exception cref="ArgumentException"><paramref name="selector"/> does not select a direct row member.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is null.</exception>
    void References<TTarget>(Expression<Func<TRow, object?>> selector);
}
