using System;
using System.Collections.Generic;
using System.Linq.Expressions;

using Assimalign.Cohesion.Database.Sql.Language.Internal;
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
[global::System.Diagnostics.DebuggerDisplay("SqlTableBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Schema.SqlTableBuilder<>.DebugView))]
public sealed class SqlTableBuilder<TRow>
{
    private readonly List<string> _columns = [];
    private readonly List<SqlSchemaColumn> _columnDefinitions = [];
    private readonly List<string> _indexes = [];
    private readonly List<SqlSchemaReference> _references = [];
    private readonly List<SqlSchemaCheck> _checks = [];
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

    /// <summary>
    /// Declares a column of a named SQL type: a domain the SQL engine registers
    /// (<c>sql.Types.Add(SqlType.CreateDomain("email", ...))</c>), or a built-in type name with its
    /// arguments (<c>VARCHAR(320)</c>, <c>DECIMAL(18, 2)</c>).
    /// </summary>
    /// <param name="selector">Selects a direct row member stored in the column.</param>
    /// <param name="sqlType">
    /// The type's name as DDL writes it: an identifier, optionally followed by one or two unsigned
    /// integer arguments in parentheses. Pass a constant string, so the <c>Sdk.Database</c> build task
    /// can read the declaration without running the program.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="selector"/> does not select a direct row member; <paramref name="sqlType"/> is
    /// empty or white space; or the member was already declared with another SQL type.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> or <paramref name="sqlType"/> is null.</exception>
    /// <remarks>
    /// This package validates built-in modifiers but does not know the engine's domain registrations.
    /// The name is kept as written, in the compiled
    /// schema's document and hash, and the SQL engine that provisions the schema resolves it as its DDL
    /// does, a built-in type name first and then a domain of its type catalog. The engine's build
    /// refuses a name it does not know, or a type not stored as the member's CLR type is (a domain
    /// over <c>VARCHAR(320)</c> for a <see cref="string"/> member), before it touches any file.
    /// </remarks>
    /// <exception cref="SqlSchemaValidationException">The SQL type name is malformed or declares invalid or unsupported length, precision or scale modifiers.</exception>
    public void Column(Expression<Func<TRow, object?>> selector, string sqlType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlType);
        string name = SelectMember(selector).Member.Name;
        if (!CompiledSchemaTypeNames.IsTypeName(sqlType))
        {
            throw new SqlSchemaValidationException([
                new SqlSchemaValidationError(SqlSchemaValidationErrorCode.UnsupportedType, $"{_name}.{name}",
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTableBuilderColumnFailure, sqlType))]);
        }

        SqlTypeModifiers.TrySplit(sqlType, out string typeName, out int? first, out int? second);
        if (SqlTypeModifiers.GetError(typeName, first, second) is { } error)
        {
            throw new SqlSchemaValidationException([
                new SqlSchemaValidationError(SqlSchemaValidationErrorCode.UnsupportedType, $"{_name}.{name}", error)]);
        }

        AddColumn(selector);
        int index = _columnDefinitions.FindIndex(column => column.Name == name);
        var column = _columnDefinitions[index];
        if (column.SqlType is not null && !string.Equals(column.SqlType, sqlType, StringComparison.Ordinal))
        {
            throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTableBuilderColumnArgument, name, column.SqlType), nameof(sqlType));
        }

        _columnDefinitions[index] = column with { SqlType = sqlType };
    }

    /// <summary>Declares a column's default from a typed CLR value.</summary>
    /// <typeparam name="TValue">The selected member's value type.</typeparam>
    /// <param name="selector">Selects a direct row member stored in the column.</param>
    /// <param name="value">The default value; strings are quoted and escaped as SQL text, and null becomes SQL NULL.</param>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="selector"/> does not select a direct row member; <paramref name="value"/>
    /// cannot be represented by the column's storage type; or the column already declares another default.
    /// </exception>
    /// <remarks>
    /// The column is declared if it has not been mentioned yet. Values render deterministically using
    /// invariant SQL literals. The SDK extracts C# compile-time constants without running the program;
    /// use <see cref="DefaultSql"/> for values that cannot be evaluated at build time. The engine also
    /// validates the literal against any named SQL type and its bounds before it touches a file.
    /// </remarks>
    public void Default<TValue>(Expression<Func<TRow, TValue>> selector, TValue value)
    {
        MemberExpression member = SelectMember(selector);
        string literal = SqlDefaultLiteralRenderer.Render(SqlDefaultLiteralRenderer.StorageType(member.Type), value);
        string name = AddColumn(selector);
        SetDefault(name, literal, nameof(value));
    }

    /// <summary>Declares a column's SQL DEFAULT from a raw SQL literal.</summary>
    /// <param name="selector">Selects a direct row member stored in the column.</param>
    /// <param name="sqlLiteral">
    /// The SQL literal as written, including quotes for a string (for example <c>'pending'</c>,
    /// <c>7</c> or <c>TRUE</c>). Pass a constant string so the <c>Sdk.Database</c> build task can read it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> or <paramref name="sqlLiteral"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="selector"/> does not select a direct row member; <paramref name="sqlLiteral"/> is
    /// empty or white space; or the column already declares another default literal.
    /// </exception>
    /// <remarks>
    /// The column is declared if it has not been mentioned yet. This package keeps the text in the
    /// compiled document and hash without parsing SQL. The SQL engine validates it as exactly one
    /// supported literal before it touches a file. SQL <c>NULL</c> is permitted
    /// and has the same database behavior as an absent default.
    /// </remarks>
    public void DefaultSql(Expression<Func<TRow, object?>> selector, string sqlLiteral)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlLiteral);
        string name = AddColumn(selector);
        SetDefault(name, sqlLiteral, nameof(sqlLiteral));
    }

    private void SetDefault(string name, string literal, string parameterName)
    {
        int index = _columnDefinitions.FindIndex(column => column.Name == name);
        var column = _columnDefinitions[index];
        if (column.DefaultLiteral is not null && !string.Equals(column.DefaultLiteral, literal, StringComparison.Ordinal))
        {
            throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTableBuilderSetDefaultArgument, name, column.DefaultLiteral), parameterName);
        }

        _columnDefinitions[index] = column with { DefaultLiteral = literal };
    }

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
    /// Declares a CHECK constraint: a SQL predicate over the table's columns that no row may make
    /// FALSE (NULL, SQL's unknown, passes).
    /// </summary>
    /// <param name="name">The constraint's name, unique among the table's constraints.</param>
    /// <param name="sql">
    /// The predicate as SQL text, for example <c>LENGTH(Name) &gt; 0 AND Total &gt;= 0</c>. It names the
    /// table's columns (their CLR member names) and may call the engine's functions.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="sql"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="sql"/> is empty or white space.</exception>
    /// <remarks>
    /// <para>
    /// This package does not parse SQL: the text is kept as written, in the compiled schema's document
    /// and hash (a <see cref="CompiledSchemaConstraintKind.Check"/> constraint), and the SQL engine
    /// that applies the schema parses it. Pass constant strings, so the <c>Sdk.Database</c> build task
    /// can read the declaration without running the program.
    /// </para>
    /// <para>
    /// A SQL engine's build binds every declared CHECK to its function catalog before it touches any
    /// file, and refuses one that does not parse as exactly one predicate, names an unknown column or
    /// function, calls a function with arguments no overload accepts, or calls a function that is not
    /// registered as immutable (owner decision 64 of 2026-10-09). A function the application registers
    /// on the engine builder is valid here; the schema package and the SDK never see it.
    /// </para>
    /// </remarks>
    public void Check(string name, string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        _checks.Add(new SqlSchemaCheck(name, sql));
    }

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
            Array.AsReadOnly(_references.ToArray()),
            Array.AsReadOnly(_checks.ToArray()));

    private string AddColumn(LambdaExpression selector)
    {
        MemberExpression member = SelectMember(selector);
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

    private static MemberExpression SelectMember(LambdaExpression selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        Expression body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
        {
            body = conversion.Operand;
        }

        if (body is not MemberExpression member ||
            !ReferenceEquals(member.Expression, selector.Parameters[0]))
        {
            throw new ArgumentException(ErrorMessages.SqlTableBuilderSelectMemberArgument, nameof(selector));
        }

        return member;
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Schema.SqlTableBuilder<TRow> value)
        {
            _ = value;
        }
    }
}
