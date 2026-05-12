using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace  Assimalign.Cohesion.ObjectValidation.Tests;

using  Assimalign.Cohesion.ObjectValidation;
using  Assimalign.Cohesion.ObjectValidation.Internal;
using  Assimalign.Cohesion.ObjectValidation.Internal.Rules;

public class RuleLengthExactTests
{
    public IValidationContext RunLengthExactTest<TValue>(int exact, TValue value)
        where TValue : IEnumerable
    {
        var rule = new LengthValidationRule<TValue>(exact)
        {
            Error = new ValidationError()
            {

            }
        };

        if (rule.TryValidate((object)value, out var context))
        {
            return context;
        }
        else
        {
            throw new Exception("Unable to validate");
        }
    }


    [Fact]
    public void ArrayFailureTest()
    {
        int[] array1 = new[] { 1, 2, 3, };
        var context = this.RunLengthExactTest(2, array1);
        Assert.Single(context.Errors);
    }

    [Fact]
    public void ArraySuccessTest()
    {
        int[] array1 = new[] { 1, 2, 3, };
        var context = this.RunLengthExactTest(3, array1);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public void StringFailureTest()
    {
        var str = "test value";
        var context = this.RunLengthExactTest(9, str);
        Assert.Single(context.Errors);
    }

    [Fact]
    public void StringSuccessTest()
    {
        var str = "test value";
        var context = this.RunLengthExactTest(10, str);
        Assert.Empty(context.Errors);
    }
}
