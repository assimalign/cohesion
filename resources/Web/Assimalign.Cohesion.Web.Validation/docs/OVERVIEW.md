# Assimalign.Cohesion.Web.Validation — Overview

Request validation for the Cohesion Web pipeline over `Assimalign.Cohesion.ObjectValidation`. A typed
endpoint validates the request-body model it binds before its handler runs, and an invalid model is
answered with `400 Bad Request` as RFC 9457 `application/problem+json`, with an `errors` map in the shape
binding failures already use.

## Scope

- **`builder.Services.AddValidation(...)`** (builder time), a component integration the application's
  compilation receives (#1380), registers a validator per model type and the application's default.
  Validators are keyed by `typeof(T)`: `AddProfile(new CustomerProfile())` builds a validator over one
  profile, `AddValidator(validator)` registers a configured `IValidator` for every type it has a profile
  for, and `AddValidator<T>(validator)` names the type. No reflection is involved.
- **Typed endpoints validate their body model.** When an application references this package, the Web
  endpoint-binding generator emits a validation call into every typed endpoint that binds a request-body
  model, after all its parameters are bound and before its handler runs. A body type with no registered
  validator, and a `null` body, are not validated.
- **The failure payload.** `400` problem details with `detail` "One or more validation errors occurred."
  and an `errors` extension member mapping each failing member to its messages:
  `{ "Name": ["..."], "Address.City": ["..."] }`. Keys are the member paths the profile declares; a nested
  profile's errors are reported under their parent member.
- **Turning it off or on.** `options.Enabled = false` turns validation off by default;
  `DisableValidation()` turns it off for a route or a route group, and `RequireValidation()` turns it on,
  the most specific declaration winning.
- **Handlers** that bind a value themselves validate it the same way:
  `if (!await context.ValidateAsync(order, context.RequestCancelled)) return;`.

## Dependencies

- `Assimalign.Cohesion.Web` — the builder and the application feature registration.
- `Assimalign.Cohesion.Web.Routing` — the endpoint metadata the decision reads and the convention-builder
  seam the verbs extend.
- `Assimalign.Cohesion.Web.ProblemDetails` — the `application/problem+json` response.
- `Assimalign.Cohesion.Http` — the HTTP context and status codes.
- `Assimalign.Cohesion.ObjectValidation` — the validators and profiles.

It never references `Assimalign.Cohesion.Web.Hosting` or any `Assimalign.Cohesion.Hosting*` library
(`COHRES001`, `COHRES004`), and `Web.Api` does not reference it: the generated code in the application
calls it. The package and `ObjectValidation` are members of the `App.Web` shared framework.

## Usage

```csharp
using Assimalign.Cohesion.ObjectValidation;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.Validation;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddRouting();
builder.Services.AddJsonSerialization(AppJsonContext.Default);
builder.Services.AddValidation(validation =>
{
    validation.AddProfile(new CustomerProfile());
});

WebApplication app = builder.Build();
app.UseRouting();

// An invalid customer is answered 400 before the handler runs.
app.MapPost("/customers", (Customer customer) => customers.Add(customer));

// Drafts are saved as they are.
app.MapPost("/drafts", (Customer customer) => drafts.Save(customer)).DisableValidation();

public sealed class CustomerProfile : ValidationProfile<Customer>
{
    public override void Configure(IValidationRuleDescriptor<Customer> descriptor)
    {
        descriptor.RuleFor(customer => customer.Name).NotEmpty();
        descriptor.RuleFor(customer => customer.Age).GreaterThanOrEqualTo(18);
        descriptor.RuleFor(customer => customer.Address).ChildRules(address => address.RuleFor(a => a.City).NotEmpty());
    }
}
```

An invalid request:

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{"type":"about:blank","title":"Bad Request","status":400,"detail":"One or more validation errors occurred.","errors":{"Name":["The following expression: 'customer => customer.Name' was empty."],"Address.City":["The following expression: 'a => a.City' was empty."]}}
```

See [DESIGN.md](DESIGN.md) for how the generator detects the package, the decision order, the key format,
and why validation is its own package rather than part of `Web.Api`.
