using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Tests;

/// <summary>
/// The builder verbs that define a directory in code: <c>AddUser</c>, <c>AddGroup</c> and
/// <c>AddStore</c>, and the directory the built application serves from them.
/// </summary>
public class ApplicationDirectoryTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Directory: Users and groups defined on the builder should be served locked")]
    public async Task Build_WithUsersAndGroupsDefinedInCode_ShouldServeThemLocked()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder
            .AddGroup(IdentityGroup.Create(new() { Name = "orders-admins" }))
            .AddUser(IdentityUser.Create(new() { Name = "ada@contoso.example", DisplayName = "Ada Lovelace" }));

        // Act
        await using IdentityHubApplication application = builder.Build();
        IIdentityHubDirectory directory = application.Context.Directory;
        IdentityUser? user = await directory.Users.FindByNameAsync("ADA@contoso.example", CancellationToken.None);
        IdentityGroup? group = await directory.Groups.FindAsync(IdentityGroupId.FromName("orders-admins"), CancellationToken.None);
        IdentityObjectWriteResult remove = await directory.Users.TryRemoveAsync(user!.Id, "1", CancellationToken.None);

        // Assert
        user.IsLocked.ShouldBeTrue();
        user.DisplayName.ShouldBe("Ada Lovelace");
        group.ShouldNotBeNull();
        group.IsLocked.ShouldBeTrue();
        remove.Status.ShouldBe(IdentityObjectWriteStatus.Locked);
        ((IIdentityHubApplication)application).Context.Directory.ShouldBeSameAs(directory);
        application.Context.ServiceProvider.GetRequiredService<IIdentityHubDirectory>().ShouldBeSameAs(directory);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Directory: The name and identifier verbs should define objects on the hosting builder")]
    public async Task Build_WithTheNameAndIdentifierVerbs_ShouldDefineTheObjects()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);

        // Act
        builder
            .AddGroup("orders-admins", group => group.Description = "Everyone who may change an order.")
            .AddGroup(IdentityGroupId.FromName("staff"), group => group.Name = "everyone")
            .AddUser("ada@contoso.example", user => user.DisplayName = "Ada Lovelace")
            .AddUser("grace@contoso.example");
        await using IdentityHubApplication application = builder.Build();

        // Assert
        IIdentityHubDirectory directory = application.Context.Directory;
        directory.Users.Locked.Select(user => user.Name.Value)
            .ShouldBe(["ada@contoso.example", "grace@contoso.example"], ignoreOrder: true);
        directory.Groups.Locked.Count.ShouldBe(2);
        (await directory.Groups.FindByNameAsync("everyone", CancellationToken.None))!.Id.ShouldBe(IdentityGroupId.FromName("staff"));
        (await directory.Groups.FindByNameAsync("orders-admins", CancellationToken.None))!.Description
            .ShouldBe("Everyone who may change an order.");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Directory: An application with nothing defined should serve an empty directory")]
    public async Task Build_WithNothingDefined_ShouldServeAnEmptyDirectory()
    {
        // Arrange
        using var data = new TemporaryDirectory();

        // Act
        await using IdentityHubApplication application = IdentityHubTestHost.CreateBuilder(data.Path).Build();

        // Assert
        application.Context.Directory.Users.Locked.ShouldBeEmpty();
        application.Context.Directory.Groups.Locked.ShouldBeEmpty();
        (await application.Context.Directory.Users.ListAsync(cancellationToken: CancellationToken.None)).Items.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Store: An application with no store should keep run-time objects in memory of its own")]
    public async Task Build_WithNoStore_ShouldKeepRunTimeObjectsInItsOwnMemory()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        await using IdentityHubApplication first = IdentityHubTestHost.CreateBuilder(data.Path).Build();
        await using IdentityHubApplication second = IdentityHubTestHost.CreateBuilder(data.Path).Build();

        // Act
        IdentityObjectWriteResult added = await first.Context.Directory.Users.TryAddAsync(
            IdentityUser.Create(new() { Name = "ada" }),
            CancellationToken.None);
        IdentityUser? inFirst = await first.Context.Directory.Users.FindByNameAsync("ada", CancellationToken.None);
        IdentityUser? inSecond = await second.Context.Directory.Users.FindByNameAsync("ada", CancellationToken.None);

        // Assert
        added.Status.ShouldBe(IdentityObjectWriteStatus.Written);
        inFirst.ShouldNotBeNull();
        inFirst.IsLocked.ShouldBeFalse();
        inSecond.ShouldBeNull();
        first.Context.ServiceProvider.GetRequiredService<IIdentityHubStore>().ShouldBeOfType<InMemoryIdentityHubStore>();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Store: A store instance should hold the run-time objects and stay with its caller")]
    public async Task AddStore_WithAnInstance_ShouldWriteToItAndLeaveItWithItsCaller()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        var store = new DisposableIdentityHubStore();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        IdentityHubApplication application = builder.AddStore(store).Build();

        // Act
        await application.Context.Directory.Groups.TryAddAsync(
            IdentityGroup.Create(new() { Name = "everyone" }),
            CancellationToken.None);
        await ((IAsyncDisposable)application).DisposeAsync();
        IdentityGroup? seenByAnotherNode = await IdentityHubDirectory.Create(store).Groups.FindByNameAsync("everyone", CancellationToken.None);

        // Assert
        seenByAnotherNode.ShouldNotBeNull();
        store.DisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Store: A store factory should run once with the context and hand the store to the application")]
    public async Task AddStore_WithAFactory_ShouldInvokeItOnceAndOwnTheStore()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        var store = new DisposableIdentityHubStore();
        int calls = 0;
        IdentityHubApplicationContext? seen = null;
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder.AddStore(context =>
        {
            calls++;
            seen = context;
            return store;
        });
        IdentityHubApplication application = builder.Build();

        // Act
        await application.Context.Directory.Users.TryAddAsync(IdentityUser.Create(new() { Name = "ada" }), CancellationToken.None);
        IdentityUser? stored = await IdentityHubDirectory.Create(store).Users.FindByNameAsync("ada", CancellationToken.None);
        await ((IAsyncDisposable)application).DisposeAsync();

        // Assert
        calls.ShouldBe(1);
        seen.ShouldBeSameAs(application.Context);
        stored.ShouldNotBeNull();
        store.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Store: The root builder's store verbs should register the same store")]
    public async Task AddStore_ThroughTheRootBuilder_ShouldRegisterTheStore()
    {
        // Arrange
        using var instanceData = new TemporaryDirectory();
        using var factoryData = new TemporaryDirectory();
        var instance = new InMemoryIdentityHubStore();
        var created = new InMemoryIdentityHubStore();
        IIdentityHubApplicationContext? seen = null;
        IIdentityHubApplicationBuilder withInstance = IdentityHubTestHost.CreateBuilder(instanceData.Path);
        IIdentityHubApplicationBuilder withFactory = IdentityHubTestHost.CreateBuilder(factoryData.Path);

        // Act
        IIdentityHubApplicationBuilder returned = withInstance.AddStore(instance);
        withFactory.AddStore(context =>
        {
            seen = context;
            return created;
        });
        await using var first = (IdentityHubApplication)withInstance.Build();
        await using var second = (IdentityHubApplication)withFactory.Build();

        // Assert
        returned.ShouldBeSameAs(withInstance);
        first.Context.ServiceProvider.GetRequiredService<IIdentityHubStore>().ShouldBeSameAs(instance);
        second.Context.ServiceProvider.GetRequiredService<IIdentityHubStore>().ShouldBeSameAs(created);
        seen.ShouldBeSameAs(second.Context);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Store: A second store should be refused however the first was registered")]
    public void AddStore_WithASecondStore_ShouldThrowInvalidOperationException()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder afterInstance = IdentityHubTestHost.CreateBuilder(data.Path);
        afterInstance.AddStore(new InMemoryIdentityHubStore());
        IdentityHubApplicationBuilder afterFactory = IdentityHubTestHost.CreateBuilder(data.Path);
        afterFactory.AddStore(_ => new InMemoryIdentityHubStore());
        IdentityHubApplicationBuilder afterServices = IdentityHubTestHost.CreateBuilder(data.Path);
        afterServices.Services.AddSingleton<IIdentityHubStore>(new InMemoryIdentityHubStore());

        // Act
        InvalidOperationException instance = Should.Throw<InvalidOperationException>(
            () => afterInstance.AddStore(_ => new InMemoryIdentityHubStore()));
        InvalidOperationException factory = Should.Throw<InvalidOperationException>(
            () => afterFactory.AddStore(new InMemoryIdentityHubStore()));
        InvalidOperationException services = Should.Throw<InvalidOperationException>(
            () => ((IIdentityHubApplicationBuilder)afterServices).AddStore(new InMemoryIdentityHubStore()));

        // Assert
        instance.Message.ShouldContain("already has a store");
        factory.Message.ShouldContain("already has a store");
        services.Message.ShouldContain("already has a store");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Store: A null store, a null factory, and a factory that returns null should be refused")]
    public void AddStore_WithNullArgumentsOrANullProduct_ShouldThrow()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        IIdentityHubApplicationBuilder root = builder;
        IdentityHubApplicationBuilder returnsNull = IdentityHubTestHost.CreateBuilder(data.Path);
        returnsNull.AddStore(_ => null!);

        // Act
        ArgumentNullException nullStore = Should.Throw<ArgumentNullException>(() => builder.AddStore((IIdentityHubStore)null!));
        ArgumentNullException nullFactory = Should.Throw<ArgumentNullException>(
            () => builder.AddStore((Func<IdentityHubApplicationContext, IIdentityHubStore>)null!));
        ArgumentNullException nullRootFactory = Should.Throw<ArgumentNullException>(
            () => root.AddStore((Func<IIdentityHubApplicationContext, IIdentityHubStore>)null!));
        InvalidOperationException nullProduct = Should.Throw<InvalidOperationException>(() => returnsNull.Build());

        // Assert
        nullStore.ParamName.ShouldBe("store");
        nullFactory.ParamName.ShouldBe("factory");
        nullRootFactory.ParamName.ShouldBe("factory");
        nullProduct.Message.ShouldContain("store factory returned null");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Store: A store factory should not find the directory, which is created over the store")]
    public void AddStore_WithAFactoryThatReadsTheDirectory_ShouldFailTheBuild()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder.AddStore(context =>
        {
            _ = context.Directory;
            return new InMemoryIdentityHubStore();
        });

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("directory is created when the identity hub application is built");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Directory: A host service factory should read the directory from the context")]
    public async Task AddService_WithAFactory_ShouldFindTheDirectoryOnTheContext()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IIdentityHubDirectory? seen = null;
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder.AddUser(IdentityUser.Create(new() { Name = "ada" }));
        builder.AddService(context =>
        {
            seen = context.Directory;
            return new IdleHostService();
        });

        // Act
        await using IdentityHubApplication application = builder.Build();

        // Assert
        seen.ShouldBeSameAs(application.Context.Directory);
        seen!.Users.Locked.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Directory: A user created at run time should be served from code once the builder defines its name")]
    public async Task Build_WithAUserDefinedUnderAStoredName_ShouldServeTheCodeDefinitionLocked()
    {
        // Arrange: a user created while the hub ran, then a deployment whose code defines the name.
        using var data = new TemporaryDirectory();
        var store = new InMemoryIdentityHubStore();
        await IdentityHubDirectory.Create(store).Users.TryAddAsync(
            IdentityUser.Create(new() { Name = "Ada@Contoso.example", DisplayName = "Created at run time" }),
            CancellationToken.None);
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder.AddStore(store).AddUser("ada@contoso.example", user => user.DisplayName = "Ada Lovelace");

        // Act
        await using IdentityHubApplication application = builder.Build();
        IdentityUser served = (await application.Context.Directory.Users.FindByNameAsync("ada@contoso.example", CancellationToken.None))!;
        IdentityObjectPage<IdentityUser> page = await application.Context.Directory.Users.ListAsync(cancellationToken: CancellationToken.None);

        // Assert
        served.IsLocked.ShouldBeTrue();
        served.DisplayName.ShouldBe("Ada Lovelace");
        page.Items.ShouldHaveSingleItem().ShouldBeSameAs(served);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Directory: A user defined twice should be refused on the line that repeats it")]
    public void AddUser_WithADuplicateIdentifierOrName_ShouldThrowInvalidOperationException()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder.AddUser(IdentityUser.Create(new() { Name = "ada" }));

        // Act
        InvalidOperationException sameId = Should.Throw<InvalidOperationException>(
            () => builder.AddUser(IdentityUser.Create(new() { Id = IdentityUserId.FromName("ada"), Name = "ada.lovelace" })));
        InvalidOperationException sameName = Should.Throw<InvalidOperationException>(
            () => builder.AddUser(IdentityUser.Create(new() { Id = IdentityUserId.New(), Name = "ADA" })));
        InvalidOperationException throughTheNameVerb = Should.Throw<InvalidOperationException>(() => builder.AddUser("Ada"));

        // Assert
        sameId.Message.ShouldContain("user 'ada.lovelace (", Case.Sensitive);
        sameId.Message.ShouldContain("already defined");
        sameName.Message.ShouldContain("user name 'ADA' is already held by user 'ada (", Case.Sensitive);
        throughTheNameVerb.Message.ShouldContain("already defined");
        builder.Services.Container.Count(descriptor => descriptor.ServiceType == typeof(IdentityUser)).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Directory: A group defined twice should be refused on the line that repeats it")]
    public void AddGroup_WithADuplicateIdentifierOrName_ShouldThrowInvalidOperationException()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder.AddGroup(IdentityGroup.Create(new() { Name = "everyone" }));

        // Act
        InvalidOperationException sameId = Should.Throw<InvalidOperationException>(
            () => builder.AddGroup(IdentityGroup.Create(new() { Id = IdentityGroupId.FromName("everyone"), Name = "all-staff" })));
        InvalidOperationException sameName = Should.Throw<InvalidOperationException>(
            () => builder.AddGroup(IdentityGroup.Create(new() { Id = IdentityGroupId.New(), Name = "Everyone" })));

        // Assert
        sameId.Message.ShouldContain("group 'all-staff (", Case.Sensitive);
        sameId.Message.ShouldContain("already defined");
        sameName.Message.ShouldContain("group name 'Everyone' is already held by group 'everyone (", Case.Sensitive);
        builder.Services.Container.Count(descriptor => descriptor.ServiceType == typeof(IdentityGroup)).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Directory: A user and a group should be free to share a name")]
    public async Task AddUserAndAddGroup_WithOneName_ShouldDefineBoth()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);

        // Act
        builder.AddUser("operations").AddGroup("operations");
        await using IdentityHubApplication application = builder.Build();

        // Assert
        application.Context.Directory.Users.Locked.Count.ShouldBe(1);
        application.Context.Directory.Groups.Locked.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Directory: Objects registered straight on the services should be checked when the application is built")]
    public void Build_WithDuplicateUsersRegisteredOnTheServices_ShouldFailNamingBoth()
    {
        // Arrange: a registration on Services passes the builder's own check by.
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        builder.AddUser("ada");
        builder.Services.AddSingleton(IdentityUser.Create(new() { Id = IdentityUserId.New(), Name = "Ada" }));

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("User name 'ada' is held by users", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Directory: A null user and a null group should be refused")]
    public void AddUserAndAddGroup_WithNull_ShouldThrowArgumentNullException()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);

        // Act
        ArgumentNullException user = Should.Throw<ArgumentNullException>(() => builder.AddUser((IdentityUser)null!));
        ArgumentNullException group = Should.Throw<ArgumentNullException>(() => builder.AddGroup((IdentityGroup)null!));

        // Assert
        user.ParamName.ShouldBe("user");
        group.ParamName.ShouldBe("group");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Directory: The directory verbs should be refused once the application is built")]
    public async Task DirectoryVerbs_AfterBuild_ShouldThrowInvalidOperationException()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(data.Path);
        IIdentityHubApplicationBuilder root = builder;
        await using IdentityHubApplication application = builder.Build();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.AddUser(IdentityUser.Create(new() { Name = "ada" })));
        Should.Throw<InvalidOperationException>(() => builder.AddUser("ada"));
        Should.Throw<InvalidOperationException>(() => builder.AddGroup(IdentityGroup.Create(new() { Name = "everyone" })));
        Should.Throw<InvalidOperationException>(() => builder.AddGroup("everyone"));
        Should.Throw<InvalidOperationException>(() => builder.AddStore(new InMemoryIdentityHubStore()));
        Should.Throw<InvalidOperationException>(() => builder.AddStore(_ => new InMemoryIdentityHubStore()));
        Should.Throw<InvalidOperationException>(() => root.AddStore(_ => new InMemoryIdentityHubStore()));
        application.Context.Directory.Users.Locked.ShouldBeEmpty();
    }

    private sealed class IdleHostService : IHostService
    {
        public ServiceId Id { get; } = ServiceId.New();

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
