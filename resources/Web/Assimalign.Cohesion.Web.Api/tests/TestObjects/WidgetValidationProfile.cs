namespace Assimalign.Cohesion.Web.Api.Tests.TestObjects;

using Assimalign.Cohesion.ObjectValidation;

/// <summary>Validates that a <see cref="Widget"/> carries a non-empty name.</summary>
internal sealed class WidgetValidationProfile : ValidationProfile<Widget>
{
    /// <inheritdoc />
    public override void Configure(IValidationRuleDescriptor<Widget> descriptor)
    {
        descriptor.RuleFor(widget => widget.Name)
            .NotEmpty();
    }
}
