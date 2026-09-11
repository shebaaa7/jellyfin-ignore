using System;
using DotNet.Globbing;

namespace Jellyfin.Plugin.Ignore;

/// <summary>
/// Matches a configured glob against paths relative to a library root.
/// </summary>
internal sealed class IgnorePatternMatcher
{
    private static readonly GlobOptions _globOptions = new GlobOptions
    {
        Evaluation =
            {
                CaseInsensitive = true
            }
    };

    private readonly Glob _glob;
    private readonly string? _subtreeRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="IgnorePatternMatcher"/> class.
    /// </summary>
    /// <param name="pattern">A normalized glob pattern.</param>
    internal IgnorePatternMatcher(string pattern)
    {
        _glob = Glob.Parse(pattern, _globOptions);
        _subtreeRoot = pattern.EndsWith("/**", StringComparison.Ordinal)
            ? pattern[..^3].TrimEnd('/')
            : null;
    }

    /// <summary>
    /// Tests a relative path and its full path against this pattern.
    /// </summary>
    /// <param name="relativePath">The slash-normalized path relative to the owning root.</param>
    /// <param name="fullPath">The slash-normalized full path.</param>
    /// <returns><see langword="true"/> when the path should be ignored.</returns>
    internal bool IsMatch(string relativePath, string fullPath)
    {
        if (_glob.IsMatch(relativePath)
            || _glob.IsMatch('/' + relativePath)
            || _glob.IsMatch(fullPath))
        {
            return true;
        }

        return _subtreeRoot is not null
            && (string.Equals(relativePath, _subtreeRoot, StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith(_subtreeRoot + '/', StringComparison.OrdinalIgnoreCase));
    }
}
