using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ignore.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ignore;

/// <summary>
/// Reconciles nested library roots with plugin-managed aliases outside the resolver callback.
/// </summary>
public sealed class LibraryAliasManager : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<LibraryAliasManager> _logger;
    private readonly string _fallbackAliasRoot;
    private readonly SemaphoreSlim _reconcileLock = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private bool _disposed;

    private static LibraryAliasManager? _instance;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryAliasManager"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="libraryMonitor">The Jellyfin filesystem monitor.</param>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="logger">The logger.</param>
    public LibraryAliasManager(
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IApplicationPaths applicationPaths,
        ILogger<LibraryAliasManager> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
        _fallbackAliasRoot = Path.Combine(applicationPaths.PluginConfigurationsPath, "ignore-per-library-aliases");
    }

    /// <summary>
    /// Requests reconciliation after the plugin configuration changes.
    /// </summary>
    internal static void RequestReconcile()
    {
        _instance?.BeginReconcile();
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _instance = this;
        BeginReconcile();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        await _reconcileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _reconcileLock.Release();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
        _reconcileLock.Dispose();
    }

    private void BeginReconcile()
    {
        _ = Task.Run(
            () => ReconcileSafelyAsync(_shutdown.Token),
            CancellationToken.None);
    }

    private async Task ReconcileSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _reconcileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await ReconcileAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            IgnoreRule.ClearSnapshot();
            _logger.LogError(exception, "Nested library alias reconciliation failed; ignore rules are disabled for safety");
            SetStatus(
                "Alias setup failed. Ignore rules are disabled so dedicated libraries are not pruned.",
                exception.Message);
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private async Task ReconcileAsync()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var configuration = plugin.Configuration;
        var libraries = _libraryManager.GetVirtualFolders();
        var plans = LibraryAliasPlanner.CreatePlans(
            libraries,
            configuration,
            originalPath => LibraryAliasRoot.SelectRoot(originalPath, _fallbackAliasRoot));

        if (plans.Length == 0)
        {
            VerifyRecordedAliases(configuration, libraries);
            IgnoreRule.UpdateSnapshot(libraries, configuration);
            SetStatus(
                configuration.ManagedAliases.Count == 0
                    ? "Ready. No nested library aliases are currently required."
                    : $"Ready. {configuration.ManagedAliases.Count} managed library alias(es) are active.",
                string.Empty);
            return;
        }

        if (!configuration.EnableAutomaticAliases)
        {
            IgnoreRule.UpdateSnapshot(libraries, configuration);
            SetStatus(
                $"{plans.Length} nested library location(s) require aliases. Enable automatic aliases and save to apply them.",
                string.Empty);
            return;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Automatic alias migration currently supports Windows NTFS junctions and Linux symlinks only.");
        }

        IgnoreRule.ClearSnapshot();
        ValidateAliasPlacement(plans, libraries);
        foreach (var plan in plans)
        {
            JunctionAlias.Ensure(plan.AliasPath, plan.OriginalPath);
            AddMapping(configuration, plan);
        }

        configuration.AliasManagementStatus = $"Applying {plans.Length} nested library alias migration(s)...";
        configuration.AliasManagementError = string.Empty;
        plugin.SaveConfiguration();

        var monitorStopped = false;
        try
        {
            _libraryMonitor.Stop();
            monitorStopped = true;

            foreach (var plan in plans)
            {
                if (!plan.Library.Locations.Any(location => LibraryAliasPlanner.PathsEqual(location, plan.AliasPath)))
                {
                    _libraryManager.AddMediaPath(plan.Library.Name, new MediaPathInfo(plan.AliasPath));
                }
            }

            foreach (var plan in plans)
            {
                var pathToRemove = plan.PreviousAliasPath ?? plan.OriginalPath;
                if (plan.Library.Locations.Any(location => LibraryAliasPlanner.PathsEqual(location, pathToRemove)))
                {
                    _libraryManager.RemoveMediaPath(plan.Library.Name, pathToRemove);
                }
            }

            await _libraryManager.ValidateTopLibraryFolders(CancellationToken.None, false).ConfigureAwait(false);
            libraries = _libraryManager.GetVirtualFolders();
            VerifyCompletedPlans(plans, libraries);
            IgnoreRule.UpdateSnapshot(libraries, configuration);

            foreach (var plan in plans.Where(plan => plan.PreviousAliasPath is not null))
            {
                TryRemoveOldAlias(plan.PreviousAliasPath!);
            }

            SetStatus(
                $"Ready. Migrated {plans.Length} nested library location(s); "
                + $"{configuration.ManagedAliases.Count} managed alias(es) are active.",
                string.Empty);
            _libraryManager.QueueLibraryScan();
        }
        finally
        {
            if (monitorStopped)
            {
                _libraryMonitor.Start();
            }
        }
    }

    private static void AddMapping(PluginConfiguration configuration, AliasMigrationPlan plan)
    {
        var existing = configuration.ManagedAliases.FirstOrDefault(alias =>
            LibraryAliasPlanner.IsSameLibrary(plan.Library, alias)
            && LibraryAliasPlanner.PathsEqual(alias.OriginalPath, plan.OriginalPath));
        if (existing is not null)
        {
            existing.AliasPath = plan.AliasPath;
            return;
        }

        configuration.ManagedAliases.Add(new ManagedLibraryAlias
        {
            LibraryId = plan.Library.ItemId ?? string.Empty,
            LibraryName = plan.Library.Name,
            OriginalPath = plan.OriginalPath,
            AliasPath = plan.AliasPath
        });
    }

    private void TryRemoveOldAlias(string previousAliasPath)
    {
        try
        {
            JunctionAlias.RemoveIfUnused(previousAliasPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not remove the superseded alias directory '{PreviousAliasPath}'. It is orphaned but harmless; Jellyfin no longer references it.",
                previousAliasPath);
        }
    }

    private static void VerifyCompletedPlans(
        IEnumerable<AliasMigrationPlan> plans,
        IReadOnlyCollection<VirtualFolderInfo> libraries)
    {
        foreach (var plan in plans)
        {
            var library = libraries.FirstOrDefault(candidate => IsSameLibrary(candidate, plan.Library))
                ?? throw new InvalidOperationException(
                    $"Library '{plan.Library.Name}' disappeared while its alias was being configured.");
            if (!library.Locations.Any(path => LibraryAliasPlanner.PathsEqual(path, plan.AliasPath)))
            {
                throw new InvalidOperationException(
                    $"Jellyfin did not retain alias '{plan.AliasPath}' for library '{plan.Library.Name}'.");
            }

            if (library.Locations.Any(path => LibraryAliasPlanner.PathsEqual(path, plan.OriginalPath)))
            {
                throw new InvalidOperationException(
                    $"Jellyfin did not remove nested path '{plan.OriginalPath}' from library '{plan.Library.Name}'.");
            }
        }
    }

    private static void VerifyRecordedAliases(
        PluginConfiguration configuration,
        IReadOnlyCollection<VirtualFolderInfo> libraries)
    {
        foreach (var alias in configuration.ManagedAliases)
        {
            var library = libraries.FirstOrDefault(candidate => LibraryAliasPlanner.IsSameLibrary(candidate, alias));
            if (library is null
                || !library.Locations.Any(location => LibraryAliasPlanner.PathsEqual(location, alias.AliasPath)))
            {
                continue;
            }

            JunctionAlias.Ensure(alias.AliasPath, alias.OriginalPath);
        }
    }

    private static bool IsSameLibrary(VirtualFolderInfo left, VirtualFolderInfo right)
    {
        var leftId = left.ItemId ?? string.Empty;
        var rightId = right.ItemId ?? string.Empty;
        return !string.IsNullOrWhiteSpace(leftId) && !string.IsNullOrWhiteSpace(rightId)
            ? string.Equals(leftId, rightId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateAliasPlacement(
        IEnumerable<AliasMigrationPlan> plans,
        IEnumerable<VirtualFolderInfo> libraries)
    {
        var libraryLocations = libraries
            .SelectMany(library => library.Locations)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(LibraryPathResolver.NormalizePath)
            .ToArray();
        foreach (var plan in plans)
        {
            var aliasPath = LibraryPathResolver.NormalizePath(plan.AliasPath);
            if (libraryLocations.Any(location =>
                    !LibraryAliasPlanner.PathsEqual(location, plan.OriginalPath)
                    && LibraryPathResolver.ContainsPath(location, aliasPath)))
            {
                throw new InvalidOperationException(
                    $"Alias '{aliasPath}' would be inside another Jellyfin library root. "
                    + "Move Jellyfin's data directory outside all media libraries before enabling automatic aliases.");
            }
        }
    }

    private static void SetStatus(string status, string error)
    {
        var plugin = Plugin.Instance;
        if (plugin is null
            || (string.Equals(plugin.Configuration.AliasManagementStatus, status, StringComparison.Ordinal)
                && string.Equals(plugin.Configuration.AliasManagementError, error, StringComparison.Ordinal)))
        {
            return;
        }

        plugin.Configuration.AliasManagementStatus = status;
        plugin.Configuration.AliasManagementError = error;
        plugin.SaveConfiguration();
    }
}
