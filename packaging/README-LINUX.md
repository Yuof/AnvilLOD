# PenguLOD for Linux

1. Keep this folder together.
2. Run `./run-pengulod.sh` to start the desktop.
3. To add a menu entry, run `./install-desktop.sh` once the folder is where you want to keep
   it. This needs Python 3.

The command-line program is `./PenguLOD`. Run `./PenguLOD --help` for its commands.

Requirements: x64 Linux with glibc 2.39 or newer (Ubuntu 24.04, Debian 13, Fedora 43 or
later), X11 or XWayland, and the fontconfig, ICE, SM and ICU libraries that desktop
installs normally have.

Dynamic LOD and the in-game quality controls need the PenguLOD SKSE plugin, a separate
download from the same release.

## Guides

- [User guide](docs/USER-GUIDE.md): start here
- [Settings reference](docs/SETTINGS.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Command line](docs/CLI.md)
- [Upgrading from AnvilLOD output](docs/UPGRADING.md)

Licenses: [LICENSE.txt](LICENSE.txt), [THIRD-PARTY-PACKAGES.txt](THIRD-PARTY-PACKAGES.txt)
and the `licenses` folder.
