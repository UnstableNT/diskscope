using CommunityToolkit.Mvvm.ComponentModel;
using DiskScope.Core.Devices;

namespace DiskScope.App.Services;

/// <summary>
/// The drive chosen in the header and the global "a test is running" lock (SPEC 4.1:
/// while a test runs, switching drives and starting other tests is blocked).
/// </summary>
public sealed partial class DriveSelection : ObservableObject
{
    private int _running;

    [ObservableProperty]
    public partial DriveInfo? Current { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    /// <summary>Marks a test as running until the returned scope is disposed.</summary>
    public IDisposable BeginTest()
    {
        _running++;
        IsBusy = true;
        return new Scope(this);
    }

    private sealed class Scope(DriveSelection owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner._running--;
            owner.IsBusy = owner._running > 0;
        }
    }
}
