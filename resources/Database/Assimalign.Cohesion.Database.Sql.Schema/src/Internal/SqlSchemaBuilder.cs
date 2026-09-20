using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.Database.Sql.Schema;

internal sealed class SqlSchemaBuilder(string name) : ISqlSchemaBuilder
{
    private readonly List<ISqlSchemaType> _types = [];
    private readonly List<ISqlSchemaTable> _tables = [];
    private readonly List<ISqlSchemaFunction> _functions = [];
    private readonly List<ISqlSchemaTrigger> _triggers = [];
    private readonly List<ISqlSchemaPrincipal> _principals = [];
    private readonly List<ISqlSchemaExtension> _extensions = [];

    public bool AllowsDestructiveChanges { get; private set; }

    public void AllowDestructiveChanges() => AllowsDestructiveChanges = true;

    public void Type<T>(Action<ISqlTypeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new SqlSchemaTypeBuilder(typeof(T));
        configure(builder);
        _types.Add(builder.Build());
    }

    public void Table<T>(Action<ISqlTableBuilder<T>> configure)
        => Table(typeof(T).Name, configure);

    public void Table<T>(string tableName, Action<ISqlTableBuilder<T>> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new SqlSchemaTableBuilder<T>(tableName);
        configure(builder);
        _tables.Add(builder.Build());
    }

    public void Extension(string extensionName, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionName);
        ArgumentNullException.ThrowIfNull(value);
        _extensions.Add(new SqlSchemaExtension(extensionName, value));
    }

    public void Function<TResult>(string name, Expression<Func<TResult>> body)
        => AddFunction(name, body);

    public void Function<TArgument, TResult>(string name, Expression<Func<TArgument, TResult>> body)
        => AddFunction(name, body);

    public void Trigger<TRow>(
        SqlTriggerEvent triggerEvent,
        Expression<Action<ISqlTriggerContext, TRow>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _triggers.Add(new SqlSchemaTrigger(typeof(TRow), triggerEvent, body));
    }

    public void Principal(string name, Action<ISqlPrincipalBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new SqlSchemaPrincipalBuilder(name);
        configure(builder);
        _principals.Add(builder.Build());
    }

    public ISqlSchema Build()
        => new SqlSchemaModel(
            AllowsDestructiveChanges,
            Snapshot(_types),
            Snapshot(_tables),
            Snapshot(_functions),
            Snapshot(_triggers),
            Snapshot(_principals),
            Snapshot(_extensions),
            name);

    private void AddFunction(string name, LambdaExpression body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        _functions.Add(new SqlSchemaFunction(name, body));
    }

    private static ReadOnlyCollection<T> Snapshot<T>(List<T> values)
        => Array.AsReadOnly(values.ToArray());
}

internal sealed record SqlSchemaModel(
    bool AllowsDestructiveChanges,
    IReadOnlyList<ISqlSchemaType> Types,
    IReadOnlyList<ISqlSchemaTable> Tables,
    IReadOnlyList<ISqlSchemaFunction> Functions,
    IReadOnlyList<ISqlSchemaTrigger> Triggers,
    IReadOnlyList<ISqlSchemaPrincipal> Principals,
    IReadOnlyList<ISqlSchemaExtension> Extensions,
    string Name) : ISqlSchema;

internal sealed class SqlSchemaTypeBuilder(Type clrType) : ISqlTypeBuilder
{
    private readonly Type _clrType = clrType;

    public int? Precision { get; private set; }

    public int? Scale { get; private set; }

    public void Decimal(int precision, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(precision, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(scale);

        if (scale > precision)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Decimal scale cannot exceed precision.");
        }

        Precision = precision;
        Scale = scale;
    }

    internal ISqlSchemaType Build()
        => new SqlSchemaType(_clrType, Precision, Scale);
}

internal sealed record SqlSchemaType(
    Type ClrType,
    int? Precision,
    int? Scale) : ISqlSchemaType;

internal sealed class SqlSchemaTableBuilder<TRow>(string name) : ISqlTableBuilder<TRow>
{
    private readonly List<string> _columns = [];
    private readonly List<ISqlSchemaColumn> _columnDefinitions = [];
    private readonly List<string> _indexes = [];
    private readonly List<ISqlSchemaReference> _references = [];

    private string? PrimaryKey { get; set; }

    public void Column(Expression<Func<TRow, object?>> selector)
        => AddColumn(selector);

    void ISqlTableBuilder<TRow>.PrimaryKey(Expression<Func<TRow, object?>> selector)
        => PrimaryKey = AddColumn(selector);

    public void Key(Expression<Func<TRow, object?>> selector)
        => PrimaryKey = AddColumn(selector);

    public void Index(Expression<Func<TRow, object?>> selector)
        => _indexes.Add(AddColumn(selector));

    void ISqlTableBuilder<TRow>.References<TTarget>(Expression<Func<TRow, object?>> selector)
        => _references.Add(new SqlSchemaReference(AddColumn(selector), typeof(TTarget)));

    internal ISqlSchemaTable Build()
        => new SqlSchemaTable(
            name,
            typeof(TRow),
            Array.AsReadOnly(_columns.ToArray()),
            Array.AsReadOnly(_columnDefinitions.ToArray()),
            PrimaryKey,
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

internal sealed record SqlSchemaTable(
    string Name,
    Type RowType,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ISqlSchemaColumn> ColumnDefinitions,
    string? PrimaryKey,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<ISqlSchemaReference> References) : ISqlSchemaTable;

internal sealed record SqlSchemaReference(string Member, Type TargetType) : ISqlSchemaReference;

internal sealed record SqlSchemaColumn(string Name, Type ClrType, bool IsNullable) : ISqlSchemaColumn;

internal sealed record SqlSchemaExtension(string Name, string Value) : ISqlSchemaExtension;

internal sealed record SqlSchemaFunction(string Name, LambdaExpression Body) : ISqlSchemaFunction;

internal sealed record SqlSchemaTrigger(
    Type RowType,
    SqlTriggerEvent Event,
    LambdaExpression Body) : ISqlSchemaTrigger;

internal sealed class SqlSchemaPrincipalBuilder(string name) : ISqlPrincipalBuilder
{
    private readonly List<ISqlSchemaGrant> _grants = [];

    private readonly string _name = name;

    public void Grant(SqlPermission permission, params string[] objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Length == 0)
        {
            throw new ArgumentException("At least one schema object is required.", nameof(objects));
        }

        foreach (string schemaObject in objects)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(schemaObject);
        }

        _grants.Add(new SqlSchemaGrant(permission, Array.AsReadOnly((string[])objects.Clone())));
    }

    internal ISqlSchemaPrincipal Build()
        => new SqlSchemaPrincipal(_name, Array.AsReadOnly(_grants.ToArray()));
}

internal sealed record SqlSchemaPrincipal(
    string Name,
    IReadOnlyList<ISqlSchemaGrant> Grants) : ISqlSchemaPrincipal;

internal sealed record SqlSchemaGrant(SqlPermission Permission, IReadOnlyList<string> Objects) : ISqlSchemaGrant;
