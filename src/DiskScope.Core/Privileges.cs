using System.Security.Principal;

namespace DiskScope.Core;

public static class Privileges
{
    /// <summary>True if the process runs elevated; raw drive access requires this (SPEC 1).</summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
