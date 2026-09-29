using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal class ValidationProfileBuilderDefault : ValidationProfileBuilder
{
    private readonly Action<IValidationProfileBuilder> _configure;
    public ValidationProfileBuilderDefault(Action<IValidationProfileBuilder> configure)
    {
        this._configure = configure;
    }
    protected override void OnBuild(IValidationProfileBuilder builder)
    {
        _configure.Invoke(builder);
    }
}
