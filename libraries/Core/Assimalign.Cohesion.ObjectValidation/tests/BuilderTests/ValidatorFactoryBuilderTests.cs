using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

public class ValidatorFactoryBuilderTests
{
    [Fact]
    public void TestFactoryBuilder()
    {
        var factory = ValidatorFactoryBuilder.Create(builder =>
        {
            builder.AddValidator("default", builder =>
            {
                builder.AddProfile<ProfileBuilderTest>();
            });
        });


        var validator = factory.CreateValidator("default");

        var result = validator.Validate(new Test());

        Assert.False(result.IsValid);
    }

    private partial class Test
    {
        public string FirstName { get; set; }
    }
    public class ProfileBuilderTest : ValidationProfileBuilder
    {
        protected override void OnBuild(IValidationProfileBuilder builder)
        {
            builder.CreateProfile<Test>(descriptor =>
            {
                descriptor.RuleFor(p => p.FirstName)
                    .NotNull()
                    .NotEmpty();
            });
        }
    }
}
