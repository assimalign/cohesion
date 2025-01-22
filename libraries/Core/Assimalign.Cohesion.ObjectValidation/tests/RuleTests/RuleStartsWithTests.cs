
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace  Assimalign.Cohesion.ObjectValidation.Tests;

using  Assimalign.Cohesion.ObjectValidation;
using  Assimalign.Cohesion.ObjectValidation.Internal.Rules;

public class RuleStartsWithTests
{
    public IValidationContext RunStartsWithTest(string value, string input, StringComparison comparison = StringComparison.InvariantCulture)
    {
        var rule = new MustStartWithValidationRule(value, comparison)
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
    public void StringSuccessStartsWithCaseInsensitive()
    {
        var context = RunStartsWithTest("chase", "Chase Crawford", StringComparison.InvariantCultureIgnoreCase);

        Assert.Empty(context.Errors);
    }

    [Fact]
    public void StringSuccessStartsWithCaseSensitive()
    {
        var context = RunStartsWithTest("Chase", "Chase Crawford");

        Assert.Empty(context.Errors);
    }

    [Fact]
    public void StringFailureStartsWithCaseInsensitive()
    {
        var context = RunStartsWithTest("hase", "Chase Crawford", StringComparison.InvariantCultureIgnoreCase);

        Assert.Single(context.Errors);
    }

    [Fact]
    public void StringFailureStartsWithCaseSensitive()
    {
        var context = RunStartsWithTest("chase", "Chase Crawford");

        Assert.Single(context.Errors);
    }
}

