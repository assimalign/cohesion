namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>
/// A top-level, partial, class-based profile whose <see cref="Configure"/> is a fluent chain of
/// scalar member mappings. The object-mapping source generator recognizes this shape and emits a
/// partial <c>TryConfigureGenerated</c> override, so the profile maps without calling
/// <c>Expression.Compile()</c> at run time.
/// </summary>
public partial class GeneratedScalarProfile : MapperProfile<PersonTarget, PersonSource>
{
    protected override void Configure(MapperProfileDescriptor<PersonTarget, PersonSource> descriptor)
    {
        descriptor
            .MapMember(target => target.FirstName, source => source.FirstName)
            .MapMember(target => target.LastName, source => source.LastName)
            .MapMember(target => target.Age, source => source.Age);
    }
}
