namespace  Assimalign.Cohesion.ObjectValidation.Tests;


public abstract class RuleBaseTest 
{
    public abstract void Int16SuccessTest();
    public abstract void Int16FailureTest();
    public abstract void Int32SuccessTest();
    public abstract void Int32FailureTest();
    public abstract void Int64SuccessTest();
    public abstract void Int64FailureTest();
    public abstract void UInt16SucessTest();
    public abstract void UInt16FailureTest();
    public abstract void UInt32SuccessTest();
    public abstract void UInt32FailureTest();
    public abstract void UInt64SuccessTest();
    public abstract void UInt64FailureTest();
    public abstract void SingleSuccessTest();
    public abstract void SingleFailureTest();
    public abstract void DecimalSucessTest();
    public abstract void DecimalFailureTest();
    public abstract void DoubleSuccessTest();
    public abstract void DoubleFailureTest();
    public abstract void DateTimeSuccessTest();
    public abstract void DateTimeFailureTest();
    public abstract void DateOnlySuccessTest();
    public abstract void DateOnlyFailureTest();
    public abstract void TimeSpanSuccessTest();
    public abstract void TimeSpanFailureTest();
    public abstract void TimeOnlySuccessTest();
    public abstract void TimeOnlyFailureTest();
    public abstract void StringSuccessTest();
    public abstract void StringFailureTest();
    public abstract void RecordSuccessTest();
    public abstract void RecordFailureTest();
    public abstract void GuidSuccessTest();
    public abstract void GuidFailureTest();
    public abstract void BooleanSuccessTest();
    public abstract void BooleanFailureTest();
}