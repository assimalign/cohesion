using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal class ValidationProfileDefault<TValue> : ValidationProfile<TValue>
{
    private readonly Action<IValidationRuleDescriptor<TValue>> configure;

    public ValidationProfileDefault(Action<IValidationRuleDescriptor<TValue>> configure)
    {
        this.configure = configure;
    }
    public override void Configure(IValidationRuleDescriptor<TValue> descriptor)
    {
        configure.Invoke(descriptor);
    }
}
