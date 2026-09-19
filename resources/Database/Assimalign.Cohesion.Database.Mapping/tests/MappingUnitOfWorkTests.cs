using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Mapping.Tests;

/// <summary>Verifies atomic persistence, cancellation, and retry of heterogeneous tracked changes.</summary>
public sealed class MappingUnitOfWorkTests
{
    /// <summary>A single commit includes additions, modifications, and deletions.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Commits all entity states together")]
    public async Task SaveChanges_MixedChanges_ShouldCommitOneTransaction()
    {
        var store = new MappingTestStore();
        store.Rows["entity:1"] = "before";
        store.Rows["entity:2"] = "delete";
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var modified = entities.Attach(new MappingTestEntity { Id = 1, Name = "before" });
        var deleted = entities.Attach(new MappingTestEntity { Id = 2, Name = "delete" });
        modified.Name = "after";
        entities.Remove(deleted);
        entities.Add(new MappingTestEntity { Id = 3, Name = "added" });

        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(3);

        store.Rows.Count.ShouldBe(2);
        store.Rows["entity:1"].ShouldBe("after");
        store.Rows["entity:3"].ShouldBe("added");
        store.Changes.ShouldContain(change => change.Kind == EntityChangeKind.Added && change.Key == 3);
        store.Changes.ShouldContain(change => change.Kind == EntityChangeKind.Modified && change.Key == 1);
        store.Changes.ShouldContain(change => change.Kind == EntityChangeKind.Deleted && change.Key == 2);
        store.BeginCount.ShouldBe(1);
        store.CommitCount.ShouldBe(1);
        store.DisposeCount.ShouldBe(1);
        store.RollbackCount.ShouldBe(0);
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(0);
    }

    /// <summary>Generic entity types and key types share one transaction without runtime discovery.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Commits different entity types together")]
    public async Task SaveChanges_DifferentEntityTypes_ShouldShareTransaction()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var notes = work.Register(new MappingTestNoteMapper(), new MappingTestNoteWriter());
        entities.Add(new MappingTestEntity { Id = 7, Name = "entity" });
        notes.Add(new MappingTestNote { Id = "7", Text = "note" });

        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(2);

