using System;
using System.Collections.Generic;
using System.Linq.Expressions;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>
/// Configures a table declared by <see cref="SqlSchemaBuilder.Table{T}(Action{SqlTableBuilder{T}})"/>
/// from its CLR row type.
/// </summary>
/// <typeparam name="TRow">The table row type.</typeparam>
/// <remarks>
/// <b>Shape (concrete-types plan, phase 4, §6.7).</b> A public sealed class with an internal
/// constructor; it replaced the <c>ISqlTableBuilder&lt;TRow&gt;</c> interface and its internal
/// implementation. The <c>Sdk.Database</c> extractor recognizes its calls by its metadata name
/// (<c>SqlTableBuilder`1</c>).
/// </remarks>
public sealed class SqlTableBuilder<TRow>
{
    private readonly List<string> _columns = [];
    private readonly List<SqlSchemaColumn> _columnDefinitions = [];
    private readonly List<string> _indexes = [];
    private readonly List<SqlSchemaReference> _references = [];
    private readonly string _name;
    private string? _primaryKey;

    /// <summary>
    /// Initializes a new builder for one table.
    /// </summary>
    /// <param name="name">The name of the table the builder produces.</param>
    internal SqlTableBuilder(string name)
    {
        _name = name;
    }

    /// <summary>Declares a column explicitly.</summary>
    /// <param name="selector">Selects a direct row member stored in the column.</param>
    /// <exception cref="ArgumentException"><paramref name="selector"/> does not select a direct row member.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is null.</exception>
    public void Column(Expression<Func<TRow, object?>> selector)
        => AddColumn(selector);

    /// <summary>Declares the table's primary key.</summary>
    /// <param name="selector">Selects the direct primary-key member.</param>
    /// <exception cref="ArgumentException"><paramref name="selector"/> does not select a direct row member.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is null.</exception>
    public void PrimaryKey(Expression<Func<TRow, object?>> selector)
        => _primaryKey = AddColumn(selector);

    /// <summary>Declares the table's primary key; the same declaration as <see cref="PrimaryKey"/>.</summary>
    /// <param name="selector">Selects the direct primary-key member.</param>
    /// <exception cref="ArgumentException"><paramref name="selector"/> does not select a direct row member.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is null.</exception>
    public void Key(Expression<Func<TRow, object?>> selector)
        => _primaryKey = AddColumn(selector);

    /// <summary>Declares an index.</summary>
    /// <param name="selector">Selects the direct indexed member.</param>
    /// <exception cref="ArgumentException"><paramref name="selector"/> does not select a direct row member.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is null.</exception>
    public void Index(Expression<Func<TRow, object?>> selector)
        => _indexes.Add(AddColumn(selector));

    /// <summary>Declares a reference from this table to <typeparamref name="TTarget"/>.</summary>
    /// <typeparam name="TTarget">The referenced table row type.</typeparam>
    /// <param name="selector">Selects this table's direct foreign-key member.</param>
    /// <exception cref="ArgumentException"><paramref name="selector"/> does not select a direct row member.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is null.</exception>
    public void References<TTarget>(Expression<Func<TRow, object?>> selector)
        => _references.Add(new SqlSchemaReference(AddColumn(selector), typeof(TTarget)));

    /// <summary>
    /// Snapshots what the builder recorded into the immutable declaration.
    /// </summary>
    /// <returns>The table declaration; later calls on this builder do not change it.</returns>
    internal SqlSchemaTable Build()
        => new(
            _name,
            typeof(TRow),
            Array.AsReadOnly(_columns.ToArray()),
            Array.AsReadOnly(_columnDefinitions.ToArray()),
            _primaryKey,
            Array.AsReadOnly(_indexes.ToArray()),
            Array.AsReadOnly(_references.ToArray()));

    private string AddColumn(Expression<Func<TRow, object?>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        Expression body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } conversion)
        {
            body = conversion.Operand;
        }

        if (body is not MemberExpression member ||
            !ReferenceEquals(member.Expression, selector.Parameters[0]))
        {
            throw new ArgumentException("A schema member selector must select one row member.", nameof(selector));
        }

        string name = member.Member.Name;
        if (!_columns.Contains(name))
        {
            _columns.Add(name);
            Type clrType = member.Type;
            _columnDefinitions.Add(new SqlSchemaColumn(
                name,
                clrType,
                !clrType.IsValueType || Nullable.GetUnderlyingType(clrType) is not null));
        }

        return name;
    }
}
