using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Execution.Internal;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph;

public sealed partial class GraphSchema
{
    internal async ValueTask<DatabaseResult> ExecuteDdlAsync(GqlSchemaCommand command, IReadOnlyDictionary<string, object?>? parameters,
        Protocol.GraphQueryLimits? limits, Assimalign.Cohesion.Database.Language.Location? location, CancellationToken token)
    {
        long affected = 0;
        await WriteAsync(async op =>
        {
            ParameterBindings.Normalize(parameters, name => new GraphPlanningException("CG01014",
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ParameterCollision, name)));
            string name = command.Name ?? GeneratedName(command);
            if (command.Kind is GqlSchemaObjectKind.Label or GqlSchemaObjectKind.RelationshipType)
            {
                bool relationship = command.Kind == GqlSchemaObjectKind.RelationshipType;
                var label = _database.Catalog.FindLabel(name, op.Context.Snapshot);
                var type = _database.Catalog.FindRelationshipType(name, op.Context.Snapshot);
                bool exists = relationship ? type is not null : label is not null;
                if (command.Drop)
                {
                    if (!exists) { if (command.IfExists) { return; } throw DatabaseException.CreateGraphFailure("CG01002", string.Format(CultureInfo.CurrentCulture,
                        relationship ? ErrorMessages.RelationshipTypeUnknown : ErrorMessages.GraphSchemaLabelDatabase, name)); }
                    var latest = _database.LatestSnapshot(op.Context);
                    using var memory = new GraphMemoryOwner(op.QueryBudget!.Memory);
                    if (relationship)
                    {
                        EnsureMutable(name, type!.Value.Owner, type.Value.OwningSchema, "DROP RELATIONSHIP TYPE");
                        if (_database.Store.StreamRelationshipTypes([name], latest, token, op.QueryBudget.Consume, memory.Add).Any())
                        { throw DatabaseException.CreateGraphFailure("CG03003", ErrorMessages.DropRelationshipTypeAsyncDatabase); }
                        await _database.Catalog.DeleteRelationshipTypeAsync(type.Value.Id, op.Context, token).ConfigureAwait(false);
                    }
                    else
                    {
                        EnsureMutable(name, label!.Value.Owner, label.Value.OwningSchema, "DROP LABEL");
                        if (_database.Store.StreamLabels([name], latest, token, op.QueryBudget.Consume, memory.Add).Any())
                        { throw DatabaseException.CreateGraphFailure("CG03003", ErrorMessages.GraphSchemaDropLabelAsyncDatabase); }
                        foreach (var index in _database.Catalog.GetIndexes(label.Value.Id, op.Context.Snapshot))
                        { await _database.Store.DropIndexAsync(name, index.PropertyKey, op.Context, token).ConfigureAwait(false); }
                        await _database.Catalog.DeleteLabelAsync(label.Value.Id, op.Context, token).ConfigureAwait(false);
                    }
                }
                else
                {
                    if (exists)
                    {
                        if (command.IfExists) { return; }
                        throw DatabaseException.CreateGraphFailure("CG03003", string.Format(CultureInfo.CurrentCulture, ErrorMessages.SchemaDefinitionDuplicate, name));
                    }
                    await EnsureDefinitionAsync(op, name, relationship, token).ConfigureAwait(false);
                }
            }
            else if (command.Drop)
            {
                bool exists = command.Kind == GqlSchemaObjectKind.Index
                    ? _database.Catalog.FindIndex(name, op.Context.Snapshot) is not null
                    : _database.Catalog.FindConstraint(name, op.Context.Snapshot) is not null;
                if (!exists && command.IfExists) { return; }
                if (command.Kind == GqlSchemaObjectKind.Index) { await DropIndexCoreAsync(op, name, token).ConfigureAwait(false); }
                else { await DropConstraintCoreAsync(op, name, token).ConfigureAwait(false); }
            }
            else
            {
                var snapshot = op.Context.Snapshot;
                var index = _database.Catalog.FindIndex(name, snapshot);
                var constraint = _database.Catalog.FindConstraint(name, snapshot);
                bool indexCommand = command.Kind == GqlSchemaObjectKind.Index;
                if (command.IfExists && (indexCommand ? index is not null : constraint is not null)) { return; }
                if (index is not null || constraint is not null)
                { throw DatabaseException.CreateGraphFailure("CG03008", string.Format(CultureInfo.CurrentCulture, ErrorMessages.SchemaNameDuplicate, name)); }
                Guid? owner = command.Relationship ? _database.Catalog.FindRelationshipType(command.Definition!, snapshot)?.Id
                    : _database.Catalog.FindLabel(command.Definition!, snapshot)?.Id;
                DatabaseType? scalar = command.Type is { } target ? StorageType(target) : null;
                if (command.IfExists && owner is { } id && (indexCommand
                    ? _database.Catalog.GetIndexes(id, snapshot).Any(item => item.PropertyKey == command.Property)
                    : _database.Catalog.GetConstraints(id, snapshot).Any(item => item.PropertyKey == command.Property && item.Type == scalar))) { return; }
                if (indexCommand)
                { await CreateIndexCoreAsync(op, command.Definition!, name, command.Property!, token).ConfigureAwait(false); }
                else
                {
                    var definition = await EnsureDefinitionAsync(op, command.Definition!, command.Relationship, token).ConfigureAwait(false);
                    await CreateConstraintCoreAsync(op, new(definition, name, command.Property!, scalar), token).ConfigureAwait(false);
                }
            }
            affected = 1;
        }, token, limits, location).ConfigureAwait(false);
        return new GraphMutationResult(affected, []);
    }

    private async ValueTask<Guid> EnsureDefinitionAsync(GraphOperation op, string name, bool relationship, CancellationToken token)
    {
        if (relationship)
        {
            var type = _database.Catalog.FindRelationshipType(name, op.Context.Snapshot);
            if (type is null)
            {
                type = new GraphRelationshipTypeMetadata(Guid.NewGuid(), name);
                await _database.Catalog.SaveRelationshipTypeAsync(type.Value, op.Context, token).ConfigureAwait(false);
            }
            return type.Value.Id;
        }
        var label = _database.Catalog.FindLabel(name, op.Context.Snapshot);
        if (label is null)
        {
            label = new GraphLabelMetadata(Guid.NewGuid(), name);
            await _database.Catalog.SaveLabelAsync(label.Value, op.Context, token).ConfigureAwait(false);
        }
        return label.Value.Id;
    }

    private static DatabaseType StorageType(GqlSchemaPropertyType type) => type switch
    {
        GqlSchemaPropertyType.Int32 => DatabaseType.Int32, GqlSchemaPropertyType.Int64 => DatabaseType.Int64,
        GqlSchemaPropertyType.Float32 => DatabaseType.Float32, GqlSchemaPropertyType.Float64 => DatabaseType.Float64,
        GqlSchemaPropertyType.Decimal => DatabaseType.Decimal, GqlSchemaPropertyType.String => DatabaseType.String,
        GqlSchemaPropertyType.Boolean => DatabaseType.Boolean, GqlSchemaPropertyType.Int8 => DatabaseType.Int8,
        GqlSchemaPropertyType.Int16 => DatabaseType.Int16,
        _ => throw DatabaseException.CreateGraphFailure("CG03003", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ConstraintTypeUnsupported, type)),
    };

    private static void ValidatePropertyType(DatabaseType? type)
    {
        if (type is { } scalar && !GraphDatabase.SupportsPropertyType(scalar))
        { throw DatabaseException.CreateGraphFailure("CG03003", string.Format(CultureInfo.CurrentCulture, ErrorMessages.SchemaSavePropertyKeyAsyncDatabase, scalar)); }
    }

    private static string GeneratedName(GqlSchemaCommand command)
    {
        // Length-prefixed UTF-8 fields avoid delimiter ambiguities; aliases and variables do not affect identity.
        string Field(string value) => Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture) + ":" + value;
        string identity = Field(command.Kind.ToString()) + Field(command.Relationship ? "relationship" : "node") +
            Field(command.Definition ?? "") + Field(command.Property ?? "") + Field(command.Type?.ToString() ?? "exists");
        return command.Kind.ToString().ToLowerInvariant() + "_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private async ValueTask CreateIndexCoreAsync(GraphOperation op, string label, string name, string propertyKey, CancellationToken cancellationToken)
    {
        var metadata = await EnsureDefinitionAsync(op, label, false, cancellationToken).ConfigureAwait(false);
        if (_database.Catalog.FindIndex(name, op.Context.Snapshot) is not null ||
            _database.Catalog.FindConstraint(name, op.Context.Snapshot) is not null)
        {
            string message = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaNameDuplicate, name);
            throw new DatabaseException(message, [new Types.Diagnostic
            { Code = "CG03008", Message = message, Severity = Types.DiagnosticSeverity.Error }]);
        }
        await _database.Catalog.SaveIndexAsync(new GraphIndexMetadata(metadata, name, propertyKey), op.Context, cancellationToken).ConfigureAwait(false);
        if (!_database.Catalog.GetDeclaredPropertyKeys(metadata, op.Context.Snapshot).Any(key => key.Name == propertyKey))
        { await _database.Catalog.SavePropertyKeyAsync(new(metadata, propertyKey), op.Context, cancellationToken).ConfigureAwait(false); }
        using var memory = new GraphMemoryOwner(op.QueryBudget!.Memory);
        await _database.Store.CreateIndexAsync(label, propertyKey, op.Context, op.QueryBudget.Consume, memory.Add, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CreateConstraintCoreAsync(GraphOperation op, GraphConstraintMetadata constraint, CancellationToken cancellationToken)
    {
        await _database.Catalog.CreateConstraintAsync(constraint, op.Context, cancellationToken).ConfigureAwait(false);
        var latest = _database.LatestSnapshot(op.Context);
        using var memory = new GraphMemoryOwner(op.QueryBudget!.Memory);
        if (_database.Catalog.FindLabel(constraint.DefinitionId, latest) is { } label)
        {
            foreach (var node in _database.Store.StreamLabels([label.Name], latest, cancellationToken, op.QueryBudget.Consume, memory.Add))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Validate(node.Properties);
            }
        }
        else
        {
            var type = _database.Catalog.FindRelationshipType(constraint.DefinitionId, latest)!.Value;
            foreach (var edge in _database.Store.StreamRelationshipTypes([type.Name], latest, cancellationToken, op.QueryBudget.Consume, memory.Add))
            {
                Validate(edge.Properties);
            }
        }
        void Validate(IReadOnlyDictionary<string, object?> properties)
        {
            bool present = properties.TryGetValue(constraint.PropertyKey, out var value) && value is not null;
            if (constraint.Type is null ? !present : present && GraphDatabase.TypeOf(value) != constraint.Type.Value)
            {
                string message = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.ConstraintDataViolation, constraint.Name, constraint.PropertyKey);
                throw new DatabaseException(message, [new Types.Diagnostic
                { Code = "CG03010", Message = message, Severity = Types.DiagnosticSeverity.Error }]);
            }
        }
    }

    private async ValueTask DropConstraintCoreAsync(GraphOperation op, string name, CancellationToken cancellationToken)
    {
        if (_database.Catalog.FindConstraint(name, op.Context.Snapshot) is null) { throw Missing("Constraint", name); }
        await _database.Catalog.DeleteConstraintAsync(name, op.Context, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask DropIndexCoreAsync(GraphOperation op, string name, CancellationToken cancellationToken)
    {
        var index = _database.Catalog.FindIndex(name, op.Context.Snapshot) ?? throw Missing("Index", name);
        // Catalog performs ownership and stale-snapshot checks before the physical drop.
        await _database.Catalog.DeleteIndexAsync(index.LabelId, name, op.Context, cancellationToken).ConfigureAwait(false);
        var label = _database.Catalog.FindLabel(index.LabelId, op.Context.Snapshot)!.Value;
        await _database.Store.DropIndexAsync(label.Name, index.PropertyKey, op.Context, cancellationToken).ConfigureAwait(false);
    }

}
