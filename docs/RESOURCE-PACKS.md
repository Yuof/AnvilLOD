# Resource packs

A resource pack supplies LOD rules, texture recipes and render models. Packs made for
DynDOLOD work as they are. PenguLOD does not bundle any pack.

## What most setups need

Two different things carry the name DynDOLOD, and they go in different places:

- **The DynDOLOD program** (the folder that holds `DynDOLODx64.exe` and `TexGenx64.exe`)
  has the rules, texture recipes and render models for the base game, in
  `Edit Scripts/DynDOLOD`. Set it as the **Primary resource folder**. It does not need to
  be installed as a mod, and PenguLOD never runs it.
- **DynDOLOD Resources SE** is a mod with LOD models and textures for the game. Install
  and enable it in your mod manager like any other mod. Do not select it as a resource
  folder: it has no rules, and PenguLOD reads enabled mods by itself.

Without the first, texture generation stops with "No texture stitching recipes match this
load order". Selecting the second as a resource folder stops with "This folder is a mod,
not a resource pack".

## Add a pack

- **Installed as a mod:** nothing to do. Resources in enabled mods are found automatically.
- **Kept elsewhere:** on **Game & resources**, set **Primary resource folder**, and list
  any others under **Additional resource folders**, one per line.

A folder outside your mods supplies only rules, recipes and render models. Meshes and
textures the game needs must be installed through your mod manager.

## Folder layout

Select a folder that contains `Rules`, `Configs` or `Render`, or one that wraps them in
`DynDOLOD`, `Data/DynDOLOD` or `Edit Scripts/DynDOLOD`. Names are matched without regard to
case.

| Resource | Location inside that folder |
| --- | --- |
| LOD rules | `Rules/DynDOLOD_SSE_*.ini` |
| Texture recipes and billboard rules | `Configs/DynDOLOD_*_TexGen_*.txt` |
| Mesh lookup | `Configs/DynDOLOD_SSE_mesh_lookup.txt` |
| World exclusions | `Configs/DynDOLOD_SSE_mod_world_ignore.txt` |
| Child worldspace settings | `Configs/DynDOLOD_SSE_childworld_*.ini`, `Configs/DynDOLOD_SSE_ChildworldMatches.txt` |
| Object render models | `Render/Objects/**/*.nif` |
| Hybrid trunk render models | `Render/Billboards/**/*_trunk.nif` |

The older layouts are accepted too: the same files directly in the folder, and render
models under `Render/Skyrim/`.

## Which file wins

When several sources supply the same file, the last one in this list is used:

1. The primary resource folder
2. Additional resource folders, in the order listed
3. Enabled mods

A **Custom rule file** set on Game & resources overrides all other rules.

**Scan results → Resource files** shows which file won and where it came from. The object
LOD output records the same in `PenguLOD.resources.json`.

## If something is missing

**Check setup** reports missing recipes or render models for the categories you selected. A
recipe for a plugin that is not in your load order is ignored.
