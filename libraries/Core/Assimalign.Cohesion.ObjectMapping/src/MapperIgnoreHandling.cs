namespace Assimalign.Extensions.Mapping;

/// <summary>
/// Determines the default behavior for handling Default and Null values when mapping.
/// </summary>
public enum MapperIgnoreHandling
{
    /// <summary>
    /// This will ALWAYS allow 'Null' and 'Default' values 
    /// to be mapped from source to target objects.
    /// </summary>
    Never = 0,
    /// <summary>
    /// This will NEVER allow 'Null' values to be mapped from source to target objects. 
    /// (Defaults will be set if the source and target type are ValueType)
    /// </summary>
    Always = 1,
    /// <summary>
    ///  This will NEITHER allow 'Null' or 'Default' values 
    ///  to be mapped from source to target objects. 
    /// </summary>
    WhenMappingDefaults = 2
}
