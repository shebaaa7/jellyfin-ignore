using Jellyfin.Plugin.Ignore.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Xunit;

namespace Jellyfin.Plugin.Ignore.Tests;

public class LibraryAliasPlannerTests
{
    private readonly string _moviesRoot = Path.Combine(Path.GetTempPath(), "Movies", "Movies");

    [Fact]
    public void CreatePlans_SelectsEveryNestedDedicatedRootMatchedByParentPatterns()
    {
        var configuration = CreateConfiguration();
        var libraries = CreateLibraries();
        var aliasRoot = Path.Combine(Path.GetTempPath(), "ignore-aliases");

        var plans = LibraryAliasPlanner.CreatePlans(libraries, configuration, _ => aliasRoot);

        Assert.Equal(3, plans.Length);
        Assert.All(plans, plan => Assert.Equal("Home Movies", plan.Library.Name));
        Assert.All(plans, plan => Assert.StartsWith(aliasRoot, plan.AliasPath));
        Assert.All(plans, plan => Assert.Null(plan.PreviousAliasPath));
        Assert.Contains(plans, plan => plan.OriginalPath == Path.Combine(_moviesRoot, "zz Home Movies"));
    }

    [Fact]
    public void CreatePlans_DoesNotMigrateNestedRootWithoutMatchingParentPattern()
    {
        var configuration = CreateConfiguration();
        configuration.LibraryConfigurations[0].Patterns = "trailers/**";

        var aliasRoot = Path.Combine(Path.GetTempPath(), "ignore-aliases");
        var plans = LibraryAliasPlanner.CreatePlans(
            CreateLibraries(),
            configuration,
            _ => aliasRoot);

        Assert.Empty(plans);
    }

    [Fact]
    public void CreatePlans_RecognizesPreviouslyManagedAliasAsComplete()
    {
        var aliasRoot = Path.Combine(Path.GetTempPath(), "ignore-aliases");
        var originalPath = CreateLibraries()[1].Locations[0];

        // Derive the canonical alias path the same way a fresh migration would, rather than
        // hardcoding a path in the internal naming scheme, so this test does not need to change
        // whenever that scheme does.
        var freshPlans = LibraryAliasPlanner.CreatePlans(CreateLibraries(), CreateConfiguration(), _ => aliasRoot);
        var aliasPath = Assert.Single(freshPlans, plan => plan.OriginalPath == originalPath).AliasPath;

        var configuration = CreateConfiguration();
        var libraries = CreateLibraries();
        configuration.ManagedAliases.Add(new ManagedLibraryAlias
        {
            LibraryId = "home-movies-id",
            LibraryName = "Home Movies",
            OriginalPath = originalPath,
            AliasPath = aliasPath
        });
        libraries[1].Locations[0] = aliasPath;

        var plans = LibraryAliasPlanner.CreatePlans(libraries, configuration, _ => aliasRoot);

        Assert.Equal(2, plans.Length);
        Assert.DoesNotContain(plans, plan => LibraryAliasPlanner.PathsEqual(plan.OriginalPath, originalPath));
    }

    [Fact]
    public void CreatePlans_RelocatesAnExistingAliasWhenTheCanonicalPathChanges()
    {
        var oldAliasRoot = Path.Combine(Path.GetTempPath(), "old-ignore-aliases");
        var newAliasRoot = Path.Combine(Path.GetTempPath(), "new-ignore-aliases");
        var originalPath = CreateLibraries()[1].Locations[0];

        var oldPlans = LibraryAliasPlanner.CreatePlans(CreateLibraries(), CreateConfiguration(), _ => oldAliasRoot);
        var oldAliasPath = Assert.Single(oldPlans, plan => plan.OriginalPath == originalPath).AliasPath;

        var configuration = CreateConfiguration();
        var libraries = CreateLibraries();
        configuration.ManagedAliases.Add(new ManagedLibraryAlias
        {
            LibraryId = "home-movies-id",
            LibraryName = "Home Movies",
            OriginalPath = originalPath,
            AliasPath = oldAliasPath
        });
        libraries[1].Locations[0] = oldAliasPath;

        var plans = LibraryAliasPlanner.CreatePlans(libraries, configuration, _ => newAliasRoot);

        var relocation = Assert.Single(plans, plan => plan.OriginalPath == originalPath);
        Assert.Equal(oldAliasPath, relocation.PreviousAliasPath);
        Assert.StartsWith(newAliasRoot, relocation.AliasPath);
        Assert.NotEqual(oldAliasPath, relocation.AliasPath);
    }

    [Fact]
    public void IgnoreRule_HasNoServiceConstructorDependency()
    {
        var constructor = Assert.Single(typeof(IgnoreRule).GetConstructors());

        Assert.Empty(constructor.GetParameters());
    }

