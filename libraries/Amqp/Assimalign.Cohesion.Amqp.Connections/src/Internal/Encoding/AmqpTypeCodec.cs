using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Encodes and decodes the described types the root package models: errors, delivery states and
/// outcomes, termini, and the transaction controller's messages (AMQP 1.0 Part 2, section 2.8; Part 3,
/// section 3.5; Part 4, section 4.5).
/// </summary>
/// <remarks>
/// <para>
/// A <c>Read*Body</c> method reads the list that follows a descriptor the caller already consumed; the
/// other readers consume the descriptor themselves and accept <c>null</c>.
/// </para>
/// <para>
/// A writer's <c>depth</c> is the number of compound and described values that enclose the value it writes.
/// Its fields are two levels deeper, one for the descriptor and one for the list, which is as deep as
/// <see cref="AmqpReader"/> ever counts them, so whatever these writers write the reader accepts. The
/// overloads without a depth write a field of a performative, one level below the performative's list.
/// </para>
/// </remarks>
internal static class AmqpTypeCodec
{
    public static void WriteError(AmqpBufferWriter writer, AmqpError? error)
    {
        if (error is null)
        {
            AmqpEncoder.WriteNull(writer);
            return;
        }

        WriteError(writer, error, 1);
    }

    internal static void WriteError(AmqpBufferWriter writer, AmqpError error, int depth)
    {
        if (error.Condition.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ErrorConditionMissing, nameof(error));
        }

        AmqpEncoder.ThrowIfTooDeep(depth);

        AmqpListWriter list = new(writer, AmqpDescriptor.Error);

