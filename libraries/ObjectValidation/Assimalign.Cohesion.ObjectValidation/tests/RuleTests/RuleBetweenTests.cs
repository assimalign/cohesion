using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;


namespace  Assimalign.Cohesion.ObjectValidation.Tests;

using  Assimalign.Cohesion.ObjectValidation;
using  Assimalign.Cohesion.ObjectValidation.Internal;
using  Assimalign.Cohesion.ObjectValidation.Internal.Rules;

public class RuleBetweenTests : RuleBaseTest
{
    public IValidationContext RunBetweenTest<TValue>(object testValue, TValue lower, TValue upper)
        where TValue : struct, IComparable, IComparable<TValue>
    {
        var rule = new BetweenValidationRule<TValue>(lower, upper)
        {
            Error = new ValidationError()
            {

            }
        };

        if (rule.TryValidate(testValue, out var context))
        {
            return context;
        }
        else
        {
            throw new Exception();
        }
    }

    public override void BooleanFailureTest()
    {
        throw new NotImplementedException();
    }

    public override void BooleanSuccessTest()
    {
        throw new NotImplementedException();
    }


    [Fact]
    public override void DateOnlyFailureTest()
    {
        var context = this.RunBetweenTest(new DateOnly(2022, 1, 1), new DateOnly(2022, 1, 1), new DateOnly(2022, 1, 3));
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DateOnlySuccessTest()
    {
        var context = this.RunBetweenTest(new DateOnly(2022, 1, 1), new DateOnly(2021, 1, 1), new DateOnly(2022, 1, 3));
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DateTimeFailureTest()
    {
        var context = this.RunBetweenTest(new DateTime(2021, 1, 1, 1,0,0), new DateTime(2022, 1, 1,2,0,0), new DateTime(2022, 1, 1,3,0,0));
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DateTimeSuccessTest()
    {
        var context = this.RunBetweenTest(new DateTime(2022, 1, 1, 2, 0, 0), new DateTime(2022, 1, 1, 1, 0, 0), new DateTime(2022, 1, 1, 3, 0, 0));
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DecimalFailureTest()
    {
        var context = this.RunBetweenTest((decimal)0.2, (decimal)0.2, (decimal)0.3);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DecimalSucessTest()
    {
        var context = this.RunBetweenTest((decimal)0.2, (decimal)0.1, (decimal)0.3);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DoubleFailureTest()
    {
        var context = this.RunBetweenTest((double)0.2, (double)0.2, (double)0.3);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DoubleSuccessTest()
    {
        var context = this.RunBetweenTest((double)0.2, (double)0.1, (double)0.3);
        Assert.Empty(context.Errors);
    }

    public override void GuidFailureTest()
    {
        throw new NotImplementedException();
    }

    public override void GuidSuccessTest()
    {
        throw new NotImplementedException();
    }

    [Fact]
    public override void Int16FailureTest()
    {
        var context = this.RunBetweenTest<short>((short)2, 2, 3);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int16SuccessTest()
    {
        var context = this.RunBetweenTest<short>((short)2, 1, 3);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int32FailureTest()
    {
        var context = this.RunBetweenTest(2, 2, 3);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int32SuccessTest()
    {
        var context = this.RunBetweenTest(2, 1, 3);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int64FailureTest()
    {
        var context = this.RunBetweenTest((long)20000000, (long)20000000, (long)20000002);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int64SuccessTest()
    {
        var context = this.RunBetweenTest((long)20000000, (long)19999999, (long)20000002);
        Assert.Empty(context.Errors);
    }

    public override void RecordFailureTest()
    {
        throw new NotImplementedException();
    }

    public override void RecordSuccessTest()
    {
        throw new NotImplementedException();
    }

    [Fact]
    public override void SingleFailureTest()
    {
        var context = this.RunBetweenTest<Single>((float)12, 12, 14);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void SingleSuccessTest()
    {
        var context = this.RunBetweenTest<Single>((float)12, 11, 14);
        Assert.Empty(context.Errors);
    }

    public override void StringFailureTest()
    {
        throw new NotImplementedException();
    }

    public override void StringSuccessTest()
    {
        throw new NotImplementedException();
    }

    public override void TimeOnlyFailureTest()
    {
        throw new NotImplementedException();
    }

    public override void TimeOnlySuccessTest()
    {
        throw new NotImplementedException();
    }

    public override void TimeSpanFailureTest()
    {
        throw new NotImplementedException();
    }

    public override void TimeSpanSuccessTest()
    {
        throw new NotImplementedException();
    }

    public override void UInt16FailureTest()
    {
        throw new NotImplementedException();
    }

    public override void UInt16SucessTest()
    {
        throw new NotImplementedException();
    }

    public override void UInt32FailureTest()
    {
        throw new NotImplementedException();
    }

    public override void UInt32SuccessTest()
    {
        throw new NotImplementedException();
    }

    public override void UInt64FailureTest()
    {
        throw new NotImplementedException();
    }

    public override void UInt64SuccessTest()
    {
        throw new NotImplementedException();
    }
}