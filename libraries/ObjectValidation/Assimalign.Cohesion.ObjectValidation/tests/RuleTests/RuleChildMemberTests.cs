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

public class RuleChildMemberTests
{
    private IValidator validator;

    public RuleChildMemberTests()
    {
        this.validator = Validator.Create(configure =>
        {
            configure.AddOptions(options =>
            {
                options.ContinueThroughValidationChain = true;
            });
            configure.AddProfile(new PersonValidationProfile());
        });
    }


    public class PersonValidationProfile : ValidationProfile<Person>
    {
        public override void Configure(IValidationRuleDescriptor<Person> descriptor)
        {

            descriptor.RuleFor(p => p.PrimaryAddress)

                 .ChildRules(configure =>
                 {
                     configure.RuleFor(p => p.StreetOne)
                        .NotEmpty();

                     configure.RuleFor(p => p.City)
                        .NotEmpty();

                     configure.RuleFor(p => p.State)
                        .NotEmpty()
                        .Custom((value, context) =>
                        {

                        });

                     configure
                        .When(p => Test(p.ZipCode), op =>
                         {
                             op.RuleFor(p => p.ZipCode)
                                .NotEmpty();
                         })
                        .When(p => !Test(p.ZipCode), ops =>
                        {
                            ops.RuleFor(p => p.ZipCode)
                                .Custom((value, context) =>
                                {
                                    if (!int.TryParse(value, out int result))
                                    {
                                        context.AddFailure("failure", "failure");
                                    }
                                });
                        });
                 });



        }

        bool Test(string t)
        {
            var r = string.IsNullOrEmpty(t);
            return r;
        }
    }


    public class Person
    {
        public string FirstName { get; set; }
        public PersonAddresses PrimaryAddress { get; set; }

        public IEnumerable<PersonAddresses> Addresses { get; set; }
        public long[] Ages { get; set; }
    }

    public class PersonAddresses
    {
        public string StreetOne { get; set; }
        public string StreetTwo { get; set; }
        public string ZipCode { get; set; }
        public string City { get; set; }
        public string State { get; set; }
    }

    [Fact]
    public void ChildRuleFailureTest01()
    {
        var person = new Person()
        {
            PrimaryAddress = new PersonAddresses()
            {

            }
        };

        var results = this.validator.Validate(person);
        Assert.Equal(4, results.Errors.Count());
    }

    [Fact]
    public void ChildRuleFailureTest02()
    {
        var person = new Person()
        {
            PrimaryAddress = new PersonAddresses()
            {
                StreetOne = "Test street"
            }
        };

        var results = this.validator.Validate(person);
        Assert.Equal(3, results.Errors.Count());
    }


    [Fact]
    public void ChildRuleFailureTest03()
    {
        var person = new Person()
        {
            PrimaryAddress = new PersonAddresses()
            {
                ZipCode = "casdf"
            }
        };

        var results = this.validator.Validate(person);
        Assert.Equal(4, results.Errors.Count());
    }

    [Fact]
    public void ChildRuleFailureTest04()
    {
        var person = new Person()
        {
            PrimaryAddress = new PersonAddresses()
            {
                StreetOne = "Test street",
                ZipCode = "24242"
            }
        };

        var results = this.validator.Validate(person);
        Assert.Equal(2, results.Errors.Count());
    }


    [Fact]
    public void ChildRuleSuccessTest04()
    {
        var person = new Person()
        {

        };

        var results = this.validator.Validate(person);
        Assert.Empty(results.Errors);
    }
}

