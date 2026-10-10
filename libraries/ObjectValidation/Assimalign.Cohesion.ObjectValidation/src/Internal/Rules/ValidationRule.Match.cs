using System;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal sealed class MatchValidationRule : ValidationRuleBase<string>
{
    /// <summary>
    /// How long one match of a caller's pattern may run before the rule fails.
    /// </summary>
    /// <remarks>
    /// A pattern the non-backtracking engine cannot take may backtrack without bound on crafted input, and
    /// Web.Validation runs the rule on request bodies (#1377). The budget is per match: <c>RuleForEach</c> runs
    /// the rule on every element, so a collection of N values may cost N budgets.
    /// </remarks>
    internal static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromSeconds(1);

    private readonly Regex _regex;

    /// <summary>
    /// Builds the pattern once, with the caller's options, for the non-backtracking engine when it supports the
    /// pattern and for the backtracking engine otherwise.
    /// </summary>
    /// <param name="pattern">The caller's pattern.</param>
    /// <param name="options">The caller's options, or <see langword="null"/> for none.</param>
    /// <param name="matchTimeout">The match timeout, or <see langword="null"/> for <see cref="DefaultMatchTimeout"/>.</param>
    /// <exception cref="ArgumentException">The pattern or the options are invalid.</exception>
    public MatchValidationRule(string pattern, RegexOptions? options = null, TimeSpan? matchTimeout = null)
    {
        this._regex = Create(pattern, options ?? RegexOptions.None, matchTimeout ?? DefaultMatchTimeout);
    }

    private static Regex Create(string pattern, RegexOptions options, TimeSpan matchTimeout)
    {
        // The non-backtracking engine runs in time linear in the value's length, and for IsMatch it accepts the
        // values the backtracking engine does, so a pattern it supports cannot be made to backtrack. The timeout
        // stays as a backstop.
        try
        {
            return new Regex(pattern, options | RegexOptions.NonBacktracking, matchTimeout);
        }
        catch (NotSupportedException)
        {
            // A backreference, lookaround, atomic group, conditional, balancing group or \G.
        }
        catch (ArgumentOutOfRangeException)
        {
            // RightToLeft or ECMAScript, which the engine does not take. An option set or timeout that is invalid
            // for both engines throws again below.
        }

        // An invalid pattern throws here or above, where the profile declares the rule. Before #1377 the static
        // Regex.IsMatch threw on every validation instead, and the catch in TryValidate reported "not invoked".
        return new Regex(pattern, options, matchTimeout);
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

            if (!this.IsMatch(value))
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

    private bool IsMatch(string value)
    {
        try
        {
            return this._regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            // A match that runs out of time fails the rule. The catch-all above would report "not invoked",
            // which passes any input slow enough to time out.
            return false;
        }
    }
}

