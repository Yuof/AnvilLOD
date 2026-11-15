# Contributing to PenguLOD

- Branch from `main`, keep each change focused, and open a pull request.
- Say in the pull request how you tested the change.
- Keep upstream copyright notices.
- Do not commit game or mod assets, personal profiles, logs or local settings. Tests use
  synthetic fixtures only.
- Update the user documentation when behaviour changes. Describe what the user does and
  sees, not how the code works.

Report bugs in [GitHub issues](https://github.com/opteron-x86/PenguLOD/issues). See
[what to include](docs/TROUBLESHOOTING.md#report-a-problem).

## Build on Linux

Install the .NET 10 SDK. On Ubuntu 24.04 the other packages are:

```bash
sudo apt-get install g++-14 cmake ninja-build pkg-config curl patch python3 \
  zlib1g-dev liblz4-dev libtbb-dev libspdlog-dev libfmt-dev libgtest-dev \
  libfontconfig1 libx11-6 libice6 libsm6 libicu-dev xvfb \
  libvulkan-dev mesa-vulkan-drivers glslang-tools appstream
```

Then:

```bash
bash tools/test.sh                           # all tests
CC=gcc-14 CXX=g++-14 bash tools/build.sh     # package in dist/linux-x64
xvfb-run -a bash tools/native-smoke.sh       # start the built desktop
bash tools/package-appimage.sh               # AppImage, optional
```

- `bash tools/build.sh --reuse-backend` skips rebuilding the grass generator after the
  first build.
- `PENGULOD_DOTNET` selects a specific `dotnet` executable.
- `PENGULOD_PACKAGE_DIR` builds into another `linux-x64` folder under `dist/`. Use it when
  a copy of the app is running from `dist/linux-x64`.
- `bash tools/test.sh --smoke-settings /path/to/preset.json` also runs a desktop test
  against a real game profile. It writes to the output folders named in that preset.

## Build on Windows

Install the .NET 10 SDK, PowerShell 7.4 or newer, Python 3.12 or newer, Git, CMake 3.28 or
newer, Ninja, and the Visual Studio 2026 C++ build tools with the Windows SDK. From
PowerShell:

```powershell
./tools/build-native-windows.ps1     # grass generator and texture helper
./tools/test-windows.ps1
./tools/build-windows.ps1 -ReuseBackend   # package in dist/win-x64, and the ZIP
./tools/windows-smoke.ps1
```

`build-windows.ps1` without `-ReuseBackend` builds the native parts as well.

## Build the SKSE plugin

On Windows:

```powershell
./skse/build.ps1 -Line all     # or -Line 1, -Line 17, -Line vr
python tools/package-runtime.py
```

The builds go to `dist/runtime`. `bash tools/test-runtime.sh` runs the plugin's tests on
Linux without Skyrim.

## Source layout

| Path | Contents |
| --- | --- |
| `src/PenguLOD.Desktop` | Desktop interface |
| `src/PenguLOD.Cli` | Command-line program |
| `src/PenguLOD.Configuration` | Presets, validation and request building shared by both |
| `src/PenguLOD.Core`, `Plugins`, `Meshes`, `Textures` | Generation engine |
| `native/texture-compute` | Optional Vulkan texture encoder |
| `tools/fasterngio` | Patches and build scripts for the grass generator |
| `skse` | SKSE plugin |
| `tests` | Engine and desktop tests |
| `packaging` | Files copied into the packages |

Open `PenguLOD.slnx` for the .NET projects.

The guides shipped in the packages are listed in `tools/build.sh` and
`tools/build-windows.ps1`. Add a new user guide to both lists.

## Merging changes from AnvilLOD

```bash
git remote add upstream https://github.com/CageTV/AnvilLOD.git  # once
git fetch upstream --tags
git switch -c upstream/anvillod-next
git merge --no-ff <upstream-tag>
```

Keep PenguLOD's `VERSION`, names and cross-platform build when resolving conflicts. Update
`UPSTREAM_VERSION` and the AnvilLOD version named in `docs/PROVENANCE.md`.

Releases are described in [docs/RELEASING.md](docs/RELEASING.md).
