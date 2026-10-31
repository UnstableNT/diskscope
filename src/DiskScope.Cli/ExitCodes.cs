namespace DiskScope.Cli;

/// <summary>Process exit codes (SPEC 5).</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int MissingRights = 2;
    public const int HealthWarning = 3;
}
