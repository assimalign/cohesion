using System;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal static class SqlViewBindingFault
{
    internal static void Inject(SqlPlanner planner, SqlCatalogView view, Exception failure)
    {
        var plans = CurrentViewImage(planner).Plans;
        lock (plans.Sync) { plans.Entries[view] = new SqlBindingEntry<SqlPlanner.ViewPlan>(_ => throw failure); }
    }

    internal static void Clear(SqlPlanner planner)
    {
        var plans = CurrentViewImage(planner).Plans;
        lock (plans.Sync) { plans.Entries.Clear(); }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CurrentViewImage")]
    private static extern SqlPlanner.ViewImage CurrentViewImage(SqlPlanner planner);
}
