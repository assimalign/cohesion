using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Identifies a schema compilation validation failure.</summary>
public enum SqlSchemaValidationErrorCode : byte
{
    /// <summary>A declaration name is duplicated.</summary>
    DuplicateDeclaration = 0,

    /// <summary>A declaration is missing required information.</summary>
    IncompleteDeclaration,

    /// <summary>A declaration refers to an object that does not exist.</summary>
    UnknownReference,

    /// <summary>A CLR value cannot be represented by the selected database model.</summary>
    UnsupportedType,

    /// <summary>An expression cannot be represented in the stable schema AST.</summary>
    UnsupportedExpression,

    /// <summary>A compiled schema document is invalid or uses an unsupported format.</summary>
    InvalidDocument,

    /// <summary>The declaration is not valid for the selected engine model.</summary>
    ModelMismatch,
}
