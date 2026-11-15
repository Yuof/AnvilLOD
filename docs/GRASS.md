# Grass cache

The grass cache stage works out where grass grows and saves it. The game does not need to
be running. To load the result, the game needs a grass cache mod such as NGIO; PenguLOD does
not change game or shader settings.

Grass caching supports Skyrim SE and AE, not VR.

## Generate a cache

1. Choose the game source and profile on **Game & resources**.
2. On **Grass cache**, set **Grass cache output** to an empty folder of its own. PenguLOD
   creates the `grass` folder inside it.
3. Leave **Worldspaces** at `all`, or click **Choose…** to pick some. This selection is
   separate from the terrain and object LOD worldspaces.
4. Choose **Placement**: **Smooth** (the default) or **Vanilla**, which follows the game's
   own placement.
5. Click **Generate grass cache**, or tick **Grass cache** on **Overview** to run it with
   the other stages.
6. Install the output folder as a mod and disable the previous cache mod. Do not merge two
   caches.

A missing plugin or mod folder stops the run before it starts. Your source files are never
changed.

## Use the cache for grass LOD

Turn on **Use generated grass cache for LOD** so object LOD reads the new cache. A run that
includes both stages does this automatically.

Generate the cache again after changing the plugin order, grass mods, landscape or placement
settings.

## Preview a small area

1. Pick one worldspace.
2. Turn on **Small-area preview** and set the cell coordinates and radius. Radius 0 is one
   cell; radius 1 is a 3 × 3 area.
3. Use a separate output folder.

A preview cannot replace a full cache or feed a full LOD run.

## Advanced settings

**Advanced grass settings** has a `GrassControl.ini` override, a minimum grass size, writer
threads and an external FasterNGIO executable. The bundled generator rejects grass inside
objects on the CPU; GPU rejection needs a compatible external build. The
[settings reference](SETTINGS.md#grass-cache) describes each option.

## Windows disk use

On Windows, put the grass output on the same drive as most of your mods. PenguLOD links
source files into a temporary folder; files on another drive are copied instead, which
takes time and space.
