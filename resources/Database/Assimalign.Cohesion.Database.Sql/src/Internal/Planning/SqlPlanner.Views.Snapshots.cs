using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private static readonly ConditionalWeakTable<SqlCatalog, ViewImageDirectory> _viewImages = new();

    private ViewImage? ViewImageState
    {
        get => _queryScopes?.ViewImage;
        set => (_queryScopes ??= new()).ViewImage = value;
    }

    // The generation fast path allocates nothing. Equivalent immutable captures share
    // the same bound bodies, including captures retained by snapshot transactions.
    private ViewImage CurrentViewImage()
    {
        if (ViewImageState is { } retained) { return retained; }
        var directory = _viewImages.GetValue(_catalog, _ => new());
        lock (directory.Sync)
        {
            var generation = _catalog.BindingGeneration;
            if (directory.Current is { } current && directory.Generation == generation) { return current; }
            var snapshot = _catalog.CaptureSnapshot();
            var functions = new SqlFunctionEnvironment(_functions.Catalog, _functions.Database, _functions.Types);
            var image = FindViewImage(directory, snapshot, () => new SqlBindingEntry<SqlFunctionEnvironment>(functions));
            directory.Current = image;
            directory.Generation = generation;
            return image;
        }
    }

    private static ViewImage FindViewImage(ViewImageDirectory directory, SqlCatalogSnapshot snapshot,
        Func<SqlBindingEntry<SqlFunctionEnvironment>> functions)
    {
        if (directory.Captures.TryGetValue(snapshot, out var retained)) { return retained; }
        directory.Images.RemoveAll(weak => !weak.TryGetTarget(out _));
        foreach (var weak in directory.Images)
        {
            if (weak.TryGetTarget(out var candidate) && SameImage(candidate.Snapshot, snapshot))
            {
                directory.Captures.Add(snapshot, candidate);
                return candidate;
            }
        }
        var image = new ViewImage(snapshot, functions());
        directory.Images.Add(new(image));
        directory.Captures.Add(snapshot, image);
        return image;
    }

    private static bool SameImage(SqlCatalogSnapshot left, SqlCatalogSnapshot right)
        => left.DefaultCollation == right.DefaultCollation && left.Tables.SequenceEqual(right.Tables) &&
            left.Views.SequenceEqual(right.Views) && left.Functions.SequenceEqual(right.Functions) &&
            left.Domains.SequenceEqual(right.Domains) && left.Namespaces.SequenceEqual(right.Namespaces);

    private bool TryReadView(string schema, string name, out SqlCatalogView view)
    {
        if (ViewImageState is not { } image) { return _catalog.TryGetView(schema, name, out view!); }
        foreach (var candidate in image.Snapshot.Views)
        {
            if (candidate.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) && candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { view = candidate; return true; }
        }
        view = null!;
        return false;
    }

    private bool HasReadNamespace(string schema)
    {
        if (ViewImageState is not { } image) { return SqlNamespaces.Find(_catalog, schema) is not null; }
        foreach (var candidate in image.Snapshot.Namespaces)
        { if (candidate.Name.Equals(schema, StringComparison.OrdinalIgnoreCase)) { return true; } }
        return false;
    }

    private bool TryReadTable(string schema, string name, out SqlCatalogTable table)
        => ViewImageState is { } image ? image.Snapshot.TryGetTable(schema, name, out table!) : _catalog.TryGetTable(schema, name, out table!);

    private IReadOnlyList<SqlCatalogIndex> ReadIndexes(ulong objectId)
        => ViewImageState is { } image ? image.Snapshot.GetIndexes(objectId) : _catalog.GetIndexes(objectId);

    [global::System.Diagnostics.DebuggerDisplay("ViewImageDirectory")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanner.ViewImageDirectory.DebugView))]
    private sealed class ViewImageDirectory
    {
        internal object Sync { get; } = new();
        internal (ulong Definition, ulong Storage) Generation { get; set; }
        internal ViewImage? Current { get; set; }
        internal List<WeakReference<ViewImage>> Images { get; } = new();
        internal ConditionalWeakTable<SqlCatalogSnapshot, ViewImage> Captures { get; } = new();

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanner.ViewImageDirectory value)
            {
                _ = value;
            }
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("ViewImage")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanner.ViewImage.DebugView))]
    internal sealed class ViewImage
    {
        internal ViewImage(SqlCatalogSnapshot snapshot, SqlBindingEntry<SqlFunctionEnvironment> functions)
        { Snapshot = snapshot; Functions = functions; }
        internal SqlCatalogSnapshot Snapshot { get; }
        internal SqlBindingEntry<SqlFunctionEnvironment> Functions { get; }
        internal ViewPlans Plans { get; } = new();

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanner.ViewImage value)
            {
                _ = value;
            }
        }
    }
}
