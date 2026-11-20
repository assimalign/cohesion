namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The built-in meaning of a keyword. The compiler switches on it to build the keyword's evaluator.
/// </summary>
internal enum KeywordKind
{
    Custom,

    // Core
    Schema,
    Id,
    Anchor,
    DynamicAnchor,
    RecursiveAnchor,
    Vocabulary,
    Comment,
    Defs,
    Ref,
    DynamicRef,
    RecursiveRef,

    // Applicator
    AllOf,
    AnyOf,
    OneOf,
    Not,
    If,
    Then,
    Else,
    Properties,
    PatternProperties,
    AdditionalProperties,
    PropertyNames,
    DependentSchemas,
    Dependencies,
    PrefixItems,
    Items,
    ItemsLegacy,
    AdditionalItems,
    Contains,
    ContainsLegacy,
    UnevaluatedProperties,
    UnevaluatedItems,

    // Validation
    Type,
    Enum,
    Const,
    MultipleOf,
    Maximum,
    ExclusiveMaximum,
    Minimum,
    ExclusiveMinimum,
    MaximumDraft04,
    MinimumDraft04,
    ExclusiveMaximumDraft04,
    ExclusiveMinimumDraft04,
    MaxLength,
    MinLength,
    Pattern,
    MaxItems,
    MinItems,
    UniqueItems,
    MaxContains,
    MinContains,
    MaxProperties,
    MinProperties,
    Required,
    DependentRequired,

    // Meta-data
    Annotation,

    // Format
    Format,
    FormatAnnotation,
    FormatAssertion,

    // Content
    ContentEncoding,
    ContentMediaType,
    ContentSchema,
}
