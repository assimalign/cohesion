using System;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A type of the AMQP 1.0 type system in the codec's supported surface: the constructors the specification
/// defines for it and the CLR types the codec maps to it.
/// </summary>
/// <param name="Name">The type's name in the specification, such as <c>uint</c>.</param>
/// <param name="Reference">The section of the specification that defines it.</param>
/// <param name="Constructors">The format codes that encode it.</param>
/// <param name="DecodesTo">
/// The CLR type every decoded value has, or <see langword="null"/> when it depends on the value: null has no
/// type, an array's depends on its elements, and a described value's on its descriptor.
/// </param>
/// <param name="EncodesFrom">The CLR types the encoder writes as this type.</param>
internal sealed record AmqpTypeDefinition(string Name, string Reference, byte[] Constructors, Type? DecodesTo, Type[] EncodesFrom);
