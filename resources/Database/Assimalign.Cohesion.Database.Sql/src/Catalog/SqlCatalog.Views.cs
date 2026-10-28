using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    private const int viewRecordKind = 17;
    private readonly Dictionary<string, DefinitionSlot<SqlCatalogView>> _views = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets an immutable capture of persisted user views.</summary>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public IReadOnlyList<SqlCatalogView> Views
    { get { lock (_sync) { ThrowIfOffline(); return Array.AsReadOnly(_views.Values.Select(slot => slot.Value).ToArray()); } } }

    /// <summary>Finds a persisted view by SQL namespace and relation name.</summary>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The relation name.</param>
    /// <param name="view">The immutable definition when found.</param>
    /// <returns>Whether the view exists.</returns>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public bool TryGetView(string schema, string name, out SqlCatalogView view)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            if (_views.Count != 0 && _views.TryGetValue(SqlObjectIdentity.View(schema, name), out var slot)) { view = slot.Value; return true; }
            view = null!; return false;
        }
    }

    /// <summary>Publishes a new view or replaces a view with a compatible output contract.</summary>
    /// <param name="view">The engine-validated canonical definition.</param>
    /// <param name="orReplace">Whether an existing view may be replaced.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>The completed durable publication.</returns>
    /// <exception cref="ArgumentNullException">The view is null.</exception>
    /// <exception cref="SqlCatalogException">A name collides, a dependency changed, replacement is incompatible or the record exceeds 8,092 bytes.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageException">Storage cannot persist the change.</exception>
    public ValueTask CreateViewAsync(SqlCatalogView view, bool orReplace = false, CancellationToken cancellationToken = default)
        => CreateViewCoreAsync(view, orReplace, null, cancellationToken);

    /// <summary>Publishes a view and its declared-script receipt in one transaction.</summary>
    /// <param name="view">The engine-validated canonical definition.</param>
    /// <param name="orReplace">Whether an existing view may be replaced.</param>
    /// <param name="publication">The exact preceding script receipt and generation.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>The completed durable publication.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="SqlCatalogException">The definition is invalid or the publication is stale.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageException">Storage cannot persist the change.</exception>
    public ValueTask CreateViewAsync(SqlCatalogView view, bool orReplace, SqlCatalogScriptPublication publication, CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(publication); return CreateViewCoreAsync(view, orReplace, publication, cancellationToken); }

    private ValueTask CreateViewCoreAsync(SqlCatalogView view, bool orReplace, SqlCatalogScriptPublication? publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        cancellationToken.ThrowIfCancellationRequested();
        var record = new DatabaseKeyWriter().AppendInt32(viewRecordKind)
            .AppendString(JsonSerializer.Serialize(SqlViewRecord.From(view), SqlCatalogJsonContext.Default.SqlViewRecord), Collation.Binary).ToArray();
        if (record.Length > SlottedPage.MaxRecordSize) { throw new SqlCatalogException("CS03033", ErrorMessages.ViewRecordTooLarge); }
        lock (_sync)
        {
            ThrowIfOffline();
            string key = SqlObjectIdentity.View(view.Schema, view.Name);
            if (_sequences.ContainsKey((view.Schema, view.Name))) { throw RelationNameCollision(view.Schema, view.Name); }
            if (_tables.ContainsKey((view.Schema, view.Name))) { throw new SqlCatalogException("CS03033", ErrorMessages.ViewTargetIsTable); }
            _views.TryGetValue(key, out var previous);
            if (previous is not null && !orReplace) { throw new SqlCatalogException("CS03033", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ViewExists, view.Schema, view.Name)); }
            if (!_namespaces.ContainsKey(view.Schema)) { throw new SqlCatalogException("CS03033", string.Format(CultureInfo.CurrentCulture, ErrorMessages.EngineNamespaceMissing, view.Schema)); }
            if (previous is not null)
            {
                var old = previous.Value.Columns;
                if (view.Columns.Count < old.Count || old.Where((column, index) => !SameViewColumn(column, view.Columns[index])).Any())
                { throw new SqlCatalogException("CS03033", ErrorMessages.ViewReplacementInvalid); }
            }
            foreach (ulong id in view.TableDependencies)
            { if (FindTableByObjectId(id) is null) { throw new SqlCatalogException("CS03033", ErrorMessages.ViewDependencyChanged); } }
            foreach (var dependency in view.Dependencies)
            {
                if (dependency.Equals(key, StringComparison.OrdinalIgnoreCase) || dependency.StartsWith("V:", StringComparison.Ordinal) && Reaches(dependency, key, new(StringComparer.OrdinalIgnoreCase)))
                { throw new SqlCatalogException("CS03033", ErrorMessages.ViewCycle); }
                if (dependency.StartsWith("V:", StringComparison.Ordinal) && !_views.ContainsKey(dependency)) { throw new SqlCatalogException("CS03033", ErrorMessages.ViewDependencyChanged); }
                if (dependency.StartsWith("C:", StringComparison.Ordinal) && !_tables.Values.Any(slot => slot.Table.Columns.Any(column => SqlObjectIdentity.Column(slot.Table.ObjectId, column.Name).Equals(dependency, StringComparison.OrdinalIgnoreCase))))
                { throw new SqlCatalogException("CS03033", ErrorMessages.ViewDependencyChanged); }
            }
            using var transaction = _storage.BeginTransaction();
            var location = UpsertRecord(transaction, previous?.Location, record);
            CommitCatalogChange(transaction, publication: publication, actualObject: key, createsObject: true);
            _views[key] = new(view, location);
            return default;
        }
    }

    private bool Reaches(string current, string target, HashSet<string> visited)
    {
        var pending = new Stack<string>();
        pending.Push(current);
        while (pending.TryPop(out var key))
        {
            if (key.Equals(target, StringComparison.OrdinalIgnoreCase)) { return true; }
            if (!visited.Add(key) || !_views.TryGetValue(key, out var slot)) { continue; }
            foreach (var dependency in slot.Value.Dependencies)
            { if (dependency.StartsWith("V:", StringComparison.Ordinal)) { pending.Push(dependency); } }
        }
        return false;
    }

    private static bool SameViewColumn(SqlCatalogColumn left, SqlCatalogColumn right)
        => left.Name.Equals(right.Name, StringComparison.Ordinal) && left.Type.Type == right.Type.Type &&
            left.Type.MaxLength == right.Type.MaxLength && left.Type.Precision == right.Type.Precision && left.Type.Scale == right.Type.Scale &&
            left.Collation == right.Collation && string.Equals(left.DomainName, right.DomainName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Drops a view, optionally cascading to dependent views.</summary>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The view name.</param>
    /// <param name="cascade">Whether dependent views are removed.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>The completed durable removal.</returns>
    /// <exception cref="SqlCatalogException">The target is absent, a table, or has dependent views under RESTRICT.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageException">Storage cannot persist the change.</exception>
    public ValueTask DropViewAsync(string schema, string name, bool cascade = false, CancellationToken cancellationToken = default)
        => DropViewCoreAsync(schema, name, cascade, null, cancellationToken);

    /// <summary>Drops a view and commits its declared-script receipt, including verified absence.</summary>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The view name.</param>
    /// <param name="cascade">Whether dependent views are removed.</param>
    /// <param name="publication">The exact preceding receipt and generation.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>The completed durable removal.</returns>
    /// <exception cref="ArgumentNullException">The publication is null.</exception>
    /// <exception cref="SqlCatalogException">The target is invalid, has dependents, or the publication is stale.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageException">Storage cannot persist the change.</exception>
    public ValueTask DropViewAsync(string schema, string name, bool cascade, SqlCatalogScriptPublication publication, CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(publication); return DropViewCoreAsync(schema, name, cascade, publication, cancellationToken); }

    private ValueTask DropViewCoreAsync(string schema, string name, bool cascade, SqlCatalogScriptPublication? publication, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            string key = SqlObjectIdentity.View(schema, name);
            if (_tables.ContainsKey((schema, name))) { throw new SqlCatalogException("CS03033", ErrorMessages.ViewTargetIsTable); }
            if (!_views.TryGetValue(key, out var slot))
            {
                if (publication is not null) { CommitScriptAbsence(publication, key); return default; }
                throw new SqlCatalogException("CS03033", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ViewMissing, schema, name));
            }
            var dropped = _views.Where(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || Reaches(pair.Key, key, new(StringComparer.OrdinalIgnoreCase))).ToArray();
            if (!cascade && dropped.Length > 1) { RefuseViewDependency(dropped.First(pair => !pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value.Value, slot.Value.Schema + "." + slot.Value.Name); }
            using var transaction = _storage.BeginTransaction();
            foreach (var pair in dropped) { _storage.DeleteRow(transaction, pair.Value.Location.PageId, pair.Value.Location.SlotIndex); }
            CommitCatalogChange(transaction, publication: publication, actualObject: SqlObjectIdentity.View(slot.Value.Schema, slot.Value.Name));
            foreach (var pair in dropped) { _views.Remove(pair.Key); }
            return default;
        }
    }

    private void EnsureNoViewReads(SqlCatalogTable table, string? column = null, bool rename = false)
    {
        var dependent = _views.Values.Select(slot => slot.Value).FirstOrDefault(view => column is null
            ? view.TableDependencies.Contains(table.ObjectId)
            : view.Dependencies.Contains(SqlObjectIdentity.Column(table.ObjectId, column), StringComparer.OrdinalIgnoreCase));
        if (dependent is not null) { RefuseViewDependency(dependent, table.Schema + "." + table.Name + (column is null ? "" : "." + column), rename); }
    }

    private static void RefuseViewDependency(SqlCatalogView view, string target = "the object", bool rename = false)
        => throw new SqlCatalogException("CS03302", rename
            ? string.Format(CultureInfo.CurrentCulture, ErrorMessages.EngineViewDependent, view.Schema, view.Name)
            : string.Format(CultureInfo.CurrentCulture, ErrorMessages.EngineViewDropDependent, target, view.Schema, view.Name));

    private void ValidateViewDirectory(IReadOnlyDictionary<ulong, SqlCatalogTable> tables)
    {
        foreach (var pair in _views)
        {
            var view = pair.Value.Value;
            if (_tables.ContainsKey((view.Schema, view.Name)) || !_namespaces.ContainsKey(view.Schema) || view.TableDependencies.Any(id => !tables.ContainsKey(id)) ||
                view.Dependencies.Where(key => key.StartsWith("V:", StringComparison.Ordinal)).Any(key => !_views.ContainsKey(key) || Reaches(key, pair.Key, new(StringComparer.OrdinalIgnoreCase))) ||
                view.Dependencies.Where(key => key.StartsWith("C:", StringComparison.Ordinal)).Any(key => !tables.Values.Any(table => table.Columns.Any(column => SqlObjectIdentity.Column(table.ObjectId, column.Name).Equals(key, StringComparison.OrdinalIgnoreCase)))))
            { throw new SqlCatalogException("CS03033", ErrorMessages.ViewMetadataInvalid); }
        }
    }

    private void LoadView(string json, (PageId PageId, int SlotIndex) location)
    {
        try
        {
            var view = (JsonSerializer.Deserialize(json, SqlCatalogJsonContext.Default.SqlViewRecord) ?? throw new JsonException()).ToView();
            if (!_views.TryAdd(SqlObjectIdentity.View(view.Schema, view.Name), new(view, location))) { throw new JsonException(); }
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NullReferenceException)
        { throw new SqlCatalogException("CS03033", ErrorMessages.ViewMetadataInvalid); }
    }
}
