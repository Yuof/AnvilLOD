# Upgrading from AnvilLOD output

From version 0.6, generated plugins, folders and the SKSE plugin are named PenguLOD instead
of AnvilLOD. Your quality settings are not changed. Settings from the AnvilLOD app are
imported on first start, and the original file is left in place.

## Switch over

1. Export your current settings as a preset, and keep your current generated mods and a
   save made before the switch.
2. Choose new, empty output folders. PenguLOD will not generate into a folder that holds
   AnvilLOD output.
3. In your mod manager, disable the old object LOD output. Keep source mods and resource
   packs enabled.
4. Generate textures, terrain and object LOD. An existing grass cache can be reused.
5. Install the new outputs as new mods and disable the old generated mods. Do not merge the
   two sets.
6. Install the PenguLOD SKSE plugin and disable `AnvilLOD.dll`. Use only one of them.
7. Enable the new generated plugins and test on a separate save.

Do not rename generated plugins. Changing plugins on an existing save is at your own risk.
To go back, restore the old output, the old SKSE plugin and the old save together.

## New names

| Item | Name |
| --- | --- |
| Large-reference master | `PenguLOD.esm` |
| Placed objects and terrain support | `PenguLOD.esp` |
| Effects plugins, when needed | `PenguLOD Effects*.esp` |
| SKSE plugin and its settings | `SKSE/Plugins/PenguLOD.dll`, `PenguLOD.ini` |
| SKSE plugin data | `SKSE/Plugins/PenguLOD/` |
| Generated meshes and textures | `meshes/pengulod/`, `textures/pengulod/` |

A plugin is written only when your settings and load order need it.

## Old files

- The PenguLOD SKSE plugin can still read AnvilLOD output when no PenguLOD output is
  installed. The AnvilLOD plugin cannot read PenguLOD output.
- If `PenguLOD.ini` is missing, `AnvilLOD.ini` is read. To carry over a customised file,
  copy it to `PenguLOD.ini`.
- Resource packs under `Data/DynDOLOD` keep working unchanged.
