using System;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal static partial class LocalProcessSignal
{
    private const uint ControlBreakEvent = 1;
    private const int StandardOutputHandle = -11;
    private const int SigKill = 9;
    private const int SigTerm = 15;

    public static bool TrySendControlBreak(int processGroupId)
    {
        if (!OperatingSystem.IsWindows() || !OwnsConsole())
        {
            return false;
        }

        return GenerateConsoleCtrlEvent(ControlBreakEvent, checked((uint)processGroupId));
    }

    public static bool TrySendTerminate(int processId, bool processGroup)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        int target = processGroup ? -processId : processId;
        return Kill(target, SigTerm) == 0;
    }

    public static bool TryCreateProcessGroup(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        return SetProcessGroup(processId, processId) == 0;
    }

    public static bool TryForceKillGroup(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        return Kill(-processId, SigKill) == 0;
    }

    private static bool OwnsConsole()
    {
        nint handle = GetStdHandle(StandardOutputHandle);
        return handle != 0 && handle != -1 && GetConsoleMode(handle, out _);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint consoleHandle, out uint mode);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int processId, int signal);

    [LibraryImport("libc", EntryPoint = "setpgid", SetLastError = true)]
    private static partial int SetProcessGroup(int processId, int processGroupId);
}
