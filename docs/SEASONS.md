# Seasons

With [Seasons of Skyrim](https://github.com/powerof3/SeasonsOfSkyrim) installed, PenguLOD
can generate winter, spring, summer and autumn versions of terrain, tree, object and grass
LOD.

## Generate seasonal LOD

1. Make sure the seasonal swap files (`Data/Seasons/*_WIN.ini`, `_SPR.ini`, `_SUM.ini`,
   `_AUT.ini`) are in your enabled mods.
2. Turn on **Seasonal LOD** on Object LOD. It is the same switch as **Generate seasonal
   terrain from Seasons of Skyrim swaps** on Terrain.
3. For seasonal grass, set **Seasons** on the Grass cache page to **Auto** or **On**.
4. Run textures, terrain and object LOD, in that order. Tree billboards must be generated
   with the same profile.

A season whose swap files change nothing gets no seasonal files. The game then shows the
normal LOD, which is identical.

## Automatic winter swaps

Seasons of Skyrim can work out winter swaps while the game runs. PenguLOD only reads swaps
that have been written to an INI. Let Seasons of Skyrim save `MainFormSwap_WIN.ini`, and
make sure that file is in the selected profile, before you generate.

## Swaps that name a missing record

A swap line that names a record not in your load order is skipped with a warning, as the
game does. Turn on **Strict seasonal swaps** to stop the run instead. The grass cache stage
always treats such a line as an error.

## Seasonal trees without a billboard

A swap to a tree that has no billboard is reported as a warning, and those trees keep
their normal LOD in that season. Generate tree billboards with the same profile to add
it. **Strict seasonal swaps** stops the run instead.

## Check the result

- `seasonal-output.json` in the run folder counts the terrain tiles, object blocks, tree
  blocks and grass cells written for each season.
- If the run reports missing seasonal tree billboards, run textures again.
- In game, compare nearby and distant trees and ground in each season you use.

## Limits

- Terrain shape and water level do not change with the season.
- Where plugins have no landscape and existing terrain LOD is used, that area keeps its
  default appearance unless seasonal terrain textures are installed.
- Snow drawn by a shader while the game runs is not part of LOD.
