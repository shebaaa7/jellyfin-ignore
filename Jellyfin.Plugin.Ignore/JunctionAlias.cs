using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Ignore;

/// <summary>
/// Creates and verifies directory aliases: NTFS junctions on Windows, plain symlinks on Linux.
/// </summary>
internal static class JunctionAlias
{
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>
    /// Ensures an alias exists and points to the expected physical target.
    /// </summary>
    /// <param name="aliasPath">The alias path.</param>
    /// <param name="targetPath">The physical target.</param>
    internal static void Ensure(string aliasPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureWindows(aliasPath, targetPath);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            EnsureLinux(aliasPath, targetPath);
            return;
        }

        throw new PlatformNotSupportedException(
            "Automatic nested-library aliases currently require Windows NTFS junctions or Linux symlinks.");
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindows(string aliasPath, string targetPath)
    {
        var (normalizedAlias, normalizedTarget) = ValidatePlacement(aliasPath, targetPath);
        if (normalizedAlias is null)
        {
            // ValidatePlacement already handled the "alias already exists and is correct" case.
            return;
        }

        var parentPath = Path.GetDirectoryName(normalizedAlias)
            ?? throw new InvalidOperationException("The planned library alias has no parent directory.");
        Directory.CreateDirectory(parentPath);
        Directory.CreateDirectory(normalizedAlias);

        try
        {
            CreateJunctionWindows(normalizedAlias, normalizedTarget!);
            Verify(normalizedAlias, normalizedTarget!);
        }
        catch
        {
            var alias = new DirectoryInfo(normalizedAlias);
            if (alias.Exists && string.IsNullOrWhiteSpace(alias.LinkTarget))
            {
                alias.Delete();
            }

            throw;
        }
    }

    [SupportedOSPlatform("linux")]
    private static void EnsureLinux(string aliasPath, string targetPath)
    {
        var (normalizedAlias, normalizedTarget) = ValidatePlacement(aliasPath, targetPath);
        if (normalizedAlias is null)
        {
            return;
        }

        var parentPath = Path.GetDirectoryName(normalizedAlias)
            ?? throw new InvalidOperationException("The planned library alias has no parent directory.");
        Directory.CreateDirectory(parentPath);

        try
        {
            Directory.CreateSymbolicLink(normalizedAlias, normalizedTarget!);
            Verify(normalizedAlias, normalizedTarget!);
        }
        catch
        {
            if (Directory.Exists(normalizedAlias))
            {
                new DirectoryInfo(normalizedAlias).Delete();
            }

            throw;
        }
    }

    /// <summary>
    /// Validates that an alias may be created at the requested path, and reports whether one
    /// already exists there and matches the target (in which case the caller has nothing left to
    /// do). The Windows and Linux creation steps differ (a junction is attached to an existing
    /// empty directory; a symlink is created directly), so only the shared validation lives here.
    /// </summary>
    private static (string? NormalizedAlias, string? NormalizedTarget) ValidatePlacement(
        string aliasPath,
        string targetPath)
    {
        var normalizedAlias = LibraryPathResolver.NormalizePath(aliasPath);
        var normalizedTarget = LibraryPathResolver.NormalizePath(targetPath);

        if (!Directory.Exists(normalizedTarget))
        {
            throw new DirectoryNotFoundException(
                "Cannot create a library alias because the media location does not exist: " + normalizedTarget);
        }

        if (LibraryPathResolver.ContainsPath(normalizedTarget, normalizedAlias)
            || LibraryPathResolver.ContainsPath(normalizedAlias, normalizedTarget))
        {
            throw new InvalidOperationException("A library alias cannot contain, or be contained by, its target.");
        }

        if (Directory.Exists(normalizedAlias))
        {
            Verify(normalizedAlias, normalizedTarget);
            return (null, null);
        }

        if (File.Exists(normalizedAlias))
        {
            throw new IOException("The planned library alias is occupied by a file: " + normalizedAlias);
        }

        return (normalizedAlias, normalizedTarget);
    }

    /// <summary>
    /// Removes an alias directory that a relocation has replaced, but only if it is genuinely a
    /// junction or symlink. Never touches the media it points to, and never removes an ordinary
    /// directory even if one happens to occupy the path.
    /// </summary>
    /// <param name="aliasPath">The alias path to remove.</param>
    internal static void RemoveIfUnused(string aliasPath)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var normalizedAlias = LibraryPathResolver.NormalizePath(aliasPath);
        if (!Directory.Exists(normalizedAlias))
        {
            return;
        }

        var alias = new DirectoryInfo(normalizedAlias);
        if (string.IsNullOrWhiteSpace(alias.LinkTarget))
        {
            return;
        }

        Directory.Delete(normalizedAlias, recursive: false);
    }

    private static void Verify(string aliasPath, string targetPath)
    {
        var alias = new DirectoryInfo(aliasPath);
        if (string.IsNullOrWhiteSpace(alias.LinkTarget))
        {
            throw new IOException(
                "The planned library alias already exists but is not a directory junction: " + aliasPath);
        }

        var resolvedTarget = alias.ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new IOException("The library alias target cannot be resolved: " + aliasPath);
        if (!LibraryAliasPlanner.PathsEqual(resolvedTarget.FullName, targetPath))
        {
            throw new IOException(
                $"The library alias points to '{resolvedTarget.FullName}', not the expected target '{targetPath}'.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateJunctionWindows(string aliasPath, string targetPath)
    {
        var printName = targetPath;
        var substituteName = targetPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\??\\UNC\\" + targetPath.TrimStart('\\')
            : "\\??\\" + targetPath;
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);
        var pathBufferLength = substituteBytes.Length + 2 + printBytes.Length + 2;
        var reparseDataLength = checked((ushort)(8 + pathBufferLength));
        var buffer = new byte[8 + reparseDataLength];

        WriteUInt32(buffer, 0, IoReparseTagMountPoint);
        WriteUInt16(buffer, 4, reparseDataLength);
        WriteUInt16(buffer, 6, 0);
        WriteUInt16(buffer, 8, 0);
        WriteUInt16(buffer, 10, checked((ushort)substituteBytes.Length));
        WriteUInt16(buffer, 12, checked((ushort)(substituteBytes.Length + 2)));
        WriteUInt16(buffer, 14, checked((ushort)printBytes.Length));
        substituteBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, 16 + substituteBytes.Length + 2);

        using var handle = CreateFile(
            aliasPath,
            GenericWrite,
            0,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(errorCode, $"Unable to open the alias directory (Windows error {errorCode}).");
        }

        if (!DeviceIoControl(
                handle,
                FsctlSetReparsePoint,
                buffer,
                buffer.Length,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
        {
            var errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(errorCode, $"Unable to create the directory junction (Windows error {errorCode}).");
        }
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[] inputBuffer,
        int inputBufferSize,
        IntPtr outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
