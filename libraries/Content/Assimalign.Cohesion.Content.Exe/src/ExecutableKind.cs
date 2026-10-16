namespace Assimalign.Cohesion.Content.Exe;

/// <summary>The role recorded by the executable header.</summary>
public enum ExecutableKind
{
    /// <summary>Unknown or mixed universal-file role.</summary>
    Unknown,
    /// <summary>A program image.</summary>
    Executable,
    /// <summary>A shared library or ELF shared object.</summary>
    Library,
    /// <summary>A relocatable object.</summary>
    Object,
    /// <summary>A core dump.</summary>
    Core,
}
