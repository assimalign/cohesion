# Assimalign.Cohesion.ObjectMapping Design

## Design Intent

The mapper favors explicit profile configuration over convention-heavy magic. Profiles record mapping actions ahead of time, and the runtime mapper simply applies those actions against a mapping context.

## Architecture

- MapperBuilder collects options and profiles before a mapper is created.
- MapperProfile and related descriptors are the configuration surface for each source and target pair.
- Mapper applies the recorded actions at runtime and supports multi-source composition through multiple profiles.

## Layout Example

```text
Assimalign.Cohesion.ObjectMapping/
  src/
    Assimalign.Cohesion.ObjectMapping.csproj
    Abstractions/
    Exceptions/
    Extensions/
    Internal/
    Properties/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Create a mapper with an explicit profile

```csharp
IMapper mapper = Mapper.Create(builder =>
{
    builder.AddProfile(new PersonProfile());
});

PersonDto dto = mapper.Map<PersonDto, Person>(person);
```

## Example 2: Merge multiple sources into one target

```csharp
IMapper mapper = Mapper.Create(builder =>
{
    builder.AddProfile(new PersonNameProfile());
    builder.AddProfile(new PersonAgeProfile());
});

Person target = mapper.Map<Person>(personName, personAge);
```
