using System;
using System.Collections.Generic;
using System.Linq;


namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal sealed class ChildValidationRule<TValue> : ValidationRuleBase<TValue>
    where TValue : class
{
    private const string arrow = " => ";

    public IValidationProfile<TValue> Profile { get; set; }

    public override string Name { get; set; }

    /// <summary>
    /// The source of the member the nested profile validates, in the default form a rule records
    /// (<c>p =&gt; p.Address</c>). A nested error whose source is in that form is reported under it
    /// (<c>p =&gt; p.Address.City</c>, not <c>a =&gt; a.City</c>), so errors on equally named members of
    /// different nested objects stay distinguishable. <see langword="null"/> keeps nested sources as
    /// the nested rules recorded them.
    /// </summary>
    public string? ParentSource { get; set; }

    /// <summary>
    /// Composes a nested member error's source under the parent member's source. Only the default
    /// <c>x =&gt; x.Member</c> form is composed; a source a profile set explicitly is kept as written.
    /// </summary>
    internal static string? ComposeSource(string? parentSource, string? nestedSource)
    {
        if (!TrySplitMemberSource(parentSource, out string parentParameter, out string parentPath)
            || !TrySplitMemberSource(nestedSource, out _, out string nestedPath))
        {
            return nestedSource;
        }

        return parentParameter + arrow + parentParameter + "." + parentPath + "." + nestedPath;
    }

    // Splits a member selector's text, "p => p.Address.City", into its parameter ("p") and member path
    // ("Address.City"). A rule's default source is its selector expression's ToString(), and a selector
    // is always a member chain rooted at the parameter, which is the only form this accepts.
    private static bool TrySplitMemberSource(string? source, out string parameter, out string path)
    {
        parameter = string.Empty;
        path = string.Empty;

        if (string.IsNullOrEmpty(source))
        {
            return false;
        }

        int separator = source.IndexOf(arrow, StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        string candidate = source.Substring(0, separator);
        string body = source.Substring(separator + arrow.Length);

        if (body.Length <= candidate.Length + 1
            || !body.StartsWith(candidate, StringComparison.Ordinal)
            || body[candidate.Length] != '.')
        {
            return false;
        }

        parameter = candidate;
        path = body.Substring(candidate.Length + 1);
        return true;
    }

    public override bool TryValidate(object value, out IValidationContext context)
    {
        if (value is null) // No need to validate an object that is null
        {
            context = new ValidationContext<TValue>(default(TValue));
            return true;
        }
        else if (value is TValue tv)
        {
            return TryValidate(tv, out context);
        }
        else
        {
            context = null;
            return false;
        }
    }

    public override bool TryValidate(TValue value, out IValidationContext context)
    {
        try
        {
            context = new ValidationContext<TValue>(value)
            {
                ValidationMode = ParentContext.ValidationMode,
                ContinueThroughValidationChain = ParentContext.ContinueThroughValidationChain,
                ThrowExceptionOnFailure = ParentContext.ThrowExceptionOnFailure
            };

            foreach (var item in Profile.ValidationItems)
            {
                if (ParentContext.ValidationMode == ValidationMode.Stop && context.Errors.Any())
                {
                    break;
                }

                var childContext = new ValidationContext<TValue>(value)
                {
                    ValidationMode = ParentContext.ValidationMode,
                    ContinueThroughValidationChain = ParentContext.ContinueThroughValidationChain,
                    ThrowExceptionOnFailure = ParentContext.ThrowExceptionOnFailure
                };

                item.Evaluate(childContext);

                foreach (var error in childContext.Errors)
                {
                    context.AddFailure(new ValidationError()
                    {
                        Code = error.Code,
                        Message = error.Message,
                        Source = ComposeSource(ParentSource, error.Source)!
                    });
                }
            }

            return true;
        }
        catch
        {
            context = null;
            return false;
        }
    }
}

