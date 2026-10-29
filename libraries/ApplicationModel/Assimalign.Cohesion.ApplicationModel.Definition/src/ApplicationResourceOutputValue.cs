namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The value of one resource output, as a parent gateway hands it to a child gateway that joins it.
/// </summary>
/// <remarks>
/// A child's settings may use outputs of its parent's resources. When the child joins, the parent
/// answers with the value of each one it has recorded, and the child fills those settings in from
/// them.
/// </remarks>
/// <param name="Output">The output, of one of the parent gateway's resources.</param>
/// <param name="Value">The value the resource's infrastructure provider reported.</param>
public sealed record ApplicationResourceOutputValue(ApplicationResourceOutput Output, string Value);
