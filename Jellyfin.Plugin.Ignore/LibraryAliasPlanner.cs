using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Ignore.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Ignore;

/// <summary>
/// Finds nested dedicated-library locations that require a separate path identity.
/// </summary>
internal static class LibraryAliasPlanner
{
    /// <summary>
    /// Creates migration plans for nested roots matched by an ancestor library's patterns.
    /// </summary>
    /// <param name="libraries">The current Jellyfin virtual folders.</param>
    /// <param name="configuration">The plugin configuration.</param>
    /// <param name="aliasRootSelector">
    /// Chooses the plugin-owned alias directory for a given original media path. Selecting a root
    /// per original path (rather than one fixed root) lets the alias live on the same volume as its
    /// target, close to the volume's drive letter, which keeps the resulting path well under the
    /// Windows 260-character limit even for deeply nested media.
    /// </param>
    /// <returns>The required migrations.</returns>
    internal static AliasMigrationPlan[] CreatePlans(
        IReadOnlyCollection<VirtualFolderInfo> libraries,
        PluginConfiguration configuration,
        Func<string, string> aliasRootSelector)
    {
        var plans = new List<AliasMigrationPlan>();

        foreach (var childLibrary in libraries)
        {
            foreach (var childLocation in childLibrary.Locations.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                var managedAlias = FindManagedAlias(configuration.ManagedAliases, childLibrary, childLocation);
                if (managedAlias is not null
                    && PathsEqual(childLocation, managedAlias.AliasPath))
                {
                    var canonicalPath = CreateAliasPath(
                        aliasRootSelector(managedAlias.OriginalPath),
                        childLibrary,
                        LibraryPathResolver.NormalizePath(managedAlias.OriginalPath));
                    if (PathsEqual(canonicalPath, managedAlias.AliasPath))
                    {
                        continue;
                    }

                    plans.Add(new AliasMigrationPlan(
                        childLibrary,
                        LibraryPathResolver.NormalizePath(managedAlias.OriginalPath),
                        canonicalPath,
                        managedAlias.AliasPath));
                    continue;
                }

                var originalPath = managedAlias?.OriginalPath ?? childLocation;
                var normalizedOriginal = LibraryPathResolver.NormalizePath(originalPath);
                if (!IsMatchedByAncestor(libraries, configuration, childLibrary, normalizedOriginal))
                {
                    continue;
                }

                var aliasPath = managedAlias?.AliasPath
                    ?? CreateAliasPath(aliasRootSelector(normalizedOriginal), childLibrary, normalizedOriginal);
                plans.Add(new AliasMigrationPlan(childLibrary, normalizedOriginal, aliasPath));
            }
        }

        return plans
            .DistinctBy(plan => (LibraryIdentity(plan.Library), plan.OriginalPath), PathPlanComparer.Instance)
            .ToArray();
    }

