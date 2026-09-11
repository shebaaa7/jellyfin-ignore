using System;
using System.IO;

namespace Jellyfin.Plugin.Ignore;

/// <summary>
/// Chooses where a library alias junction should live.
/// </summary>
internal static class LibraryAliasRoot
{
    private const string AliasFolderName = ".ja";

    /// <summary>
    /// Selects a short-path alias root on the same volume as the media location. A short root
    /// keeps deeply nested media well clear of the Windows 260-character path limit, which native
    /// image decoders (unlike managed file APIs) can silently fail against even though .NET itself
    /// tolerates the longer path.
    /// </summary>
    /// <param name="originalPath">The physical media location the alias will point to.</param>
    /// <param name="fallbackRoot">
    /// Used on any platform other than Windows, and when the media path has no drive-rooted volume
    /// (e.g. a UNC share). Linux has no equivalent path-length limit (typical filesystems allow
    /// paths in the thousands of characters), and placing a directory directly at "/" generally
    /// requires root, which the Jellyfin service user does not have — so there is neither a need
    /// nor a safe way to mirror the Windows drive-root placement there.
    /// </param>
    /// <returns>The directory that should contain the alias.</returns>
    internal static string SelectRoot(string originalPath, string fallbackRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return fallbackRoot;
        }

        var normalized = LibraryPathResolver.NormalizePath(originalPath);
        var driveRoot = Path.GetPathRoot(normalized);
        return string.IsNullOrEmpty(driveRoot)
            ? fallbackRoot
            : Path.Combine(driveRoot, AliasFolderName);
    }
}
