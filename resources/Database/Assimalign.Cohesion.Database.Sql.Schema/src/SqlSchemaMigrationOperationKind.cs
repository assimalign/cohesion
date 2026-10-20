namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Identifies the semantic change performed by a migration step.</summary>
public enum SqlSchemaMigrationOperationKind : byte
{
    /// <summary>Adds a table.</summary>
    AddTable = 0,
    /// <summary>Alters table key or constraint metadata.</summary>
    AlterTable,
    /// <summary>Drops a table.</summary>
    DropTable,
    /// <summary>Adds a column.</summary>
    AddColumn,
    /// <summary>Alters a column.</summary>
    AlterColumn,
    /// <summary>Drops a column.</summary>
    DropColumn,
    /// <summary>Adds an index.</summary>
    AddIndex,
    /// <summary>Drops an index.</summary>
    DropIndex,
    /// <summary>Adds a foreign-key or check constraint.</summary>
    AddConstraint,
    /// <summary>Drops a foreign-key or check constraint.</summary>
    DropConstraint,
    /// <summary>
    /// Adds a domain the schema declares (<c>schema.Type&lt;T&gt;</c>, phase E3). A SQL engine's domains
    /// are registered on the engine, not stored in the database, so the step changes no catalog: the
    /// engine that declares the database registers the domain when it builds.
    /// </summary>
    AddDomain,
    /// <summary>Drops a domain the schema no longer declares; like <see cref="AddDomain"/>, it changes no catalog.</summary>
    DropDomain,
}
