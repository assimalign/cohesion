using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Core.Tests;

public class AppEnvironmentScopeTests
{
    private const string DisplayPrefix = "Cohesion Test [Core] - AppEnvironmentScope: ";

    [Fact(DisplayName = DisplayPrefix + "A flow in a scope reads the scope's variables")]
    public void GetValue_InScope_ShouldReadTheScopesVariable()
    {
        // Arrange
        AppEnvironmentScope scope = AppEnvironmentScope.Create(
            new Dictionary<string, string> { [AppEnvironment.Variables.Resource] = "orders-api" },
            CancellationToken.None);

        // Act
        string? value;
        using (AppEnvironment.Enter(scope))
        {
            value = AppEnvironment.GetValue(AppEnvironment.Variables.Resource);
        }

        // Assert
        value.ShouldBe("orders-api");
    }

    [Fact(DisplayName = DisplayPrefix + "A contract variable the scope does not set reads as unset")]
    public void GetValue_InScope_WithContractVariableTheScopeDoesNotSet_ShouldHideTheProcessValue()
    {
        // Arrange
        string variable = AppEnvironment.Variables.Configuration($"ScopeTest{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(variable, "process");
        try
        {
            AppEnvironmentScope scope = AppEnvironmentScope.Create(new Dictionary<string, string>(), CancellationToken.None);

            // Act
            string? inside;
            using (AppEnvironment.Enter(scope))
            {
                inside = AppEnvironment.GetValue(variable);
            }

            string? outside = AppEnvironment.GetValue(variable);

            // Assert
            inside.ShouldBeNull();
            outside.ShouldBe("process");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "A variable outside the contract reads through to the process")]
    public void GetValue_InScope_WithVariableOutsideTheContract_ShouldReadTheProcess()
    {
        // Arrange
        string variable = $"SCOPE_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "process");
        try
        {
            AppEnvironmentScope scope = AppEnvironmentScope.Create(new Dictionary<string, string>(), CancellationToken.None);

            // Act
            string? value;
            using (AppEnvironment.Enter(scope))
            {
                value = AppEnvironment.GetValue(variable);
            }

            // Assert
            value.ShouldBe("process");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "Leaving a scope restores the one the flow ran in before")]
    public void Enter_WhenDisposed_ShouldRestoreThePreviousScope()
    {
        // Arrange
        AppEnvironmentScope outer = AppEnvironmentScope.Create(new Dictionary<string, string>(), CancellationToken.None);
        AppEnvironmentScope inner = AppEnvironmentScope.Create(new Dictionary<string, string>(), CancellationToken.None);

        // Act
        AppEnvironmentScope? during;
        AppEnvironmentScope? after;
        using (AppEnvironment.Enter(outer))
        {
            using (AppEnvironment.Enter(inner))
            {
                during = AppEnvironment.Current;
            }

            after = AppEnvironment.Current;
        }

        // Assert
        during.ShouldBeSameAs(inner);
        after.ShouldBeSameAs(outer);
        AppEnvironment.Current.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "Work the flow starts keeps the scope")]
    public async Task Enter_WithWorkStartedInside_ShouldFlowTheScope()
    {
        // Arrange
        AppEnvironmentScope scope = AppEnvironmentScope.Create(
            new Dictionary<string, string> { [AppEnvironment.Variables.Gateway] = "local" },
            CancellationToken.None);

        // Act
        Task<string?> work;
        using (AppEnvironment.Enter(scope))
        {
            work = Task.Run(() => AppEnvironment.GetValue(AppEnvironment.Variables.Gateway));
        }

        // Assert
        (await work).ShouldBe("local");
    }

    [Fact(DisplayName = DisplayPrefix + "Every variable a scope reads is the scope's over the process's outside the contract")]
    public void GetValues_InScope_ShouldMergeTheScopeOverTheProcess()
    {
        // Arrange
        string contract = AppEnvironment.Variables.Configuration($"ScopeTest{Guid.NewGuid():N}");
        string other = $"SCOPE_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(contract, "process");
        Environment.SetEnvironmentVariable(other, "process");
        try
        {
            AppEnvironmentScope scope = AppEnvironmentScope.Create(
                new Dictionary<string, string> { [AppEnvironment.Variables.Resource] = "orders-api" },
                CancellationToken.None);

            // Act
            IDictionary<string, string?> values;
            using (AppEnvironment.Enter(scope))
            {
                values = AppEnvironment.GetValues();
            }

            // Assert
            values[AppEnvironment.Variables.Resource].ShouldBe("orders-api");
            values[other].ShouldBe("process");
            values.ContainsKey(contract).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(contract, null);
            Environment.SetEnvironmentVariable(other, null);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "A scope refuses a variable without a name")]
    public void Create_WithEmptyName_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => AppEnvironmentScope.Create(
            new Dictionary<string, string> { [" "] = "value" },
            CancellationToken.None));
    }

    [Fact(DisplayName = DisplayPrefix + "A configuration key's variable separates its sections with double underscores")]
    public void Configuration_WithColonSeparatedKey_ShouldReturnTheBridgeVariable()
    {
        // Act
        string variable = AppEnvironment.Variables.Configuration("Orders:Url");

        // Assert
        variable.ShouldBe("COHESION_CONFIG__Orders__Url");
    }
}
