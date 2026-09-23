using System;
using System.Globalization;
using System.Linq;
using Velocity.Abstractions.Hardware;
using Velocity.Abstractions.Privileges;
using Velocity.Abstractions.Tweaks;

namespace Velocity.Core.Tweaks;

/// <summary>
/// Evaluates the declarative part of a tweak's compatibility, before any tweak code runs.
/// </summary>
/// <remarks>
/// Keeping this separate from <see cref="ITweak.CheckCompatibilityAsync"/> means the catalogue can
/// be filtered for the UI without instantiating or executing anything, and means every tweak gets
/// the same platform and privilege gating rather than each one re-implementing it slightly
/// differently.
/// </remarks>
public static class CompatibilityEvaluator
{
    /// <summary>Evaluates the descriptor's declared requirements against a machine.</summary>
    /// <param name="descriptor">Tweak metadata.</param>
    /// <param name="profile">The machine.</param>
    /// <param name="layout">Interpreted processor layout for the machine.</param>
    /// <param name="privileges">Privileges available to the current process.</param>
    /// <returns>
    /// <see cref="CompatibilityStatus.Supported"/> when the declared requirements are met. A tweak
    /// may still declare itself incompatible for reasons only it can determine.
    /// </returns>
    public static CompatibilityResult Evaluate(
        TweakDescriptor descriptor,
        SystemProfile profile,
        CpuLayout layout,
        IPrivilegeContext privileges)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(privileges);

        int build = profile.OperatingSystem.BuildNumber;

        if (build < descriptor.MinimumWindowsBuild)
        {
            return CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedOperatingSystem,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Requires Windows build {descriptor.MinimumWindowsBuild} or later; this machine reports build {build}."));
        }

        if (descriptor.MaximumWindowsBuild is int maximum && build > maximum)
        {
            return CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedOperatingSystem,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"This setting is no longer honoured after Windows build {maximum}; this machine reports build {build}."));
        }

        if (descriptor.RequiresElevation && !privileges.CanElevate)
        {
            return CompatibilityResult.Unsupported(
                CompatibilityStatus.ElevationUnavailable,
                "Requires the privileged helper service, which is not available.");
        }

        HardwareRequirements requirements = descriptor.Hardware;

        if (requirements.CpuVendors.Count > 0 && !requirements.CpuVendors.Contains(profile.Cpu.Vendor))
        {
            return CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedHardware,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Applies to {string.Join(" or ", requirements.CpuVendors)} processors; this machine has {profile.Cpu.Vendor}."));
        }

        if (requirements.GpuVendors.Count > 0 &&
            !profile.Gpus.Any(gpu => requirements.GpuVendors.Contains(gpu.Vendor)))
        {
            return CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedHardware,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Requires a {string.Join(" or ", requirements.GpuVendors)} display adapter."));
        }

        if (profile.Cpu.PhysicalCoreCount < requirements.MinimumPhysicalCores)
        {
            return CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedHardware,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Requires at least {requirements.MinimumPhysicalCores} physical cores; this machine has {profile.Cpu.PhysicalCoreCount}."));
        }

        if (requirements.RequiresHybridCpu is bool hybridRequired && hybridRequired != profile.Cpu.IsHybrid)
        {
            return CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedHardware,
                hybridRequired
                    ? "Applies only to hybrid processors with distinct performance and efficiency cores."
                    : "Does not apply to hybrid processors.");
        }

        if (requirements.RequiresMultipleCoreComplexes is bool multipleRequired)
        {
            bool hasMultiple = layout.Complexes.Count(complex => complex.SharedCacheLevel >= 3) > 1;
            if (multipleRequired != hasMultiple)
            {
                return CompatibilityResult.Unsupported(
                    CompatibilityStatus.UnsupportedHardware,
                    multipleRequired
                        ? "Applies only to processors with more than one last level cache complex."
                        : "Does not apply to processors with more than one last level cache complex.");
            }
        }

        if (requirements.ExcludedMachineKinds.Contains(profile.MachineKind))
        {
            return CompatibilityResult.Unsupported(
                CompatibilityStatus.UnsupportedHardware,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Not offered on {profile.MachineKind} systems."));
        }

        return CompatibilityResult.Supported();
    }
}
