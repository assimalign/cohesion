namespace Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

// This test-only derived format composes the shipped header schema without modifying EBML.
internal static class DerivedSchema
{
    internal static EbmlSchema Create() => new EbmlSchemaBuilder().Include(EbmlSchema.Header)
        .Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xC0), "Envelope", EbmlElementType.MasterElement,
            unknownSizeAllowed: true, minOccurs: 1, maxOccurs: 1))
        .Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xC1), "Count", EbmlElementType.UnsignedInteger,
            [VInt.FromEncoded(0xC0)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL, maximumValue: 10UL))
        .Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xC2), "DefaultCount", EbmlElementType.UnsignedInteger,
            [VInt.FromEncoded(0xC0)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL, maximumValue: 10UL, defaultValue: 7UL))
        .Build();
}
