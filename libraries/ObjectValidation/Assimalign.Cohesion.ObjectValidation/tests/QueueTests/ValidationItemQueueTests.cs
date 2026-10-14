using System.Collections.Generic;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

/// <summary>
/// <see cref="ValidationItemQueue"/> is first in, first out: enumeration, the indexer, copies, <c>Peek</c> and
/// <c>Pop</c> all start at the item pushed first, which is the member declared first (#1221).
/// </summary>
public class ValidationItemQueueTests
{
    private sealed class NamedItem : IValidationItem
    {
        public NamedItem(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public IValidationRuleQueue ItemRuleStack { get; } = new ValidationRuleQueue();

        public void Evaluate(IValidationContext context)
        {
        }
    }

    private static ValidationItemQueue CreateQueue(params string[] names)
    {
        ValidationItemQueue queue = new();
        IValidationItemQueue contract = queue;

        foreach (string name in names)
        {
            contract.Push(new NamedItem(name));
        }

        return queue;
    }

    private static string[] Names(IEnumerable<IValidationItem> items) => items.Select(item => ((NamedItem)item).Name).ToArray();

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Item queue: items enumerate in the order they were pushed, as the indexer counts them")]
    public void GetEnumerator_ItemsPushedInOrder_ShouldEnumerateFirstInFirstOutLikeTheIndexer()
    {
        // Arrange
        ValidationItemQueue queue = CreateQueue("first", "second", "third");
        IValidationItemQueue contract = queue;

        // Act
        string[] enumerated = Names(queue);
        string[] indexed = Names(Enumerable.Range(0, contract.Count).Select(index => contract[index]));

        // Assert
        enumerated.ShouldBe(["first", "second", "third"]);
        indexed.ShouldBe(enumerated);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Item queue: ToArray and CopyTo copy in queue order")]
    public void ToArrayAndCopyTo_ItemsPushedInOrder_ShouldCopyInQueueOrder()
    {
        // Arrange
        ValidationItemQueue queue = CreateQueue("first", "second", "third");
        IValidationItem[] copy = new IValidationItem[4];

        // Act
        IValidationItem[] array = queue.ToArray();
        queue.CopyTo(copy, 1);

        // Assert
        Names(array).ShouldBe(["first", "second", "third"]);
        Names(copy.Skip(1)).ShouldBe(["first", "second", "third"]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Item queue: Peek and Pop take the item pushed first")]
    public void Pop_ItemsPushedInOrder_ShouldRemoveTheItemPushedFirst()
    {
        // Arrange
        ValidationItemQueue queue = CreateQueue("first", "second", "third");
        IValidationItemQueue contract = queue;

        // Act
        IValidationItem peeked = contract.Peek();
        IValidationItem popped = contract.Pop();
        bool popAgain = contract.TryPop(out IValidationItem second);
        bool peekAgain = contract.TryPeek(out IValidationItem next);

        // Assert
        Names([peeked, popped, second, next]).ShouldBe(["first", "first", "second", "third"]);
        popAgain.ShouldBeTrue();
        peekAgain.ShouldBeTrue();
        contract.Count.ShouldBe(1);
        contract[0].ShouldBeSameAs(next);
        Names(queue).ShouldBe(["third"]);
    }
}
