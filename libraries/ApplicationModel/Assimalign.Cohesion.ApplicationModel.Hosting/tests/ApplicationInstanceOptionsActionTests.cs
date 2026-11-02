using System;
using System.Collections.Generic;
using System.Threading;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

/// <summary>
/// The actions an application adds to any area's resource: kept by the options every area's options
/// derive from, and created once from the application's services.
/// </summary>
public class ApplicationInstanceOptionsActionTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ApplicationInstanceOptions actions: ";

    [Fact(DisplayName = DisplayPrefix + "Added actions are created in the order they were added")]
    public void CreateActions_WithInstanceAndFactory_ShouldReturnThemInOrder()
    {
        // Arrange
        var first = new TestAction { Name = "flush-cache" };
        var second = new TestAction { Name = "rotate-keys" };
        TestOptions options = Create();
        options.AddAction(first).AddAction(_ => second);

        // Act
        IReadOnlyList<IApplicationInstanceAction> actions = options.CreateActions(new TestServiceProvider());

        // Assert
        actions.ShouldBe([first, second]);
    }

    [Fact(DisplayName = DisplayPrefix + "A factory receives the application's services")]
    public void CreateActions_WithFactory_ShouldPassTheProvider()
    {
        // Arrange
        var level = new LogLevelSetting("Information");
        TestServiceProvider provider = new TestServiceProvider().Add(level);
        TestOptions options = Create();
        options.AddAction(services => new TestAction
        {
            Name = ((LogLevelSetting)services.GetService(typeof(LogLevelSetting))!).Level.ToLowerInvariant(),
        });

        // Act
        IApplicationInstanceAction action = options.CreateActions(provider).ShouldHaveSingleItem();

        // Assert
        action.Name.ShouldBe(new ActionName("information"));
    }

    [Fact(DisplayName = DisplayPrefix + "No added action creates an empty list")]
    public void CreateActions_WithoutActions_ShouldReturnEmpty()
    {
        // Arrange
        TestOptions options = Create();

        // Act & Assert
        options.CreateActions(new TestServiceProvider()).ShouldBeEmpty();
        options.JsonContexts.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "Each context an added action came with is kept once, in order")]
    public void AddAction_WithJsonContexts_ShouldKeepEachContextOnce()
    {
        // Arrange
        TestOptions options = Create();

        // Act
        options
            .AddAction(new TestAction { Name = "log-level" }, TestJsonContext.Default)
            .AddAction(_ => new TestAction { Name = "log-format" }, TestJsonContext.Default)
            .AddAction(new TestAction { Name = "flush-cache" });

        // Assert
        options.JsonContexts.ShouldHaveSingleItem().ShouldBeSameAs(TestJsonContext.Default);
    }

    [Fact(DisplayName = DisplayPrefix + "A factory that returns null fails the creation")]
    public void CreateActions_WithFactoryReturningNull_ShouldThrow()
    {
        // Arrange
        TestOptions options = Create();
        options.AddAction(_ => null!);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => options.CreateActions(new TestServiceProvider()));

        // Assert
        exception.Message.ShouldContain("AddAction", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A factory that returns a disposable action fails the creation")]
    public void CreateActions_WithFactoryReturningDisposable_ShouldThrow()
    {
        // Arrange
        TestOptions options = Create();
        options.AddAction(_ => new DisposableTestAction());

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => options.CreateActions(new TestServiceProvider()));

        // Assert
        exception.Message.ShouldContain("drain-queue", Case.Sensitive);
        exception.Message.ShouldContain(nameof(DisposableTestAction), Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "A disposable action added as an instance stays the caller's")]
    public void CreateActions_WithDisposableInstance_ShouldReturnIt()
    {
        // Arrange
        var action = new DisposableTestAction();
        TestOptions options = Create();
        options.AddAction(action);

        // Act
        IApplicationInstanceAction created = options.CreateActions(new TestServiceProvider()).ShouldHaveSingleItem();

        // Assert
        created.ShouldBeSameAs(action);
    }

    [Fact(DisplayName = DisplayPrefix + "A null action, factory, or provider is refused")]
    public void AddAction_WithNull_ShouldThrow()
    {
        // Arrange
        TestOptions options = Create();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => options.AddAction((IApplicationInstanceAction)null!));
        Should.Throw<ArgumentNullException>(() => options.AddAction((Func<IServiceProvider, IApplicationInstanceAction>)null!));
        Should.Throw<ArgumentNullException>(() => options.CreateActions(null!));
    }

    // Options created outside any gateway's environment, so the resource runs unmanaged.
    private static TestOptions Create()
    {
        using (AppEnvironment.Enter(AppEnvironmentScope.Create(new Dictionary<string, string>(), CancellationToken.None)))
        {
            return new TestOptions();
        }
    }
}
