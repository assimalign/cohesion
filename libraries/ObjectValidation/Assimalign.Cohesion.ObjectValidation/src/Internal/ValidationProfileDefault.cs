using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal class ValidationProfileDefault<TValue> : ValidationProfile<TValue>
{
    private readonly Action<IValidationRuleDescriptor<TValue>> _configure;

    public ValidationProfileDefault(Action<IValidationRuleDescriptor<TValue>> configure)
    {
        this._configure = configure;
    }
    public override void Configure(IValidationRuleDescriptor<TValue> descriptor)
    {
        _configure.Invoke(descriptor);
    }
}
