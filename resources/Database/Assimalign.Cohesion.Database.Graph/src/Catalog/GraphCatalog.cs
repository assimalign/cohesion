using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Catalog;

/// <summary>The durable, snapshot-visible definitions of one logical graph database.</summary>
/// <remarks>
/// <para>
/// Mutations join the supplied transaction without committing it. The caller holds the
/// database's definition lock and handles any graph data affected by a definition change.
/// Existing schema-owned definitions refuse all alterations and drops. Initial marked
/// definitions support the ownership enforcement seam; compiled provisioning is separate.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> One sealed type with a private
/// constructor behind <see cref="Open"/>: it replaces the former <c>IGraphCatalog</c> interface,
/// the <c>GraphCatalog</c> static factory and their internal implementation (the triplet of
/// <c>database-area.md</c> rule 1).
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("GraphCatalog")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Catalog.GraphCatalog.DebugView))]
public sealed class GraphCatalog
{
    private readonly GraphStorage _storage;
    private readonly TransactionCoordinator _coordinator;
    private readonly object _sync = new();
    private readonly Dictionary<(byte Kind, Guid Parent, string Name), List<Reference>> _versions = new();
    private readonly Dictionary<Guid, HashSet<(byte Kind, string Name)>> _definitions = new();
    private readonly Dictionary<(byte Kind, Guid Parent), SortedSet<string>> _children = new();
    private readonly Dictionary<string, HashSet<(byte Kind, Guid Parent)>> _schemaNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Reference>> _propertyNames = new(StringComparer.Ordinal);

    // Per-instance fault/record-visit seam, reached only by this model's tests.
    internal Action? BeforeRecordRead { get; set; }
    internal Action? BeforeDirectoryKeyVisit { get; set; }

    private GraphCatalog(GraphStorage storage, TransactionCoordinator coordinator)
        => (_storage, _coordinator) = (storage, coordinator);

