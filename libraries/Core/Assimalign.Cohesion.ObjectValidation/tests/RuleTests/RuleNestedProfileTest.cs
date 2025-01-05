using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;


using Assimalign.Cohesion.ObjectValidation;
using Assimalign.Cohesion.ObjectValidation.Internal;
using Assimalign.Cohesion.ObjectValidation.Internal.Rules;


public class RuleNestedProfileTest
{
    public class Person
    {
        public PersonAddress PrimaryAddress { get; set; }
        public IEnumerable<PersonAddress> Addresses { get; set; }
    }

    public class PersonAddress
    {
        public string StreetOne { get; set; }
    }

    public class PersonAddressValidationProfile : ValidationProfile<PersonAddress>
    {
        public override void Configure(IValidationRuleDescriptor<PersonAddress> descriptor)
        {
            descriptor.RuleFor(p => p.StreetOne)
                .NotEmpty();
        }
    }

    public class PersonValidationProfile : ValidationProfile<Person>
    {
        public override void Configure(IValidationRuleDescriptor<Person> descriptor)
        {
            descriptor.RuleFor(p => p.PrimaryAddress)
                .UseProfile(new PersonAddressValidationProfile());

            descriptor.RuleForEach(p => p.Addresses)
                .UseProfile(new PersonAddressValidationProfile());
        }
    }


    [Fact]
    public void NestedProfileObjectFailureTest()
    {
        var person = new Person()
        {
            Addresses = new PersonAddress[]
            {
                new PersonAddress()
                {
                    StreetOne = "test"
                },
                new PersonAddress()
                {
                    StreetOne = "test"
                }
            },
            PrimaryAddress = new PersonAddress()
        };

        var validator = Validator.Create(configure =>
        {
            configure.AddProfile(new PersonValidationProfile());
        });

        var validation = validator.Validate(person);

        Assert.False(validation.IsValid);
        Assert.Equal(1, validation.Errors.Count());
    }


    [Fact]
    public void NestedProfileObjectCollectionFailureTest()
    {
        var person = new Person()
        {
            Addresses = new PersonAddress[]
            {
                new PersonAddress(),
                new PersonAddress()
            },
            PrimaryAddress = new PersonAddress()
        };

        var validator = Validator.Create(configure =>
        {
            configure.AddOptions(options =>
            {
                options.ValidationMode = ValidationMode.Cascade;
            });

            configure.AddProfile(new PersonValidationProfile());
        });

        var validation = validator.Validate(person);

        Assert.False(validation.IsValid);
        Assert.Equal(2, validation.Errors.Count());
    }
}
