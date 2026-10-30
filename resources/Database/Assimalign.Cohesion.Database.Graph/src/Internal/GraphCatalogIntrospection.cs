using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphCatalogIntrospection
{
    internal static DatabaseResult Execute(GraphDatabase database, GraphOperation operation,
        GqlQueryExpression query, GqlCatalogSurface surface, CancellationToken token)
    {
        // A hand-built AST must obey the same read-only rule as a parsed SHOW statement.
        if (query.Creates.Count != 0 || query.DeleteVariables.Count != 0 || query.DetachDelete ||
            query.Stages.Any(stage => stage is GqlInsertStage or GqlDeleteStage))
        {
            throw new GraphPlanningException("CG00007", ErrorMessages.IntrospectionExecuteGraphPlanning, query.Location);
        }
        if (query.Stages.Count != 0 || query.MatchClauses is null || query.MatchClauses.Count != 0 || query.Projections.Count != 0 ||
            query.ReturnStar || query.SetQuantifier != GqlSetQuantifier.All || query.OrderBy is null ||
            query.OrderBy.Count != 0 || query.Offset is not null || query.Limit is not null)
        {
            throw new GraphPlanningException("CG05001", ErrorMessages.IntrospectionExecuteGraphPlanning2, query.Location);
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
        if (surface is GqlCatalogSurface.RelationshipTypes or GqlCatalogSurface.PropertyKeys or GqlCatalogSurface.Constraints or GqlCatalogSurface.ObjectOwnership)
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
            if (surface == GqlCatalogSurface.Constraints)
            {
                foreach (var constraint in catalog.GetConstraints(id, snapshot))
                { token.ThrowIfCancellationRequested(); rows.Add([name, kind, id, definition, constraint.Name, constraint.PropertyKey, constraint.Type?.ToString(), constraint.Type is null]); }
            }
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
            if (surface == GqlCatalogSurface.ObjectOwnership)
            {
                foreach (var constraint in catalog.GetConstraints(id, snapshot))
                {
                    token.ThrowIfCancellationRequested();
                    Ownership(kind, id, definition, "CONSTRAINT", constraint.Name, owner, schema);
                }
            }
        }

        void Ownership(string kind, Guid id, string definition, string objectType, string objectName,
            DatabaseObjectOwner owner, string? schema)
            => rows.Add([name, kind, id, definition, objectType, objectName,
                owner == DatabaseObjectOwner.Schema ? "Schema" : "Adhoc", schema]);
    }

    private static DatabaseColumn[] Columns(GqlCatalogSurface surface)
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
            GqlCatalogSurface.Constraints =>
                [("DATABASE_NAME", DatabaseType.String), ("DEFINITION_TYPE", DatabaseType.String), ("DEFINITION_ID", DatabaseType.Guid),
                 ("DEFINITION_NAME", DatabaseType.String), ("CONSTRAINT_NAME", DatabaseType.String), ("PROPERTY_KEY", DatabaseType.String),
                 ("DATA_TYPE", DatabaseType.String), ("IS_REQUIRED", DatabaseType.Boolean)],
            GqlCatalogSurface.ObjectOwnership =>
                [("DATABASE_NAME", DatabaseType.String), ("DEFINITION_TYPE", DatabaseType.String), ("DEFINITION_ID", DatabaseType.Guid),
                 ("DEFINITION_NAME", DatabaseType.String), ("OBJECT_TYPE", DatabaseType.String), ("OBJECT_NAME", DatabaseType.String),
                 ("OWNER", DatabaseType.String), ("OWNING_SCHEMA", DatabaseType.String)],
            _ => throw new GraphPlanningException("CG05001", ErrorMessages.IntrospectionColumnsGraphPlanning),
        };
        var columns = new DatabaseColumn[definitions.Length];
        for (int ordinal = 0; ordinal < definitions.Length; ordinal++)
        {
            columns[ordinal] = new DatabaseColumn
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
