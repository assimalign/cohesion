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


public class RuleEqualToTests : RuleBaseTest
{

    public IValidationContext RunEqualToTest<TValue>(TValue testValue, TValue comparisonValue)
    {
        var rule = new EqualToValidationRule<TValue>(comparisonValue)
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

    [Fact]
    public override void BooleanFailureTest()
    {
        var context = this.RunEqualToTest(true, false);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void BooleanSuccessTest()
    {
        var context = this.RunEqualToTest(true, true);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DateOnlyFailureTest()
    {
#if NET6_0_OR_GREATER
        var context = this.RunEqualToTest(new DateOnly(2022, 1, 3), new DateOnly(2022, 1, 1));
        Assert.Single(context.Errors);
#endif
    }

    [Fact]
    public override void DateOnlySuccessTest()
    {
#if NET6_0_OR_GREATER
        var date = new DateOnly(2022, 1, 2);
        var context = this.RunEqualToTest(date, date);
        Assert.Empty(context.Errors);
#endif
    }

    [Fact]
    public override void DateTimeFailureTest()
    {
        var dateTime = new DateTime(2022, 1, 2, 11, 4, 0);
        var context = this.RunEqualToTest(new DateTime(2022, 1, 2, 11, 4, 1), dateTime);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DateTimeSuccessTest()
    {  
        var dateTime = new DateTime(2022, 1, 2, 11, 4, 0);
        var context = this.RunEqualToTest(dateTime, dateTime);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DecimalFailureTest()
    {
        var deci = (decimal)0.3;
        var context = this.RunEqualToTest((decimal)0.4, deci);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DecimalSucessTest()
    {
        var deci = (decimal)0.3;
        var context = this.RunEqualToTest(deci, deci);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DoubleFailureTest()
    {
        var dbl = (double)0.3;
        var context = this.RunEqualToTest((double)0.4, dbl);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DoubleSuccessTest()
    {
        var dbl = (double)0.3;
        var context = this.RunEqualToTest(dbl, dbl);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void GuidFailureTest()
    {
        var guid = Guid.NewGuid();
        var context = this.RunEqualToTest(Guid.NewGuid(), guid);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void GuidSuccessTest()
    {
        var guid = Guid.NewGuid();
        var context = this.RunEqualToTest(guid, guid);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int16FailureTest()
    {
        var context = this.RunEqualToTest((short)-12, (short)13);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int16SuccessTest()
    {   
        var int16 = (short)12;
        var context = this.RunEqualToTest(int16, int16);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int32FailureTest()
    {
        var context = this.RunEqualToTest(-12, 13);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int32SuccessTest()
    {
        var int32 = (int)-12;
        var context = this.RunEqualToTest(int32, int32);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int64FailureTest()
    {
        var context = this.RunEqualToTest((long)12, (long)13);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int64SuccessTest()
    {
        var int64 = (long)-12;
        var context = this.RunEqualToTest(int64, int64);
        Assert.Empty(context.Errors);
    }

    public partial record TestRecord
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public int Age { get; set; }
    }

    [Fact]
    public override void RecordFailureTest()
    {
        var record1 = new TestRecord() { Age = 25, FirstName = "Chase", LastName = "Crawford" };
        var record2 = new TestRecord() { Age = 25, FirstName = "Chase", LastName = "crawford" };
        var context = this.RunEqualToTest(record2, record1);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void RecordSuccessTest()
    {
        var record = new TestRecord() { Age = 25, FirstName = "Chase", LastName = "Crawford" };
        var context = this.RunEqualToTest(record, record);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void SingleFailureTest()
    {
        var context = this.RunEqualToTest((float)12.1, (float)12.0);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void SingleSuccessTest()
    {
        var flt = (float)12.0;
        var context = this.RunEqualToTest(flt, flt);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void StringFailureTest()
    {
        var stringValue = "My name is Chase and I am a nerd.";
        var context = this.RunEqualToTest("My name is Chase and I am a super nerd.", stringValue);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void StringSuccessTest()
    {   
        var stringValue = "My name is Chase and I am a nerd.";
        var context = this.RunEqualToTest(stringValue, stringValue);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void TimeOnlyFailureTest()
    {
#if NET6_0_OR_GREATER
        var time = new TimeOnly(11, 31, 0);
        var context = this.RunEqualToTest(new TimeOnly(11, 31, 1), time);
        Assert.Single(context.Errors);
#endif
    }

    [Fact]
    public override void TimeOnlySuccessTest()
    {
#if NET6_0_OR_GREATER
        var time = new TimeOnly(11, 31, 0);
        var context = this.RunEqualToTest(time, time);
        Assert.Empty(context.Errors);
#endif
    }

    [Fact]
    public override void TimeSpanFailureTest()
    {
        var current = DateTime.Now;
        var timeSpan = current.TimeOfDay;
        var context = this.RunEqualToTest(current.AddMilliseconds(1).TimeOfDay, timeSpan);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void TimeSpanSuccessTest()
    {
        var timeSpan = DateTime.Now.TimeOfDay;
        var context = this.RunEqualToTest(timeSpan, timeSpan);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void UInt16FailureTest()
    {
        var context = this.RunEqualToTest((ushort)12, (ushort)13);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void UInt16SucessTest()
    {
        var int16 = (ushort)12;
        var context = this.RunEqualToTest(int16, int16);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void UInt32FailureTest()
    {
        var context = this.RunEqualToTest((uint)12, (uint)13);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void UInt32SuccessTest()
    {
        var int32 = (uint)12;
        var context = this.RunEqualToTest(int32, int32);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void UInt64FailureTest()
    {
        var context = this.RunEqualToTest((ulong)12, (ulong)13);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void UInt64SuccessTest()
    {
        var int64 = (ulong)12;
        var context = this.RunEqualToTest(int64, int64);
        Assert.Empty(context.Errors);
    }
}

