using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace  Assimalign.Cohesion.ObjectValidation.Tests;

using  Assimalign.Cohesion.ObjectValidation;
using  Assimalign.Cohesion.ObjectValidation.Internal.Rules;

public class RuleEndsWithTests
{
    public IValidationContext RunEndsWithTest(string value, string input, StringComparison comparison = StringComparison.InvariantCulture)
    {
        var rule = new MustEndWithValidationRule(value, comparison)
        {
            Error = new ValidationError()
            {

            }
        };

        if (rule.TryValidate((object)input, out var context))
        {
            return context;
        }
        else
        {
            throw new Exception("Unable to validate.");
        }
    }

    [Fact]
    public void StringSuccessEndsWithCaseInsensitive()
    {
        var context = RunEndsWithTest("crawford", "Chase Crawford", StringComparison.InvariantCultureIgnoreCase);

        Assert.Empty(context.Errors);
    }

    [Fact]
    public void StringSuccessEndsWithCaseSensitive()
    {
        var context = RunEndsWithTest("Crawford", "Chase Crawford");

        Assert.Empty(context.Errors);
    }

    [Fact]
    public void StringFailureEndsWithCaseInsensitive()
    {
        var context = RunEndsWithTest("crawfor", "Chase Crawford", StringComparison.InvariantCultureIgnoreCase);

        Assert.Single(context.Errors);
    }

    [Fact]
    public void StringFailureEndsWithCaseSensitive()
    {
        var context = RunEndsWithTest("crawford", "Chase Crawford");

        Assert.Single(context.Errors);
    }
}

