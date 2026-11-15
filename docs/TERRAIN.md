# Terrain

The terrain stage builds the distant landscape: meshes, colour and normal textures, water,
and optionally the heightmaps used by Community Shaders Terrain Shadows.

## Generate

1. On **Terrain**, set **Terrain output** to an empty folder of its own.
2. Choose the worldspaces on **Overview**. Terrain and object LOD share this selection.
3. Click **Generate terrain**, or tick **Terrain** on **Overview** to run it with the other
   stages.
4. Install the folder as a mod, replacing any earlier terrain LOD.

When terrain and object LOD run together, object LOD uses the new terrain automatically.
Otherwise turn on **Use generated terrain for object LOD**.

If a run is interrupted, run it again. Finished tiles are reused.

## Main settings

| Setting | Default | Notes |
| --- | --- | --- |
| Texture size | 512 | A power of two from 64 to 2048. Larger uses more texture memory. |
| Brightness | 1 | Lower darkens the distant ground. |
| Generate water LOD | On | |
| Generate seasonal terrain from Seasons of Skyrim swaps | Off | See [seasons](SEASONS.md). |
| Look for textures/pbr equivalents | Off | Turn on only if the game itself uses PBR landscape textures. |

The [settings reference](SETTINGS.md#terrain) lists the rest.

## Missing landscape data

Three settings, all on by default, let a run continue where data is missing. Whatever they
fill in is listed in the warnings and in `PenguLOD.terrain.json`.

| Setting | What it does |
| --- | --- |
| Fill missing landscape from existing terrain LOD | Where plugins have no landscape, uses terrain LOD that is already installed. |
| Fill uncovered land at the default height | Where there is neither landscape nor existing terrain LOD, makes flat land at the worldspace's default height. |
| Use the default texture for missing landscape textures | Paints a missing texture with the default landscape texture. |

Turn a setting off to stop the run instead. The problems are listed before the first tile
is built.

The default landscape texture is `textures/landscape/dirt02.dds`, unless
`sDefaultLandDiffuseTexture` in your Skyrim INI names another. That texture must be
installed.

## Terrain shadow heightmaps

**Export Community Shaders heightmaps** is on by default.

- Install the heightmap files loose, not in an archive, and do not rename them.
- A worldspace with holes in its terrain cannot be exported. Turn the option off when
  generating such a worldspace.
- **Maximum heightmap dimension** caps the resolution for large worldspaces.

## Limits

Terrain textures are a fixed bake of colour, normals and, for PBR textures, ambient
occlusion. Reflections, parallax and changing light are not reproduced.
