using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

public class RuleCollectionErrorSourceTests
{
    [Theory(DisplayName = "Cohesion Test [ObjectValidation] - RuleForEach: scalar sources include each element's index on every rule and validation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validate_ScalarCollectionFailures_ShouldIndexEachElementWithoutChangingSharedRules(bool asynchronous)
    {
        // Arrange
        IValidator validator = Validator.Create(builder =>
        {
            builder.AddOptions(options => options.ContinueThroughValidationChain = true);
            builder.AddProfile(new ScalarProfile());
        });
        var model = new CollectionModel { Names = ["valid", "", "valid", ""] };

        // Act
        ValidationResult first = asynchronous
            ? await validator.ValidateAsync(model, CancellationToken.None)
            : validator.Validate(model);
        ValidationResult second = asynchronous
            ? await validator.ValidateAsync(new CollectionModel { Names = [""] }, CancellationToken.None)
            : validator.Validate(new CollectionModel { Names = [""] });

        // Assert
        first.Errors.Select(error => error.Source).ShouldBe([
            "model => model.Names[1]", "model => model.Names[3]",
            "model => model.Names[1]", "model => model.Names[3]"
        ]);
        second.Errors.Select(error => error.Source).ShouldBe([
            "model => model.Names[0]", "model => model.Names[0]"
        ]);
        first.Errors.All(error => error.Code == "400" && !string.IsNullOrEmpty(error.Message)).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [ObjectValidation] - RuleForEach: nested ChildRules and UseProfile sources include every collection index")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validate_NestedCollections_ShouldComposeAllElementIndices(bool asynchronous)
    {
        // Arrange
        IValidator validator = Validator.Create(builder => builder.AddProfile(new NestedProfile()));
        var model = new CollectionModel
        {
            Groups = [
                new Group { Items = [new Item { Quantity = 1 }] },
                new Group { Items = [new Item { Quantity = 2 }, new Item { Quantity = 3 }, new Item()] },
                new Group { Items = [new Item { Quantity = 1 }, new Item()] }
            ]
        };

        // Act
        ValidationResult result = asynchronous
            ? await validator.ValidateAsync(model, CancellationToken.None)
            : validator.Validate(model);

        // Assert
        result.Errors.Select(error => error.Source).ShouldBe([
            "model => model.Groups[1].Items[2].Quantity",
            "model => model.Groups[2].Items[1].Quantity"
        ]);
    }

    [Theory(DisplayName = "Cohesion Test [ObjectValidation] - RuleForEach: explicit sources remain unchanged for scalar and nested rules")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validate_ExplicitCollectionSources_ShouldKeepThemAsWritten(bool asynchronous)
    {
        // Arrange
        IValidator validator = Validator.Create(builder => builder.AddProfile(new ExplicitSourceProfile()));
        var model = new CollectionModel
        {
            Names = ["", ""],
            Groups = [new Group { Items = [new Item()] }]
        };

        // Act
        ValidationResult result = asynchronous
            ? await validator.ValidateAsync(model, CancellationToken.None)
            : validator.Validate(model);

        // Assert
        result.Errors.Select(error => error.Source).ShouldBe(["name", "name", "quantity"]);
    }

    public sealed class CollectionModel
    {
        public IEnumerable<string> Names { get; set; } = [];

        public IEnumerable<Group> Groups { get; set; } = [];
    }

    public sealed class Group
    {
        public IEnumerable<Item> Items { get; set; } = [];
    }

    public sealed class Item
    {
        public int Quantity { get; set; }
    }

    public sealed class ScalarProfile : ValidationProfile<CollectionModel>
    {
        public override void Configure(IValidationRuleDescriptor<CollectionModel> descriptor)
        {
            descriptor.RuleForEach(model => model.Names).NotEmpty().Matches("^valid$");
        }
    }

    public sealed class NestedProfile : ValidationProfile<CollectionModel>
    {
        public override void Configure(IValidationRuleDescriptor<CollectionModel> descriptor)
        {
            descriptor.RuleForEach(model => model.Groups).ChildRules(group =>
                group.RuleForEach(value => value.Items).UseProfile(new ItemProfile()));
        }
    }

    public sealed class ItemProfile : ValidationProfile<Item>
    {
        public override void Configure(IValidationRuleDescriptor<Item> descriptor)
        {
            descriptor.RuleFor(item => item.Quantity).GreaterThan(0);
        }
    }

    public sealed class ExplicitSourceProfile : ValidationProfile<CollectionModel>
    {
        public override void Configure(IValidationRuleDescriptor<CollectionModel> descriptor)
        {
            descriptor.RuleForEach(model => model.Names).NotEmpty(error => error.Source = "name");
            descriptor.RuleForEach(model => model.Groups).ChildRules(group =>
                group.RuleForEach(value => value.Items).ChildRules(item =>
                    item.RuleFor(value => value.Quantity).GreaterThan(0, error => error.Source = "quantity")));
        }
    }
}
