// ============================================================================
// ITurzxPatch v1
// ============================================================================
// Contract between a "Host Loader" (TurzxPatcher.exe) and external patch
// modules (DLLs) that want to apply additional runtime reflection patches
// to the loaded TURZX.exe assembly.
//
// IMPORTANT - COLLISION AVOIDANCE BETWEEN REPOS:
// This file MUST be byte-identical in BOTH repositories:
//   - TurzxPatcher\src\Plugins\ITurzxPatch.cs
//   - TurzxSensorBridge\shared\ITurzxPatch.cs   (this file)
// Reason: .NET type identity is based on Assembly + Namespace + TypeName.
// If the file is modified in one repo WITHOUT updating the other copy,
// patch modules from TurzxSensorBridge can no longer be recognized as
// ITurzxPatch by TurzxPatcher (InvalidCastException or
// silently "0 patches found").
//
// When changing this interface:
//   1. Increase version comment below (v1 -> v2)
//   2. Make changes only additively (new optional methods with default
//      implementation via extension methods, do NOT change existing
//      signatures) OR deliberate breaking change with version check
//      in the loader
//   3. Synchronously update the copy in BOTH repos
//   4. Reference the new version in both READMEs
//
// Namespace is deliberately NOT "TurzxPatcher.*" or "TurzxSensorBridge.*"
// but neutral, so both repos can use exactly the same namespace
// without referencing each other.
// ============================================================================

using System;
using System.Reflection;

namespace TurzxShared.Plugins
{
    /// <summary>
    /// Contract for an external patch module that is loaded by the Host Loader
    /// (TurzxPatcher) from the "patches\" subdirectory.
    /// Version: 1
    /// </summary>
    public interface ITurzxPatch
    {
        /// <summary>
        /// Unique human-readable name of the patch for console/log output.
        /// Example: "Aquacomputer LibreHardwareMonitor Sensor Bridge Patch"
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Minimum supported ITurzxPatch interface version that this module was
        /// developed against. The loader compares this with its own
        /// HostInterfaceVersion and WARNS (does not abort) on mismatch,
        /// to allow backward compatibility with future extensions.
        /// </summary>
        int InterfaceVersion { get; }

        /// <summary>
        /// Called exactly once, AFTER the host has:
        ///  - Loaded TURZX.exe via Assembly.LoadFrom
        ///  - Registered the AssemblyResolve handler for UsbMonitorL
        ///  - Applied the AppDomainManager/GetEntryAssembly() fix
        ///  - Set Environment.CurrentDirectory to the TURZX directory
        /// and BEFORE the TURZX entry point is called.
        ///
        /// This method MUST NOT throw any Exception outward. All errors
        /// must be caught internally and logged via Console.WriteLine,
        /// to prevent a faulty patch module from crashing the entire host.
        /// </summary>
        /// <param name="turzxAssembly">The loaded TURZX.exe assembly.</param>
        /// <param name="turzxDir">Absolute path to the TURZX installation directory.</param>
        void Apply(Assembly turzxAssembly, string turzxDir);
    }
}
