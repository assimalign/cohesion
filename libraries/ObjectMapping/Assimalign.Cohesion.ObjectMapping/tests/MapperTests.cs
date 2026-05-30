using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

public class MapperTests
{
    private sealed class FirstNameProfile : MapperProfile<PersonTarget, PersonSource>
    {
        protected override void Configure(MapperProfileDescriptor<PersonTarget, PersonSource> descriptor)
        {
            descriptor.MapMember(target => target.FirstName, source => source.FirstName);
        }
    }

    private static Mapper CreatePersonMapper(MapperIgnoreHandling ignoreHandling = MapperIgnoreHandling.Never)
    {
        return new MapperBuilder(new MapperOptions { IgnoreHandling = ignoreHandling })
            .AddProfile<PersonTarget, PersonSource>(descriptor => descriptor
                .MapMember(target => target.FirstName, source => source.FirstName)
                .MapMember(target => target.LastName, source => source.LastName)
                .MapMember(target => target.Age, source => source.Age))
            .Build();
    }

    [Fact]
    public void Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new Mapper(null!));
    }

    [Fact]
    public void Name_WhenNotConfigured_DefaultsToDefault()
    {
        // Arrange
        var mapper = new MapperBuilder().Build();

        // Act / Assert
        mapper.Name.ShouldBe("Default");
    }

    [Fact]
    public void Name_ReflectsConfiguredOptionName()
    {
        // Arrange
        var mapper = new MapperBuilder(new MapperOptions { Name = "custom" }).Build();

        // Act / Assert
        mapper.Name.ShouldBe("custom");
    }

    [Fact]
    public void Profiles_ExposesRegisteredProfiles()
    {
        // Arrange
        var mapper = CreatePersonMapper();

        // Act / Assert
        mapper.Profiles.Count.ShouldBe(1);
        mapper.Profiles[0].TargetType.ShouldBe(typeof(PersonTarget));
        mapper.Profiles[0].SourceType.ShouldBe(typeof(PersonSource));
        mapper.Profiles[0].MapActions.Count.ShouldBe(3);
    }

    [Fact]
    public void Map_TargetFirstOverload_PopulatesTargetAndReturnsSameInstance()
    {
        // Arrange
        var mapper = CreatePersonMapper();
        var target = new PersonTarget();
        var source = new PersonSource { FirstName = "Ann", LastName = "Lee", Age = 30 };

        // Act
        var result = mapper.Map(target, source, typeof(PersonTarget), typeof(PersonSource));

        // Assert
        result.ShouldBeSameAs(target);
        target.FirstName.ShouldBe("Ann");
        target.LastName.ShouldBe("Lee");
        target.Age.ShouldBe(30);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Map_WithNullArgument_ThrowsArgumentNullException(bool nullTarget, bool nullSource, bool nullTargetType, bool nullSourceType)
    {
        // Arrange
        var mapper = CreatePersonMapper();
        object? target = nullTarget ? null : new PersonTarget();
        object? source = nullSource ? null : new PersonSource();
        Type? targetType = nullTargetType ? null : typeof(PersonTarget);
        Type? sourceType = nullSourceType ? null : typeof(PersonSource);

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => mapper.Map(target!, source!, targetType!, sourceType!));
    }

    [Fact]
    public void Map_WhenSourceNotAssignableToSourceType_ThrowsArgumentException()
    {
        // Arrange
        var mapper = CreatePersonMapper();

        // Act / Assert: actual source is PersonSource but declared as AgeSource
        Should.Throw<ArgumentException>(() =>
            mapper.Map(new PersonTarget(), new PersonSource(), typeof(PersonTarget), typeof(AgeSource)));
    }

    [Fact]
    public void Map_WhenTargetNotAssignableToTargetType_ThrowsArgumentException()
    {
        // Arrange
        var mapper = CreatePersonMapper();

        // Act / Assert: actual target is PersonTarget but declared as AgeSource
        Should.Throw<ArgumentException>(() =>
            mapper.Map(new PersonTarget(), new PersonSource(), typeof(AgeSource), typeof(PersonSource)));
    }

    [Fact]
    public void Map_WithNoMatchingProfile_LeavesTargetUnchanged()
    {
        // Arrange
        var mapper = CreatePersonMapper();
        var target = new PersonTarget { FirstName = "keep" };

        // Act: there is no AgeSource -> PersonTarget profile
        var result = (PersonTarget)mapper.Map(target, new AgeSource { Age = 5 }, typeof(PersonTarget), typeof(AgeSource));

        // Assert
        result.FirstName.ShouldBe("keep");
        result.Age.ShouldBe(0);
    }

    [Fact]
    public void Build_CalledTwice_ThrowsInvalidOperationException()
    {
        // Arrange
        var builder = new MapperBuilder();
        builder.Build();

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_WithConstructorOptions_NullOptions_ThrowsArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new MapperBuilder(null!));
    }

    [Fact]
    public void AddProfile_WithNullProfileInstance_ThrowsArgumentNullException()
    {
        // Arrange
        var builder = new MapperBuilder();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.AddProfile((IMapperProfile)null!));
    }

    [Fact]
    public void AddProfile_WithNullConfigureCallback_ThrowsArgumentNullException()
    {
        // Arrange
        var builder = new MapperBuilder();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => builder.AddProfile<PersonTarget, PersonSource>(null!));
    }

    [Fact]
    public void IMapperBuilder_AddProfileAndBuild_WorkThroughInterface()
    {
        // Arrange
        IMapperBuilder builder = new MapperBuilder();

        // Act
        IMapper mapper = builder.AddProfile(new FirstNameProfile()).Build();
        var result = mapper.Map<PersonTarget, PersonSource>(new PersonSource { FirstName = "Via Interface" });

        // Assert
        result.FirstName.ShouldBe("Via Interface");
    }
}
