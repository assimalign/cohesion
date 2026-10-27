namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Specifies identity reuse within one complete matched path.</summary>
public enum GqlPathMode : byte
{
    /// <summary>Forbids repeated relationship identities; the default when no mode is written.</summary>
    Trail,
    /// <summary>Allows repeated node and relationship identities in a bounded path.</summary>
    Walk,
    /// <summary>Forbids repeated nodes except one repetition joining the first and last node.</summary>
    Simple,
    /// <summary>Forbids repeated node identities.</summary>
    Acyclic,
}
