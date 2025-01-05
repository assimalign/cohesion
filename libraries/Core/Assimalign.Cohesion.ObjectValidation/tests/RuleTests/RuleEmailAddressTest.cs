using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace  Assimalign.Cohesion.ObjectValidation.Tests;

using  Assimalign.Cohesion.ObjectValidation;
using  Assimalign.Cohesion.ObjectValidation.Internal;
using  Assimalign.Cohesion.ObjectValidation.Internal.Rules;

public class RuleEmailAddressTest
{
    public IValidationContext RunEmailAddressTest<TValue>(TValue value)
        where TValue : IEnumerable<char>
    {
        var rule = new EmailValidationRule<TValue>()
        {
            Error = new ValidationError()
            {

            }
        };

        if (rule.TryValidate((object)value, out var context))
        {
            return context;
        }
        else
        {
            throw new Exception("Unable to validate.");
        }
    }


    [Fact]
    public void EmailAddressSuccessTest()
    {
        var email = "ccrawford@assimalign.com";
        var context = this.RunEmailAddressTest(email);

        Assert.Empty(context.Errors);
    }


    [Fact]
    public void EmailAddressFailureTest01()
    {
        var email = "@ccrawford/assimalign.com";
        var context = this.RunEmailAddressTest(email);

        Assert.Single(context.Errors);
    }



    [Fact]
    public void EmailAddressFailureTest02()
    {
        var email = "ccrawford";
        var context = this.RunEmailAddressTest(email);

        Assert.Single(context.Errors);
    }
}