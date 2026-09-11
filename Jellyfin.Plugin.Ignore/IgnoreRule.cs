using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Ignore.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.Ignore;

/// <summary>
/// Applies ignore rules from an immutable cache prepared outside Jellyfin's resolver callback.
/// </summary>
public class IgnoreRule : IResolverIgnoreRule
{
    private static CompiledLibraryRoot[] _libraryRoots = Array.Empty<CompiledLibraryRoot>();

    /// <summary>
    /// Clears all active ignore rules while library aliases are being reconciled.
    /// </summary>
    internal static void ClearSnapshot()
    {
        Volatile.Write(ref _libraryRoots, Array.Empty<CompiledLibraryRoot>());
    }

    /// <summary>
    /// Replaces the active ignore-rule snapshot.
    /// </summary>
    /// <param name="libraries">The current Jellyfin virtual folders.</param>
    /// <param name="configuration">The plugin configuration.</param>
    internal static void UpdateSnapshot(
        IEnumerable<VirtualFolderInfo> libraries,
        PluginConfiguration configuration)
    {
        var compiledConfigurations = configuration.LibraryConfigurations
            .Select(library => new CompiledLibraryConfiguration(
                library.LibraryId,
                library.LibraryName,
                GlobPath.ParsePatterns(library.Patterns)))
            .ToArray();

        var roots = new List<CompiledLibraryRoot>();
        foreach (var library in libraries)
        {
            var configuredLibrary = FindConfiguration(compiledConfigurations, library);
            if (configuredLibrary is null || configuredLibrary.Patterns.Length == 0)
            {
                continue;
            }

            foreach (var location in library.Locations.Where(location => !string.IsNullOrWhiteSpace(location)))
            {
                roots.Add(new CompiledLibraryRoot(
                    LibraryPathResolver.NormalizePath(location),
                    configuredLibrary.Patterns));
            }
        }

        Volatile.Write(
            ref _libraryRoots,
            roots.OrderByDescending(root => root.RootPath.Length).ToArray());
    }

    /// <inheritdoc />
    public bool ShouldIgnore(FileSystemMetadata fileInfo, BaseItem? parent)
    {
        var candidatePath = GetCandidatePath(fileInfo, parent);
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            var normalizedCandidate = LibraryPathResolver.NormalizePath(candidatePath);
            var root = Volatile.Read(ref _libraryRoots)
                .FirstOrDefault(candidate =>
                    LibraryPathResolver.ContainsPath(candidate.RootPath, normalizedCandidate));
            if (root is null)
            {
                return false;
            }

            var relativePath = GlobPath.Normalize(Path.GetRelativePath(root.RootPath, normalizedCandidate));
            var fullPath = GlobPath.Normalize(normalizedCandidate);
            return root.Patterns.Any(pattern => pattern.IsMatch(relativePath, fullPath));
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static string GetCandidatePath(FileSystemMetadata fileInfo, BaseItem? parent)
    {
        if (!string.IsNullOrWhiteSpace(fileInfo.FullName))
        {
            return fileInfo.FullName;
        }

        return parent is null || string.IsNullOrWhiteSpace(parent.Path)
            ? fileInfo.Name
            : Path.Join(parent.Path, fileInfo.Name);
    }

    private static CompiledLibraryConfiguration? FindConfiguration(
        CompiledLibraryConfiguration[] configurations,
        VirtualFolderInfo library)
    {
        var libraryId = library.ItemId ?? string.Empty;
        var idMatch = configurations.FirstOrDefault(configuration =>
            !string.IsNullOrWhiteSpace(configuration.LibraryId)
            && string.Equals(configuration.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase));

        return idMatch ?? configurations.FirstOrDefault(configuration =>
            string.IsNullOrWhiteSpace(configuration.LibraryId)
            && string.Equals(configuration.LibraryName, library.Name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record CompiledLibraryConfiguration(
        string LibraryId,
        string LibraryName,
        IgnorePatternMatcher[] Patterns);

    private sealed record CompiledLibraryRoot(
        string RootPath,
        IgnorePatternMatcher[] Patterns);
}
