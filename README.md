# Jellyfin Ignore Per-Library

This is a community fork of [fdett/jellyfin-ignore](https://github.com/fdett/jellyfin-ignore).
It expands the original global ignore list into separate glob patterns for each
Jellyfin library. New maintainers, bug reports, and pull requests are welcome.

## Why nested libraries need aliases

Jellyfin's ignore-rule callback receives a path and immediate parent, but not
the virtual library that initiated a scan. Jellyfin also merges overlapping
physical roots into one internal tree. Code that merely chooses the deepest
matching root cannot tell a parent-library scan from a nested-library scan and
can remove the nested library's items.

This fork solves that ambiguity by automatically assigning each affected
dedicated library a stable alias path outside the parent library: an NTFS
junction on Windows, a plain symlink on Linux. Jellyfin then sees two
distinct path identities:

- The parent library scans the original path, where its ignore pattern applies.
- The dedicated library scans the plugin-managed alias, where the parent's
  patterns cannot apply.

The media files stay in their original folders. They are not moved, renamed, or
copied, and users do not have to create junctions themselves.

For example:

- `Movies` is rooted at `D:\Media\Movies`.
- `Home Movies` has locations under that root, including
  `D:\Media\Movies\zz Home Movies`.
- `zz Home Movies/**` is configured only for `Movies`.

After automatic alias setup, `Movies` skips the original nested
folder while `Home Movies` scans the same media through its private alias.

## Compatibility and safety

Release `0.9.0.0` targets Jellyfin `12.0.0` and .NET `10.0`. Plugin ABI versions
must match the installed Jellyfin server version.

Automatic aliases work on Windows (NTFS junctions) and Linux (symlinks); the
feature is opt-in on both. Before changing a library, the plugin creates and
verifies every alias, records its mapping, adds all alias paths, then removes
the corresponding nested paths. If any step fails, ignore rules are disabled
so the plugin cannot prune a dedicated library. On any other platform,
enabling automatic aliases throws a clear `PlatformNotSupportedException`
rather than silently doing nothing.

**On Windows**, aliases live in a short, hidden directory (`.ja\<hash>`) at
the root of the same drive as their media, not nested under Jellyfin's own
configuration directory. Keeping the alias path short matters there: native
image decoders can silently fail to read a perfectly valid poster/folder
image once its full path passes Windows' 260-character limit, even though
Jellyfin's own file scanning and streaming tolerate longer paths.

**On Linux**, aliases live under the plugin's own configuration directory
(alongside its config XML) instead. Linux has no equivalent path-length
limit worth working around, and placing a directory at `/` generally
requires root, which the Jellyfin service user does not have — so there is
neither a need nor a safe way to mirror the Windows drive-root placement.
Symlinks are created with .NET's built-in `Directory.CreateSymbolicLink`, no
elevated privileges required.

If the alias-placement scheme ever changes, already-migrated aliases are
automatically relocated to the new path the next time the plugin reconciles;
the old alias is removed only after the new one is verified and Jellyfin has
switched over. Disabling
automatic management stops new migrations but intentionally keeps existing
working aliases and library paths; it does not delete junctions or media.
The configuration page displays every original-to-alias mapping for review.

The resolver callback uses only an immutable in-memory snapshot. It never calls
`ILibraryManager.GetVirtualFolders()`, avoiding the fresh-install recursion that
affected the earlier prototype.

## Configuration

1. Install the plugin and restart Jellyfin.
2. Open **Dashboard → Plugins → Jellyfin Ignore Per-Library**.
3. Enter one glob per line beneath the library where it should apply.
4. For overlapping or nested roots, enable **Automatically manage aliases for
   nested library locations**.
5. Select **Save and apply** and review the displayed status and mappings.

Patterns use [DotNet.Glob](https://github.com/dazinator/DotNet.Glob) syntax and
should normally be relative to the selected library root. `/` is the preferred
separator. A trailing `/**` includes the folder itself so Jellyfin can prune it
before descending:

```text
zz Home Movies/**
zzz behind the scenes/**
zzz bloopers/**
extras/**
**/*.m3u
```

For libraries with multiple locations, paths are matched relative to the
specific location that contains them.

## Installation

After a public release is available, the repository URL can be added under
**Dashboard → Plugins → Repositories**:

```text
https://raw.githubusercontent.com/shebaaa7/jellyfin-ignore/master/manifest.json
```

For a manual installation:

1. Create a versioned folder beneath Jellyfin's plugin directory. A default
   Windows path is:

   ```text
   C:\ProgramData\Jellyfin\Server\plugins\Jellyfin Ignore Per-Library_<version>
   ```

2. Copy two files into that folder:
   - `Jellyfin.Plugin.Ignore.dll` — from the release archive, or from
     `artifacts/publish-<version>/` in this repo after a `dotnet publish`.
   - `meta.json` — **not included in the release archive**, so it must be
     copied in separately. A ready copy for the current release is saved at
     `artifacts/publish-0.7.0.0/meta.json`. For a new version, copy that file
     and update its `"version"` field to match.

   The folder should end up containing just those two files, e.g.:

   ```text
   Jellyfin Ignore Per-Library_0.7.0.0\
     Jellyfin.Plugin.Ignore.dll
     meta.json
   ```

3. If replacing an older version with the same plugin GUID, move the old
   version's folder aside (e.g. rename with a `.disabled` suffix) rather than
   leaving both in place, so Jellyfin doesn't load two copies of the same
   plugin.
4. Restart Jellyfin so it rescans the plugins directory.
5. Check the Jellyfin log for `Loaded plugin: "Jellyfin Ignore Per-Library"
   "<version>"` to confirm it loaded the intended version, and for any
   `PluginManager: Error deserializing` entries.

**`meta.json` encoding matters.** It must be plain UTF-8 with **no byte-order
mark (BOM)**. Jellyfin's JSON parser rejects a BOM with `'0xEF' is an invalid
start of a value`, and when that happens it does not just skip the plugin —
it deletes the folder and falls back to loading whatever older version of the
same plugin it can still find elsewhere, which is easy to mistake for a
successful deploy. On Windows, avoid `Set-Content -Encoding utf8` (which adds
a BOM); use
`[System.IO.File]::WriteAllText(path, json, (New-Object System.Text.UTF8Encoding($false)))`
or a plain text editor set to save without a BOM instead.

## Development and verification

The solution targets `net10.0` and uses Jellyfin `12.0.0` controller/model
packages:

```powershell
dotnet restore Jellyfin.Plugin.Ignore.sln --source https://api.nuget.org/v3/index.json
dotnet test Jellyfin.Plugin.Ignore.sln --configuration Release
dotnet publish Jellyfin.Plugin.Ignore/Jellyfin.Plugin.Ignore.csproj --configuration Release
```

Tests cover pattern boundaries, alias planning, stable managed mappings, a
service-free resolver callback, actual junction creation, and separation of an
original parent path from its dedicated alias. An isolated real Jellyfin 12
test additionally covers a fresh database, automatic three-location migration,
scanning, direct streaming, external subtitles, restart, and rescan.

## Publishing a new version

1. Update `build.yaml` and `Directory.Build.props`.
2. Run `build_plugin.sh` with `jprm`, or create an equivalent zip from the
   release publish output.
3. Add a `manifest.json` entry containing the release asset URL, MD5 checksum,
   and timestamp.
4. Commit and push the changes.
5. Create the matching GitHub release and attach the zip archive.

## Credits and license

Original plugin by [fdett](https://github.com/fdett). This fork remains licensed
under the GNU General Public License v3.0; see [LICENSE](LICENSE).
