using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StorageErrorCodeTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Storage failures use the kernel storage code range")]
    public void StorageFailures_ShouldUseKernelStorageCodes()
    {
        StorageFormatException.ErrorCode.ShouldBe("CK06001");
        StorageOfflineException.ErrorCode.ShouldBe("CK06002");
        StorageCorruptionException.ErrorCode.ShouldBe("CK06003");
    }
}
