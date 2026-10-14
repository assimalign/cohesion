using Assimalign.Cohesion.ObjectValidation;

namespace Assimalign.Cohesion.Web.AotGuard;

/// <summary>
/// The validation profile for <see cref="GuardItem"/>: a posted item must have a name. Web.Validation
/// runs it on every typed endpoint that binds a <see cref="GuardItem"/> body.
/// </summary>
internal sealed class GuardItemProfile : ValidationProfile<GuardItem>
{
    public override void Configure(IValidationRuleDescriptor<GuardItem> descriptor)
    {
        descriptor.RuleFor(item => item.Name).NotEmpty();
    }
}
