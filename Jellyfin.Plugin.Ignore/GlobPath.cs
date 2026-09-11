using System;
using System.Linq;

namespace Jellyfin.Plugin.Ignore;

/// <summary>
/// Shared parsing and normalization for configured glob patterns.
/// </summary>
internal static class GlobPath
{
    /// <summary>
    /// Parses newline-separated patterns.
    /// </summary>
    /// <param name="patterns">The configured patterns.</param>
    /// <returns>The compiled matchers.</returns>
    internal static IgnorePatternMatcher[] ParsePatterns(string patterns)
    {
        return patterns
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(pattern => new IgnorePatternMatcher(Normalize(pattern)))
            .ToArray();
    }

    /// <summary>
    /// Converts a path to the separator format accepted by the glob engine.
    /// </summary>
    /// <param name="path">The path to normalize.</param>
    /// <returns>The normalized path.</returns>
    internal static string Normalize(string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        while (normalizedPath.StartsWith("./", StringComparison.Ordinal))
        {
            normalizedPath = normalizedPath[2..];
        }

        return normalizedPath.TrimStart('/');
    }
}
