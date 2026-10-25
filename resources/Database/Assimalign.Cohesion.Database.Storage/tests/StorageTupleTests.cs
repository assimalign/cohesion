using System;
using System.Text;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public class StorageTupleTests
{
	[Fact(DisplayName = "Cohesion Test [StorageTuple] - ToBytes/FromBytes: Should roundtrip fields")]
	public void ToBytesAndFromBytes_ShouldRoundtripFields()
	{
		var tuple = new StorageTuple(
			new StorageTupleField("id", BitConverter.GetBytes(42)),
			new StorageTupleField("name", Encoding.UTF8.GetBytes("Alice")),
			new StorageTupleField("active", new byte[] { 1 }));

		byte[] payload = tuple.ToBytes();
		var decoded = StorageTuple.FromBytes(payload);

		decoded.Count.ShouldBe(3);
		decoded.TryGetField("id", out var idField).ShouldBeTrue();
		BitConverter.ToInt32(idField.Value.Span).ShouldBe(42);

		decoded.TryGetField("name", out var nameField).ShouldBeTrue();
		Encoding.UTF8.GetString(nameField.Value.Span).ShouldBe("Alice");

		decoded.TryGetField("active", out var activeField).ShouldBeTrue();
		activeField.Value.Span[0].ShouldBe((byte)1);
	}

	[Fact(DisplayName = "Cohesion Test [StorageTuple] - TryGetField: Should return false for missing field")]
	public void TryGetField_ShouldReturnFalseForMissingField()
	{
		var tuple = new StorageTuple(new StorageTupleField("id", BitConverter.GetBytes(7)));

		bool found = tuple.TryGetField("missing", out _);

		found.ShouldBeFalse();
	}

	[Fact(DisplayName = "Cohesion Test [StorageTuple] - FromBytes: Should reject malformed payload")]
	public void FromBytes_ShouldRejectMalformedPayload()
	{
		byte[] malformed = { 1, 0, 0, 0, 2, 0, 0, 0, (byte)'i' };

		Should.Throw<ArgumentException>(() => StorageTuple.FromBytes(malformed)).GetType().ShouldBe(typeof(ArgumentException));
	}

	[Fact(DisplayName = "Cohesion Test [StorageTupleField] - Ctor: Should reject empty names")]
	public void StorageTupleFieldCtor_ShouldRejectEmptyNames()
	{
		Should.Throw<ArgumentException>(() => new StorageTupleField("", ReadOnlyMemory<byte>.Empty)).GetType().ShouldBe(typeof(ArgumentException));
	}
}
