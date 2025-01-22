using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

public class ValidationProfileBuilderTests
{
    public class ValidationProfileBuilderDefault : ValidationProfileBuilder
    {
        public class TestObject1
        {
            public string Name { get; set; }
        }
        public class TestsObject2
        {
            public string Name { get; set; }
        }
        protected override void OnBuild(IValidationProfileBuilder builder)
        {
            builder
                .CreateProfile<TestObject1>(descriptor =>
                {
                    descriptor.RuleFor(p => p.Name)
                        .NotNull();
                })
                .CreateProfile<TestsObject2>(descriptor =>
                {
                    descriptor.RuleFor(p => p.Name)
                        .NotNull();
                });
        }
    }

    [Fact]
    public void TestProfileCount()
    {
        var validator = Validator.Create(builder =>
        {
            builder.AddProfile(new ValidationProfileBuilderDefault());
        });

        Assert.Equal(2, validator.Profiles.Count());
    }
    
}
