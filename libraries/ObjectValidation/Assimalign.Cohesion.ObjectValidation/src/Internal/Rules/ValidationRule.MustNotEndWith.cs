using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ObjectValidation.Internal;


[global::System.Diagnostics.DebuggerDisplay("MustNotEndWithValidationRule {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class MustNotEndWithValidationRule : ValidationRuleBase<string>
{
    private readonly string _value;
    private readonly StringComparison _comparison;

    public MustNotEndWithValidationRule(string value, StringComparison comparison = StringComparison.InvariantCulture)
    {
        this._value = value;
        this._comparison = comparison;
    }

    public override string Name { get; set; }

    public override bool TryValidate(object value, out IValidationContext context)
    {
        if (value is null)
        {
            context = new ValidationContext<string>(default);
            context.AddFailure(this.Error);
            return true;
        }
        else if (value is string str)
        {
            return TryValidate(str, out context);
        }
        else
        {
            context = null;
            return false;
        }
    }

    public override bool TryValidate(string value, out IValidationContext context)
    {
        try
        {
            context = new ValidationContext<string>(value);

            if (value.EndsWith(this._value, _comparison))
            {
                context.AddFailure(this.Error);
            }

            return true;
        }
        catch
        {
            context = null;
            return false;
        }
    }

    private sealed class DebugView
    {
        private readonly MustNotEndWithValidationRule _target;

        public DebugView(MustNotEndWithValidationRule value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public global::System.Type ValueType => _target.ValueType;
        public global::Assimalign.Cohesion.ObjectValidation.IValidationError Error => _target.Error;
        public global::Assimalign.Cohesion.ObjectValidation.IValidationContext ParentContext => _target.ParentContext;
    }
}