using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Ignore.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        LibraryConfigurations = new Collection<LibraryIgnoreConfiguration>();
        ManagedAliases = new Collection<ManagedLibraryAlias>();
    }

    /// <summary>
    /// Gets or sets a value indicating whether nested library locations should be migrated
    /// automatically to plugin-managed NTFS junction aliases.
    /// </summary>
    public bool EnableAutomaticAliases { get; set; }

    /// <summary>
    /// Gets or sets the ignore patterns for each library.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "Jellyfin's plugin configuration serializers require a public setter.")]
    public Collection<LibraryIgnoreConfiguration> LibraryConfigurations { get; set; }

    /// <summary>
    /// Gets or sets the alias migrations owned by this plugin.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "Jellyfin's plugin configuration serializers require a public setter.")]
    public Collection<ManagedLibraryAlias> ManagedAliases { get; set; }

    /// <summary>
    /// Gets or sets the latest alias-management status for the configuration page.
    /// </summary>
    public string AliasManagementStatus { get; set; } = "Alias management has not run yet.";

    /// <summary>
    /// Gets or sets the latest alias-management error for the configuration page.
    /// </summary>
    public string AliasManagementError { get; set; } = string.Empty;
}

/// <summary>
/// Ignore patterns scoped to one Jellyfin virtual library.
/// </summary>
public class LibraryIgnoreConfiguration
{
    /// <summary>
    /// Gets or sets the stable Jellyfin library item identifier.
    /// </summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name, retained as a fallback for older data and diagnostics.
    /// </summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets newline-separated glob patterns relative to the library root.
    /// </summary>
    public string Patterns { get; set; } = string.Empty;
}

/// <summary>
/// A stable mapping from a physical nested library location to a plugin-managed alias.
/// </summary>
public class ManagedLibraryAlias
{
    /// <summary>
    /// Gets or sets the stable Jellyfin library item identifier.
    /// </summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin library display name used for diagnostics and fallback matching.
    /// </summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original physical media location.
    /// </summary>
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the stable plugin-managed junction path configured in Jellyfin.
    /// </summary>
    public string AliasPath { get; set; } = string.Empty;
}
