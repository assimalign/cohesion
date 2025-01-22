using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal class ValidationProfileBuilderDefault : ValidationProfileBuilder
{
    private readonly Action<IValidationProfileBuilder> configure;
    public ValidationProfileBuilderDefault(Action<IValidationProfileBuilder> configure)
    {
        this.configure = configure;
    }
    protected override void OnBuild(IValidationProfileBuilder builder)
    {
        configure.Invoke(builder);
    }
}