    internal static bool IsSameLibrary(VirtualFolderInfo library, ManagedLibraryAlias alias)
    {
        var libraryId = library.ItemId ?? string.Empty;
        return !string.IsNullOrWhiteSpace(alias.LibraryId) && !string.IsNullOrWhiteSpace(libraryId)
            ? string.Equals(alias.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(alias.LibraryName, library.Name, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            LibraryPathResolver.NormalizePath(left),
            LibraryPathResolver.NormalizePath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsMatchedByAncestor(
        IEnumerable<VirtualFolderInfo> libraries,
        PluginConfiguration configuration,
        VirtualFolderInfo childLibrary,
        string childPath)
    {
        foreach (var parentLibrary in libraries.Where(library => !IsSameLibrary(library, childLibrary)))
        {
            var parentConfiguration = FindLibraryConfiguration(configuration, parentLibrary);
            if (parentConfiguration is null)
            {
                continue;
            }

            var patterns = GlobPath.ParsePatterns(parentConfiguration.Patterns);
            if (patterns.Length == 0)
            {
                continue;
            }

            foreach (var parentLocation in parentLibrary.Locations.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                var logicalParentPath = FindManagedAlias(configuration.ManagedAliases, parentLibrary, parentLocation)?.OriginalPath
                    ?? parentLocation;
                var normalizedParent = LibraryPathResolver.NormalizePath(logicalParentPath);
                if (PathsEqual(normalizedParent, childPath)
                    || !LibraryPathResolver.ContainsPath(normalizedParent, childPath))
                {
                    continue;
                }

                var relativePath = GlobPath.Normalize(Path.GetRelativePath(normalizedParent, childPath));
                var fullPath = GlobPath.Normalize(childPath);
                if (patterns.Any(pattern => pattern.IsMatch(relativePath, fullPath)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSameLibrary(VirtualFolderInfo left, VirtualFolderInfo right)
    {
        var leftId = left.ItemId ?? string.Empty;
        var rightId = right.ItemId ?? string.Empty;
        return !string.IsNullOrWhiteSpace(leftId) && !string.IsNullOrWhiteSpace(rightId)
            ? string.Equals(leftId, rightId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static LibraryIgnoreConfiguration? FindLibraryConfiguration(
        PluginConfiguration configuration,
        VirtualFolderInfo library)
    {
        var libraryId = library.ItemId ?? string.Empty;
        var idMatch = configuration.LibraryConfigurations.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate.LibraryId)
            && string.Equals(candidate.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase));
        return idMatch ?? configuration.LibraryConfigurations.FirstOrDefault(candidate =>
            string.IsNullOrWhiteSpace(candidate.LibraryId)
            && string.Equals(candidate.LibraryName, library.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static ManagedLibraryAlias? FindManagedAlias(
        IEnumerable<ManagedLibraryAlias> aliases,
        VirtualFolderInfo library,
        string path)
    {
        return aliases.FirstOrDefault(alias =>
            IsSameLibrary(library, alias)
            && (PathsEqual(path, alias.OriginalPath) || PathsEqual(path, alias.AliasPath)));
    }

    private static string CreateAliasPath(
        string aliasRoot,
        VirtualFolderInfo library,
        string originalPath)
    {
        // Deliberately short and not name-derived: the alias directory name only needs to be
        // unique, not readable. Every extra character here erodes the path budget available to the
        // real media path nested beneath it before the alias hits Windows' 260-character limit.
        // The human-readable library name and original path are still recorded in
        // PluginConfiguration.ManagedAliases for the configuration page to display.
        var identity = LibraryIdentity(library) + "\n" + originalPath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..8];
        return Path.Combine(aliasRoot, hash);
    }

    private static string LibraryIdentity(VirtualFolderInfo library)
    {
        return string.IsNullOrWhiteSpace(library.ItemId) ? library.Name : library.ItemId;
    }

    private sealed class PathPlanComparer : IEqualityComparer<(string Library, string Path)>
    {
        internal static readonly PathPlanComparer Instance = new PathPlanComparer();

        public bool Equals((string Library, string Path) left, (string Library, string Path) right)
        {
            return string.Equals(left.Library, right.Library, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    left.Path,
                    right.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        public int GetHashCode((string Library, string Path) value)
        {
            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Library),
                pathComparer.GetHashCode(value.Path));
        }
    }
}

/// <summary>
/// One dedicated library location that must be changed to an alias path.
/// </summary>
/// <param name="Library">The dedicated library.</param>
/// <param name="OriginalPath">The existing physical root.</param>
/// <param name="AliasPath">The stable plugin-managed path.</param>
/// <param name="PreviousAliasPath">
/// Set when this plan relocates an already-migrated alias to a new path (for example, after the
/// alias-placement scheme changes) rather than migrating directly from <paramref name="OriginalPath"/>.
/// Null for a fresh migration.
/// </param>
internal sealed record AliasMigrationPlan(
    VirtualFolderInfo Library,
    string OriginalPath,
    string AliasPath,
    string? PreviousAliasPath = null);