        store.Rows["entity:7"].ShouldBe("entity");
        store.Rows["note:7"].ShouldBe("note");
        store.BeginCount.ShouldBe(1);
        store.CommitCount.ShouldBe(1);
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(0);
    }

    /// <summary>A failed later write rolls back earlier writes from another entity set.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Rolls back all entity types on failure")]
    public async Task SaveChanges_SecondEntityTypeFails_ShouldRollbackEveryWrite()
    {
        var store = new MappingTestStore { FailOnWrite = 2 };
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var notes = work.Register(new MappingTestNoteMapper(), new MappingTestNoteWriter());
        entities.Add(new MappingTestEntity { Id = 7, Name = "entity" });
        notes.Add(new MappingTestNote { Id = "7", Text = "note" });

        await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(CancellationToken.None));

        store.Rows.ShouldBeEmpty();
        store.CommitCount.ShouldBe(0);
        store.RollbackCount.ShouldBe(1);
        store.DisposeCount.ShouldBe(1);
        store.FailOnWrite = null;
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(2);
        store.Rows.Count.ShouldBe(2);
        store.CommitCount.ShouldBe(1);
    }

    /// <summary>Failed writes retain original snapshots for an explicit retry.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Keeps baselines when a partial apply fails")]
    public async Task SaveChanges_PartialApplyFails_ShouldRetainOriginalBaselineForRetry()
    {
        var store = new MappingTestStore { FailOnWrite = 2 };
        store.Rows["entity:1"] = "first-before";
        store.Rows["entity:2"] = "second-before";
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var first = entities.Attach(new MappingTestEntity { Id = 1, Name = "first-before" });
        var second = entities.Attach(new MappingTestEntity { Id = 2, Name = "second-before" });
        first.Name = "first-after";
        second.Name = "second-after";

        await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(CancellationToken.None));

        store.Rows["entity:1"].ShouldBe("first-before");
        store.Rows["entity:2"].ShouldBe("second-before");
        first.Name = "first-retry";
        store.Changes.Clear();
        store.FailOnWrite = null;
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(2);
        store.Changes.ShouldContain(change => change.Key == 1
            && change.Original.Name == "first-before" && change.Current.Name == "first-retry");
        store.Changes.ShouldContain(change => change.Key == 2 && change.Original.Name == "second-before");
        store.Rows["entity:1"].ShouldBe("first-retry");
        store.Rows["entity:2"].ShouldBe("second-after");
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(0);
    }

    /// <summary>A commit failure leaves persistence and tracked state retryable.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Retains changes after commit failure")]
    public async Task SaveChanges_CommitFails_ShouldRollbackAndPermitRetry()
    {
        var store = new MappingTestStore { FailCommit = true };
        store.Rows["entity:1"] = "before";
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = entities.Attach(new MappingTestEntity { Id = 1, Name = "before" });
        entity.Name = "after";
        entities.Add(new MappingTestEntity { Id = 2, Name = "added" });

        await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(CancellationToken.None));

        store.Rows.Count.ShouldBe(1);
        store.Rows["entity:1"].ShouldBe("before");
        store.CommitCount.ShouldBe(0);
        store.RollbackCount.ShouldBe(1);
        store.FailCommit = false;
        store.Changes.Clear();
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(2);
        store.Changes.ShouldContain(change => change.Key == 1 && change.Original.Name == "before");
        store.Changes.ShouldContain(change => change.Key == 2 && change.Kind == EntityChangeKind.Added);
        store.Rows["entity:1"].ShouldBe("after");
        store.Rows["entity:2"].ShouldBe("added");
    }

    /// <summary>A failed delete retains its identity and deletion request for retry.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Retains deleted entities after failure")]
    public async Task SaveChanges_DeleteCommitFails_ShouldPreservePendingDelete()
    {
        var store = new MappingTestStore { FailCommit = true };
        store.Rows["entity:1"] = "before";
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = entities.Attach(new MappingTestEntity { Id = 1, Name = "before" });
        entities.Remove(entity);

        await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(CancellationToken.None));

        store.Rows["entity:1"].ShouldBe("before");
        store.FailCommit = false;
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
        store.Rows.ShouldBeEmpty();
        entities.Find(1).ShouldBeNull();
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(0);
    }

    /// <summary>Cancellation after a staged write causes asynchronous rollback before return.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Rolls back partial writes on cancellation")]
    public async Task SaveChanges_CanceledAfterFirstWrite_ShouldRollbackAndPermitRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new MappingTestStore { AfterWrite = _ => cancellation.Cancel() };
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        entities.Add(new MappingTestEntity { Id = 1, Name = "first" });
        entities.Add(new MappingTestEntity { Id = 2, Name = "second" });

        await Should.ThrowAsync<OperationCanceledException>(async () => await work.SaveChangesAsync(cancellation.Token));

        store.Rows.ShouldBeEmpty();
        store.RollbackCount.ShouldBe(1);
        store.DisposeCount.ShouldBe(1);
        store.CommitCount.ShouldBe(0);
        store.AfterWrite = null;
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(2);
        store.Rows.Count.ShouldBe(2);
    }

    /// <summary>A pre-canceled operation performs no persistence work.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Honors cancellation before opening a transaction")]
    public async Task SaveChanges_AlreadyCanceled_ShouldAvoidTransaction()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        entities.Add(new MappingTestEntity { Id = 1 });
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await work.SaveChangesAsync(cancellation.Token));

        store.BeginCount.ShouldBe(0);
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
    }

    /// <summary>An entirely empty unit of work avoids a store transaction.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Skips transactions for no work")]
    public async Task SaveChanges_NoRegisteredEntities_ShouldBeNoOp()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);

        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(0);

        store.BeginCount.ShouldBe(0);
    }

    /// <summary>A disposal error after a successful commit cannot undo accepted baselines.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Accepts committed changes before disposing")]
    public async Task SaveChanges_DisposeFailsAfterCommit_ShouldKeepAcceptedBaseline()
    {
        var store = new MappingTestStore { FailDispose = true };
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        entities.Add(new MappingTestEntity { Id = 1, Name = "committed" });

        await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(CancellationToken.None));

        store.Rows["entity:1"].ShouldBe("committed");
        store.CommitCount.ShouldBe(1);
        store.FailDispose = false;
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(0);
        store.BeginCount.ShouldBe(1);
    }

    /// <summary>Persistence callbacks cannot mutate the tracked entity collections mid-save.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Rejects reentrant tracking")]
    public async Task SaveChanges_TrackingDuringApply_ShouldRejectAndRollback()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        entities.Add(new MappingTestEntity { Id = 1, Name = "before" });
        store.AfterWrite = _ => entities.Add(new MappingTestEntity { Id = 2, Name = "reentrant" });

        await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(CancellationToken.None));

        store.Rows.ShouldBeEmpty();
        entities.Find(2).ShouldBeNull();
        store.AfterWrite = null;
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
    }

    /// <summary>A mapper failure occurs before the transaction and does not discard changes.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Retains changes after capture failure")]
    public async Task SaveChanges_CaptureFails_ShouldAvoidTransactionAndPermitRetry()
    {
        var store = new MappingTestStore();
        var mapper = new MappingTestMapper();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(mapper, new MappingTestWriter(store));
        var entity = entities.Attach(new MappingTestEntity { Id = 1, Name = "before" });
        entity.Name = "after";
        mapper.FailCapture = true;

        await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(CancellationToken.None));

        store.BeginCount.ShouldBe(0);
        mapper.FailCapture = false;
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
        store.Changes[0].Original.Name.ShouldBe("before");
        store.Rows["entity:1"].ShouldBe("after");
    }

    /// <summary>Cancellation after a successful commit cannot leave already-written changes pending.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Accepts a successful commit racing cancellation")]
    public async Task SaveChanges_CancellationAfterCommit_ShouldAcceptCommittedChanges()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new MappingTestStore { AfterCommit = cancellation.Cancel };
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        entities.Add(new MappingTestEntity { Id = 1, Name = "committed" });

        (await work.SaveChangesAsync(cancellation.Token)).ShouldBe(1);

        cancellation.IsCancellationRequested.ShouldBeTrue();
        store.Rows["entity:1"].ShouldBe("committed");
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(0);
        store.BeginCount.ShouldBe(1);
    }

    /// <summary>The save guard remains active across an actual asynchronous store suspension.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Rejects concurrent operations across awaits")]
    public async Task SaveChanges_AwaitingTransaction_ShouldRejectConcurrentSaveAndRegistration()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var store = new MappingTestStore
        {
            BeforeBegin = async token =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        entities.Add(new MappingTestEntity { Id = 1, Name = "first" });

        Task<int> save = work.SaveChangesAsync(cancellation.Token).AsTask();
        await entered.Task.WaitAsync(cancellation.Token);
        try
        {
            await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(cancellation.Token));
            Should.Throw<InvalidOperationException>(() => work.Register(new MappingTestNoteMapper(), new MappingTestNoteWriter()));
            Should.Throw<InvalidOperationException>(() => entities.Add(new MappingTestEntity { Id = 2 }));
        }
        finally
        {
            release.SetResult();
        }

        (await save).ShouldBe(1);
        store.Rows.Count.ShouldBe(1);
        store.CommitCount.ShouldBe(1);
    }
}
