using System.Text.Json.Serialization;

using Assimalign.Cohesion.ObjectValidation;

namespace Assimalign.Cohesion.Web.Validation.Tests.TestObjects;

internal sealed class ValidationOrder
{
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("ship_to")]
    public Address Address { get; set; } = new();

    public ValidationLine[] Items { get; set; } = [];

    public ValidationOrder[] Batches { get; set; } = [];

    public ValidationLine[][] Matrix { get; set; } = [];

    [JsonPropertyName("")]
    public Address EmptyNamedAddress { get; set; } = new();
}

internal sealed class ValidationLine
{
    public int Quantity { get; set; }
}

internal sealed class ValidationOrderProfile : ValidationProfile<ValidationOrder>
{
    public override void Configure(IValidationRuleDescriptor<ValidationOrder> descriptor)
    {
        descriptor.RuleFor(order => order.DisplayName).NotEmpty();
        descriptor.RuleFor(order => order.Address).ChildRules(address => address.RuleFor(a => a.City!).NotEmpty());
        descriptor.RuleForEach(order => order.Items).ChildRules(item => item.RuleFor(i => i.Quantity).GreaterThan(0));
    }
}

[JsonSerializable(typeof(ValidationOrder))]
internal sealed partial class ValidationOrderJsonContext : JsonSerializerContext;
