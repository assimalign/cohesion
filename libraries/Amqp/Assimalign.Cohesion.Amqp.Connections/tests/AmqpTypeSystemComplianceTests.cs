using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Runs the type-system corpus (AMQP 1.0 Part 1) through the encoder and the reader: every constructor the
/// specification defines decodes to the CLR type the surface names, the encoder writes each supported CLR type
/// as the encoding the specification gives it, and what has no encoding is refused with the message for it.
/// </summary>
public class AmqpTypeSystemComplianceTests
{
    // Part 1 defines 39 format codes for its 24 primitive types, plus 0x00, which begins a described value.
    private const int constructorCount = 40;

    private readonly ITestOutputHelper _output;

    public AmqpTypeSystemComplianceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> DecodeCaseNames()
    {
        return CaseNames(AmqpTypeEncodingDirection.RoundTrip, AmqpTypeEncodingDirection.DecodeOnly, AmqpTypeEncodingDirection.DecodeRefused);
    }

    public static TheoryData<string> EncodeCaseNames()
    {
        return CaseNames(AmqpTypeEncodingDirection.RoundTrip, AmqpTypeEncodingDirection.EncodeOnly, AmqpTypeEncodingDirection.EncodeRefused);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - ReadValue: Should decode or refuse each encoding in the type-system corpus as its type requires")]
    [MemberData(nameof(DecodeCaseNames))]
    public void ReadValue_WithCorpusCase_ShouldFollowItsType(string name)
    {
        // Arrange
        AmqpTypeEncodingCase entry = AmqpTypeSystemCorpus.Find(name);
        byte[] encoded = Convert.FromHexString(entry.Hex);

        // Act
        object? decoded = null;
        Exception? exception = Record.Exception(() => decoded = Decode(encoded));

        // Assert
        if (entry.Direction == AmqpTypeEncodingDirection.DecodeRefused)
        {
            AmqpException refusal = exception.ShouldBeOfType<AmqpException>(name);
            refusal.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
            refusal.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.DecodeError);
            refusal.Message.ShouldBe(entry.Refusal);
            return;
        }

        exception.ShouldBeNull(name);

        if (entry.Value is null)
        {
            decoded.ShouldBeNull(name);
            return;
        }

        // The same AMQP value encodes the same way, which compares arrays, maps, NaN and negative zero exactly.
        decoded.ShouldNotBeNull(name).GetType().ShouldBe(entry.Value.GetType(), name);
        Convert.ToHexStringLower(Encode(decoded)).ShouldBe(Convert.ToHexStringLower(Encode(entry.Value)), name);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should write or refuse each value in the type-system corpus as its type requires")]
    [MemberData(nameof(EncodeCaseNames))]
    public void WriteValue_WithCorpusCase_ShouldFollowItsType(string name)
    {
        // Arrange
        AmqpTypeEncodingCase entry = AmqpTypeSystemCorpus.Find(name);
        AmqpTypeDefinition type = AmqpTypeSystemCorpus.FindType(entry.Type);

        // Act
        byte[]? encoded = null;
        Exception? exception = Record.Exception(() => encoded = Encode(entry.Value));

        // Assert
        if (entry.Direction == AmqpTypeEncodingDirection.EncodeRefused)
        {
            ArgumentException refusal = exception.ShouldBeOfType<ArgumentException>(name);
            refusal.ParamName.ShouldBe("value");
            refusal.Message.ShouldStartWith(entry.Refusal!, Case.Sensitive);
            return;
        }

        exception.ShouldBeNull(name);
        Convert.ToHexStringLower(encoded!).ShouldBe(entry.Hex, name);

        if (type.DecodesTo is Type decodesTo)
        {
            Decode(Convert.FromHexString(entry.Hex)).ShouldNotBeNull(name).GetType().ShouldBe(decodesTo, name);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - WriteValue: Should refuse a CLR type outside the supported surface, naming it")]
    public void WriteValue_WithUnsupportedClrType_ShouldThrowArgumentException()
    {
        foreach (object value in AmqpTypeSystemCorpus.Unsupported)
        {
            // Act
            ArgumentException exception = Should.Throw<ArgumentException>(() => Encode(value), value.GetType().ToString());

            // Assert
            exception.ParamName.ShouldBe("value");
            exception.Message.ShouldContain(value.GetType().ToString(), Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Corpus: Should cover every constructor and every supported CLR type of the type system")]
    public void Corpus_OnEveryType_ShouldCoverConstructorsAndClrTypes()
    {
        // Arrange
        IReadOnlyList<AmqpTypeEncodingCase> cases = AmqpTypeSystemCorpus.Cases;
        string[] names = cases.Select(entry => entry.Name).ToArray();

        foreach (AmqpTypeDefinition type in AmqpTypeSystemCorpus.Types)
        {
            // Act
            AmqpTypeEncodingCase[] own = cases.Where(entry => entry.Type == type.Name).ToArray();
            AmqpTypeEncodingCase[] decoded = own.Where(entry => entry.Direction is AmqpTypeEncodingDirection.RoundTrip or AmqpTypeEncodingDirection.DecodeOnly).ToArray();
            AmqpTypeEncodingCase[] encoded = own.Where(entry => entry.Direction is AmqpTypeEncodingDirection.RoundTrip or AmqpTypeEncodingDirection.EncodeOnly).ToArray();

            _output.WriteLine(
                $"{type.Name} ({type.Reference}): {type.Constructors.Length} constructor(s), {decoded.Length} decoded, {encoded.Length} encoded, " +
                $"{own.Count(entry => entry.Direction is AmqpTypeEncodingDirection.DecodeRefused or AmqpTypeEncodingDirection.EncodeRefused)} refused.");

            // Assert: every constructor decodes, and every CLR type the surface names is written.
            foreach (byte constructor in type.Constructors)
            {
                decoded.ShouldContain(entry => Convert.FromHexString(entry.Hex)[0] == constructor, $"{type.Name}: 0x{constructor:x2}");
            }

            foreach (Type clrType in type.EncodesFrom)
            {
                encoded.ShouldContain(entry => entry.Value != null && entry.Value.GetType() == clrType, $"{type.Name}: {clrType}");
            }

            if (type.DecodesTo is Type decodesTo)
            {
                decoded.ShouldAllBe(entry => entry.Value != null && entry.Value.GetType() == decodesTo, type.Name);
            }
        }

        AmqpTypeSystemCorpus.Types.SelectMany(type => type.Constructors).Distinct().Count().ShouldBe(constructorCount);
        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Length);
        cases.ShouldAllBe(entry => AmqpTypeSystemCorpus.Types.Any(type => type.Name == entry.Type));
        cases.ShouldAllBe(entry => (entry.Refusal != null) == (entry.Direction == AmqpTypeEncodingDirection.DecodeRefused || entry.Direction == AmqpTypeEncodingDirection.EncodeRefused));
    }

    private static TheoryData<string> CaseNames(params AmqpTypeEncodingDirection[] directions)
    {
        TheoryData<string> names = new();

        foreach (AmqpTypeEncodingCase entry in AmqpTypeSystemCorpus.Cases)
        {
            if (directions.Contains(entry.Direction))
            {
                names.Add(entry.Name);
            }
        }

        return names;
    }

    private static byte[] Encode(object? value)
    {
        using AmqpBufferWriter writer = new();

        AmqpEncoder.WriteValue(writer, value);
        return writer.ToArray();
    }

    private static object? Decode(byte[] encoded)
    {
        AmqpReader reader = new(encoded);
        object? value = reader.ReadValue();

        reader.IsEnd.ShouldBeTrue();
        return value;
    }
}
