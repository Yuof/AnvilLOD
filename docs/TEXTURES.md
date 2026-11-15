# Textures

The texture stage makes the textures LOD uses and the images of distant trees and grass. It
reads the whole load order; the worldspace selection does not limit it.

## What it generates

| Category | What it makes | Default |
| --- | --- | --- |
| Stitched object textures | Object LOD texture sheets built from the recipes in your mods and resource packs | On |
| Tree billboards | Images of each tree, rendered from its model | On |
| Grass billboards | Images of each grass type, for grass LOD | Off |
| Hybrid tree trunk textures | Trunk images for installed hybrid tree LOD models | On |
| Rendered object textures | Object textures rendered from templates (experimental) | Off |

Recipes and render models come from your enabled mods and from any
[resource packs](RESOURCE-PACKS.md) you add.

## Generate

1. On **Textures**, set **Texture output** to an empty folder of its own.
2. Tick the categories you want. Each run replaces the whole folder, so tick everything you
   want to keep.
3. Click **Generate textures**, or tick **Textures** and **Object LOD** on **Overview** to
   run both.
4. Install the folder as a mod. The object LOD output must win conflicts with it.

After changing a texture setting, run textures and then object LOD, and install both.

**Use generated textures for LOD** makes a later object LOD run read this folder. A run that
includes both stages does this automatically.

## Billboard quality

- **HD front and side billboards** gives each tree and grass card separate front and side
  images. Turn it off to render one view.
- **Maximum billboard size**, **Tree world units per pixel** and **Grass world units per
  pixel** set the resolution. A lower units-per-pixel value is sharper and larger. Grass
  has its own value because a tuft is far smaller than a tree.
- **Size tree textures for the largest placed reference** gives trees placed at a large
  scale more detail, up to the maximum size.
- **Grass texture layout** is **Auto** by default and detects the packed layout used by
  Community Shaders complex grass. Choose **Basic** or **Packed** only if detection gets it
  wrong.

## Brightness

Each kind of LOD has its own brightness control. Change one at a time and compare in the
same place, weather and time of day.

| Too bright or dark | Adjust | Then rerun |
| --- | --- | --- |
| Tree billboards | **Brightness multiplier** on Textures | Textures, then object LOD |
| Distant grass | **Grass tip brightness** and **Grass root brightness** on Object LOD | Object LOD |
| Ground | **Brightness** on Terrain | Terrain |
| Objects | **Object brightness** on Object LOD | Object LOD |

For PBR source textures, use **PBR scale**, **PBR gamma** and **PBR occlusion strength**
under **Material calibration**.

## Preview one model

1. Enter part of a model path in **Model path filter**.
2. Use a separate output folder.
3. Generate, select a result and click **Preview selected texture**.

Clear the filter for a complete run. A filtered folder cannot feed a full LOD run.

## Failed items

A complete run publishes everything it could generate. A model that cannot be rendered, or
a recipe whose source texture is not installed, is logged as an error, counted in the
summary and listed as `Failed` in the report. It is left out of the output:

- A tree without a billboard has no tree LOD, unless a mod already supplies a billboard
  for it.
- Grass without a billboard is drawn in grass LOD with its own model texture.
- A stitched texture that was not built is read from your mods where one supplies it.

Fix the model or install the missing texture, then run textures again. A filtered preview
publishes only when every selected model renders.

## Limits

- Billboards are a fixed render. They do not reproduce wind, translucency, parallax or
  lighting that changes with the view.
- Trees that rely on animated, blended or view-dependent effects are skipped and listed in
  the report.
- Output is not identical to TexGen's. Existing TexGen output can stay installed below
  PenguLOD's to cover anything PenguLOD does not generate.
- Recipes use the TexGen format, including copy, stitching and mipmap conversion recipes.
  See the [TexGen configuration guide](https://dyndolod.info/Help/TexGen-Configuration).

## Reports

`PenguLOD.textures-report.json` in the texture output lists what was generated, skipped,
kept and failed. `PenguLOD.recipes.json` in the object LOD output lists how each model's
textures were matched and which ones fell back to their supplied LOD textures.
