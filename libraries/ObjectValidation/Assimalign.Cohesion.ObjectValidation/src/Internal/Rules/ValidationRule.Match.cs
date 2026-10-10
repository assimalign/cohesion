using System;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal sealed class MatchValidationRule : ValidationRuleBase<string>
{
    /// <summary>
    /// How long one match of a caller's pattern may run before the rule fails.
    /// </summary>
    /// <remarks>
    /// The library cannot tell whether a caller's pattern backtracks without bound on crafted input, and
    /// Web.Validation runs the rule on request bodies (#1377).
    /// </remarks>
    internal static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromSeconds(1);

    private readonly Regex _regex;

    /// <summary>
    /// Builds the pattern once, with the caller's options.
    /// </summary>
    /// <param name="pattern">The caller's pattern.</param>
    /// <param name="options">The caller's options, or <see langword="null"/> for none.</param>
    /// <param name="matchTimeout">The match timeout, or <see langword="null"/> for <see cref="DefaultMatchTimeout"/>.</param>
    /// <exception cref="ArgumentException">The pattern or the options are invalid.</exception>
    public MatchValidationRule(string pattern, RegexOptions? options = null, TimeSpan? matchTimeout = null)
    {
        // An invalid pattern throws here, where the profile declares the rule. Before #1377 the static
        // Regex.IsMatch threw on every validation instead, and the catch below reported "not invoked".
        this._regex = new Regex(pattern, options ?? RegexOptions.None, matchTimeout ?? DefaultMatchTimeout);
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