    /// <summary>Loads the catalog after shared recovery has scrubbed abandoned writers.</summary>
    /// <param name="storage">The database's graph storage.</param>
    /// <param name="coordinator">The coordinator bound to that storage.</param>
    /// <returns>The snapshot-visible catalog.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="GraphCatalogException">Persisted metadata is malformed.</exception>
    public static GraphCatalog Open(GraphStorage storage, TransactionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(coordinator);
        var catalog = new GraphCatalog(storage, coordinator);
        using var iterator = storage.GetUnitIterator(0);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            var record = GraphCatalogCodec.Decode(unit.Data.Span, unit.PageId, unit.SlotIndex);
            var (writer, _) = RecordVersionStamp.ReadStamps(unit.Data.Span);
            catalog.Add(record, new Reference(unit.PageId, unit.SlotIndex, writer, record.Id));
        }
        storage.CatalogVersionUndone = catalog.CreatedVersionUndone;
        return catalog;
    }

    private void CreatedVersionUndone(PageId page, int slot, ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length < 18 || bytes.Span[17] != GraphCatalogCodec.FormatVersion) { return; }
        var record = GraphCatalogCodec.Decode(bytes.Span, page, slot);
        var key = (record.Kind, record.Parent, record.Name);
        lock (_sync)
        {
            if (!_versions.TryGetValue(key, out var versions)) { return; }
            for (int i = versions.Count - 1; i >= 0; i--)
            {
                if (versions[i].PageId == page && versions[i].SlotIndex == slot) { RemoveStaleReference(key, versions, i); }
            }
        }
    }

    // Run before logical recovery can scrub any catalog record, including abandoned v1 records.
    internal static void EnsureFormat(GraphStorage storage)
    {
        using var iterator = storage.GetUnitIterator(0);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            GraphCatalogCodec.Decode(unit.Data.Span, unit.PageId, unit.SlotIndex);
        }
    }

    /// <summary>Finds a visible label by its ordinal name.</summary>
    /// <param name="name">The case-sensitive name.</param>
    /// <param name="snapshot">The reader's snapshot.</param>
    /// <returns>The definition, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="snapshot"/> is null.</exception>
    public GraphLabelMetadata? FindLabel(string name, TransactionSnapshot snapshot)
        => Find(1, Guid.Empty, name, snapshot)?.Record.Label;

    /// <summary>Finds a visible label by its stable identity.</summary>
    /// <param name="id">The definition identity.</param>
    /// <param name="snapshot">The reader's snapshot.</param>
    /// <returns>The visible definition, or null if absent or of another kind.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="GraphCatalogException">Persisted metadata is malformed.</exception>
    public GraphLabelMetadata? FindLabel(Guid id, TransactionSnapshot snapshot)
        => FindDefinition(id, snapshot, 1)?.Record.Label;

    /// <summary>Lists visible label definitions in ordinal name order.</summary>
    /// <param name="snapshot">The reader's snapshot.</param>
    /// <returns>The visible definitions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public IReadOnlyList<GraphLabelMetadata> GetLabels(TransactionSnapshot snapshot)
        => List(1, null, snapshot).Select(item => item.Record.Label!.Value).ToArray();

    /// <summary>Finds a visible relationship type by its ordinal name.</summary>
    /// <param name="name">The case-sensitive name.</param>
    /// <param name="snapshot">The reader's snapshot.</param>
    /// <returns>The definition, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="snapshot"/> is null.</exception>
    public GraphRelationshipTypeMetadata? FindRelationshipType(string name, TransactionSnapshot snapshot)
        => Find(2, Guid.Empty, name, snapshot)?.Record.RelationshipType;

    /// <summary>Finds a visible relationship type by its stable identity.</summary>
    /// <param name="id">The definition identity.</param>
    /// <param name="snapshot">The reader's snapshot.</param>
    /// <returns>The visible definition, or null if absent or of another kind.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="GraphCatalogException">Persisted metadata is malformed.</exception>
    public GraphRelationshipTypeMetadata? FindRelationshipType(Guid id, TransactionSnapshot snapshot)
        => FindDefinition(id, snapshot, 2)?.Record.RelationshipType;

    /// <summary>Lists visible relationship type definitions in ordinal name order.</summary>
    /// <param name="snapshot">The reader's snapshot.</param>
    /// <returns>The visible definitions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public IReadOnlyList<GraphRelationshipTypeMetadata> GetRelationshipTypes(TransactionSnapshot snapshot)
        => List(2, null, snapshot).Select(item => item.Record.RelationshipType!.Value).ToArray();

    /// <summary>Creates or alters a label definition in the supplied transaction.</summary>
    /// <param name="label">The complete definition.</param>
    /// <param name="context">The active logical transaction.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="ArgumentException">The definition is invalid.</exception>
    /// <exception cref="GraphCatalogException">The identity or name conflicts with an existing definition.</exception>
    /// <exception cref="GraphElementTooLargeException">The encoded catalog record exceeds one slotted-page record.</exception>
    /// <exception cref="DatabaseObjectLockedException">The existing definition is owned by a schema.</exception>
    public ValueTask SaveLabelAsync(GraphLabelMetadata label, TransactionContext context, CancellationToken cancellationToken = default)
        => SaveDefinitionAsync(new CatalogRecord(label: label), context, cancellationToken);

    /// <summary>Creates or alters a relationship type definition in the supplied transaction.</summary>
    /// <param name="relationshipType">The complete definition.</param>
    /// <param name="context">The active logical transaction.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="ArgumentException">The definition is invalid.</exception>
    /// <exception cref="GraphCatalogException">The identity or name conflicts with an existing definition.</exception>
    /// <exception cref="GraphElementTooLargeException">The encoded catalog record exceeds one slotted-page record.</exception>
    /// <exception cref="DatabaseObjectLockedException">The existing definition is owned by a schema.</exception>
    public ValueTask SaveRelationshipTypeAsync(GraphRelationshipTypeMetadata relationshipType, TransactionContext context, CancellationToken cancellationToken = default)
        => SaveDefinitionAsync(new CatalogRecord(relationshipType: relationshipType), context, cancellationToken);

    /// <summary>Drops a label and its property/index metadata in the supplied transaction.</summary>
    /// <param name="id">The stable definition identity.</param>
    /// <param name="context">The active logical transaction.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The definition is owned by a schema.</exception>
    public ValueTask DeleteLabelAsync(Guid id, TransactionContext context, CancellationToken cancellationToken = default)
        => DeleteDefinitionAsync(1, id, context, cancellationToken);

    /// <summary>Drops a relationship type and its property/index metadata in the supplied transaction.</summary>
    /// <param name="id">The stable definition identity.</param>
    /// <param name="context">The active logical transaction.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The definition is owned by a schema.</exception>
    public ValueTask DeleteRelationshipTypeAsync(Guid id, TransactionContext context, CancellationToken cancellationToken = default)
        => DeleteDefinitionAsync(2, id, context, cancellationToken);

    /// <summary>Lists a definition's property keys and effective named/unnamed constraints in ordinal name order.</summary>
    /// <param name="definitionId">The label or relationship-type identity.</param>
    /// <param name="snapshot">The visibility snapshot.</param>
    /// <returns>The visible property definitions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public IReadOnlyList<GraphPropertyKeyMetadata> GetPropertyKeys(Guid definitionId, TransactionSnapshot snapshot)
    {
        var declared = GetDeclaredPropertyKeys(definitionId, snapshot);
        lock (_sync)
        {
            if (!_children.ContainsKey((5, definitionId))) { return declared; }
        }
        var constraints = GetConstraints(definitionId, snapshot);
        if (constraints.Count == 0) { return declared; }
        var properties = declared.ToDictionary(item => item.Name, StringComparer.Ordinal);
        foreach (var constraint in constraints)
        {
            var property = properties.GetValueOrDefault(constraint.PropertyKey,
                new GraphPropertyKeyMetadata(definitionId, constraint.PropertyKey));
            properties[constraint.PropertyKey] = property with
            {
                Type = constraint.Type ?? property.Type,
                Required = property.Required || constraint.Type is null,
            };
        }
        return properties.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    internal IReadOnlyList<GraphPropertyKeyMetadata> GetDeclaredPropertyKeys(Guid definitionId, TransactionSnapshot snapshot)
        => List(3, definitionId, snapshot).Select(item => item.Record.Property!.Value).ToArray();

    // Tokens describe names a definition has had, independently of a particular entity's
    // current values. Consult the writer stamps already loaded by Add, without decoding
    // metadata or scanning definitions on the read path. Undone versions leave this directory.
    internal bool HasPropertyKey(string name, TransactionSnapshot snapshot)
    {
        lock (_sync)
        {
            if (!_propertyNames.TryGetValue(name, out var versions)) { return false; }
            foreach (var version in versions)
            {
                if (snapshot.IsVisible(version.Writer)) { return true; }
            }
            return false;
        }
    }

    internal void EnsurePropertyKeyCompatible(GraphPropertyKeyMetadata property, TransactionSnapshot snapshot)
    {
        if (property.Type is { } type && GetConstraints(property.DefinitionId, snapshot)
            .Any(item => item.PropertyKey == property.Name && item.Type is { } expected && expected != type))
        {
            throw ConstraintConflict(property.Name);
        }
    }

    /// <summary>Creates or alters property metadata on an existing mutable definition.</summary>
    /// <param name="property">The complete property definition.</param>
    /// <param name="context">The active logical transaction.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="ArgumentException">The property metadata is invalid.</exception>
    /// <exception cref="GraphCatalogException">The owning definition is absent, or a named type rule conflicts (CG03009).</exception>
    /// <exception cref="GraphElementTooLargeException">The encoded catalog record exceeds one slotted-page record.</exception>
    /// <exception cref="DatabaseObjectLockedException">The owning definition is schema-owned.</exception>
    public ValueTask SavePropertyKeyAsync(GraphPropertyKeyMetadata property, TransactionContext context, CancellationToken cancellationToken = default)
    {
        EnsureActive(context);
        var record = new CatalogRecord(property: property);
        GraphCatalogCodec.Validate(record);
        RequireMutableDefinition(property.DefinitionId, context.Snapshot, "ALTER");
        EnsureParentUnchanged(property.DefinitionId, context);
        EnsurePropertyKeyCompatible(property, Latest(context));
        return SaveAsync(record, EnsureUnchanged(3, property.DefinitionId, property.Name, context), context, cancellationToken);
    }

    /// <summary>Drops property metadata from an existing mutable definition.</summary>
    /// <param name="definitionId">The label or relationship-type identity.</param>
    /// <param name="name">The property key.</param>
    /// <param name="context">The active logical transaction.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="GraphCatalogException">The owning definition is absent.</exception>
    /// <exception cref="DatabaseObjectLockedException">The owning definition is schema-owned.</exception>
    public ValueTask DeletePropertyKeyAsync(Guid definitionId, string name, TransactionContext context, CancellationToken cancellationToken = default)
    {
        EnsureActive(context);
        RequireMutableDefinition(definitionId, context.Snapshot, "ALTER");
        EnsureParentUnchanged(definitionId, context);
        return DeleteAsync([EnsureUnchanged(3, definitionId, name, context)], context, cancellationToken);
    }

    /// <summary>Lists index definitions belonging to a label in ordinal name order.</summary>
    /// <param name="labelId">The label identity.</param>
    /// <param name="snapshot">The visibility snapshot.</param>
    /// <returns>The visible index definitions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public IReadOnlyList<GraphIndexMetadata> GetIndexes(Guid labelId, TransactionSnapshot snapshot)
        => List(4, labelId, snapshot).Select(item => item.Record.Index!.Value).ToArray();

    /// <summary>Finds an index by its database-wide, ordinal name.</summary>
    /// <param name="name">The schema-rule name.</param>
    /// <param name="snapshot">The visibility snapshot.</param>
    /// <returns>The visible index, or null when absent or a constraint owns the name.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="GraphCatalogException">Persisted metadata is malformed.</exception>
    public GraphIndexMetadata? FindIndex(string name, TransactionSnapshot snapshot)
        => FindSchemaRule(name, snapshot)?.Record.Index;

    /// <summary>Lists all database indexes in ordinal name order.</summary>
    /// <param name="snapshot">The visibility snapshot.</param>
    /// <returns>The visible indexes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public IReadOnlyList<GraphIndexMetadata> GetIndexes(TransactionSnapshot snapshot)
        => List(4, null, snapshot).Select(item => item.Record.Index!.Value).ToArray();

    /// <summary>Finds a constraint by its database-wide, ordinal name.</summary>
    /// <param name="name">The schema-rule name.</param>
    /// <param name="snapshot">The visibility snapshot.</param>
    /// <returns>The visible constraint, or null when absent or an index owns the name.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="GraphCatalogException">Persisted metadata is malformed.</exception>
    public GraphConstraintMetadata? FindConstraint(string name, TransactionSnapshot snapshot)
        => FindSchemaRule(name, snapshot)?.Record.Constraint;

    /// <summary>Lists all database constraints in ordinal name order.</summary>
    /// <param name="snapshot">The visibility snapshot.</param>
    /// <returns>The visible constraints.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public IReadOnlyList<GraphConstraintMetadata> GetConstraints(TransactionSnapshot snapshot)
        => List(5, null, snapshot).Select(item => item.Record.Constraint!.Value).ToArray();

    /// <summary>Lists constraints belonging to one definition in ordinal name order.</summary>
    /// <param name="definitionId">The owning label or relationship-type identity.</param>
    /// <param name="snapshot">The visibility snapshot.</param>
    /// <returns>The visible constraints.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public IReadOnlyList<GraphConstraintMetadata> GetConstraints(Guid definitionId, TransactionSnapshot snapshot)
        => List(5, definitionId, snapshot).Select(item => item.Record.Constraint!.Value).ToArray();

    /// <summary>Creates an independently named type or existence constraint.</summary>
    /// <param name="constraint">The constraint definition.</param>
    /// <param name="context">The active transaction; the caller holds the database writer lock and validates existing data.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="ArgumentException">The metadata is invalid.</exception>
    /// <exception cref="GraphCatalogException">The name is taken, the owner is missing, a constraint conflicts, or the snapshot is stale.</exception>
    /// <exception cref="DatabaseObjectLockedException">The owning definition is schema-owned.</exception>
    /// <exception cref="GraphElementTooLargeException">The encoded metadata exceeds one record.</exception>
    public async ValueTask CreateConstraintAsync(GraphConstraintMetadata constraint, TransactionContext context, CancellationToken cancellationToken = default)
    {
        EnsureActive(context);
        var record = new CatalogRecord(constraint: constraint);
        GraphCatalogCodec.Validate(record);
        RequireMutableDefinition(constraint.DefinitionId, context.Snapshot, "ALTER");
        EnsureParentUnchanged(constraint.DefinitionId, context);
        EnsureSchemaNameAvailable(record, context, allowExisting: false);
        var latest = Latest(context);
        if (GetConstraints(constraint.DefinitionId, latest).Any(item => item.PropertyKey == constraint.PropertyKey &&
            (item.Type is null) == (constraint.Type is null)) ||
            constraint.Type is { } type && GetPropertyKeys(constraint.DefinitionId, latest)
                .Any(item => item.Name == constraint.PropertyKey && item.Type is { } expected && expected != type))
        {
            throw ConstraintConflict(constraint.PropertyKey);
        }
        EnsureUnchanged(5, constraint.DefinitionId, constraint.Name, context);
        // A constraint introduces a known property key, independently of the rule's lifetime.
        // Keep unnamed rules untouched, and roll back new key discovery with a failed creation.
        if (Find(3, constraint.DefinitionId, constraint.PropertyKey, context.Snapshot) is null)
        {
            await SavePropertyKeyAsync(new GraphPropertyKeyMetadata(constraint.DefinitionId, constraint.PropertyKey),
                context, cancellationToken).ConfigureAwait(false);
        }
        await SaveAsync(record, null, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tombstones a named constraint without changing other constraints or property metadata.</summary>
    /// <param name="name">The database-wide constraint name.</param>
    /// <param name="context">The active transaction whose caller holds the database writer lock.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>Completion of the mutation; absent constraints are unchanged.</returns>
    /// <exception cref="GraphCatalogException">The snapshot is stale.</exception>
    /// <exception cref="DatabaseObjectLockedException">The owning definition is schema-owned.</exception>
    public ValueTask DeleteConstraintAsync(string name, TransactionContext context, CancellationToken cancellationToken = default)
    {
        EnsureActive(context);
        var prior = FindSchemaRule(name, context.Snapshot);
        var latest = FindSchemaRule(name, Latest(context));
        if (prior?.Reference != latest?.Reference) { throw Conflict(); }
        if (prior?.Record.Constraint is not { } constraint) { return ValueTask.CompletedTask; }
        RequireMutableDefinition(constraint.DefinitionId, context.Snapshot, "ALTER");
        EnsureParentUnchanged(constraint.DefinitionId, context);
        return DeleteAsync([prior], context, cancellationToken);
    }

    private static GraphCatalogException ConstraintConflict(string property)
        => new("CG03009", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ConstraintConflict, property)) { IsCallerError = true };

    /// <summary>Publishes named index metadata alongside the physical graph-store index.</summary>
    /// <param name="index">The complete index definition.</param>
    /// <param name="context">The active transaction that also owns the physical index change.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="ArgumentException">The index metadata is invalid.</exception>
    /// <exception cref="GraphCatalogException">The label is absent or the name identifies another property.</exception>
    /// <exception cref="GraphElementTooLargeException">The encoded catalog record exceeds one slotted-page record.</exception>
    /// <exception cref="DatabaseObjectLockedException">The label is schema-owned.</exception>
    public ValueTask SaveIndexAsync(GraphIndexMetadata index, TransactionContext context, CancellationToken cancellationToken = default)
    {
        EnsureActive(context);
        var record = new CatalogRecord(index: index);
        GraphCatalogCodec.Validate(record);
        RequireMutableDefinition(index.LabelId, context.Snapshot, "ALTER", labelOnly: true);
        EnsureParentUnchanged(index.LabelId, context);
        var prior = EnsureUnchanged(4, index.LabelId, index.Name, context);
        if (prior?.Record.Index is { } previous && previous.PropertyKey != index.PropertyKey)
        {
            throw new GraphCatalogException("CG03003", string.Format(CultureInfo.CurrentCulture, ErrorMessages.CatalogIndexPropertyMismatch,
                index.Name, previous.PropertyKey)) { IsCallerError = true };
        }
        EnsureSchemaNameAvailable(record, context, allowExisting: true);
        return SaveAsync(record, prior, context, cancellationToken);
    }

    /// <summary>Tombstones named index metadata alongside the physical graph-store index.</summary>
    /// <param name="labelId">The label identity.</param>
    /// <param name="name">The index name.</param>
    /// <param name="context">The active transaction that also owns the physical index change.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>A task representing the mutation.</returns>
    /// <exception cref="GraphCatalogException">The label is absent.</exception>
    /// <exception cref="DatabaseObjectLockedException">The label is schema-owned.</exception>
    public ValueTask DeleteIndexAsync(Guid labelId, string name, TransactionContext context, CancellationToken cancellationToken = default)
    {
        EnsureActive(context);
        RequireMutableDefinition(labelId, context.Snapshot, "ALTER", labelOnly: true);
        EnsureParentUnchanged(labelId, context);
        return DeleteAsync([EnsureUnchanged(4, labelId, name, context)], context, cancellationToken);
    }

    private ValueTask SaveDefinitionAsync(CatalogRecord record, TransactionContext context, CancellationToken cancellationToken)
    {
        EnsureActive(context);
        GraphCatalogCodec.Validate(record);
        var current = Find(record.Kind, Guid.Empty, record.Name, Latest(context));
        if (current is { } latest)
        {
            RefuseSchemaOwner(latest.Record, "ALTER");
        }
        var previous = EnsureUnchanged(record.Kind, Guid.Empty, record.Name, context);
        if (previous is { } found)
        {
            RefuseSchemaOwner(found.Record, "ALTER");
            if (found.Record.Id != record.Id)
            {
                throw new GraphCatalogException("CG03003", string.Format(CultureInfo.CurrentCulture, ErrorMessages.CatalogIdentityMismatch,
                    record.Name)) { IsCallerError = true };
            }
        }
        var latestSnapshot = Latest(context);
        // Confirm/prune the candidate before enforcing immutable identity routing. An invisible
        // tombstoned version may still be needed by an older snapshot, so its id cannot rename.
        FindDefinition(record.Id, latestSnapshot);
        lock (_sync)
        {
            if (_definitions.TryGetValue(record.Id, out var keys) && keys.Any(key => key != (record.Kind, record.Name)))
            {
                throw new GraphCatalogException("CG03003", ErrorMessages.CatalogIdentityReused) { IsCallerError = true };
            }
        }
        return SaveAsync(record, previous, context, cancellationToken);
    }

    private ValueTask DeleteDefinitionAsync(byte kind, Guid id, TransactionContext context, CancellationToken cancellationToken)
    {
        EnsureActive(context);
        var previous = FindDefinition(id, context.Snapshot, kind) ?? default;
        var latestSnapshot = Latest(context);
        var latest = FindDefinition(id, latestSnapshot, kind) ?? default;
        if (latest.Record is not null)
        {
            RefuseSchemaOwner(latest.Record, "DROP");
        }
        if (previous.Record?.Id != latest.Record?.Id || previous.Reference != latest.Reference)
        {
            throw Conflict();
        }
        if (previous.Record is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        RefuseSchemaOwner(previous.Record, "DROP");
        var records = new List<Found?> { previous };
        foreach (byte childKind in new byte[] { 3, 4, 5 })
        {
            var children = List(childKind, id, context.Snapshot);
            var latestChildren = List(childKind, id, latestSnapshot);
            if (!children.Select(item => item.Reference).SequenceEqual(latestChildren.Select(item => item.Reference)))
            {
                throw Conflict();
            }
            records.AddRange(children.Select(item => (Found?)item));
        }
        return DeleteAsync(records, context, cancellationToken);
    }

    private TransactionSnapshot Latest(TransactionContext context)
        => new(context.Sequence, TransactionSequence.None, new TransactionSequence(ulong.MaxValue),
            _coordinator.GetOpenContexts().Where(item => item.State == TransactionState.Active).Select(item => item.Sequence));

    private Found? FindSchemaRule(string name, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            if (!_schemaNames.TryGetValue(name, out var routes)) { return null; }
            foreach (var route in routes.ToArray())
            {
                if (Find(route.Kind, route.Parent, name, snapshot) is { } found) { return found; }
            }
            return null;
        }
    }

    private void EnsureSchemaNameAvailable(CatalogRecord record, TransactionContext context, bool allowExisting)
    {
        var previous = FindSchemaRule(record.Name, context.Snapshot);
        var latest = FindSchemaRule(record.Name, Latest(context));
        if (previous?.Reference != latest?.Reference) { throw Conflict(); }
        if (latest is { } found && (!allowExisting || found.Record.Kind != record.Kind || found.Record.Parent != record.Parent))
        {
            throw new GraphCatalogException("CG03008", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.SchemaNameDuplicate, record.Name)) { IsCallerError = true };
        }
    }

    private Found? EnsureUnchanged(byte kind, Guid parent, string name, TransactionContext context)
    {
        var previous = Find(kind, parent, name, context.Snapshot);
        var latest = Find(kind, parent, name, Latest(context));
        if (previous?.Reference != latest?.Reference)
        {
            throw Conflict();
        }
        return previous;
    }

    private void EnsureParentUnchanged(Guid id, TransactionContext context)
    {
        var latestSnapshot = Latest(context);
        var previous = FindDefinition(id, context.Snapshot) ?? default;
        var latest = FindDefinition(id, latestSnapshot) ?? default;
        if (latest.Record is not null)
        {
            RefuseSchemaOwner(latest.Record, "ALTER");
        }
        if (previous.Record?.Id != latest.Record?.Id || previous.Reference != latest.Reference)
        {
            throw Conflict();
        }
    }

    internal static GraphCatalogException Conflict()
        => new("CG04007", ErrorMessages.GraphCatalogConflictGraphCatalog) { IsConflict = true };

    private void RequireMutableDefinition(Guid id, TransactionSnapshot snapshot, string operation, bool labelOnly = false)
    {
        var found = FindDefinition(id, snapshot, labelOnly ? (byte)1 : null) ?? default;
        if (found.Record is null)
        {
            throw new GraphCatalogException("CG03003", ErrorMessages.CatalogOwnerMissing) { IsCallerError = true };
        }
        RefuseSchemaOwner(found.Record, operation);
    }

    private static void RefuseSchemaOwner(CatalogRecord record, string operation)
    {
        if (record.Owner == DatabaseObjectOwner.Schema)
        {
            throw new DatabaseObjectLockedException(record.Name, record.OwningSchema!,
                operation + (record.Kind == 1 ? " LABEL" : " RELATIONSHIP TYPE"));
        }
    }

    private async ValueTask SaveAsync(CatalogRecord record, Found? previous, TransactionContext context, CancellationToken cancellationToken)
    {
        byte[] bytes = GraphCatalogCodec.Encode(record, context.Sequence);
        var location = await _coordinator.ApplyStatementAsync(context, bracket =>
        {
            if (previous is { } old)
            {
                Tombstone(bracket, old.Reference, context.Sequence);
            }
            var inserted = _storage.InsertEntry(bracket, bytes);
            _coordinator.VersionStore.RecordCreated(context.Sequence, inserted.PageId, inserted.SlotIndex);
            return ValueTask.FromResult(inserted);
        }, durable: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            Add(record, new Reference(location.PageId, location.SlotIndex, context.Sequence, record.Id));
        }
    }

    private async ValueTask DeleteAsync(IEnumerable<Found?> records, TransactionContext context, CancellationToken cancellationToken)
    {
        await _coordinator.ApplyStatementAsync(context, bracket =>
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record is { } found)
                {
                    Tombstone(bracket, found.Reference, context.Sequence);
                }
            }
            return ValueTask.FromResult(true);
        }, durable: false, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void Tombstone(StorageTransaction bracket, Reference reference, TransactionSequence writer)
    {
        var bytes = _storage.ReadEntry(reference.PageId, reference.SlotIndex);
        _storage.UpdateEntry(bracket, reference.PageId, reference.SlotIndex, RecordVersionStamp.WithDeleter(bytes.Span, writer));
        _coordinator.VersionStore.RecordTombstoned(writer, reference.PageId, reference.SlotIndex);
    }

    private void Add(CatalogRecord record, Reference reference)
    {
        var key = (record.Kind, record.Parent, record.Name);
        if (!_versions.TryGetValue(key, out var versions))
        {
            _versions[key] = versions = [];
        }
        for (int index = versions.Count - 1; index >= 0; index--)
        {
            if (versions[index].PageId == reference.PageId && versions[index].SlotIndex == reference.SlotIndex)
            {
                RemoveStaleReference(key, versions, index);
            }
        }
        _versions[key] = versions;
        versions.Add(reference);
        if (record.Kind == 3)
        {
            if (!_propertyNames.TryGetValue(record.Name, out var properties)) { _propertyNames[record.Name] = properties = []; }
            properties.Add(reference);
        }
        if (record.Kind is 4 or 5)
        {
            if (!_schemaNames.TryGetValue(record.Name, out var routes)) { _schemaNames[record.Name] = routes = []; }
            routes.Add((record.Kind, record.Parent));
        }
        if (record.Kind <= 2)
        {
            if (!_definitions.TryGetValue(record.Id, out var keys))
            {
                _definitions[record.Id] = keys = [];
            }
            keys.Add((record.Kind, record.Name));
        }
        else
        {
            var parent = (record.Kind, record.Parent);
            if (!_children.TryGetValue(parent, out var names))
            {
                _children[parent] = names = new SortedSet<string>(StringComparer.Ordinal);
            }
            names.Add(record.Name);
        }
    }

    private Found? FindDefinition(Guid id, TransactionSnapshot snapshot, byte? kind = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            if (!_definitions.TryGetValue(id, out var keys))
            {
                return null;
            }
            // A dropped name may be recreated with another identity. The directory is only a
            // candidate selector: the visible record must still have the requested identity.
            Found? result = null;
            foreach (var key in keys.ToArray())
            {
                if (kind is { } expected && key.Kind != expected) { continue; }
                var found = Find(key.Kind, Guid.Empty, key.Name, snapshot);
                if (found?.Record.Id == id && (result is null || found.Value.Reference.Writer > result.Value.Reference.Writer))
                {
                    result = found;
                }
            }
            return result;
        }
    }

    private void RemoveStaleReference((byte Kind, Guid Parent, string Name) key, List<Reference> versions, int index)
    {
        var removed = versions[index];
        versions.RemoveAt(index);
        if (key.Kind == 3 && _propertyNames.TryGetValue(key.Name, out var properties))
        {
            properties.Remove(removed);
            if (properties.Count == 0) { _propertyNames.Remove(key.Name); }
        }
        if (key.Kind <= 2 && !versions.Any(item => item.Id == removed.Id) &&
            _definitions.TryGetValue(removed.Id, out var keys))
        {
            keys.Remove((key.Kind, key.Name));
            if (keys.Count == 0) { _definitions.Remove(removed.Id); }
        }
        if (versions.Count == 0)
        {
            _versions.Remove(key);
            if (key.Kind is 4 or 5 && _schemaNames.TryGetValue(key.Name, out var routes))
            {
                routes.Remove((key.Kind, key.Parent));
                if (routes.Count == 0) { _schemaNames.Remove(key.Name); }
            }
            var parent = (key.Kind, key.Parent);
            if (_children.TryGetValue(parent, out var names))
            {
                names.Remove(key.Name);
                if (names.Count == 0) { _children.Remove(parent); }
            }
        }
    }

    private Found? Find(byte kind, Guid parent, string name, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            var key = (kind, parent, name);
            if (!_versions.TryGetValue(key, out var versions))
            {
                return null;
            }
            Found? result = null;
            for (int index = versions.Count - 1; index >= 0; index--)
            {
                var reference = versions[index];
                BeforeRecordRead?.Invoke();
                // A reclaimed location (a deleted or reverted slot, a freed page, a page
                // reused by another owner or as a non-data page) is a stale reference;
                // CRC and I/O errors propagate (#1342).
                if (!_storage.TryReadRecord(reference.PageId, reference.SlotIndex, 0, out var bytes))
                {
                    RemoveStaleReference(key, versions, index);
                    continue;
                }
                if (bytes.Length < RecordVersionStamp.HeaderSize + 2)
                {
                    GraphCatalogCodec.Decode(bytes.Span, reference.PageId, reference.SlotIndex);
                }
                var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes.Span);
                if (writer != reference.Writer)
                {
                    RemoveStaleReference(key, versions, index);
                    continue;
                }
                var record = GraphCatalogCodec.Decode(bytes.Span, reference.PageId, reference.SlotIndex);
                if ((record.Kind, record.Parent, record.Name) != key || record.Id != reference.Id)
                {
                    RemoveStaleReference(key, versions, index);
                    continue;
                }
                if (snapshot.IsVisible(writer) && (deleter == TransactionSequence.None || !snapshot.IsVisible(deleter))
                    && (result is null || writer > result.Value.Reference.Writer))
                {
                    result = new Found(reference, record);
                }
            }
            return result;
        }
    }

    private IReadOnlyList<Found> List(byte kind, Guid? parent, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            var result = new List<Found>();
            var keys = parent is { } owner
                ? _children.TryGetValue((kind, owner), out var names)
                    ? names.Select(name => (Kind: kind, Parent: owner, Name: name)).ToArray() : []
                : _versions.Keys.Where(key => key.Kind == kind).OrderBy(key => key.Name, StringComparer.Ordinal).ToArray();
            foreach (var key in keys)
            {
                BeforeDirectoryKeyVisit?.Invoke();
                if (Find(key.Kind, key.Parent, key.Name, snapshot) is { } found)
                {
                    result.Add(found);
                }
            }
            return result;
        }
    }

    private static void EnsureActive(TransactionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.State != TransactionState.Active)
        {
            throw new InvalidOperationException(ErrorMessages.GraphCatalogEnsureActiveFailure);
        }
    }

    private readonly record struct Reference(PageId PageId, int SlotIndex, TransactionSequence Writer, Guid Id);

    private readonly record struct Found(Reference Reference, CatalogRecord Record);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Catalog.GraphCatalog value)
        {
            _ = value;
        }
    }
}
