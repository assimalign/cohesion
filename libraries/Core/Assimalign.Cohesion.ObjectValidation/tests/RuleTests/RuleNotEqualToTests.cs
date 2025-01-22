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

public class RuleNotEqualToTests : RuleBaseTest
{

    public IValidationContext RunNotEqualToTest<TValue>(object testValue, TValue value)
    {
        var rule = new NotEqualToValidationRule<TValue>(value)
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

    [Fact]
    public override void BooleanFailureTest()
    {
        var context = this.RunNotEqualToTest(true, true);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void BooleanSuccessTest()
    {
        var context = this.RunNotEqualToTest(true, false);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DateOnlyFailureTest()
    {
    #if NET6_0_OR_GREATER
        var date = new DateOnly(2022, 1, 2);
        var context = this.RunNotEqualToTest(date, date);
        Assert.Single(context.Errors);
    #endif
    }

    [Fact]
    public override void DateOnlySuccessTest()
    {
    #if NET6_0_OR_GREATER
        var date = new DateOnly(2022, 1, 2);
        var context = this.RunNotEqualToTest(new DateOnly(2022,1,3), date);
        Assert.Empty(context.Errors);
    #endif
    }

    [Fact]
    public override void DateTimeFailureTest()
    {
        var dateTime = new DateTime(2022, 1, 2, 11, 4, 0);
        var context = this.RunNotEqualToTest(dateTime, dateTime);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DateTimeSuccessTest()
    {
        var dateTime = new DateTime(2022, 1, 2, 11, 4, 0);
        var context = this.RunNotEqualToTest(new DateTime(2022, 1, 2, 11, 4, 1), dateTime);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DecimalFailureTest()
    {
        var deci = (decimal)0.3;
        var context = this.RunNotEqualToTest(deci, deci);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DecimalSucessTest()
    {
        var deci = (decimal)0.3;
        var context = this.RunNotEqualToTest((decimal)0.4, deci);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void DoubleFailureTest()
    {
        var dbl = (double)0.3;
        var context = this.RunNotEqualToTest(dbl, dbl);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void DoubleSuccessTest()
    {
        var dbl = (double)0.3;
        var context = this.RunNotEqualToTest((double)0.4, dbl);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void GuidFailureTest()
    {
        var guid = Guid.NewGuid();
        var context = this.RunNotEqualToTest(guid, guid);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void GuidSuccessTest()
    {
        var guid = Guid.NewGuid();
        var context = this.RunNotEqualToTest(Guid.NewGuid(), guid);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int16FailureTest()
    {
        var int16 = (short)12;
        var context = this.RunNotEqualToTest(int16, int16);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int16SuccessTest()
    {
        var context = this.RunNotEqualToTest((short)-12, (short)13);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int32FailureTest()
    {
        var int32 = (int)-12;
        var context = this.RunNotEqualToTest(int32, int32);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int32SuccessTest()
    {
        var context = this.RunNotEqualToTest(-12, 13);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void Int64FailureTest()
    {
        var int64 = (long)-12;
        var context = this.RunNotEqualToTest(int64, int64);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void Int64SuccessTest()
    {
        var context = this.RunNotEqualToTest((long)12, (long)13);
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
        var record = new TestRecord() { Age = 25, FirstName = "Chase", LastName = "Crawford" };
        var context = this.RunNotEqualToTest(record, record);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void RecordSuccessTest()
    {
        var record1 = new TestRecord() { Age = 25, FirstName = "Chase", LastName = "Crawford" };
        var record2 = new TestRecord() { Age = 25, FirstName = "Chase", LastName = "crawford" };
        var context = this.RunNotEqualToTest(record2, record1);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void SingleFailureTest()
    {
        var flt = (float)12.0;
        var context = this.RunNotEqualToTest(flt, flt);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void SingleSuccessTest()
    {
        var context = this.RunNotEqualToTest((float)12.1, (float)12.0);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void StringFailureTest()
    {
        var stringValue = "My name is Chase and I am a nerd.";
        var context = this.RunNotEqualToTest(stringValue, stringValue);
        Assert.Single(context.Errors); 
    }

    [Fact]
    public override void StringSuccessTest()
    {
        var stringValue = "My name is Chase and I am a nerd.";
        var context = this.RunNotEqualToTest("My name is Chase and I am a super nerd.", stringValue);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void TimeOnlyFailureTest()
    {
    #if NET6_0_OR_GREATER
        var time = new TimeOnly(11, 31, 0);
        var context = this.RunNotEqualToTest(time, time);
        Assert.Single(context.Errors);
    #endif
    }

    [Fact]
    public override void TimeOnlySuccessTest()
    {
#if NET6_0_OR_GREATER
        var time = new TimeOnly(11, 31, 0);
        var context = this.RunNotEqualToTest(new TimeOnly(11, 31, 1), time);
        Assert.Empty(context.Errors);
#endif
    }

    [Fact]
    public override void TimeSpanFailureTest()
    {
        var timeSpan = DateTime.Now.TimeOfDay;
        var context = this.RunNotEqualToTest(timeSpan, timeSpan);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void TimeSpanSuccessTest()
    {
        var current = DateTime.Now;
        var timeSpan = current.TimeOfDay;
        var context = this.RunNotEqualToTest(current.AddMilliseconds(1).TimeOfDay, timeSpan);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void UInt16FailureTest()
    {
        var int16 = (ushort)12;
        var context = this.RunNotEqualToTest(int16, int16);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void UInt16SucessTest()
    {
        var context = this.RunNotEqualToTest((ushort)12, (ushort)13);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void UInt32FailureTest()
    {
        var int32 = (uint)12;
        var context = this.RunNotEqualToTest(int32, int32);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void UInt32SuccessTest()
    {
        var context = this.RunNotEqualToTest((uint)12, (uint)13);
        Assert.Empty(context.Errors);
    }

    [Fact]
    public override void UInt64FailureTest()
    {
        var int64 = (ulong)12;
        var context = this.RunNotEqualToTest(int64, int64);
        Assert.Single(context.Errors);
    }

    [Fact]
    public override void UInt64SuccessTest()
    {
        var context = this.RunNotEqualToTest((ulong)12, (ulong)13);
        Assert.Empty(context.Errors);
    }
}
