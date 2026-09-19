using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Mapping.Tests;

/// <summary>Verifies that uncertain publication can never become a retryable save.</summary>
public sealed class MappingCommitOutcomeTests
{
    /// <summary>The same lost acknowledgement can follow either server outcome.</summary>
    [Theory(DisplayName = "Cohesion Test [Database Mapping] - Commit: Unknown outcomes permanently fault tracking")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_UnknownCommit_ShouldRejectEveryScopeOperation(bool published)
    {
        var store = new UnknownCommitStore(published, false);
        var work = MappingUnitOfWork.Create(store);
        var mapper = new MappingTestMapper();
        var writer = new UnknownCommitWriter();
        var entities = work.Register(mapper, writer);
        var entity = new MappingTestEntity { Id = 1, Name = "pending" };
        entities.Add(entity);

        var failure = await Should.ThrowAsync<MappingCommitOutcomeUnknownException>(
            async () => await work.SaveChangesAsync(CancellationToken.None));

        store.Published.ShouldBe(published);
        store.Disposed.ShouldBeTrue();
        failure.InnerException.ShouldBeOfType<InvalidOperationException>();
        await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(CancellationToken.None));
        Should.Throw<InvalidOperationException>(() => work.Register(mapper, writer));
        Should.Throw<InvalidOperationException>(() => entities.Find(1));
        Should.Throw<InvalidOperationException>(() => entities.Attach(entity));
        Should.Throw<InvalidOperationException>(() => entities.Add(new MappingTestEntity { Id = 2 }));
        Should.Throw<InvalidOperationException>(() => entities.Remove(entity));
        store.BeginCount.ShouldBe(1);
    }

    /// <summary>Cleanup cannot erase an already-recorded unresolved commit.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - Commit: Disposal failure preserves the unknown outcome guard")]
    public async Task SaveChanges_DisposalMasksUnknownCommit_ShouldStillPreventReplay()
    {
        var store = new UnknownCommitStore(true, true);
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new UnknownCommitWriter());
        entities.Add(new MappingTestEntity { Id = 1 });

        await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(CancellationToken.None));

        var failure = await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(CancellationToken.None));
        failure.InnerException.ShouldBeOfType<MappingCommitOutcomeUnknownException>();
        store.Published.ShouldBeTrue();
        store.BeginCount.ShouldBe(1);
    }
}
