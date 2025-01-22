using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace  Assimalign.Cohesion.ObjectValidation.Tests;

using  Assimalign.Cohesion.ObjectValidation;
using  Assimalign.Cohesion.ObjectValidation.Internal;
using  Assimalign.Cohesion.ObjectValidation.Internal.Rules;

public class RuleBetweenOrEqualToTests : RuleBaseTest
{

    public IValidationContext RunBetweenOrEqualToTest<TValue>(object testValue, TValue lower, TValue upper)
        where TValue : struct, IComparable, IComparable<TValue>
    {
        var rule = new BetweenOrEqualToValidationRule<TValue>(lower, upper)
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
#if NET6_0_OR_GREATER

        var context = this.RunBetweenOrEqualToTest(new DateOnly(2022, 1, 1), new DateOnly(2022, 1, 2), new DateOnly(2022, 1, 3));
        Assert.Single(context.Errors);
#endif
    }

    [Fact]
    public override void DateOnlySuccessTest()
    {
#if NET6_0_OR_GREATER
        var context = this.RunBetweenOrEqualToTest(new DateOnly(2022, 1, 1), new DateOnly(2021, 1, 1), new DateOnly(2022, 1, 3));
        Assert.Empty(context.Errors);
#endif
    }

    [Fact]
    public override void DateTimeFailureTest()
    {
        var context = this.RunBetweenOrEqualToTest(new DateTime(2022, 1, 1, 1, 0, 1), new DateTime(2022, 1, 1, 1, 0, 2), new DateTime(2022, 1, 1, 1, 0, 4));
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DateTimeSuccessTest()
    {
        var context = this.RunBetweenOrEqualToTest(new DateTime(2022, 1, 1, 1, 0, 0), new DateTime(2022, 1, 1, 1, 0, 0), new DateTime(2022, 1, 1, 1, 0, 3));
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DecimalFailureTest()
    {
        var context = this.RunBetweenOrEqualToTest((decimal)0.1, (decimal)0.2, (decimal)0.3);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DecimalSucessTest()
    {
        var context = this.RunBetweenOrEqualToTest((decimal)0.1, (decimal)0.1, (decimal)0.3);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DoubleFailureTest()
    {
        var context = this.RunBetweenOrEqualToTest((double)0.1, (double)0.2, (double)0.3);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DoubleSuccessTest()
    {
        var context = this.RunBetweenOrEqualToTest((double)0.2, (double)0.1, (double)0.3);
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
        var context = this.RunBetweenOrEqualToTest<short>((short)1, 2, 3);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int16SuccessTest()
    {
        var context = this.RunBetweenOrEqualToTest<short>((short)2, 1, 3);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int32FailureTest()
    {
        var context = this.RunBetweenOrEqualToTest(1, 2, 3);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int32SuccessTest()
    {
        var context = this.RunBetweenOrEqualToTest(2, 1, 3);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int64FailureTest()
    {
        var context = this.RunBetweenOrEqualToTest((long)20000003, (long)20000000, (long)20000002);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int64SuccessTest()
    {
        var context = this.RunBetweenOrEqualToTest((long)20000000, (long)19999999, (long)20000002);
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

    public override void SingleFailureTest()
    {
        throw new NotImplementedException();
    }

    public override void SingleSuccessTest()
    {
        throw new NotImplementedException();
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

