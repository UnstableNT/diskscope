using DiskScope.Core;
using DiskScope.Core.Devices;
using Microsoft.Extensions.Logging;

namespace DiskScope.Cli;

/// <summary>Sets up logging and maps exceptions to the exit codes from SPEC 5.</summary>
internal static partial class CommandRunner
{
    public static int Run(bool verbose, Func<ILoggerFactory, int> command) =>
        RunAsync(verbose, logging => Task.FromResult(command(logging))).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(bool verbose, Func<ILoggerFactory, Task<int>> command)
    {
        using var logging = Logging.Create(verbose);
        var logger = logging.CreateLogger("DiskScope.Cli");
        try
        {
            return await command(logging).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DriveNotFoundException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Error;
        }
        catch (DiskScopeException ex) when (ex.IsAccessDenied)
        {
            LogFailure(logger, ex);
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Für diesen Befehl sind Administratorrechte erforderlich.");
            return ExitCodes.MissingRights;
        }
        catch (DiskScopeException ex)
        {
            LogFailure(logger, ex);
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Error;
        }
        catch (DiskScope.Core.Destructive.WriteRefusedException ex)
        {
            foreach (var reason in ex.Reasons)
            {
                Console.Error.WriteLine(reason);
            }

            return Privileges.IsElevated ? ExitCodes.Error : ExitCodes.MissingRights;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Abgebrochen.");
            return ExitCodes.Error;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Befehl fehlgeschlagen")]
    private static partial void LogFailure(ILogger logger, Exception exception);
}
