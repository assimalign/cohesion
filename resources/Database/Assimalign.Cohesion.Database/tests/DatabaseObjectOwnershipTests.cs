using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The ownership vocabulary the root keeps for every model catalog that persists it (owner
/// decision 51 of 2026-10-09): <see cref="DatabaseObjectOwner"/>, whose values are persisted, and
/// <see cref="DatabaseObjectLockedException"/>, the refusal of an ad-hoc change to a schema-owned
/// object.
/// </summary>
public class DatabaseObjectOwnershipTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Ownership: the persisted owner values and the locked-object refusal are fixed")]
    public void ObjectOwnership_SeparatesAdhocAndSchemaObjects()
    {
        // Assert
        ((byte)DatabaseObjectOwner.Adhoc).ShouldBe((byte)0);
        ((byte)DatabaseObjectOwner.Schema).ShouldBe((byte)1);

        var exception = new DatabaseObjectLockedException("Customers", "AppSchema", "DROP TABLE");
        exception.ShouldBeAssignableTo<DatabaseException>();
        exception.ObjectName.ShouldBe("Customers");
        exception.OwningSchema.ShouldBe("AppSchema");
        exception.Operation.ShouldBe("DROP TABLE");
        exception.Message.ShouldBe(
            "Object 'Customers' is owned by schema 'AppSchema' and cannot be changed by DROP TABLE. Alter the schema and redeploy it.");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Ownership: an owner other than a compiled schema supplies its own message")]
    public void ObjectLocked_WithMessage_ShouldKeepTheIdentityAndTheMessage()
    {
        // Act
        var exception = new DatabaseObjectLockedException("sales", "sales", "DROP DATABASE", "The declaration owns 'sales'.");

        // Assert
        exception.ObjectName.ShouldBe("sales");
        exception.OwningSchema.ShouldBe("sales");
        exception.Operation.ShouldBe("DROP DATABASE");
        exception.Message.ShouldBe("The declaration owns 'sales'.");
        Should.Throw<System.ArgumentException>(() => new DatabaseObjectLockedException("sales", "sales", "DROP DATABASE", " "))
            .ParamName.ShouldBe("message");
    }
}
