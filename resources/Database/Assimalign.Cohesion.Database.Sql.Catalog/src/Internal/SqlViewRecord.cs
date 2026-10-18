using System.Linq;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Internal;

internal sealed record SqlViewRecord(string Schema, string Name, string Definition, SqlViewColumn[] Columns,
    ulong[] TableDependencies, string[] Dependencies, string[] FunctionCalls, DatabaseObjectOwner Owner, string? OwningSchema)
{
    internal static SqlViewRecord From(SqlCatalogView view) => new(view.Schema, view.Name, view.Definition,
        view.Columns.Select(column => new SqlViewColumn(column.Name, column.Type, column.Collation?.Name, column.DomainName)).ToArray(),
        view.TableDependencies.ToArray(), view.Dependencies.ToArray(), view.FunctionCalls.ToArray(), view.Owner, view.OwningSchema);
    internal SqlCatalogView ToView() => new(Schema, Name, Definition,
        Columns.Select(column => new SqlCatalogColumn(column.Name, column.Type, collation: column.Collation is null ? null : Types.Collation.FromName(column.Collation), domainName: column.DomainName)).ToArray(),
        TableDependencies, Dependencies, FunctionCalls, Owner, OwningSchema);
}

internal sealed record SqlViewColumn(string Name, DatabaseTypeInfo Type, string? Collation, string? DomainName);
