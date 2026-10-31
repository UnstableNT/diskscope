using System.ComponentModel;
using System.Diagnostics;

namespace DiskScope.App.Services;

/// <summary>Restarts the app elevated ("Als Administrator neu starten", SPEC 6).</summary>
public static class Elevation
{
    private const int ErrorCancelled = 1223;

    /// <returns>False if the user declined the UAC prompt.</returns>
    public static bool TryRestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }
}
