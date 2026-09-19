using Microsoft.CodeAnalysis;

namespace Assimalign.Cohesion.SourceGeneration.Database;

internal static class SqlMapperDiagnostics
{
    internal static readonly DiagnosticDescriptor UnsupportedDeclaration = new(
        "COHMAP001", "Schema declaration cannot generate a mapper", "{0}",
        "Database.Mapping", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedEntity = new(
        "COHMAP002", "Entity cannot be materialized statically", "{0}",
        "Database.Mapping", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidKey = new(
        "COHMAP003", "Entity identity requires a stable key", "{0}",
        "Database.Mapping", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor ConflictingDeclaration = new(
        "COHMAP004", "Entity has conflicting schema declarations", "{0}",
        "Database.Mapping", DiagnosticSeverity.Error, isEnabledByDefault: true);
}
