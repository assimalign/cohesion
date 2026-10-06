using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>
/// The completed C# declaration of one logical database's schema: what
/// <see cref="SqlSchemaBuilder"/> recorded, as immutable snapshots the compiler reads. Internal
/// behind the opaque <see cref="SqlSchema"/> (concrete-types plan, §6.7): a public positional
/// record would expose a public constructor and a <c>with</c> clone around the builders'
/// validation.
/// </summary>
/// <param name="AllowsDestructiveChanges">Whether migration planning may include destructive operations.</param>
/// <param name="Types">The declared custom types.</param>
/// <param name="Tables">The declared tables.</param>
/// <param name="Functions">The declared functions.</param>
/// <param name="Triggers">The declared triggers.</param>
/// <param name="Principals">The declared database principals.</param>
/// <param name="Extensions">The model-specific extension values.</param>
/// <param name="Name">The logical database name.</param>
internal sealed record SqlSchemaDeclaration(
    bool AllowsDestructiveChanges,
    IReadOnlyList<SqlSchemaType> Types,
    IReadOnlyList<SqlSchemaTable> Tables,
    IReadOnlyList<SqlSchemaFunction> Functions,
    IReadOnlyList<SqlSchemaTrigger> Triggers,
    IReadOnlyList<SqlSchemaPrincipal> Principals,
    IReadOnlyList<SqlSchemaExtension> Extensions,
    string Name);

/// <summary>A custom type declaration.</summary>
/// <param name="ClrType">The represented CLR type.</param>
/// <param name="Precision">The decimal precision, when configured.</param>
/// <param name="Scale">The decimal scale, when configured.</param>
internal sealed record SqlSchemaType(
    Type ClrType,
    int? Precision,
    int? Scale);

/// <summary>A table declaration.</summary>
/// <param name="Name">The stable table name.</param>
/// <param name="RowType">The table row type whose complete shape the compiler inspects.</param>
/// <param name="Columns">The row members explicitly mentioned by column, key, index, or reference declarations.</param>
/// <param name="ColumnDefinitions">The typed column declarations.</param>
/// <param name="PrimaryKey">The primary-key member name.</param>
/// <param name="Indexes">The indexed member names.</param>
/// <param name="References">The declared references.</param>
internal sealed record SqlSchemaTable(
    string Name,
    Type RowType,
    IReadOnlyList<string> Columns,
    IReadOnlyList<SqlSchemaColumn> ColumnDefinitions,
    string? PrimaryKey,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<SqlSchemaReference> References);

/// <summary>A table reference.</summary>
/// <param name="Member">The foreign-key member name.</param>
/// <param name="TargetType">The referenced table row type.</param>
internal sealed record SqlSchemaReference(string Member, Type TargetType);

/// <summary>A typed column selected from a schema row type.</summary>
/// <param name="Name">The column name.</param>
/// <param name="ClrType">The declared CLR value type.</param>
/// <param name="IsNullable">Whether null values are permitted.</param>
internal sealed record SqlSchemaColumn(string Name, Type ClrType, bool IsNullable);

/// <summary>A model-specific schema extension.</summary>
/// <param name="Name">The extension name.</param>
/// <param name="Value">The canonical extension value.</param>
internal sealed record SqlSchemaExtension(string Name, string Value);

/// <summary>A database function declaration.</summary>
/// <param name="Name">The function name.</param>
/// <param name="Body">The expression body retained for schema compilation.</param>
internal sealed record SqlSchemaFunction(string Name, LambdaExpression Body);

/// <summary>A database trigger declaration.</summary>
/// <param name="RowType">The target table row type.</param>
/// <param name="Event">The event that invokes the trigger.</param>
/// <param name="Body">The expression body retained for schema compilation.</param>
internal sealed record SqlSchemaTrigger(
    Type RowType,
    SqlTriggerEvent Event,
    LambdaExpression Body);

/// <summary>A database principal declaration.</summary>
/// <param name="Name">The principal name.</param>
/// <param name="Grants">The principal's grants.</param>
internal sealed record SqlSchemaPrincipal(
    string Name,
    IReadOnlyList<SqlSchemaGrant> Grants);

/// <summary>One permission grant.</summary>
/// <param name="Permission">The granted permission.</param>
/// <param name="Objects">The schema object names covered by the grant.</param>
internal sealed record SqlSchemaGrant(SqlPermission Permission, IReadOnlyList<string> Objects);
