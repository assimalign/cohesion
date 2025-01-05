using Assimalign.Extensions.Validation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;



public static partial class ValidatorTestObjectDescriptor
{
    public static void ConfigureBetweenOrEqualToTest(this IValidationRuleDescriptor<ValidatorTestObject> descriptor)
    {
        // Short Tests
        descriptor.RuleFor(p => p.BetweenOrEqualToFields.ShortEqualToSuccessField)
            .BetweenOrEqualTo((short)10, (short)13);
        descriptor.RuleFor(p => p.BetweenOrEqualToFields.ShortEqualToFailureField)
            .BetweenOrEqualTo((short)10, (short)13);

        // Int Tests
        descriptor.RuleFor(p => p.BetweenOrEqualToFields.IntEqualToSuccessField)
            .BetweenOrEqualTo(10, 13);
        descriptor.RuleFor(p => p.BetweenOrEqualToFields.IntEqualToFailureField)
            .BetweenOrEqualTo(10, 13);

        // Long Tests
        descriptor.RuleFor(p => p.BetweenOrEqualToFields.LongEqualToSuccessField)
            .BetweenOrEqualTo(10, 13);
        descriptor.RuleFor(p => p.BetweenOrEqualToFields.LongEqualToFailureField)
            .BetweenOrEqualTo(10, 13);

        // DateTime Tests
        descriptor.RuleFor(p => p.BetweenOrEqualToFields.DateTimeEqualToSuccessField)
            .BetweenOrEqualTo(new DateTime(2021, 12, 1), new DateTime(2021, 12, 2));
        descriptor.RuleFor(p => p.BetweenOrEqualToFields.DateTimeEqualToFailureField)
            .BetweenOrEqualTo(new DateTime(2021, 12, 1), new DateTime(2021, 12, 2));
    }
}



public partial class ValidatorTestObject
{
    public ValidatorTestBetweenOrEqualToObject BetweenOrEqualToFields { get; set; } = new();
}



public class ValidatorTestBetweenOrEqualToObject
{
    public short ShortEqualToSuccessField { get; set; } = 10;
    public short? ShortEqualToFailureField { get; set; } = 14;

    public int IntEqualToSuccessField { get; set; } = 10;
    public int IntEqualToFailureField { get; set; } = 14;

    public long? LongEqualToSuccessField { get; set; } = 10;
    public long LongEqualToFailureField { get; set; } = 14;

    public DateTime DateTimeEqualToSuccessField { get; set; } = new DateTime(2021, 12, 1);
    public DateTime DateTimeEqualToFailureField { get; set; } = new DateTime(2021, 12, 3);
}

