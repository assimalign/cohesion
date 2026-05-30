# Assimalign.Cohesion.ObjectMapping

A lightweight, profile-based object-to-object mapper. Mapping rules are declared
explicitly in profiles, built into an immutable mapper, and replayed at run time.
The scalar mapping path uses compiled getters and setters (no per-call reflection).

See [docs/OVERVIEW.md](docs/OVERVIEW.md) and [docs/DESIGN.md](docs/DESIGN.md) for
details, including the error model, performance posture, and AOT constraint.

## Quick start

```csharp
public class PersonSource
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public int Age { get; set; }
}

public class PersonTarget
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public int Age { get; set; }
}

IMapper mapper = new MapperBuilder()
    .AddProfile<PersonTarget, PersonSource>(profile => profile
        .MapMember(target => target.FirstName, source => source.FirstName)
        .MapMember(target => target.LastName, source => source.LastName)
        .MapMember(target => target.Age, source => source.Age))
    .Build();

PersonTarget person = mapper.Map<PersonTarget, PersonSource>(
    new PersonSource { FirstName = "John", LastName = "Doe", Age = 42 });
```

## Class-based profiles

```csharp
public class PersonProfile : MapperProfile<PersonTarget, PersonSource>
{
    protected override void Configure(MapperProfileDescriptor<PersonTarget, PersonSource> descriptor)
    {
        descriptor
            .MapMember(target => target.FirstName, source => source.FirstName)
            .MapMember(target => target.LastName, source => source.LastName);
    }
}

IMapper mapper = new MapperBuilder().AddProfile(new PersonProfile()).Build();
```

## Map multiple sources into a single target

```csharp
public class Person   { public string FirstName { get; set; } public string LastName { get; set; } public int Age { get; set; } }
public class Person1  { public string FirstName { get; set; } public string LastName { get; set; } }
public class Person2  { public int Age { get; set; } }

IMapper mapper = new MapperBuilder()
    .AddProfile<Person, Person1>(p => p
        .MapMember(t => t.FirstName, s => s.FirstName)
        .MapMember(t => t.LastName, s => s.LastName))
    .AddProfile<Person, Person2>(p => p
        .MapMember(t => t.Age, s => s.Age))
    .Build();

var person = mapper.Map<Person>(
    new Person1 { FirstName = "John", LastName = "Doe" },
    new Person2 { Age = 30 });
```

## Nested objects and collections

```csharp
IMapper mapper = new MapperBuilder()
    .AddProfile<OrderTarget, OrderSource>(p => p
        .MapMember(t => t.Id, s => s.Id)
        .MapMemberTypes(t => t.Customer, s => s.Customer)         // nested complex member
        .MapMemberEnumerables(t => t.Items, s => s.Items))        // enumerable member
    .AddProfile<CustomerTarget, CustomerSource>(p => p
        .MapMember(t => t.Name, s => s.Name))
    .AddProfile<LineItemTarget, LineItemSource>(p => p
        .MapMember(t => t.Sku, s => s.Sku)
        .MapMember(t => t.Quantity, s => s.Quantity))
    .Build();

OrderTarget order = mapper.Map<OrderTarget, OrderSource>(source);
```

## Named mappers via a factory

```csharp
IMapperFactory factory = new MapperFactoryBuilder()
    .AddMapper("people", builder => builder
        .AddProfile<PersonTarget, PersonSource>(p => p
            .MapMember(t => t.FirstName, s => s.FirstName))
        .Build())
    .Build();

IMapper mapper = factory.Create("people");
```
