using Assimalign.Extensions.Validation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


public partial class ValidatorTestValues
{
    public const int IntEqualToValue = 10;
    public const int LongEqualToValue = 10;
    public const int ShortEqualToValue = 10;
    public static DateTime DateTimeEqualTo = new DateTime(2021, 12, 1);
}


public static partial class ValidatorTestObjectDescriptor
{
    public static void ConfigureEqualToTest(this IValidationRuleDescriptor<ValidatorTestObject> descriptor)
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
    }
}


public partial class ValidatorTestObject
{
    public ValidatorTestEqualToObject EqualToFields { get; set; } = new();
}


public class ValidatorTestEqualToObject
{
    public short ShortEqualToSuccessField { get; set; } = 10;
    public short ShortEqualToFailureField { get; set; } = 14;

    public int IntEqualToSuccessField { get; set; } = 10;
    public int IntEqualToFailureField { get; set; } = 14;

    public long? LongEqualToSuccessField { get; set; } = 10;
    public long LongEqualToFailureField { get; set; } = 14;

    public DateTime DateTimeEqualToSuccessField { get; set; } = new DateTime(2021, 12, 1);
    public DateTime DateTimeEqualToFailureField { get; set; } = new DateTime(2021, 12, 2);
}
