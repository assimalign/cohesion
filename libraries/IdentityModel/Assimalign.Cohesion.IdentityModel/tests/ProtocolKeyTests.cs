using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the protocol key model.
/// </summary>
public sealed class ProtocolKeyTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Unspecified key use should mean unrestricted")]
    public void CanSignAndCanEncrypt_WhenUseIsUnspecified_ShouldBothBeTrue()
    {
        // Most real-world JWKS documents and many SAML KeyDescriptors omit `use`; both
        // specs define absence as valid-for-any-purpose. A consumer filtering with the
        // helpers finds these keys; naive equality against Signing would not.
        var unspecified = new ProtocolKey(new ProtocolKeyDescriptor { KeyId = "kid-1" });
        var signing = new ProtocolKey(new ProtocolKeyDescriptor { KeyId = "kid-2", Use = ProtocolKeyUse.Signing });
        var encryption = new ProtocolKey(new ProtocolKeyDescriptor { KeyId = "kid-3", Use = ProtocolKeyUse.Encryption });

        unspecified.Use.ShouldBe(ProtocolKeyUse.Unspecified);
        unspecified.CanSign.ShouldBeTrue();
        unspecified.CanEncrypt.ShouldBeTrue();
        signing.CanSign.ShouldBeTrue();
        signing.CanEncrypt.ShouldBeFalse();
        encryption.CanSign.ShouldBeFalse();
        encryption.CanEncrypt.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Key should carry multiple algorithms and role scope")]
    public void Constructor_WhenConstructed_ShouldCarryAlgorithmsAndRoleScope()
    {
        // A SAML KeyDescriptor can declare several EncryptionMethod algorithms; the model
        // must not truncate the list, or algorithm negotiation fails interop.
        var descriptor = new ProtocolKeyDescriptor
        {
            Use = ProtocolKeyUse.Encryption,
            KeyId = "sp-enc",
            Role = ProtocolRole.RelyingParty,
        };
        descriptor.Certificates.Add("MIIC...base64...");
        descriptor.Algorithms.Add("http://www.w3.org/2009/xmlenc11#aes128-gcm");
        descriptor.Algorithms.Add("http://www.w3.org/2001/04/xmlenc#aes256-cbc");
        descriptor.Algorithms.Add("http://www.w3.org/2001/04/xmlenc#rsa-oaep-mgf1p");

        var key = new ProtocolKey(descriptor);
        descriptor.Algorithms.Add("late-mutation");

        key.Algorithms.Count.ShouldBe(3);
        key.Role.ShouldBe(ProtocolRole.RelyingParty);
        key.Certificates.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Key materialization should reject invalid entries")]
    public void Constructor_WhenEntriesAreInvalid_ShouldThrow()
    {
        var withBlankCertificate = new ProtocolKeyDescriptor();
        withBlankCertificate.Certificates.Add("  ");

        var withUndefinedProperty = new ProtocolKeyDescriptor();
        withUndefinedProperty.Properties["kty"] = default;

        Should.Throw<ArgumentException>(() => new ProtocolKey(withBlankCertificate));
        Should.Throw<ArgumentException>(() => new ProtocolKey(withUndefinedProperty));
        Should.Throw<ArgumentNullException>(() => new ProtocolKey(null!));
    }
}
