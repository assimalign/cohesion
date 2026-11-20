using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubEntryPageTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should keep the entries and the continuation")]
    public void Constructor_WithEntriesAndAContinuation_ShouldExposeThem()
    {
        // Arrange
        IdentityHubEntry<TestGrant> first = Entry("code-1");
        IdentityHubEntry<TestGrant> second = Entry("code-2");

        // Act
        var page = new IdentityHubEntryPage<TestGrant>([first, second], "token");
        var last = new IdentityHubEntryPage<TestGrant>(Array.Empty<IdentityHubEntry<TestGrant>>(), null);

        // Assert
        page.Entries.ShouldBe([first, second]);
        page.Continuation.ShouldBe("token");
        last.Entries.ShouldBeEmpty();
        last.Continuation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should copy the list of entries")]
    public void Constructor_WhenTheCallerChangesItsListAfterwards_ShouldKeepTheEntries()
    {
        // Arrange
        IdentityHubEntry<TestGrant> first = Entry("code-1");
        var entries = new List<IdentityHubEntry<TestGrant>> { first };
        var page = new IdentityHubEntryPage<TestGrant>(entries, null);

        // Act
        entries.Add(Entry("code-2"));

        // Assert
        page.Entries.ShouldBe([first]);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse a null list, a null entry, and an empty continuation")]
    public void Constructor_WithANullListOrEntryOrEmptyContinuation_ShouldThrow()
    {
        // Act
        ArgumentNullException nullList = Should.Throw<ArgumentNullException>(() => new IdentityHubEntryPage<TestGrant>(null!, null));
        ArgumentException nullEntry = Should.Throw<ArgumentException>(
            () => new IdentityHubEntryPage<TestGrant>([Entry("code-1"), null!], null));
        ArgumentException emptyToken = Should.Throw<ArgumentException>(
            () => new IdentityHubEntryPage<TestGrant>([Entry("code-1")], string.Empty));

        // Assert
        nullList.ParamName.ShouldBe("entries");
        nullEntry.ParamName.ShouldBe("entries");
        emptyToken.ParamName.ShouldBe("continuation");
    }

    private static IdentityHubEntry<TestGrant> Entry(string code)
    {
        var grant = new TestGrant(code, "ABCD-EFGH", "client-1", Subject: null, ExpiresAt: DateTimeOffset.UnixEpoch);
        return new IdentityHubEntry<TestGrant>(code, grant, "1");
    }
}
