using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

[global::System.Diagnostics.DebuggerDisplay("ValidationProfileBuilderDefault")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        public DebugView(ValidationProfileBuilderDefault value)
        {
        }
    }
}
