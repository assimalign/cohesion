using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using FluentValidation;

var validator1 = Validator.Create(configure =>
{
    configure.AddProfile(builder =>
    {
        builder.CreateProfile<ValidatorTestObject>(descriptor =>
        {
            // Short Tests
            descriptor.RuleFor(p => p.EqualToFields.ShortEqualToSuccessField)
                .EqualTo(ValidatorTestValues.ShortEqualToValue);

            descriptor.RuleFor(p => p.EqualToFields.ShortEqualToFailureField)
                .EqualTo(ValidatorTestValues.ShortEqualToValue);

            // Int Tests
            descriptor.RuleFor(p => p.EqualToFields.IntEqualToSuccessField)
                .EqualTo(ValidatorTestValues.IntEqualToValue);
            descriptor.RuleFor(p => p.EqualToFields.IntEqualToFailureField)
                .EqualTo(ValidatorTestValues.IntEqualToValue);

            // Long Tests
            descriptor.RuleFor(p => p.EqualToFields.LongEqualToSuccessField)
                .EqualTo(ValidatorTestValues.LongEqualToValue);
            descriptor.RuleFor(p => p.EqualToFields.LongEqualToFailureField)
                .EqualTo(ValidatorTestValues.LongEqualToValue);


            descriptor.RuleFor(p => p.EqualToFields.DateTimeEqualToSuccessField)
                .EqualTo(ValidatorTestValues.DateTimeEqualTo);
            descriptor.RuleFor(p => p.EqualToFields.DateTimeEqualToFailureField)
                .EqualTo(ValidatorTestValues.DateTimeEqualTo);
        });
    });
});

validator1!.Validate(new ValidatorTestObject());

///Benchmark comparison between FluentValidation and Assimalign

BenchmarkRunner.Run<ValidatorBenchmarks>();


[SimpleJob(RuntimeMoniker.Net60)]
[SimpleJob(RuntimeMoniker.Net70)]
[SimpleJob(RuntimeMoniker.Net80)]
//[SimpleJob(RuntimeMoniker.NativeAot80)]
//[SimpleJob(RuntimeMoniker.NativeAot70)]
public class ValidatorBenchmarks
{
    Assimalign.Extensions.Validation.IValidator? validator1;
    TestFluentValidator? validator2;
    ValidatorTestObject instance = new();

    [GlobalSetup]
    public void Setup()
    {
        validator1 = Validator.Create(configure =>
        {
            configure.AddProfile(builder =>
            {
                builder.CreateProfile<ValidatorTestObject>(descriptor =>
                {
                    // Short Tests
                    descriptor.RuleFor(p => p.EqualToFields.ShortEqualToSuccessField)
                        .EqualTo(ValidatorTestValues.ShortEqualToValue);

                    descriptor.RuleFor(p => p.EqualToFields.ShortEqualToFailureField)
                        .EqualTo(ValidatorTestValues.ShortEqualToValue);

                    // Int Tests
                    descriptor.RuleFor(p => p.EqualToFields.IntEqualToSuccessField)
                        .EqualTo(ValidatorTestValues.IntEqualToValue);
                    descriptor.RuleFor(p => p.EqualToFields.IntEqualToFailureField)
                        .EqualTo(ValidatorTestValues.IntEqualToValue);

                    // Long Tests
                    descriptor.RuleFor(p => p.EqualToFields.LongEqualToSuccessField)
                        .EqualTo(ValidatorTestValues.LongEqualToValue);
                    descriptor.RuleFor(p => p.EqualToFields.LongEqualToFailureField)
                        .EqualTo(ValidatorTestValues.LongEqualToValue);


                    descriptor.RuleFor(p => p.EqualToFields.DateTimeEqualToSuccessField)
                        .EqualTo(ValidatorTestValues.DateTimeEqualTo);
                    descriptor.RuleFor(p => p.EqualToFields.DateTimeEqualToFailureField)
                        .EqualTo(ValidatorTestValues.DateTimeEqualTo);
                });
            });
        });
        validator2 = new TestFluentValidator();
    }


    [Benchmark]
    public void AssimalignValidator()
    {
        validator1!.Validate(instance);
    }

    [Benchmark]
    public void FluentValidationValidator()
    {
        validator2!.Validate(instance);
    }
}


public class TestFluentValidator : AbstractValidator<ValidatorTestObject>
{
    public TestFluentValidator()
    {
        // Short Tests
        RuleFor(p => p.EqualToFields.ShortEqualToSuccessField)
            .Equals(ValidatorTestValues.ShortEqualToValue);

        RuleFor(p => p.EqualToFields.ShortEqualToFailureField)
            .Equals(ValidatorTestValues.ShortEqualToValue);

        // Int Tests
        RuleFor(p => p.EqualToFields.IntEqualToSuccessField)
            .Equals(ValidatorTestValues.IntEqualToValue);

        RuleFor(p => p.EqualToFields.IntEqualToFailureField)
            .Equals(ValidatorTestValues.IntEqualToValue);

        // Long Tests
        RuleFor(p => p.EqualToFields.LongEqualToSuccessField)
            .Equals(ValidatorTestValues.LongEqualToValue);

        RuleFor(p => p.EqualToFields.LongEqualToFailureField)
            .Equals(ValidatorTestValues.LongEqualToValue);


        RuleFor(p => p.EqualToFields.DateTimeEqualToSuccessField)
            .Equals(ValidatorTestValues.DateTimeEqualTo);
        RuleFor(p => p.EqualToFields.DateTimeEqualToFailureField)
            .Equals(ValidatorTestValues.DateTimeEqualTo);
    }
}