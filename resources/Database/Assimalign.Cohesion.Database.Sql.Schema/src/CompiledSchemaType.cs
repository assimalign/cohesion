using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>
/// Describes a compiled custom scalar type: a domain the schema declares (<c>schema.Type&lt;T&gt;</c>),
/// which the SQL engine that provisions the schema registers over the storage type (phase E3).
/// </summary>
/// <param name="Name">The domain's SQL name: the CLR type's simple name, a SQL identifier.</param>
/// <param name="StorageType">The underlying storage type.</param>
/// <param name="Precision">The decimal precision.</param>
/// <param name="Scale">The decimal scale; omitted scale becomes zero with an explicit precision in a compiled schema.</param>
public sealed record CompiledSchemaType(string Name, DatabaseType StorageType, int? Precision, int? Scale);
