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

public class RuleGreaterThanEqualToTests : RuleBaseTest
{

    public IValidationContext RunGreaterThanOrEqualToTest<TValue>(object testValue, TValue value)
        where TValue : struct, IComparable, IComparable<TValue>
    {
        var rule = new GreaterThanOrEqualToValidationRule<TValue>(value)
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
        var context = this.RunGreaterThanOrEqualToTest(new DateOnly(2022, 1, 1), new DateOnly(2022, 1, 2));
        Assert.Single(context.Errors);
#endif
    }

    [Fact]
    public override void DateOnlySuccessTest()
    {
#if NET6_0_OR_GREATER
        var context = this.RunGreaterThanOrEqualToTest(new DateOnly(2022, 1, 3), new DateOnly(2022, 1, 2));
        Assert.Empty(context.Errors);
#endif
    }

    [Fact]
    public override void DateTimeFailureTest()
    {
        var context = this.RunGreaterThanOrEqualToTest(new DateTime(2022, 1, 1, 1, 1, 1), new DateTime(2022, 1, 1, 1, 1, 2));
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DateTimeSuccessTest()
    {
        var context = this.RunGreaterThanOrEqualToTest(new DateTime(2022, 1, 1, 1, 1, 2), new DateTime(2022, 1, 1, 1, 1, 2));
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DecimalFailureTest()
    {
        var context = this.RunGreaterThanOrEqualToTest((decimal)0.1, (decimal)0.2);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DecimalSucessTest()
    {
        var context = this.RunGreaterThanOrEqualToTest((decimal)0.2, (decimal)0.1);
        Assert.Empty(context.Errors);
    }


    [Fact]
    public override void DoubleFailureTest()
    {
        var context = this.RunGreaterThanOrEqualToTest((double)0.1, (double)0.2);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DoubleSuccessTest()
    {
        var context1 = this.RunGreaterThanOrEqualToTest((double)0.2, (double)0.2);
        var context2 = this.RunGreaterThanOrEqualToTest((double)0.3, (double)0.2);
        Assert.Empty(context1.Errors);
        Assert.Empty(context2.Errors);
    }

    [Fact]
    public override void GuidFailureTest()
    {
        var guid1 = SequentialGuid.New();
        var guid2 = SequentialGuid.New();
        var context = this.RunGreaterThanOrEqualToTest(guid1, guid2);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void GuidSuccessTest()
    {
        var guid = Guid.NewGuid();
        var context = this.RunGreaterThanOrEqualToTest(guid, guid);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int16FailureTest()
    {
        var context = this.RunGreaterThanOrEqualToTest((short)-3, (short)-2);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int16SuccessTest()
    {
        var context = this.RunGreaterThanOrEqualToTest((short)-2, (short)-2);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int32FailureTest()
    {
        var context = this.RunGreaterThanOrEqualToTest(-3, -2);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int32SuccessTest()
    {
        var context = this.RunGreaterThanOrEqualToTest(-2, -2);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int64FailureTest()
    {
        var context = this.RunGreaterThanOrEqualToTest((long)-3, (long)-2);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int64SuccessTest()
    {
        var context = this.RunGreaterThanOrEqualToTest((long)-2, (long)-2);
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

