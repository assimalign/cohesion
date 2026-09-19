using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Mapping.Tests;

/// <summary>Verifies identity and explicit entity lifecycle within one unit of work.</summary>
public sealed class EntityIdentityTests
{
    /// <summary>Materialization preserves the first instance and its local changes.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - Attach: Preserves one instance per key")]
    public void Attach_SameKey_ShouldReturnOriginalInstance()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var first = new MappingTestEntity { Id = 7, Name = "local" };

        entities.Attach(first).ShouldBeSameAs(first);
        entities.Attach(new MappingTestEntity { Id = 7, Name = "remote" }).ShouldBeSameAs(first);

        entities.Find(7).ShouldBeSameAs(first);
        first.Name.ShouldBe("local");
        entities.Find(99).ShouldBeNull();
    }

    /// <summary>Different units of work have separate identity scopes.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - Attach: Identity is scoped to the unit of work")]
    public void Attach_SeparateUnitsOfWork_ShouldPreserveSeparateInstances()
    {
        var store = new MappingTestStore();
        var firstSet = MappingUnitOfWork.Create(store).Register(new MappingTestMapper(), new MappingTestWriter(store));
        var secondSet = MappingUnitOfWork.Create(store).Register(new MappingTestMapper(), new MappingTestWriter(store));
        var first = new MappingTestEntity { Id = 7 };
        var second = new MappingTestEntity { Id = 7 };

        firstSet.Attach(first);
        secondSet.Attach(second);

        firstSet.Find(7).ShouldBeSameAs(first);
        secondSet.Find(7).ShouldBeSameAs(second);
    }

    /// <summary>Adding a duplicate never silently replaces the tracked instance.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - Add: Rejects duplicate keys")]
    public void Add_DuplicateKey_ShouldRejectReplacement()
    {
        var store = new MappingTestStore();
        var entities = MappingUnitOfWork.Create(store).Register(new MappingTestMapper(), new MappingTestWriter(store));
        var original = new MappingTestEntity { Id = 7 };
        entities.Attach(original);

        Should.Throw<InvalidOperationException>(() => entities.Add(new MappingTestEntity { Id = 7 }));

        entities.Find(7).ShouldBeSameAs(original);
    }

    /// <summary>Remove requires the tracked object, even when another object has the same key.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - Remove: Rejects a different instance")]
    public void Remove_DifferentInstanceWithSameKey_ShouldReject()
    {
        var store = new MappingTestStore();
        var entities = MappingUnitOfWork.Create(store).Register(new MappingTestMapper(), new MappingTestWriter(store));
        var original = entities.Attach(new MappingTestEntity { Id = 7 });

        Should.Throw<InvalidOperationException>(() => entities.Remove(new MappingTestEntity { Id = 7 }));

        entities.Find(7).ShouldBeSameAs(original);
    }

    /// <summary>An entity added and then removed needs no persistence operation.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - Remove: Cancels an unsaved addition")]
    public async Task Remove_NewEntity_ShouldCancelAddition()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = new MappingTestEntity { Id = 7 };
        entities.Add(entity);

        entities.Remove(entity);

        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(0);
        entities.Find(7).ShouldBeNull();
        store.BeginCount.ShouldBe(0);
    }

    /// <summary>A completed delete releases the key for a subsequent tracked instance.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - SaveChanges: Releases deleted identity after commit")]
    public async Task SaveChanges_DeletedEntity_ShouldReleaseIdentity()
    {
        var store = new MappingTestStore();
        store.Rows["entity:7"] = "before";
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = entities.Attach(new MappingTestEntity { Id = 7, Name = "before" });
        entities.Remove(entity);

        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);

        entities.Find(7).ShouldBeNull();
        store.Rows.ContainsKey("entity:7").ShouldBeFalse();
        var replacement = new MappingTestEntity { Id = 7, Name = "after" };
        entities.Add(replacement);
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
        entities.Find(7).ShouldBeSameAs(replacement);
        store.Rows["entity:7"].ShouldBe("after");
    }

    /// <summary>The explicitly supplied key comparer controls identity semantics.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - Register: Honors the declared key comparer")]
    public void Register_CustomComparer_ShouldControlIdentity()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var notes = work.Register(new MappingTestNoteMapper(), new MappingTestNoteWriter(), StringComparer.OrdinalIgnoreCase);
        var original = notes.Attach(new MappingTestNote { Id = "ABC", Text = "original" });

        notes.Attach(new MappingTestNote { Id = "abc", Text = "replacement" }).ShouldBeSameAs(original);
        notes.Find("aBc").ShouldBeSameAs(original);
    }

    /// <summary>Separate logical mappings can use the same CLR entity and key types.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - Register: Keeps logical mapping identities separate")]
    public void Register_SameEntityTypeInDifferentMappings_ShouldKeepSeparateIdentities()
    {
        var store = new MappingTestStore();
        var work = MappingUnitOfWork.Create(store);
        var firstSet = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var secondSet = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var first = firstSet.Attach(new MappingTestEntity { Id = 7, Name = "first mapping" });
        var second = secondSet.Attach(new MappingTestEntity { Id = 7, Name = "second mapping" });

        firstSet.Find(7).ShouldBeSameAs(first);
        secondSet.Find(7).ShouldBeSameAs(second);
        first.ShouldNotBeSameAs(second);
    }

    /// <summary>A pending delete retains its identity until a successful commit.</summary>
    [Fact(DisplayName = "Cohesion Test [Database Mapping] - Attach: Preserves the canonical instance during pending delete")]
    public async Task Attach_EntityPendingDelete_ShouldNotResurrectOrReplaceIt()
    {
        var store = new MappingTestStore();
        store.Rows["entity:7"] = "before";
        var work = MappingUnitOfWork.Create(store);
        var entities = work.Register(new MappingTestMapper(), new MappingTestWriter(store));
        var entity = entities.Attach(new MappingTestEntity { Id = 7, Name = "before" });
        entities.Remove(entity);

        entities.Attach(new MappingTestEntity { Id = 7, Name = "replacement" }).ShouldBeSameAs(entity);

        entities.Find(7).ShouldBeSameAs(entity);
        (await work.SaveChangesAsync(CancellationToken.None)).ShouldBe(1);
        store.Rows.ShouldBeEmpty();
        entities.Find(7).ShouldBeNull();
    }
}
