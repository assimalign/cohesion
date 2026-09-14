using System;

using Assimalign.Cohesion.ObjectMapping;

Console.WriteLine("Cohesion SDK smoke: Analyzer:" + typeof(UserMapper).FullName);

internal sealed class SourceUser
{
    public string Name { get; set; } = string.Empty;

    public int Id { get; set; }
}

internal sealed class TargetUser
{
    public string DisplayName { get; set; } = string.Empty;

    public int UserId { get; set; }
}

internal partial class UserMapper : MapperProfile<TargetUser, SourceUser>
{
    protected override void Configure(MapperProfileDescriptor<TargetUser, SourceUser> descriptor) =>
        descriptor
            .MapMember(target => target.DisplayName, source => source.Name)
            .MapMember(target => target.UserId, source => source.Id);
}
