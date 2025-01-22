using Assimalign.Extensions.Validation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


public class ValidatorTestProfile : ValidationProfile<ValidatorTestObject>
{
    public override void Configure(IValidationRuleDescriptor<ValidatorTestObject> descriptor)
    {
        descriptor.ConfigureEqualToTest();
        descriptor.ConfigureBetweenTest();
        descriptor.ConfigureBetweenOrEqualToTest();
    }
}
