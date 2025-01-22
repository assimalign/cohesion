using Assimalign.Extensions.Validation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;



public static partial class ValidatorTestObjectDescriptor
{
    public static void ConfigureBetweenTest(this IValidationRuleDescriptor<ValidatorTestObject> descriptor)
    {

        // Short Tests
        descriptor.RuleFor(p => p.BetweenFields.ShortEqualToSuccessField)
            .Between((short)8, (short)45);
        descriptor.RuleFor(p => p.BetweenFields.ShortEqualToFailureField)
             .Between((short)10, (short)13);

        // Int Tests
        descriptor.RuleFor(p => p.BetweenFields.IntEqualToSuccessField)
             .BetweenOrEqualTo(10, 13);
        descriptor.RuleFor(p => p.BetweenFields.IntEqualToFailureField)
             .Between(10, 13);

        // Long Tests
        descriptor.RuleFor(p => p.BetweenFields.LongEqualToSuccessField)
             .BetweenOrEqualTo(10, 13);
        descriptor.RuleFor(p => p.BetweenFields.LongEqualToFailureField)
             .Between(10, 13);

        // DateTime Tests
        descriptor.RuleFor(p => p.BetweenFields.DateTimeEqualToSuccessField)
             .Between(new DateTime(2021, 12, 1), new DateTime(2021, 12, 3));
        descriptor.RuleFor(p => p.BetweenFields.DateTimeEqualToFailureField)
            .Between(new DateTime(2021, 12, 1), new DateTime(2021, 12, 3));
    }
}



public partial class ValidatorTestObject
{
    public ValidatorTestBetweenObject BetweenFields { get; set; } = new();
}



public class ValidatorTestBetweenObject
{
    public short ShortEqualToSuccessField { get; set; } = 11;
    public short? ShortEqualToFailureField { get; set; } = 10;

    public int IntEqualToSuccessField { get; set; } = 11;
    public int IntEqualToFailureField { get; set; } = 10;

    public long? LongEqualToSuccessField { get; set; } = 11;
    public long LongEqualToFailureField { get; set; } = 10;

    public DateTime DateTimeEqualToSuccessField { get; set; } = new DateTime(2021, 12, 2);
    public DateTime DateTimeEqualToFailureField { get; set; } = new DateTime(2021, 12, 1);
}

