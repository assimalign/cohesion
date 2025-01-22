using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

using Assimalign.Cohesion.ObjectValidation;

public class ValidatorExceptionTests
{
    public class TestObject
    {
        public string FirstName { get; set; }
    }

    public class TestObjectValidationProfile : ValidationProfile<TestObject>
    {
        private Action<IValidationRuleDescriptor<TestObject>> descriptor;
        public TestObjectValidationProfile(Action<IValidationRuleDescriptor<TestObject>> descriptor)
        {
            this.descriptor = descriptor;
        }
        public override void Configure(IValidationRuleDescriptor<TestObject> descriptor)
        {
            this.descriptor.Invoke(descriptor);
        }
    }

    [Fact] 
    public void ExceptionThrowsOnDuplicateValidationProfileFromValidatorCreateTest()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            var validator = Validator.Create(configure =>
            {
                configure.AddProfile(new TestObjectValidationProfile(descriptor =>
                {
                    descriptor.RuleFor(p => p.FirstName)
                        .NotEmpty();
                }));

                configure.AddProfile(new TestObjectValidationProfile(descriptor =>
                {
                    descriptor.RuleFor(p => p.FirstName)
                        .NotEmpty();
                }));
            });
        });
    }

}
