# Objects

The object LOD stage builds distant buildings, rocks and other objects from the LOD models
and rules supplied by your mods and [resource packs](RESOURCE-PACKS.md). This page covers
its optional extras. The [settings reference](SETTINGS.md#object-lod) lists every option.

## Build missing object LOD models

Some objects have no LOD model and so vanish at a distance. PenguLOD can make one from the
full model.

1. On **Object LOD → Automatic object LOD**, turn on **Build missing object LOD models**.
2. Run object LOD.

| Setting | Default | Effect |
| --- | --- | --- |
| LOD 4 / LOD 8 / LOD 16 object triangle budget | 2500 / 1000 / 400 | Maximum triangles per generated model. A model that cannot fit is skipped at that level. |
| Minimum object size | 128 | Objects smaller than this, in game units, are skipped. |
| Object reduction multiplier | 1 | Lower keeps more detail; higher removes more. |

LOD models installed by your mods are always used first, and the active rules still decide
which objects appear. Generated models go straight into the object LOD output; there is
nothing extra to install.

This option adds scenery and therefore drawing cost. Look at buildings, fences and other
thin shapes in game before relying on it.

**Scan results → Automatic object LOD** and `PenguLOD.objects.json` in the output list what
was generated and what was skipped, with the reason.

## Reduce texture cost

Both options are under **Object LOD → Object atlases** and are off by default.

- **Pack object texture atlases** combines compatible object textures into shared sheets,
  so more surfaces can be drawn together. **Atlas cell size** sets the detail per texture:
  512 favours quality, 256 saves more memory.
- **Optimize object LOD textures** writes smaller compressed copies of textures that are
  not in an atlas. **Maximum standalone LOD texture size** defaults to 1024.

A surface that a texture repeats over is cut along the repeats so that it fits a sheet,
which adds triangles to it. Surfaces that cannot be packed keep their original textures.
Your source textures are never changed; the new ones are written into the object LOD
output.

After changing either option, run object LOD only and replace the installed output.
Smaller textures do not guarantee a higher frame rate, so compare the same view before and
after. `PenguLOD.atlas.json` and `PenguLOD.object-textures.json` record what changed.

## Base Object Swapper

Objects removed by a Base Object Swapper `_SWAP.ini` are left out of LOD. Only removals are
supported, meaning swaps to the invisible XMarker (`0x3B`). Swaps to another model, moved
objects, conditional rules and random chances are not applied to LOD.

After changing a swap file, run object LOD again and replace the installed output. The scan
report's `baseObjectSwaps` section lists each removed reference and each unsupported entry.

## Limits

- Animated and glowing effect layers, such as smoke and window glow, are not baked into
  static LOD.
- Generated models do not reproduce animated materials or materials that change with the
  view.