    [Fact]
    public void IgnoreRule_SeparatesOriginalParentPathFromDedicatedAliasPath()
    {
        var configuration = CreateConfiguration();
        var libraries = CreateLibraries();
        var aliasPath = Path.Combine(Path.GetTempPath(), "ignore-aliases", "HomeMovies-123456789ABC");
        libraries[1].Locations = [aliasPath];
        IgnoreRule.UpdateSnapshot(libraries, configuration);
        var rule = new IgnoreRule();

        var originalIsIgnored = rule.ShouldIgnore(new FileSystemMetadata
        {
            FullName = Path.Combine(_moviesRoot, "zz Home Movies", "Season 1", "episode.mkv")
        }, null);
        var aliasIsIgnored = rule.ShouldIgnore(new FileSystemMetadata
        {
            FullName = Path.Combine(aliasPath, "Season 1", "episode.mkv")
        }, null);

        Assert.True(originalIsIgnored);
        Assert.False(aliasIsIgnored);
    }

    [Fact]
    public void JunctionAlias_CreatesVerifiableDirectoryAlias()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var testRoot = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "junction-test-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(testRoot, "target");
        var alias = Path.Combine(testRoot, "alias");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "marker.txt"), "junction-ok");

        try
        {
            JunctionAlias.Ensure(alias, target);

            Assert.Equal("junction-ok", File.ReadAllText(Path.Combine(alias, "marker.txt")));
            JunctionAlias.Ensure(alias, target);
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void JunctionAlias_RemoveIfUnused_DeletesOnlyAGenuineJunction()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var testRoot = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "junction-remove-test-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(testRoot, "target");
        var alias = Path.Combine(testRoot, "alias");
        var plainDirectory = Path.Combine(testRoot, "plain");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "marker.txt"), "junction-ok");
        Directory.CreateDirectory(plainDirectory);
        File.WriteAllText(Path.Combine(plainDirectory, "keep.txt"), "not-a-junction");

        try
        {
            JunctionAlias.Ensure(alias, target);

            JunctionAlias.RemoveIfUnused(plainDirectory);
            Assert.True(Directory.Exists(plainDirectory));
            Assert.True(File.Exists(Path.Combine(plainDirectory, "keep.txt")));

            JunctionAlias.RemoveIfUnused(alias);
            Assert.False(Directory.Exists(alias));
            Assert.True(File.Exists(Path.Combine(target, "marker.txt")));
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void LibraryAliasRoot_SelectsAShortPathOnTheSameDriveAsTheMedia()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var originalPath = Path.Combine(_moviesRoot, "zz Home Movies");
        var driveRoot = Path.GetPathRoot(originalPath)!;

        var selected = LibraryAliasRoot.SelectRoot(originalPath, fallbackRoot: "should-not-be-used");

        Assert.StartsWith(driveRoot, selected);
        Assert.True(selected.Length < driveRoot.Length + 10, $"Expected a short alias root, got '{selected}'.");
    }

    [Fact]
    public void LibraryAliasRoot_UsesTheFallbackRootOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var originalPath = Path.Combine(_moviesRoot, "zz Home Movies");
        var fallback = Path.Combine(Path.GetTempPath(), "fallback-aliases");

        var selected = LibraryAliasRoot.SelectRoot(originalPath, fallback);

        // Linux has no MAX_PATH-style limit and "/" is not writable by the Jellyfin service user,
        // so unlike Windows there is no short drive-root placement to prefer here.
        Assert.Equal(fallback, selected);
    }

    [Theory]
    [InlineData("zz Home Movies", true)]
    [InlineData("zz Home Movies/Season 1/episode.mkv", true)]
    [InlineData("zzz behind the scenes/episode.mkv", false)]
    public void FolderSubtreePattern_IncludesFolderBoundary(string relativePath, bool expected)
    {
        var matcher = new IgnorePatternMatcher("zz Home Movies/**");

        var actual = matcher.IsMatch(relativePath, "D:/Media/Movies/" + relativePath);

        Assert.Equal(expected, actual);
    }

    private PluginConfiguration CreateConfiguration()
    {
        var configuration = new PluginConfiguration();
        configuration.LibraryConfigurations.Add(new LibraryIgnoreConfiguration
        {
            LibraryId = "movies-id",
            LibraryName = "Movies",
            Patterns = "zz Home Movies/**\nzzz behind the scenes/**\nzzz bloopers/**"
        });
        configuration.LibraryConfigurations.Add(new LibraryIgnoreConfiguration
        {
            LibraryId = "home-movies-id",
            LibraryName = "Home Movies",
            Patterns = string.Empty
        });
        return configuration;
    }

    private VirtualFolderInfo[] CreateLibraries()
    {
        return
        [
            new VirtualFolderInfo
            {
                ItemId = "movies-id",
                Name = "Movies",
                Locations = [_moviesRoot]
            },
            new VirtualFolderInfo
            {
                ItemId = "home-movies-id",
                Name = "Home Movies",
                Locations =
                [
                    Path.Combine(_moviesRoot, "zz Home Movies"),
                    Path.Combine(_moviesRoot, "zzz behind the scenes"),
                    Path.Combine(_moviesRoot, "zzz bloopers")
                ]
            }
        ];
    }
}
