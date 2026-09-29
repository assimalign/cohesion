using System;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphCatalogIntrospection
{
    internal static QueryResult Execute(GraphDatabaseInstance database, GraphOperation operation,
        GqlQueryExpression query, GqlCatalogSurface surface, CancellationToken token)
    {
        // A hand-built AST must obey the same read-only rule as a parsed SHOW statement.
        if (query.Creates.Count != 0 || query.DeleteVariables.Count != 0 || query.DetachDelete)
        {
            throw new DatabaseException("GQL0007: Graph catalog introspection is read-only.");
        }
        if (query.Matches.Count != 0 || query.Predicate is not null || query.Projections.Count != 0)
        {
            throw new DatabaseException("COHDBG001: SHOW cannot be composed with graph clauses.");
        }

        var columns = Columns(surface);
        var rows = new List<object?[]>();
        var snapshot = operation.Context.Snapshot;
        var catalog = database.Catalog;
        string name = database.Name.ToString();
        foreach (var label in catalog.GetLabels(snapshot))
        {
            token.ThrowIfCancellationRequested();
            if (surface == GqlCatalogSurface.Labels) { rows.Add([name, label.Id, label.Name]); }
            AddDefinition("LABEL", label.Id, label.Name, label.Owner, label.OwningSchema);
            if (surface is GqlCatalogSurface.Indexes or GqlCatalogSurface.ObjectOwnership)
            {
                foreach (var index in catalog.GetIndexes(label.Id, snapshot))
                {
                    token.ThrowIfCancellationRequested();
                    if (surface == GqlCatalogSurface.Indexes)
                    {
                        rows.Add([name, label.Id, label.Name, index.Name, index.PropertyKey, false]);
                    }
                    else { Ownership("LABEL", label.Id, label.Name, "INDEX", index.Name, label.Owner, label.OwningSchema); }
                }
            }
        }
        if (surface is GqlCatalogSurface.RelationshipTypes or GqlCatalogSurface.PropertyKeys or GqlCatalogSurface.ObjectOwnership)
        {
            foreach (var type in catalog.GetRelationshipTypes(snapshot))
            {
                token.ThrowIfCancellationRequested();
                if (surface == GqlCatalogSurface.RelationshipTypes) { rows.Add([name, type.Id, type.Name]); }
                AddDefinition("RELATIONSHIP TYPE", type.Id, type.Name, type.Owner, type.OwningSchema);
            }
        }
        return new GraphQueryResult(columns, rows);

        void AddDefinition(string kind, Guid id, string definition, DatabaseObjectOwner owner, string? schema)
        {
            if (surface == GqlCatalogSurface.ObjectOwnership) { Ownership(kind, id, definition, kind, definition, owner, schema); }
            if (surface is not (GqlCatalogSurface.PropertyKeys or GqlCatalogSurface.ObjectOwnership)) { return; }
            foreach (var property in catalog.GetPropertyKeys(id, snapshot))
            {
                token.ThrowIfCancellationRequested();
                if (surface == GqlCatalogSurface.PropertyKeys)
                {
                    rows.Add([name, kind, id, definition, property.Name, property.Type?.ToString(), property.Required]);
                }
                else { Ownership(kind, id, definition, "PROPERTY KEY", property.Name, owner, schema); }
            }
        }

        void Ownership(string kind, Guid id, string definition, string objectType, string objectName,
            DatabaseObjectOwner owner, string? schema)
            => rows.Add([name, kind, id, definition, objectType, objectName,
                owner == DatabaseObjectOwner.Schema ? "Schema" : "Adhoc", schema]);
    }

    private static QueryColumn[] Columns(GqlCatalogSurface surface)
    {
        (string Name, DatabaseType Type)[] definitions = surface switch
        {
            GqlCatalogSurface.Labels =>
                [("DATABASE_NAME", DatabaseType.String), ("LABEL_ID", DatabaseType.Guid), ("LABEL_NAME", DatabaseType.String)],
            GqlCatalogSurface.RelationshipTypes =>
                [("DATABASE_NAME", DatabaseType.String), ("RELATIONSHIP_TYPE_ID", DatabaseType.Guid), ("RELATIONSHIP_TYPE_NAME", DatabaseType.String)],
            GqlCatalogSurface.PropertyKeys =>
                [("DATABASE_NAME", DatabaseType.String), ("DEFINITION_TYPE", DatabaseType.String), ("DEFINITION_ID", DatabaseType.Guid),
                 ("DEFINITION_NAME", DatabaseType.String), ("PROPERTY_KEY", DatabaseType.String), ("DATA_TYPE", DatabaseType.String), ("IS_REQUIRED", DatabaseType.Boolean)],
            GqlCatalogSurface.Indexes =>
                [("DATABASE_NAME", DatabaseType.String), ("LABEL_ID", DatabaseType.Guid), ("LABEL_NAME", DatabaseType.String),
                 ("INDEX_NAME", DatabaseType.String), ("PROPERTY_KEY", DatabaseType.String), ("IS_UNIQUE", DatabaseType.Boolean)],
            GqlCatalogSurface.ObjectOwnership =>
                [("DATABASE_NAME", DatabaseType.String), ("DEFINITION_TYPE", DatabaseType.String), ("DEFINITION_ID", DatabaseType.Guid),
                 ("DEFINITION_NAME", DatabaseType.String), ("OBJECT_TYPE", DatabaseType.String), ("OBJECT_NAME", DatabaseType.String),
                 ("OWNER", DatabaseType.String), ("OWNING_SCHEMA", DatabaseType.String)],
            _ => throw new DatabaseException("COHDBG001: Unknown graph catalog introspection subject."),
        };
        var columns = new QueryColumn[definitions.Length];
        for (int ordinal = 0; ordinal < definitions.Length; ordinal++)
        {
            columns[ordinal] = new QueryColumn
            {
                Name = definitions[ordinal].Name,
                Type = definitions[ordinal].Type,
                Ordinal = ordinal,
                IsNullable = definitions[ordinal].Name is "DATA_TYPE" or "OWNING_SCHEMA",
            };
        }
        return columns;
    }
}
