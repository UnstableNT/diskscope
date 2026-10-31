using DiskScope.Core.Benchmarks;
using DiskScope.Core.Destructive;
using DiskScope.Core.Devices;

namespace DiskScope.App.Services;

/// <summary>Guard, confirmation dialog and write operations bundled for the test tabs.</summary>
public sealed class WriteSupport(WriteGuard guard, IWriteConfirmation confirmation, DestructiveOperations operations)
{
    public DestructiveOperations Operations { get; } = operations;

    public DestructivePlan Plan(DriveInfo drive, DriveRange? range, bool dismount) => guard.Plan(drive.Id, range, dismount);

    public Task ShowPlanAsync(DestructivePlan plan, string operation) => confirmation.ShowPlanAsync(plan, operation);

    /// <summary>Dry run, dialog with typed model name, then authorization. Null if the user cancelled.</summary>
    public async Task<WriteAuthorization?> AuthorizeAsync(DriveInfo drive, DriveRange? range, bool dismount, string operation)
    {
        var plan = guard.Plan(drive.Id, range, dismount);
        var typed = await confirmation.ConfirmAsync(plan, operation).ConfigureAwait(true);
        return typed is null ? null : guard.Authorize(drive.Id, range, dismount, typed, ConfirmationKind.Model);
    }
}
