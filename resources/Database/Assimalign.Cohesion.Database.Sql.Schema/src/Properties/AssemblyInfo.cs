using System.Runtime.CompilerServices;

// The schema declaration model (SqlSchemaDeclaration and its records) is internal behind the
// opaque SqlSchema, and the compiler is internal behind SqlSchema.Compile: the project's own tests
// read the declaration model and drive the compiler with a non-SQL engine model (concrete-types
// plan, §6.7).
[assembly: InternalsVisibleTo("Assimalign.Cohesion.Database.Sql.Schema.Tests")]
