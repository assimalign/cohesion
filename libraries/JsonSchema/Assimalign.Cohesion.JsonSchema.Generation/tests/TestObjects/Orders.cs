using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.JsonSchema.Generation.Tests;

public sealed class Address
{
    public string Street { get; set; } = string.Empty;

    public string? City { get; set; }
}

public enum OrderStatus
{
    Pending,
    Shipped,
    Delivered,
}

public sealed class OrderLine
{
    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public decimal Price { get; set; }
}

/// <summary>An order: a constructor with a defaulted parameter, required and nullable members, an enum, a list and a dictionary.</summary>
public sealed class Order
{
    public Order(string number, int priority = 3)
    {
        Number = number;
        Priority = priority;
    }

    public string Number { get; }

    public int Priority { get; }

    public required Address Shipping { get; init; }

    public Address? Billing { get; set; }

    public List<OrderLine> Lines { get; set; } = new();

    public OrderStatus Status { get; set; }

    public OrderStatus? PreviousStatus { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }

    public DateTimeOffset Created { get; set; }

    public Guid Id { get; set; }

    public string? Note { get; set; }
}

/// <summary>Uses <see cref="Address"/> once.</summary>
public sealed class Customer
{
    public string Name { get; set; } = string.Empty;

    public Address Home { get; set; } = new();
}

/// <summary>Uses <see cref="Address"/> twice, once nullable.</summary>
public sealed class Person
{
    public string Name { get; set; } = string.Empty;

    public Address Home { get; set; } = new();

    public Address? Work { get; set; }
}

public sealed class Page<T>
{
    public List<T> Items { get; set; } = new();

    public int Total { get; set; }
}

public sealed class Report
{
    public Page<Order>? Current { get; set; }

    public Page<Order>? Archived { get; set; }
}
