# Releasing

`VERSION` sets the version of the generator, the packages and the SKSE plugin. Versions
follow SemVer; alpha, beta and rc versions are published as pre-releases.
`UPSTREAM_VERSION` records the AnvilLOD version PenguLOD was derived from and does not
change for a PenguLOD release.

## Steps

1. Set the version:

   ```bash
   python3 tools/version.py 0.7.0-beta.1
   ```

2. Add a `## [0.7.0-beta.1] - YYYY-MM-DD` section to `CHANGELOG.md`. It becomes the release
   notes on GitHub and Nexus. Write one line per change, starting with Added, Changed, Fixed
   or Removed, and say only what changed. If output has to be generated again, say so in one
   line above the list. Causes, measurements and how a fix was checked belong in the pull
   request. Update the version in `README.md`.
3. Test and build locally:

   ```bash
   bash tools/test.sh
   bash tools/build.sh
   xvfb-run -a bash tools/native-smoke.sh
   ```

4. Open a pull request and wait for every check to pass. **VirusTotal check of the plugin DLLs**
   lists the engines that flag a game plugin DLL; Nexus blocks an upload that is flagged. A first
   result can change within hours, so before merging run **Actions → VirusTotal check** with the
   pull request number, which has the DLLs analysed again. The DLLs are built reproducibly with a
   named compiler (`MSVC_TOOLSET` in the workflow), so the ones checked are the ones released.
   Then merge the pull request.
5. Tag the merge commit and push only the tag:

   ```bash
   git tag -a v0.7.0-beta.1 -m 'PenguLOD 0.7.0-beta.1'
   git push origin v0.7.0-beta.1
   ```

The tag build checks that the tag matches `VERSION`, runs the tests on Linux and Windows,
and publishes a GitHub release only when every job passes. It refuses to overwrite an
existing release.

## What is published

- `PenguLOD-<version>-linux-x64.tar.gz` and `PenguLOD-<version>-x86_64.AppImage`
- `PenguLOD-<version>-win-x64.zip`
- `PenguLOD-<version>-SKSE.zip`: the three plugin builds and a FOMOD installer. It holds no
  source archives, because mod sites reject uploads that contain them.
- `PenguLOD-<version>-source.tar.gz`
- `SHA256SUMS`

## The VirusTotal check

`tools/virustotal-scan.py` needs the repository secret `VIRUSTOTAL_API_KEY` (**Settings → Secrets
and variables → Actions**). Keep the key out of files, issues and chat. Without it nothing is
checked, and builds of forks, which do not get secrets, still pass.

- On a pull request the check fails when any engine flags a DLL. On `main` and on a tag it only
  reports.
- A flagged DLL has so far meant a generic signature that matches what one compiler version
  produces, not something in the plugin: the same source built by MSVC 14.51 was clean and built
  by 14.52 was flagged. Changing `MSVC_TOOLSET` is the first thing to try, and a false-positive
  report to the vendor the second.
- **Actions → VirusTotal check** takes a release tag or a pull request number and has the DLLs
  analysed again.

## Upload a release to Nexus Mods

The **Upload release to Nexus** workflow is started by hand. It downloads the Windows, Linux
and SKSE packages of a published GitHub release, checks them against the release checksums
and the tag's successful build, and adds each one as a new version of its file on the mod
page. The previous versions are archived, and the mod's version and changelog are updated
from `CHANGELOG.md`.

Nexus accepts ZIP, 7z and RAR archives, so the Linux package is uploaded as a ZIP with the same
contents and file permissions.

One-time setup:

1. On the Nexus mod page, upload the three files once by hand so that each has a file ID.
2. In the repository, open **Settings → Environments** and create an environment named
   `nexus`.
3. Add the secret `NEXUSMODS_API_KEY` with your Nexus Mods API key. Keep the key out of
   files, issues and chat.
4. Add the variables `NEXUSMODS_MOD_ID`, `NEXUSMODS_FILE_ID_WINDOWS`,
   `NEXUSMODS_FILE_ID_LINUX` and `NEXUSMODS_FILE_ID_SKSE`. The IDs are shown under
   **Advanced** in the mod page's Files tab and in the edit menu of Manage Files.

For each release, once the tag build has finished:

1. Open **Actions → Upload release to Nexus → Run workflow**.
2. Select `main` and enter the tag, such as `v0.7.0-beta.1`.

If an upload fails partway, check the Files and Changelog tabs on Nexus before running it
again, so that a file is not uploaded twice. The page description and images are edited on
Nexus.

## If a tagged build fails

Fix the problem on a branch and release a new version. Do not move a published tag or
replace released files.
