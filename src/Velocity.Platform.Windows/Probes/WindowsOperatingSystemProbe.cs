using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Velocity.Abstractions.Hardware;

namespace Velocity.Platform.Windows.Probes;

/// <summary>Reads the Windows product name, build and revision.</summary>
/// <remarks>
/// The build number comes from <see cref="Environment.OSVersion"/>, which is accurate on .NET 5 and
/// later. The update build revision and the feature update label are only in the registry, and both
/// matter: compatibility of several settings changes within a single build number.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsOperatingSystemProbe : IOperatingSystemProbe
{
    private const string CurrentVersionPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <inheritdoc />
    public string ProbeName => "operating-system";

    /// <inheritdoc />
    public Task<OperatingSystemInfo> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Version version = Environment.OSVersion.Version;
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(CurrentVersionPath);

        string productName = key?.GetValue("ProductName") as string ?? "Windows";
        string? displayVersion = key?.GetValue("DisplayVersion") as string;
        int revision = key?.GetValue("UBR") is int ubr ? ubr : 0;

        // Windows 11 keeps reporting "Windows 10 ..." in ProductName; the build number is the
        // reliable discriminator and is what compatibility checks use.
        if (version.Build >= 22000 && productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }

        return Task.FromResult(new OperatingSystemInfo
        {
            ProductName = productName,
            DisplayVersion = displayVersion,
            MajorVersion = version.Major,
            MinorVersion = version.Minor,
            BuildNumber = version.Build,
            UpdateBuildRevision = revision,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        });
    }
}