        list.WriteSymbol(error.Condition);
        list.WriteString(error.Description);
        AmqpEncoder.WriteSymbolMap(writer, error.Info, depth + 2);
        list.Field(error.Info is null);
        list.Complete();
    }

    public static AmqpError? ReadError(ref AmqpReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        ExpectDescriptor(ref reader, AmqpDescriptor.Error);
        return ReadErrorBody(ref reader);
    }

    internal static AmqpError ReadErrorBody(ref AmqpReader reader)
    {
        AmqpReader fields = reader.ReadList(out int count);

        AmqpSymbol? condition = count > 0 ? fields.ReadSymbol() : null;
        string? description = count > 1 ? fields.ReadString() : null;
        Dictionary<AmqpSymbol, object?>? info = count > 2 ? fields.ReadFields() : null;

        if (condition is not AmqpSymbol value)
        {
            throw MandatoryFieldMissing("error", "condition");
        }

        return new AmqpError(value, description, info);
    }

    public static void WriteDeliveryState(AmqpBufferWriter writer, AmqpDeliveryState? state)
    {
        if (state is null)
        {
            AmqpEncoder.WriteNull(writer);
            return;
        }

        WriteDeliveryState(writer, state, 1);
    }

    internal static void WriteDeliveryState(AmqpBufferWriter writer, AmqpDeliveryState state, int depth)
    {
        AmqpEncoder.ThrowIfTooDeep(depth);

        switch (state)
        {
            case AmqpAccepted:
                WriteEmpty(writer, AmqpDescriptor.Accepted);
                return;
            case AmqpReleased:
                WriteEmpty(writer, AmqpDescriptor.Released);
                return;
            case AmqpRejected rejected:
            {
                AmqpListWriter list = new(writer, AmqpDescriptor.Rejected);

                if (rejected.Error is null)
                {
                    list.WriteNull();
                }
                else
                {
                    WriteError(writer, rejected.Error, depth + 2);
                    list.Field(false);
                }

                list.Complete();
                return;
            }
            case AmqpModified modified:
            {
                AmqpListWriter list = new(writer, AmqpDescriptor.Modified);

                list.WriteBoolean(modified.DeliveryFailed, false);
                list.WriteBoolean(modified.UndeliverableHere, false);
                AmqpEncoder.WriteSymbolMap(writer, modified.MessageAnnotations, depth + 2);
                list.Field(modified.MessageAnnotations is null);
                list.Complete();
                return;
            }
            case AmqpReceived received:
            {
                AmqpListWriter list = new(writer, AmqpDescriptor.Received);

                list.WriteUInt(received.SectionNumber);
                list.WriteULong(received.SectionOffset);
                list.Complete();
                return;
            }
            case AmqpDeclared declared:
            {
                AmqpListWriter list = new(writer, AmqpDescriptor.Declared);

                list.WriteBinary(declared.TransactionId.Span);
                list.Complete();
                return;
            }
            case AmqpTransactionalState transactional:
            {
                AmqpListWriter list = new(writer, AmqpDescriptor.TransactionalState);

                list.WriteBinary(transactional.TransactionId.Span);

                if (transactional.Outcome is null)
                {
                    list.WriteNull();
                }
                else
                {
                    WriteDeliveryState(writer, transactional.Outcome, depth + 2);
                    list.Field(false);
                }

                list.Complete();
                return;
            }
            default:
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.ValueTypeUnsupported, state.GetType()),
                    nameof(state));
        }
    }

    public static AmqpDeliveryState? ReadDeliveryState(ref AmqpReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        if (!reader.TryReadDescriptor(out ulong? code, out _) || !IsDeliveryState(code))
        {
            throw AmqpException.DecodeError(ErrorMessages.DeliveryStateInvalid);
        }

        return ReadDeliveryStateBody(ref reader, code.GetValueOrDefault());
    }

    /// <summary>
    /// Reads an outcome, the terminal subset of the delivery states.
    /// </summary>
    public static AmqpOutcome? ReadOutcome(ref AmqpReader reader)
    {
        AmqpDeliveryState? state = ReadDeliveryState(ref reader);

        return state switch
        {
            null => null,
            AmqpOutcome outcome => outcome,
            _ => throw AmqpException.DecodeError(ErrorMessages.OutcomeInvalid)
        };
    }

    internal static AmqpDeliveryState ReadDeliveryStateBody(ref AmqpReader reader, ulong code)
    {
        AmqpReader fields = reader.ReadList(out int count);

        switch (code)
        {
            case AmqpDescriptor.Accepted:
                return AmqpAccepted.Instance;
            case AmqpDescriptor.Released:
                return AmqpReleased.Instance;
            case AmqpDescriptor.Rejected:
                return new AmqpRejected(count > 0 ? ReadError(ref fields) : null);
            case AmqpDescriptor.Modified:
            {
                bool deliveryFailed = count > 0 && fields.ReadBoolean(false);
                bool undeliverableHere = count > 1 && fields.ReadBoolean(false);
                Dictionary<AmqpSymbol, object?>? annotations = count > 2 ? fields.ReadFields() : null;

                return new AmqpModified(deliveryFailed, undeliverableHere, annotations);
            }
            case AmqpDescriptor.Received:
            {
                uint? sectionNumber = count > 0 ? fields.ReadNullableUInt() : null;
                ulong? sectionOffset = count > 1 ? fields.ReadNullableULong() : null;

                if (sectionNumber is null)
                {
                    throw MandatoryFieldMissing("received", "section-number");
                }

                if (sectionOffset is null)
                {
                    throw MandatoryFieldMissing("received", "section-offset");
                }

                return new AmqpReceived(sectionNumber.Value, sectionOffset.Value);
            }
            case AmqpDescriptor.Declared:
                return new AmqpDeclared(ReadTransactionId(ref fields, count, "declared"));
            case AmqpDescriptor.TransactionalState:
            {
                byte[] transactionId = ReadTransactionId(ref fields, count, "transactional-state");
                AmqpOutcome? outcome = count > 1 ? ReadOutcome(ref fields) : null;

                return new AmqpTransactionalState(transactionId, outcome);
            }
            default:
                throw AmqpException.DecodeError(ErrorMessages.DeliveryStateInvalid);
        }
    }

    public static void WriteSource(AmqpBufferWriter writer, AmqpSource? source)
    {
        if (source is null)
        {
            AmqpEncoder.WriteNull(writer);
            return;
        }

        WriteSource(writer, source, 1);
    }

    internal static void WriteSource(AmqpBufferWriter writer, AmqpSource source, int depth)
    {
        AmqpEncoder.ThrowIfTooDeep(depth);

        AmqpListWriter list = new(writer, AmqpDescriptor.Source);

        list.WriteString(source.Address);
        list.WriteUInt((uint)source.Durable, 0u);
        WriteExpiryPolicy(ref list, source.ExpiryPolicy);
        list.WriteUInt(source.Timeout, 0u);
        list.WriteBoolean(source.Dynamic, false);
        AmqpEncoder.WriteSymbolMap(writer, source.DynamicNodeProperties, depth + 2);
        list.Field(source.DynamicNodeProperties is null);
        list.WriteSymbol(source.DistributionMode);
        WriteFilterSet(writer, source.Filter, depth + 2);
        list.Field(source.Filter is null);

        if (source.DefaultOutcome is null)
        {
            list.WriteNull();
        }
        else
        {
            WriteDeliveryState(writer, source.DefaultOutcome, depth + 2);
            list.Field(false);
        }

        list.WriteSymbolMultiple(source.Outcomes);
        list.WriteSymbolMultiple(source.Capabilities);
        list.Complete();
    }

    public static AmqpSource? ReadSource(ref AmqpReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        ExpectDescriptor(ref reader, AmqpDescriptor.Source);
        return ReadSourceBody(ref reader);
    }

    internal static AmqpSource ReadSourceBody(ref AmqpReader reader)
    {
        AmqpReader fields = reader.ReadList(out int count);
        AmqpSource source = new();

        source.Address = count > 0 ? fields.ReadAddress() : null;
        source.Durable = ReadDurability(ref fields, count > 1);
        source.ExpiryPolicy = ReadExpiryPolicy(ref fields, count > 2);
        source.Timeout = (count > 3 ? fields.ReadNullableUInt() : null) ?? 0u;
        source.Dynamic = count > 4 && fields.ReadBoolean(false);
        source.DynamicNodeProperties = count > 5 ? fields.ReadFields() : null;
        source.DistributionMode = count > 6 ? fields.ReadSymbol() : null;
        source.Filter = count > 7 ? fields.ReadFilterSet() : null;
        source.DefaultOutcome = count > 8 ? ReadOutcome(ref fields) : null;
        source.Outcomes = count > 9 ? fields.ReadSymbolMultiple() : null;
        source.Capabilities = count > 10 ? fields.ReadSymbolMultiple() : null;

        return source;
    }

    /// <summary>
    /// Writes a target terminus: an <see cref="AmqpTarget"/>, or the <see cref="AmqpCoordinator"/> a
    /// transaction controller attaches to.
    /// </summary>
    public static void WriteTarget(AmqpBufferWriter writer, IAmqpTarget? target)
    {
        switch (target)
        {
            case null:
                AmqpEncoder.WriteNull(writer);
                return;
            case AmqpTarget amqpTarget:
                WriteTarget(writer, amqpTarget, 1);
                return;
            case AmqpCoordinator coordinator:
                WriteCoordinator(writer, coordinator);
                return;
            default:
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.ValueTypeUnsupported, target.GetType()),
                    nameof(target));
        }
    }

    internal static void WriteTarget(AmqpBufferWriter writer, AmqpTarget target, int depth)
    {
        AmqpEncoder.ThrowIfTooDeep(depth);

        AmqpListWriter list = new(writer, AmqpDescriptor.Target);

        list.WriteString(target.Address);
        list.WriteUInt((uint)target.Durable, 0u);
        WriteExpiryPolicy(ref list, target.ExpiryPolicy);
        list.WriteUInt(target.Timeout, 0u);
        list.WriteBoolean(target.Dynamic, false);
        AmqpEncoder.WriteSymbolMap(writer, target.DynamicNodeProperties, depth + 2);
        list.Field(target.DynamicNodeProperties is null);
        list.WriteSymbolMultiple(target.Capabilities);
        list.Complete();
    }

    internal static void WriteCoordinator(AmqpBufferWriter writer, AmqpCoordinator coordinator)
    {
        AmqpListWriter list = new(writer, AmqpDescriptor.Coordinator);

        list.WriteSymbolMultiple(coordinator.Capabilities);
        list.Complete();
    }

    /// <summary>
    /// Reads a target terminus: a <c>target</c> or a <c>coordinator</c>.
    /// </summary>
    public static IAmqpTarget? ReadTarget(ref AmqpReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        if (!reader.TryReadDescriptor(out ulong? code, out _))
        {
            throw AmqpException.DecodeError(ErrorMessages.TargetInvalid);
        }

        return code switch
        {
            AmqpDescriptor.Target => ReadTargetBody(ref reader),
            AmqpDescriptor.Coordinator => ReadCoordinatorBody(ref reader),
            _ => throw AmqpException.DecodeError(ErrorMessages.TargetInvalid)
        };
    }

    internal static AmqpTarget ReadTargetBody(ref AmqpReader reader)
    {
        AmqpReader fields = reader.ReadList(out int count);
        AmqpTarget target = new();

        target.Address = count > 0 ? fields.ReadAddress() : null;
        target.Durable = ReadDurability(ref fields, count > 1);
        target.ExpiryPolicy = ReadExpiryPolicy(ref fields, count > 2);
        target.Timeout = (count > 3 ? fields.ReadNullableUInt() : null) ?? 0u;
        target.Dynamic = count > 4 && fields.ReadBoolean(false);
        target.DynamicNodeProperties = count > 5 ? fields.ReadFields() : null;
        target.Capabilities = count > 6 ? fields.ReadSymbolMultiple() : null;

        return target;
    }

    internal static AmqpCoordinator ReadCoordinatorBody(ref AmqpReader reader)
    {
        AmqpReader fields = reader.ReadList(out int count);

        return new AmqpCoordinator
        {
            Capabilities = count > 0 ? fields.ReadSymbolMultiple() : null
        };
    }

    internal static void WriteDeclare(AmqpBufferWriter writer, AmqpDeclare declare, int depth)
    {
        AmqpEncoder.ThrowIfTooDeep(depth);

        AmqpListWriter list = new(writer, AmqpDescriptor.Declare);

        AmqpEncoder.WriteValue(writer, declare.GlobalId, depth + 2);
        list.Field(declare.GlobalId is null);
        list.Complete();
    }

    internal static AmqpDeclare ReadDeclareBody(ref AmqpReader reader)
    {
        AmqpReader fields = reader.ReadList(out int count);

        return new AmqpDeclare
        {
            GlobalId = count > 0 ? fields.ReadValue() : null
        };
    }

    internal static void WriteDischarge(AmqpBufferWriter writer, AmqpDischarge discharge)
    {
        AmqpListWriter list = new(writer, AmqpDescriptor.Discharge);

        list.WriteBinary(discharge.TransactionId.Span);
        list.WriteBoolean(discharge.Fail, false);
        list.Complete();
    }

    internal static AmqpDischarge ReadDischargeBody(ref AmqpReader reader)
    {
        AmqpReader fields = reader.ReadList(out int count);
        byte[] transactionId = ReadTransactionId(ref fields, count, "discharge");
        bool fail = count > 1 && fields.ReadBoolean(false);

        return new AmqpDischarge(transactionId, fail);
    }

    /// <summary>
    /// Writes the <c>unsettled</c> map of an <c>attach</c>: delivery tags to the local delivery state.
    /// </summary>
    public static void WriteUnsettled(AmqpBufferWriter writer, IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? unsettled)
    {
        if (unsettled is null)
        {
            AmqpEncoder.WriteNull(writer);
            return;
        }

        int start = AmqpEncoder.BeginCompound(writer, AmqpFormatCode.Map32);

        foreach (KeyValuePair<AmqpDeliveryTag, AmqpDeliveryState?> entry in unsettled)
        {
            AmqpEncoder.WriteBinary(writer, entry.Key.Value.Span);

            // The map is a field of the attach, so its states are one level below it.
            if (entry.Value is null)
            {
                AmqpEncoder.WriteNull(writer);
            }
            else
            {
                WriteDeliveryState(writer, entry.Value, 2);
            }
        }

        AmqpEncoder.EndCompound(writer, start, unsettled.Count * 2, AmqpFormatCode.Map8);
    }

    public static Dictionary<AmqpDeliveryTag, AmqpDeliveryState?>? ReadUnsettled(ref AmqpReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        AmqpReader entries = reader.ReadMap(out int count);
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> unsettled = new(count / 2);

        for (int i = 0; i < count; i += 2)
        {
            if (!entries.ReadBinary(out ReadOnlySpan<byte> tag) || !AmqpDeliveryTag.TryCreate(tag, out AmqpDeliveryTag deliveryTag))
            {
                throw AmqpException.DecodeError(ErrorMessages.DeliveryTagInvalid);
            }

            if (!unsettled.TryAdd(deliveryTag, ReadDeliveryState(ref entries)))
            {
                throw AmqpException.DecodeError(ErrorMessages.MapKeyDuplicate);
            }
        }

        return unsettled;
    }

    /// <summary>
    /// Creates the exception for a described type that arrived without a field AMQP 1.0 makes mandatory.
    /// </summary>
    public static AmqpException MandatoryFieldMissing(string type, string field)
    {
        return AmqpException.DecodeError(string.Format(CultureInfo.CurrentCulture, ErrorMessages.MandatoryFieldMissing, field, type));
    }

    private static void ExpectDescriptor(ref AmqpReader reader, ulong expected)
    {
        if (!reader.TryReadDescriptor(out ulong? code, out _) || code != expected)
        {
            throw AmqpException.DecodeError(ErrorMessages.DescriptorUnexpected);
        }
    }

    private static bool IsDeliveryState(ulong? code)
    {
        return code is AmqpDescriptor.Received
            or AmqpDescriptor.Accepted
            or AmqpDescriptor.Rejected
            or AmqpDescriptor.Released
            or AmqpDescriptor.Modified
            or AmqpDescriptor.Declared
            or AmqpDescriptor.TransactionalState;
    }

    private static byte[] ReadTransactionId(ref AmqpReader fields, int count, string type)
    {
        if (count < 1 || !fields.ReadBinary(out ReadOnlySpan<byte> transactionId))
        {
            throw MandatoryFieldMissing(type, "txn-id");
        }

        return transactionId.ToArray();
    }

    private static void WriteEmpty(AmqpBufferWriter writer, ulong descriptor)
    {
        AmqpEncoder.WriteDescriptor(writer, descriptor);
        writer.WriteByte(AmqpFormatCode.List0);
    }

    /// <summary>
    /// Writes a source's filter set, whose values must be described filters or <c>null</c> (AMQP 1.0 Part 3,
    /// section 3.5.8).
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when a filter's value is not an <see cref="AmqpDescribedValue"/> or <see langword="null"/>.</exception>
    private static void WriteFilterSet(AmqpBufferWriter writer, IReadOnlyDictionary<AmqpSymbol, object?>? filters, int depth)
    {
        if (filters is not null)
        {
            foreach (KeyValuePair<AmqpSymbol, object?> filter in filters)
            {
                if (filter.Value is not (null or AmqpDescribedValue))
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.FilterValueInvalid, filter.Key),
                        nameof(filters));
                }
            }
        }

        AmqpEncoder.WriteSymbolMap(writer, filters, depth);
    }

    /// <summary>
    /// Reads a terminus's durability, which must be one of the three AMQP 1.0 defines (Part 3, section 3.5.5).
    /// </summary>
    private static AmqpTerminusDurability ReadDurability(ref AmqpReader fields, bool present)
    {
        uint durable = (present ? fields.ReadNullableUInt() : null) ?? 0u;

        if (durable > (uint)AmqpTerminusDurability.UnsettledState)
        {
            throw AmqpException.ProtocolViolation(
                AmqpErrorCondition.InvalidField,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.TerminusDurabilityInvalid, durable));
        }

        return (AmqpTerminusDurability)durable;
    }

    /// <summary>
    /// Reads a terminus's expiry policy, which must be one of the four AMQP 1.0 defines (Part 3,
    /// section 3.5.6); an absent policy is <c>session-end</c>.
    /// </summary>
    private static AmqpSymbol ReadExpiryPolicy(ref AmqpReader fields, bool present)
    {
        if ((present ? fields.ReadSymbol() : null) is not AmqpSymbol policy)
        {
            return AmqpTerminusExpiryPolicy.SessionEnd;
        }

        if (policy != AmqpTerminusExpiryPolicy.LinkDetach
            && policy != AmqpTerminusExpiryPolicy.SessionEnd
            && policy != AmqpTerminusExpiryPolicy.ConnectionClose
            && policy != AmqpTerminusExpiryPolicy.Never)
        {
            throw AmqpException.ProtocolViolation(
                AmqpErrorCondition.InvalidField,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.TerminusExpiryPolicyInvalid, policy));
        }

        return policy;
    }

    private static void WriteExpiryPolicy(ref AmqpListWriter list, AmqpSymbol policy)
    {
        // session-end is the default, so it is left out like every other default.
        if (policy.Value is null || policy == AmqpTerminusExpiryPolicy.SessionEnd)
        {
            list.WriteNull();
        }
        else
        {
            list.WriteSymbol(policy);
        }
    }
}
