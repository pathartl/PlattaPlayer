using System;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Platform.Windows;

namespace PlattaPlayer.App;

/// <summary>
/// Resolves platform-specific integrations for the current OS. The Windows SMTC implementation
/// is wired in here; other platforms fall back to no-ops.
/// </summary>
internal static class PlatformIntegration
{
    public static ISystemMediaControls CreateSystemMediaControls()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            return new WindowsSystemMediaControls();

        return new NullSystemMediaControls();
    }
}
