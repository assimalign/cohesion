using System;
using System.Text;
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

		Assert.Equal(3, decoded.Count);
		Assert.True(decoded.TryGetField("id", out var idField));
		Assert.Equal(42, BitConverter.ToInt32(idField.Value.Span));

		Assert.True(decoded.TryGetField("name", out var nameField));
		Assert.Equal("Alice", Encoding.UTF8.GetString(nameField.Value.Span));

		Assert.True(decoded.TryGetField("active", out var activeField));
		Assert.Equal(1, activeField.Value.Span[0]);
	}

	[Fact(DisplayName = "Cohesion Test [StorageTuple] - TryGetField: Should return false for missing field")]
	public void TryGetField_ShouldReturnFalseForMissingField()
	{
		var tuple = new StorageTuple(new StorageTupleField("id", BitConverter.GetBytes(7)));

		bool found = tuple.TryGetField("missing", out _);

		Assert.False(found);
	}

	[Fact(DisplayName = "Cohesion Test [StorageTuple] - FromBytes: Should reject malformed payload")]
	public void FromBytes_ShouldRejectMalformedPayload()
	{
		byte[] malformed = { 1, 0, 0, 0, 2, 0, 0, 0, (byte)'i' };

		Assert.Throws<ArgumentException>(() => StorageTuple.FromBytes(malformed));
	}

	[Fact(DisplayName = "Cohesion Test [StorageTupleField] - Ctor: Should reject empty names")]
	public void StorageTupleFieldCtor_ShouldRejectEmptyNames()
	{
		Assert.Throws<ArgumentException>(() => new StorageTupleField("", ReadOnlyMemory<byte>.Empty));
	}
}
