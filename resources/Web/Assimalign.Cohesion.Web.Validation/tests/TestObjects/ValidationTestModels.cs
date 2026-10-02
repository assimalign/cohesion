using System.Text.Json.Serialization;

using Assimalign.Cohesion.ObjectValidation;

namespace Assimalign.Cohesion.Web.Validation.Tests.TestObjects;

/// <summary>A request-body model with a validator: a name, an age, and a nested address.</summary>
internal sealed class Customer
{
    public string? Name { get; set; }

    public int Age { get; set; }

    public Address? Address { get; set; }
}

/// <summary>The nested member <see cref="CustomerProfile"/> validates through <c>ChildRules</c>.</summary>
internal sealed class Address
{
    public string? City { get; set; }
}

/// <summary>A request-body model no validator is registered for.</summary>
internal sealed class Note
{
    public string? Text { get; set; }
}

/// <summary>
/// A request-body model whose members fail independently: a user name checked by two chained rules, an
/// age, and a nested address.
/// </summary>
internal sealed class Signup
{
    public string? UserName { get; set; }

    public int Age { get; set; }

    public Address? Address { get; set; }
}

/// <summary>
/// The rules for <see cref="Customer"/>: a non-empty name, an adult age, and, when an address is given, a
/// non-empty city.
/// </summary>
internal sealed class CustomerProfile : ValidationProfile<Customer>
{
    public override void Configure(IValidationRuleDescriptor<Customer> descriptor)
    {
        descriptor.RuleFor(customer => customer.Name!).NotEmpty();
        descriptor.RuleFor(customer => customer.Age).GreaterThanOrEqualTo(18);
        descriptor.RuleFor(customer => customer.Address!).ChildRules(address => address.RuleFor(a => a.City!).NotEmpty());
    }
}

/// <summary>
/// The rules for <see cref="Signup"/>: a user name that is given and at least three characters long, an
/// adult age, and, when an address is given, a non-empty city. An empty user name fails both of its rules.
/// </summary>
internal sealed class SignupProfile : ValidationProfile<Signup>
{
    public override void Configure(IValidationRuleDescriptor<Signup> descriptor)
    {
        descriptor.RuleFor(signup => signup.UserName!).NotEmpty().MinLength(3);
        descriptor.RuleFor(signup => signup.Age).GreaterThanOrEqualTo(18);
        descriptor.RuleFor(signup => signup.Address!).ChildRules(address => address.RuleFor(a => a.City!).NotEmpty());
    }
}

/// <summary>
/// The source-generated serialization contracts for the test models — the <c>JsonTypeInfo</c> resolver
/// shape applications register with <c>AddJsonSerialization</c>.
/// </summary>
[JsonSerializable(typeof(Customer))]
[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(Signup))]
internal sealed partial class ValidationTestJsonContext : JsonSerializerContext;
