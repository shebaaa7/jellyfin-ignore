using System;
using System.IO;

namespace Jellyfin.Plugin.Ignore;

/// <summary>
/// Normalizes paths and performs directory-boundary containment checks.
/// </summary>
internal static class LibraryPathResolver
{
    private static readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Returns whether a candidate is the root itself or a descendant of it.
    /// </summary>
    /// <param name="rootPath">The normalized root path.</param>
    /// <param name="candidatePath">The normalized candidate path.</param>
    /// <returns><see langword="true"/> when the candidate belongs to the root.</returns>
    internal static bool ContainsPath(string rootPath, string candidatePath)
    {
        if (string.Equals(rootPath, candidatePath, _pathComparison))
        {
            return true;
        }

        return candidatePath.Length > rootPath.Length
            && candidatePath.StartsWith(rootPath, _pathComparison)
            && (Path.EndsInDirectorySeparator(rootPath)
                || IsDirectorySeparator(candidatePath[rootPath.Length]));
    }

    /// <summary>
    /// Returns an absolute path without a trailing directory separator.
    /// </summary>
    /// <param name="path">The path to normalize.</param>
    /// <returns>The normalized absolute path.</returns>
    internal static string NormalizePath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsDirectorySeparator(char character)
    {
        return character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar;
    }
}
