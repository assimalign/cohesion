namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// The output structures of JSON Schema 2020-12 Core §12.4 that an evaluation can produce.
/// </summary>
public enum JsonSchemaOutputFormat
{
    /// <summary>
    /// Only the overall result. Evaluation stops as soon as the outcome is known, so this is the
    /// fastest format.
    /// </summary>
    Flag,

    /// <summary>
    /// A flat list of output units: the errors of an invalid instance, or the annotations of a valid
    /// one.
    /// </summary>
    Basic,

    /// <summary>
    /// A hierarchy that follows the schema, with units that carry no error or annotation removed and
    /// single-child chains collapsed.
    /// </summary>
    Detailed,

    /// <summary>
    /// The full hierarchy: one unit for every schema and keyword that took part in the evaluation.
    /// </summary>
    Verbose,
}
