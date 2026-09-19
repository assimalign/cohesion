using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Mapping.Tests;

/// <summary>Verifies detached snapshot tracking without proxies or runtime member discovery.</summary>
public sealed class EntityChangeTrackingTests
{
    /// <summary>Scalar changes compare against the latest committed snapshot.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Detects mapped scalar changes")]
    public async Task SaveChanges_MappedScalarChanged_ShouldWriteAndAcceptBaseline()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = entities.Attach(new MappingTestEntity { Id = 7, Name = "before" });

        entity.Name = "after";

        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
        store.Rows["entity:7"].ShouldBe("after");
        store.Changes[0].Kind.ShouldBe(EntityChangeKind.Modified);
        store.Changes[0].Original.Name.ShouldBe("before");
        store.Changes[0].Current.Name.ShouldBe("after");
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(0);
        store.BeginCount.ShouldBe(1);
    }

    /// <summary>Returning a value to its baseline eliminates an unsaved change.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Ignores reverted scalar changes")]
    public async Task SaveChanges_ScalarReverted_ShouldBeNoOp()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = entities.Attach(new MappingTestEntity { Id = 7, Name = "before" });

        entity.Name = "temporary";
        entity.Name = "before";

        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(0);
        store.BeginCount.ShouldBe(0);
    }

    /// <summary>Changes outside the mapper's snapshot are intentionally ignored.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Ignores unmapped state")]
    public async Task SaveChanges_UnmappedCollectionMutated_ShouldBeNoOp()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = entities.Attach(new MappingTestEntity { Id = 7, Name = "before" });

        entity.Unmapped.Add("not part of the mapping");

        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(0);
        store.BeginCount.ShouldBe(0);
    }

    /// <summary>Null assignments are actual mapped changes.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Tracks null assignments")]
    public async Task SaveChanges_ScalarAssignedNull_ShouldWriteNull()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = entities.Attach(new MappingTestEntity { Id = 7, Name = "before" });

        entity.Name = null;

        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
        store.Rows["entity:7"].ShouldBeNull();
    }

    /// <summary>Detached binary snapshots detect edits within an existing array.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Tracks in-place binary changes")]
    public async Task SaveChanges_BinaryArrayMutated_ShouldPreserveOriginalBytes()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = entities.Attach(new MappingTestEntity { Id = 7, Payload = [1, 2] });

        entity.Payload![0] = 9;

        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
        store.Changes[0].Original.Payload.ShouldBe(new byte[] { 1, 2 });
        store.Changes[0].Current.Payload.ShouldBe(new byte[] { 9, 2 });
        entity.Payload[1] = 8;
        store.Changes[0].Current.Payload.ShouldBe(new byte[] { 9, 2 });
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
        store.Changes[1].Original.Payload.ShouldBe(new byte[] { 9, 2 });
    }

    /// <summary>Binary equality compares contents rather than array references.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Ignores equal binary replacement")]
    public async Task SaveChanges_BinaryReplacedWithEqualContents_ShouldBeNoOp()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = entities.Attach(new MappingTestEntity { Id = 7, Payload = [1, 2] });

        entity.Payload = [1, 2];

        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(0);
        store.BeginCount.ShouldBe(0);
    }

    /// <summary>Binary null and empty values remain distinct snapshots.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Distinguishes null and empty binary values")]
    public async Task SaveChanges_BinaryNullChangedToEmpty_ShouldWrite()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = entities.Attach(new MappingTestEntity { Id = 7 });

        entity.Payload = [];

        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
        store.Changes[0].Original.Payload.ShouldBeNull();
        store.Changes[0].Current.Payload.ShouldBeEmpty();
        entity.Payload = null;
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
    }

    /// <summary>Changing a tracked key is rejected before any transaction opens.</summary>
    /// <param name="added">Whether the entity is newly added rather than attached.</param>
    [Theory(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Rejects changed identity before writing")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_KeyMutated_ShouldRejectBeforeTransaction(bool added)
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = new MappingTestEntity { Id = 7, Name = "before" };
        if (added)
        {
            entities.Add(entity);
        }
        else
        {
            entities.Attach(entity);
        }

        entity.Id = 8;

        await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(CancellationToken.None));
        store.BeginCount.ShouldBe(0);
        store.Rows.ShouldBeEmpty();
        entity.Id = 7;
        entity.Name = "after";
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
    }
}
