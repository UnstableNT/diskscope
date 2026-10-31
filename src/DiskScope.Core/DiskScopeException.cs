using System.ComponentModel;

namespace DiskScope.Core;

/// <summary>A Win32 call failed. Carries the function name and the Win32 error code (SPEC 7).</summary>
public sealed class DiskScopeException : Exception
{
    public const int ErrorAccessDenied = 5;

    public DiskScopeException()
    {
        Function = string.Empty;
    }

    public DiskScopeException(string message)
        : base(message)
    {
        Function = string.Empty;
    }

    public DiskScopeException(string message, Exception innerException)
        : base(message, innerException)
    {
        Function = string.Empty;
    }

    public DiskScopeException(string function, int win32Error, string? target = null)
        : base(FormatMessage(function, win32Error, target))
    {
        Function = function;
        Win32Error = win32Error;
    }

    public string Function { get; }

    public int Win32Error { get; }

    public bool IsAccessDenied => Win32Error == ErrorAccessDenied;

    private static string FormatMessage(string function, int win32Error, string? target)
    {
        var text = new Win32Exception(win32Error).Message;
        return target is null
            ? $"{function} fehlgeschlagen (Win32 {win32Error}): {text}"
            : $"{function} für {target} fehlgeschlagen (Win32 {win32Error}): {text}";
    }
}
