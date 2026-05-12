# Component Model Mapping
A light weight object mapper.



# Examples 


## 1. Create Nested Object Map 

## 2. Map Multiple Sources to a Single Target
Create multiple profiles 

```csharp
public class Person
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public int Age { get; set; }
}
public class Person1
{
    public string FirstName { get; set; }
    public string LastName { get; set; }   
}
public class Person2
{
    public string Name { get; set; }
    public int Age { get; set; }
}

public class PersonProfile1 : MapperProfile<Person, Person1>
{
    public override void Configure(IMapperActionDescriptor<Person, Person1> descriptor) 
    {
        descriptor
            .MapMember(target => target.FirstName, source => source.FirstName)
            .MapMember(target => target.LastName, source => source.LastName);
    }
}

public class PersonProfile2 : MapperProfile<Person, Person2>
{
    public override void Configure(IMapperActionDescriptor<Person, Person2> descriptor) 
    {
        descriptor.MapMember(target => target.Age, source => source.Age);
    }
}

public static void Main()
{
    var mapper = Mapper.Create(builder =>
    {
        builder.AddOptions(configure =>
        {
            
        });
        builder.AddProfile(new PersonProfile1());
        builder.AddProfile(new PersonProfile2());
    });

    var p1 = new Person1() { FirstName = "John", LastName = "Doe" };
    var p2 = new Person2() { Age = 30};

    var person = mapper.Map<Person>(p1, p2);
}

````
