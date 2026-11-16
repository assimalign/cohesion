using System.Collections.Generic;
using System.Threading;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

public sealed class StructureDirectoryTests
{
    [Fact(DisplayName = "Cohesion Test [Indexing] - Heterogeneous structures share format, purge, bounded pruning and retirement dispatch (#1808)")]
    public void Maintenance_DifferentStructures_ShouldShareDirectoryAndPageGrant()
    {
        // Arrange.
        var directory = new IndexStructureDirectory();
        var posting = new PostingStructure();
        var vector = new VectorStructure();
        directory.Add(posting);
        directory.Add(vector);
        var writers = new HashSet<TransactionSequence> { new(7) };

        // Act.
        IndexStructureDirectory.EnsureFormat([posting, vector]);
        directory.PurgeWriters(null!, writers, CancellationToken.None).ShouldBe(2);
        directory.Prune(new(1000), 64, CancellationToken.None).ShouldBe(64);
        directory.Prune(new(1000), 64, CancellationToken.None).ShouldBe(64);
        directory.Retire(1, "posting");
        directory.PurgeWriters(null!, writers, CancellationToken.None).ShouldBe(1);

        // Assert: the second structure receives the unused part of the second grant.
        posting.Formats.ShouldBe(1);
        vector.Formats.ShouldBe(1);
        posting.Pruned.ShouldBe(100);
        vector.Pruned.ShouldBe(28);
        posting.Purges.ShouldBe(1);
        vector.Purges.ShouldBe(2);
        posting.Retirements.ShouldBe(1);
        vector.Retirements.ShouldBe(0);
    }

    private abstract class TestStructure : IndexStructure
    {
        protected TestStructure(ulong id, string name) : base(id, name) { }
        internal int Formats { get; private set; }
        internal int Pruned { get; private set; }
        internal int Purges { get; private set; }
        internal int Retirements { get; private set; }
        protected override void EnsureFormatCore() => Formats++;
        protected override long PurgeWritersCore(StorageTransaction bracket, IReadOnlySet<TransactionSequence> writers)
        { Purges++; return 1; }
        protected override (long Removed, bool Completed) PruneCore(TransactionSequence bound, CancellationToken token)
        { Pruned++; return (1, Pruned == 100); }
        protected override void RetireCore() => Retirements++;
    }

    private sealed class PostingStructure : TestStructure
    {
        internal PostingStructure() : base(1, "posting") { }
    }

    private sealed class VectorStructure : TestStructure
    {
        internal VectorStructure() : base(2, "vector") { }
    }
}
