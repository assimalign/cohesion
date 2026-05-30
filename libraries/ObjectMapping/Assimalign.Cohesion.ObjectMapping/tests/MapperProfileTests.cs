using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

using Assimalign.Cohesion.ObjectMapping.Internal;

public class MapperProfileTests
{
    private sealed class PersonProfile : MapperProfile<PersonTarget, PersonSource>
    {
        protected override void Configure(MapperProfileDescriptor<PersonTarget, PersonSource> descriptor)
        {
            descriptor
                .MapMember(target => target.FirstName, source => source.FirstName)
                .MapMember(target => target.LastName, source => source.LastName);
        }
    }

    private sealed class ProfileCapture : MapperProfile<PersonTarget, PersonSource>
    {
        public MapperProfileDescriptor<PersonTarget, PersonSource>? CapturedDescriptor { get; private set; }

        protected override void Configure(MapperProfileDescriptor<PersonTarget, PersonSource> descriptor)
        {
            CapturedDescriptor = descriptor;
        }
    }

    [Fact]
    public void MapperProfile_Subclass_IsAppliedByMapper()
    {
        // Arrange
        var mapper = new MapperBuilder().AddProfile(new PersonProfile()).Build();

        // Act
        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { FirstName = "Al", LastName = "Bo" });

        // Assert
        result.FirstName.ShouldBe("Al");
        result.LastName.ShouldBe("Bo");
    }

    [Fact]
    public void MapperProfile_ExposesTypesAndActions()
    {
        // Arrange
        var profile = new PersonProfile();

        // Act / Assert
        profile.TargetType.ShouldBe(typeof(PersonTarget));
        profile.SourceType.ShouldBe(typeof(PersonSource));
        profile.MapActions.Count.ShouldBe(2);
    }

    [Fact]
    public void Descriptor_Profile_ReferencesOwningProfile()
    {
        // Arrange
        var profile = new ProfileCapture();

        // Act / Assert
        profile.CapturedDescriptor.ShouldNotBeNull();
        profile.CapturedDescriptor!.Profile.ShouldBeSameAs(profile);
    }

    [Fact]
    public void MapAction_WithContextCallback_RunsCustomLogic()
    {
        // Arrange
        var mapper = new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                .MapAction(context =>
                {
                    var source = (PersonSource)context.Source;
                    var target = (PersonTarget)context.Target;
                    target.FirstName = source.FirstName?.ToUpperInvariant();
                }))
            .Build();

        // Act
        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { FirstName = "jo" });

        // Assert
        result.FirstName.ShouldBe("JO");
    }

    [Fact]
    public void MapAction_WithTypedCallback_RunsCustomLogic()
    {
        // Arrange
        var mapper = new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                .MapAction((PersonTarget target, PersonSource source) =>
                {
                    target.FirstName = $"{source.FirstName} {source.LastName}";
                }))
            .Build();

        // Act
        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { FirstName = "Jo", LastName = "Lo" });

        // Assert
        result.FirstName.ShouldBe("Jo Lo");
    }

    [Fact]
    public void MapAction_WithNullAction_ThrowsArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor.MapAction((IMapperAction)null!)));
    }

    [Fact]
    public void MapMemberByName_MapsTopLevelMember()
    {
        // Arrange
        var mapper = new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor.MapMember("FirstName", "FirstName"))
            .Build();

        // Act
        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { FirstName = "Named" });

        // Assert
        result.FirstName.ShouldBe("Named");
    }

    [Fact]
    public void MapMemberByName_SupportsDottedSourcePath()
    {
        // Arrange
        var mapper = new MapperBuilder()
            .AddProfile<PersonTarget, OrderSource>(descriptor => descriptor.MapMember("FirstName", "Customer.Name"))
            .Build();
        var source = new OrderSource { Customer = new CustomerSource { Name = "Deep" } };

        // Act
        var result = mapper.Map<PersonTarget, OrderSource>(source);

        // Assert
        result.FirstName.ShouldBe("Deep");
    }

    [Fact]
    public void MapMemberByName_NullName_ThrowsArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor.MapMember((string)null!, "FirstName")));
    }

    [Fact]
    public void MapAllProperties_MapsMembersSharingNameAndType()
    {
        // Arrange
        var mapper = new MapperBuilder()
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor.MapAllProperties())
            .Build();

        // Act
        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { FirstName = "F", LastName = "L", MiddleName = "M", Age = 5 });

        // Assert
        result.FirstName.ShouldBe("F");
        result.LastName.ShouldBe("L");
        result.MiddleName.ShouldBe("M");
        result.Age.ShouldBe(5);
    }

    [Fact]
    public void MapAllFields_MapsFieldsSharingNameAndType()
    {
        // Arrange
        var mapper = new MapperBuilder()
            .AddProfile<FieldTarget, FieldSource>(descriptor => descriptor.MapAllFields())
            .Build();

        // Act
        var result = mapper.Map<FieldTarget, FieldSource>(new FieldSource { Name = "n", Value = 3 });

        // Assert
        result.Name.ShouldBe("n");
        result.Value.ShouldBe(3);
    }

    [Fact]
    public void MapAllMembers_MapsPropertiesAndFields()
    {
        // Arrange
        var mapper = new MapperBuilder()
            .AddProfile<FieldTarget, FieldSource>(descriptor => descriptor.MapAllMembers())
            .Build();

        // Act
        var result = mapper.Map<FieldTarget, FieldSource>(new FieldSource { Name = "n", Value = 3 });

        // Assert
        result.Name.ShouldBe("n");
        result.Value.ShouldBe(3);
    }

    [Fact]
    public void DefaultMapperProfile_NullConfigure_ThrowsArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new DefaultMapperProfile<PersonTarget, PersonSource>(null!));
    }
}
