using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.Database;

internal sealed class DatabaseSchemaBuilder(string name) : IDatabaseSchemaBuilder
{
    private readonly List<IDatabaseSchemaType> _types = [];
    private readonly List<IDatabaseSchemaTable> _tables = [];
    private readonly List<IDatabaseSchemaFunction> _functions = [];
    private readonly List<IDatabaseSchemaTrigger> _triggers = [];
    private readonly List<IDatabaseSchemaPrincipal> _principals = [];

    public void Type<T>(Action<IDatabaseTypeBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new DatabaseSchemaTypeBuilder(typeof(T));
        configure(builder);
        _types.Add(builder.Build());
    }

    public void Table<T>(Action<IDatabaseTableBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new DatabaseSchemaTableBuilder<T>();
        configure(builder);
        _tables.Add(builder.Build());
    }

    public void Function<TResult>(string name, Expression<Func<TResult>> body)
        => AddFunction(name, body);

    public void Function<TArgument, TResult>(string name, Expression<Func<TArgument, TResult>> body)
        => AddFunction(name, body);

    public void Trigger<TRow>(
        TriggerEvent triggerEvent,
        Expression<Action<IDatabaseTriggerContext, TRow>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _triggers.Add(new DatabaseSchemaTrigger(typeof(TRow), triggerEvent, body));
    }

    public void Principal(string name, Action<IDatabasePrincipalBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new DatabaseSchemaPrincipalBuilder(name);
        configure(builder);
        _principals.Add(builder.Build());
    }

    public IDatabaseSchema Build()
        => new DatabaseSchemaModel(
            Snapshot(_types),
            Snapshot(_tables),
            Snapshot(_functions),
            Snapshot(_triggers),
            Snapshot(_principals),
            name);

    private void AddFunction(string name, LambdaExpression body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        _functions.Add(new DatabaseSchemaFunction(name, body));
    }

    private static ReadOnlyCollection<T> Snapshot<T>(List<T> values)
        => Array.AsReadOnly(values.ToArray());
}

internal sealed record DatabaseSchemaModel(
    IReadOnlyList<IDatabaseSchemaType> Types,
    IReadOnlyList<IDatabaseSchemaTable> Tables,
    IReadOnlyList<IDatabaseSchemaFunction> Functions,
    IReadOnlyList<IDatabaseSchemaTrigger> Triggers,
    IReadOnlyList<IDatabaseSchemaPrincipal> Principals,
    string Name) : IDatabaseSchema;

internal sealed class DatabaseSchemaTypeBuilder(Type clrType) : IDatabaseTypeBuilder
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

    internal IDatabaseSchemaType Build()
        => new DatabaseSchemaType(_clrType, Precision, Scale);
}

internal sealed record DatabaseSchemaType(
    Type ClrType,
    int? Precision,
    int? Scale) : IDatabaseSchemaType;

internal sealed class DatabaseSchemaTableBuilder<TRow> : IDatabaseTableBuilder<TRow>
{
    private readonly List<string> _columns = [];
    private readonly List<string> _indexes = [];
    private readonly List<IDatabaseSchemaReference> _references = [];

    private string? PrimaryKey { get; set; }

    public void Column(Expression<Func<TRow, object?>> selector)
        => AddColumn(selector);

    void IDatabaseTableBuilder<TRow>.PrimaryKey(Expression<Func<TRow, object?>> selector)
        => PrimaryKey = AddColumn(selector);

    public void Key(Expression<Func<TRow, object?>> selector)
        => PrimaryKey = AddColumn(selector);

    public void Index(Expression<Func<TRow, object?>> selector)
        => _indexes.Add(AddColumn(selector));

    void IDatabaseTableBuilder<TRow>.References<TTarget>(Expression<Func<TRow, object?>> selector)
        => _references.Add(new DatabaseSchemaReference(AddColumn(selector), typeof(TTarget)));

    internal IDatabaseSchemaTable Build()
        => new DatabaseSchemaTable(
            typeof(TRow),
            Array.AsReadOnly(_columns.ToArray()),
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
        }

        return name;
    }
}

internal sealed record DatabaseSchemaTable(
    Type RowType,
    IReadOnlyList<string> Columns,
    string? PrimaryKey,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<IDatabaseSchemaReference> References) : IDatabaseSchemaTable;

internal sealed record DatabaseSchemaReference(string Member, Type TargetType) : IDatabaseSchemaReference;

internal sealed record DatabaseSchemaFunction(string Name, LambdaExpression Body) : IDatabaseSchemaFunction;

internal sealed record DatabaseSchemaTrigger(
    Type RowType,
    TriggerEvent Event,
    LambdaExpression Body) : IDatabaseSchemaTrigger;

internal sealed class DatabaseSchemaPrincipalBuilder(string name) : IDatabasePrincipalBuilder
{
    private readonly List<IDatabaseSchemaGrant> _grants = [];

    private readonly string _name = name;

    public void Grant(Permission permission, params string[] objects)
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

        _grants.Add(new DatabaseSchemaGrant(permission, Array.AsReadOnly((string[])objects.Clone())));
    }

    internal IDatabaseSchemaPrincipal Build()
        => new DatabaseSchemaPrincipal(_name, Array.AsReadOnly(_grants.ToArray()));
}

internal sealed record DatabaseSchemaPrincipal(
    string Name,
    IReadOnlyList<IDatabaseSchemaGrant> Grants) : IDatabaseSchemaPrincipal;

internal sealed record DatabaseSchemaGrant(Permission Permission, IReadOnlyList<string> Objects) : IDatabaseSchemaGrant;
