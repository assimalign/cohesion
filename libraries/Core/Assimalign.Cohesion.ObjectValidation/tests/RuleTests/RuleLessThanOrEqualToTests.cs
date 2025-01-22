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

public class RuleLessThanOrEqualToTests : RuleBaseTest
{
    public IValidationContext RunLessThanOrEqualToTest<TValue>(object testValue, TValue value)
        where TValue : struct, IComparable, IComparable<TValue>
    {
        var rule = new LessThanOrEqualToValidationRule<TValue>(value)
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
            throw new Exception("Unable to validate.");
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
        var context = this.RunLessThanOrEqualToTest(new DateOnly(2022, 1, 2), new DateOnly(2022, 1, 1));
        Assert.Single(context.Errors);
#endif
    }

    [Fact]
    public override void DateOnlySuccessTest()
    {
#if NET6_0_OR_GREATER
        var context = this.RunLessThanOrEqualToTest(new DateOnly(2022, 1, 2), new DateOnly(2022, 1, 2));
        Assert.Empty(context.Errors);
#endif
    }

    [Fact]
    public override void DateTimeFailureTest()
    {
        var context = this.RunLessThanOrEqualToTest(new DateTime(2022, 1, 1, 1, 1, 3), new DateTime(2022, 1, 1, 1, 1, 2));
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DateTimeSuccessTest()
    {
        var context = this.RunLessThanOrEqualToTest(new DateTime(2022, 1, 1, 1, 1, 1), new DateTime(2022, 1, 1, 1, 1, 1));
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DecimalFailureTest()
    {
        var context = this.RunLessThanOrEqualToTest((decimal)0.3, (decimal)0.2);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DecimalSucessTest()
    {
        var context1 = this.RunLessThanOrEqualToTest((decimal)0.2, (decimal)0.2);
        var context2 = this.RunLessThanOrEqualToTest((decimal)0.1, (decimal)0.2);
        Assert.Empty(context1.Errors);
        Assert.Empty(context2.Errors);
    }

    [Fact]
    public override void DoubleFailureTest()
    {
        var context = this.RunLessThanOrEqualToTest((double)0.3, (double)0.2);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DoubleSuccessTest()
    {
        var context1 = this.RunLessThanOrEqualToTest((double)0.2, (double)0.2);
        var context2 = this.RunLessThanOrEqualToTest((double)0.1, (double)0.2);
        Assert.Empty(context1.Errors);
        Assert.Empty(context2.Errors);
    }

    [Fact]
    public override void GuidFailureTest()
    {
        var guid1 = SequentialGuid.New();
        var guid2 = SequentialGuid.New();
        var context = this.RunLessThanOrEqualToTest(guid2, guid1);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void GuidSuccessTest()
    {
        var guid = Guid.NewGuid();
        var context = this.RunLessThanOrEqualToTest(guid, guid);
        Assert.Empty(context.Errors);
    }


    [Fact]
    public override void Int16FailureTest()
    {
        var context = this.RunLessThanOrEqualToTest((short)0, (short)-2);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int16SuccessTest()
    {
        var context = this.RunLessThanOrEqualToTest((short)-2, (short)0);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int32FailureTest()
    {
        var context = this.RunLessThanOrEqualToTest(-1, -2);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int32SuccessTest()
    {
        var context = this.RunLessThanOrEqualToTest(0, 0);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int64FailureTest()
    {
        var context = this.RunLessThanOrEqualToTest((long)0, (long)-2);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int64SuccessTest()
    {
        var context = this.RunLessThanOrEqualToTest((long)-2, (long)-2);
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
