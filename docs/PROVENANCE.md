# Credits and dependencies

PenguLOD is based on [AnvilLOD by CageTV](https://github.com/CageTV/AnvilLOD) and keeps its
copyright notices and GPL-3.0-only license.

## What the packages contain

| Component | Used for | Included |
| --- | --- | --- |
| .NET and Avalonia | Runtime and desktop interface | Yes |
| Mutagen and texture libraries | Reading plugins and archives, processing textures | Yes |
| [FasterNGIO](https://github.com/panthuncia/FasterNGIO) | Grass cache generation | Yes, with its patched source |
| Vulkan texture helper | Optional faster texture encoding | Yes. Needs a Vulkan driver on your system |
| Game and mod assets, resource packs | Source models, textures, rules and recipes | No. PenguLOD reads the ones you have installed |

Every package carries the license notices of its dependencies in `licenses/`, with the full
list in `THIRD-PARTY-PACKAGES.txt`. The FasterNGIO source that was built, including
PenguLOD's patches, is in `backends/fasterngio/FasterNGIO-source.tar.gz`.

Generated output can contain assets from your installed mods. Those belong to their authors
and are not covered by PenguLOD's license.

## Game runtime

Dynamic LOD and the in-game quality controls need the PenguLOD SKSE plugin. It is a separate
download on the release page, `PenguLOD-<version>-SKSE.zip`, with builds for Skyrim SE
1.5.97 to AE 1.6.1170 (GOG 1.6.1179), for Skyrim 1.7 and newer, and for Skyrim VR 1.4.15. The
plugin runs inside the game, including under Proton or Wine.

The plugin is derived from AnvilLOD 0.2.70. Do not enable `AnvilLOD.dll` and `PenguLOD.dll`
together. See [upgrading](UPGRADING.md).

## Source

The source for every release is the matching tag in the
[repository](https://github.com/opteron-x86/PenguLOD). Dependency versions are pinned there:
FasterNGIO under `tools/fasterngio`, Windows native libraries in `tools/windows/vcpkg.json`,
and the SKSE plugin's libraries under `skse/`.
