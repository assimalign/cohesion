using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

/// <summary>
/// Pins the comparison of a resumed link's two unsettled maps (AMQP 1.0 Part 2, section 2.6.13), including the
/// worked example of Part 3, section 3.4.6.
/// </summary>
public class AmqpUnsettledReconciliationTests
{
    public static TheoryData<bool, bool> IncompleteFlags => new()
    {
        { false, false },
        { true, false },
        { false, true },
        { true, true }
    };

    [Fact(DisplayName = "Cohesion Test [Amqp] - Create: Should return one empty, complete comparison when neither map lists a delivery")]
    public void Create_OnNoUnsettledDeliveries_ShouldReturnEmptyCompleteComparison()
    {
        // Act
        AmqpUnsettledReconciliation none = AmqpUnsettledReconciliation.Create(null, false, null, false);
        AmqpUnsettledReconciliation empty = AmqpUnsettledReconciliation.Create(Map(), false, Map(), false);

        // Assert
        empty.ShouldBeSameAs(none);
        none.IsComplete.ShouldBeTrue();
        none.SourceIncomplete.ShouldBeFalse();
        none.TargetIncomplete.ShouldBeFalse();
        none.Resumable.ShouldBeEmpty();
        none.Settled.ShouldBeEmpty();
        none.UnknownToReceiver.ShouldBeEmpty();
        none.InDoubt.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Create: Should partition the unsettled maps of the Part 3 resumption example")]
    public void Create_OnPart3Example_ShouldPartitionTags()
    {
        // Arrange: the maps of AMQP 1.0 Part 3, section 3.4.6.
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> source = Map(
            (1, null), (2, null), (3, null), (4, null),
            (5, new AmqpReceived(0, 200)), (6, new AmqpReceived(1, 150)), (7, new AmqpReceived(0, 500)),
            (8, new AmqpReceived(3, 5)), (9, new AmqpReceived(2, 0)),
            (10, AmqpAccepted.Instance), (11, AmqpAccepted.Instance), (12, AmqpAccepted.Instance),
            (13, AmqpAccepted.Instance), (14, AmqpAccepted.Instance));
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> target = Map(
            (2, new AmqpReceived(3, 0)), (3, AmqpAccepted.Instance), (4, null), (6, new AmqpReceived(2, 0)),
            (7, new AmqpReceived(0, 100)), (8, AmqpAccepted.Instance), (9, null), (11, new AmqpReceived(1, 2000)),
            (12, AmqpAccepted.Instance), (13, AmqpReleased.Instance), (14, null));

        // Act
        AmqpUnsettledReconciliation reconciliation = AmqpUnsettledReconciliation.Create(source, false, target, false);

        // Assert
        reconciliation.IsComplete.ShouldBeTrue();
        Numbers(reconciliation.Resumable).ShouldBe([2, 3, 4, 6, 7, 8, 9, 11, 12, 13, 14]);
        Numbers(reconciliation.UnknownToReceiver).ShouldBe([1, 5, 10]);
        reconciliation.Settled.ShouldBeEmpty();
        reconciliation.InDoubt.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Create: Should consider settled a delivery only the target lists while the source's map is complete")]
    public void Create_OnTargetOnlyTagWithCompleteSource_ShouldConsiderItSettled()
    {
        // Arrange
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> source = Map((1, null));
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> target = Map((1, null), (2, AmqpAccepted.Instance));

        // Act
        AmqpUnsettledReconciliation reconciliation = AmqpUnsettledReconciliation.Create(source, false, target, false);

        // Assert
        Numbers(reconciliation.Resumable).ShouldBe([1]);
        Numbers(reconciliation.Settled).ShouldBe([2]);
        reconciliation.UnknownToReceiver.ShouldBeEmpty();
        reconciliation.InDoubt.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Create: Should keep resumable every delivery the target lists while the source's map is incomplete")]
    public void Create_OnIncompleteSourceMap_ShouldKeepTargetTagsResumable()
    {
        // Arrange
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> source = Map((1, null));
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> target = Map((2, null), (3, new AmqpReceived(0, 10)));

        // Act
        AmqpUnsettledReconciliation reconciliation = AmqpUnsettledReconciliation.Create(source, true, target, false);

        // Assert
        reconciliation.SourceIncomplete.ShouldBeTrue();
        reconciliation.TargetIncomplete.ShouldBeFalse();
        reconciliation.IsComplete.ShouldBeFalse();
        Numbers(reconciliation.Resumable).ShouldBe([2, 3]);
        Numbers(reconciliation.UnknownToReceiver).ShouldBe([1]);
        reconciliation.Settled.ShouldBeEmpty();
        reconciliation.InDoubt.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Create: Should hold in doubt a delivery only the source lists while the target's map is incomplete")]
    public void Create_OnIncompleteTargetMap_ShouldHoldSourceOnlyTagsInDoubt()
    {
        // Arrange
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> source = Map((1, null), (2, null));
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> target = Map((2, null), (3, null));

        // Act
        AmqpUnsettledReconciliation reconciliation = AmqpUnsettledReconciliation.Create(source, false, target, true);

        // Assert
        reconciliation.SourceIncomplete.ShouldBeFalse();
        reconciliation.TargetIncomplete.ShouldBeTrue();
        reconciliation.IsComplete.ShouldBeFalse();
        Numbers(reconciliation.Resumable).ShouldBe([2]);
        Numbers(reconciliation.Settled).ShouldBe([3]);
        Numbers(reconciliation.InDoubt).ShouldBe([1]);
        reconciliation.UnknownToReceiver.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - Create: Should report incomplete maps even when neither lists a delivery")]
    [MemberData(nameof(IncompleteFlags))]
    public void Create_OnIncompleteMapsWithoutTags_ShouldReportThem(bool sourceIncomplete, bool targetIncomplete)
    {
        // Act
        AmqpUnsettledReconciliation reconciliation = AmqpUnsettledReconciliation.Create(null, sourceIncomplete, null, targetIncomplete);

        // Assert
        reconciliation.SourceIncomplete.ShouldBe(sourceIncomplete);
        reconciliation.TargetIncomplete.ShouldBe(targetIncomplete);
        reconciliation.IsComplete.ShouldBe(!sourceIncomplete && !targetIncomplete);
        reconciliation.Resumable.ShouldBeEmpty();
        reconciliation.Settled.ShouldBeEmpty();
        reconciliation.UnknownToReceiver.ShouldBeEmpty();
        reconciliation.InDoubt.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - Create: Should place every tag either map lists in exactly one set")]
    [MemberData(nameof(IncompleteFlags))]
    public void Create_OnOverlappingMaps_ShouldPlaceEveryTagInOneSet(bool sourceIncomplete, bool targetIncomplete)
    {
        // Arrange
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> source = Map((1, null), (2, null), (3, null), (4, AmqpAccepted.Instance));
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> target = Map((3, null), (4, AmqpAccepted.Instance), (5, null), (6, null));

        // Act
        AmqpUnsettledReconciliation reconciliation = AmqpUnsettledReconciliation.Create(source, sourceIncomplete, target, targetIncomplete);
        AmqpDeliveryTag[] placed =
        [
            .. reconciliation.Resumable,
            .. reconciliation.Settled,
            .. reconciliation.UnknownToReceiver,
            .. reconciliation.InDoubt
        ];

        // Assert
        placed.Length.ShouldBe(6);
        placed.Distinct().Count().ShouldBe(6);
        placed.ShouldBe(source.Keys.Union(target.Keys), ignoreOrder: true);
        Numbers(reconciliation.Resumable).ShouldBe(sourceIncomplete ? [3, 4, 5, 6] : [3, 4]);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Create: Should not change when a map changes after the comparison")]
    public void Create_OnMapChangedAfterComparison_ShouldKeepItsSets()
    {
        // Arrange
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> source = Map((1, null));
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> target = Map((1, null));
        AmqpUnsettledReconciliation reconciliation = AmqpUnsettledReconciliation.Create(source, false, target, false);

        // Act
        source.Remove(AmqpDeliveryTag.Create(1UL));
        target[AmqpDeliveryTag.Create(2UL)] = null;

        // Assert
        Numbers(reconciliation.Resumable).ShouldBe([1]);
        reconciliation.Settled.ShouldBeEmpty();
    }

    private static Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> Map(params (ulong Tag, AmqpDeliveryState? State)[] entries)
    {
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> map = [];

        foreach ((ulong tag, AmqpDeliveryState? state) in entries)
        {
            map.Add(AmqpDeliveryTag.Create(tag), state);
        }

        return map;
    }

    private static ulong[] Numbers(IEnumerable<AmqpDeliveryTag> tags)
    {
        return [.. tags.Select(tag => (ulong)tag.Value.Span[0]).Order()];
    }
}
