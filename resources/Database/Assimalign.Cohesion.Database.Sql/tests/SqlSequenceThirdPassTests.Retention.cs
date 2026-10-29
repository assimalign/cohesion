using System.Threading;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSequenceThirdPassTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Deferred collation: LIMIT uses uncharged full retention when top N cannot bound the sort")]
    public void Collect_DeferredCollationLimit_ShouldMatchUnboundedAccounting()
    {
        // Arrange: the collation defers resolution, so even LIMIT 1 needs all 128 candidates.
        var collation = new SqlBoundCollation(new SqlDeferredCollation(default, default, Collation.Binary));
        var order = new[] { new SqlBoundOrdering(new SqlBoundSlot(0), false, false, collation) };
        var limit = new SqlRowCount(null, "LIMIT", null, 1);
        var offset = new SqlRowCount(null, "OFFSET", null, 3);
        var metrics = new SqlStatementMetrics();
        var ordinary = new SqlRowCollector(order, null, limit, offset, CancellationToken.None);
        var sequence = new SqlRowCollector(order, null, limit, offset, CancellationToken.None, metrics);

        // Act
        for (int value = 128; value > 0; value--)
        {
            ordinary.Add([value], [value]);
            sequence.Add([value], [value]);
        }

        // Assert: deferral includes the three OFFSET rows for projection before discard.
        sequence.RetainedCount.ShouldBe(128);
        var selected = sequence.Finish(includeOffset: true);
        selected.Count.ShouldBe(4);
        for (int index = 0; index < selected.Count; index++) { selected[index].ShouldBe(new object?[] { index + 1 }); }
        selected[3].ShouldBe(ordinary.Finish().ShouldHaveSingleItem());
        metrics.PeakOrderedRows.ShouldBe(128);
        metrics.PeakHashMemoryBytes.ShouldBe(0);
        metrics.HashMemoryBytes.ShouldBe(0);
        _output.WriteLine("LIMIT=1; OFFSET=3; retained=128; selected for projection=4; charged peak=0; final grant=0");
    }
}
