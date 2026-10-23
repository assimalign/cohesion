using System;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.ObjectValidation.Internal;


[global::System.Diagnostics.DebuggerDisplay("LengthMinValidationRule {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(LengthMinValidationRule<>.DebugView))]
internal sealed class LengthMinValidationRule<TValue> : ValidationRuleBase<TValue>
    where TValue : IEnumerable
{
    private readonly int _length;

    public LengthMinValidationRule(int length)
    {
        this._length = length;
    }

    public override string Name { get; set; }

    public override bool TryValidate(object value, out IValidationContext context)
    {
        context = null;

        if (value is null)
        {
            context = new ValidationContext<TValue>(default(TValue));
            context.AddFailure(this.Error);
            return true;
        }
        else if (value is TValue tv)
        {
            return TryValidate(tv, out context);
        }
        else
        {
            return false;
        }
    }

    public override bool TryValidate(TValue value, out IValidationContext context)
    {
        try
        {
            context = new ValidationContext<TValue>(value);

            if (IsUnderMinLength(value))
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

    private bool IsUnderMinLength(object member)
    {
        return member switch
        {
            null => true,
            string stringValue      => stringValue.Length < this._length,
            ICollection collection  when collection.Count < this._length => true,
            Array array             when array.Length < this._length => true,
            IEnumerable enumerable  when enumerable.Cast<object>().Count() < this._length => true,
            _ => false
        };
    }

    private sealed class DebugView
    {
        private readonly LengthMinValidationRule<TValue> _target;

        public DebugView(LengthMinValidationRule<TValue> value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public global::System.Type ValueType => _target.ValueType;
        public global::Assimalign.Cohesion.ObjectValidation.IValidationError Error => _target.Error;
        public global::Assimalign.Cohesion.ObjectValidation.IValidationContext ParentContext => _target.ParentContext;
    }
}

