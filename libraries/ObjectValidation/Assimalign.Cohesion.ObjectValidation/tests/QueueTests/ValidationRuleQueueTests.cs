using System.Collections;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

/// <summary>
/// <see cref="ValidationRuleQueue"/> is first in, first out, as <see cref="IValidationRuleQueue"/> says: every
/// view of it — enumeration, copies, <c>Peek</c> and <c>Pop</c> — starts at the rule pushed first, which is the
/// rule chained first (#1221).
/// </summary>
public class ValidationRuleQueueTests
{
    private sealed class NamedRule : IValidationRule
    {
        public NamedRule(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public bool TryValidate(object value, out IValidationContext context)
        {
            context = null!;
            return false;
        }
    }

    private static ValidationRuleQueue CreateQueue(params string[] names)
    {
        ValidationRuleQueue queue = new();
        IValidationRuleQueue contract = queue;

        foreach (string name in names)
        {
            contract.Push(new NamedRule(name));
        }

        return queue;
    }

    private static string[] Names(IEnumerable rules) => rules.Cast<IValidationRule>().Select(rule => rule.Name).ToArray();

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Rule queue: rules enumerate in the order they were pushed")]
    public void GetEnumerator_RulesPushedInOrder_ShouldEnumerateFirstInFirstOut()
    {
        // Arrange
        ValidationRuleQueue queue = CreateQueue("first", "second", "third");

        // Act
        string[] names = Names(queue);

        // Assert
        names.ShouldBe(["first", "second", "third"]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Rule queue: ToArray and both CopyTo overloads copy in queue order")]
    public void ToArrayAndCopyTo_RulesPushedInOrder_ShouldCopyInQueueOrder()
    {
        // Arrange
        ValidationRuleQueue queue = CreateQueue("first", "second", "third");
        IValidationRule[] typed = new IValidationRule[4];
        object[] untyped = new object[4];

        // Act
        IValidationRule[] array = queue.ToArray();
        queue.CopyTo(typed, 1);
        ((ICollection)queue).CopyTo(untyped, 1);

        // Assert
        Names(array).ShouldBe(["first", "second", "third"]);
        Names(typed.Skip(1)).ShouldBe(["first", "second", "third"]);
        Names(untyped.Skip(1)).ShouldBe(["first", "second", "third"]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Rule queue: Peek and Pop take the rule pushed first")]
    public void Pop_RulesPushedInOrder_ShouldRemoveTheRulePushedFirst()
    {
        // Arrange
        ValidationRuleQueue queue = CreateQueue("first", "second", "third");
        IValidationRuleQueue contract = queue;

        // Act
        IValidationRule peeked = contract.Peek();
        IValidationRule popped = contract.Pop();
        bool popAgain = contract.TryPop(out IValidationRule second);
        bool peekAgain = contract.TryPeek(out IValidationRule next);

        // Assert
        peeked.Name.ShouldBe("first");
        popped.Name.ShouldBe("first");
        popAgain.ShouldBeTrue();
        second.Name.ShouldBe("second");
        peekAgain.ShouldBeTrue();
        next.Name.ShouldBe("third");
        queue.Count.ShouldBe(1);
        Names(queue).ShouldBe(["third"]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Rule queue: a queue created from a collection keeps the collection's order")]
    public void Constructor_FromCollection_ShouldKeepTheCollectionsOrder()
    {
        // Arrange
        IValidationRule[] rules = [new NamedRule("first"), new NamedRule("second"), new NamedRule("third")];

        // Act
        ValidationRuleQueue queue = new(rules);

        // Assert
        Names(queue).ShouldBe(["first", "second", "third"]);
        ((IValidationRuleQueue)queue).Peek().Name.ShouldBe("first");
    }
}
