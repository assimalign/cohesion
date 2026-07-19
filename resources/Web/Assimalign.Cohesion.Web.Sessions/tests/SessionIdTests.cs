using System;
using System.Linq;

using Assimalign.Cohesion.Web.Sessions.Internal;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Sessions.Tests;

public class SessionIdTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - SessionId: Should be a 22-character URL-safe 128-bit token")]
    public void Create_ShouldReturnUrlSafe128BitToken()
    {
        // Act
        string id = SessionId.Create();

        // Assert — 16 bytes of base64url (no padding) is 22 characters, all cookie-safe.
        id.Length.ShouldBe(22);
        id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - SessionId: Successive ids should differ")]
    public void Create_Twice_ShouldProduceDistinctIds()
    {
        SessionId.Create().ShouldNotBe(SessionId.Create());
    }
}
