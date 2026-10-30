namespace Byoa.Wallet.Tests;

/// <summary>A test of Windows-only code (user32, the registry, Windows paths): it is skipped, with the reason, elsewhere.</summary>
sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

/// <summary>A theory of Windows-only code; every case is skipped, with the reason, elsewhere.</summary>
sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

/// <summary>A test of macOS-only code (the Keychain, the app bundle): it is skipped, with the reason, elsewhere.</summary>
sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "macOS only";
    }
}

/// <summary>A test of Linux-only code (the Secret Service, /proc, the desktop entry): it is skipped, with the reason, elsewhere.</summary>
sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only";
    }
}

/// <summary>A test of what macOS and Linux share (Unix sockets, file modes): it is skipped, with the reason, on Windows.</summary>
sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix only (Windows has no Unix sockets or file modes)";
    }
}