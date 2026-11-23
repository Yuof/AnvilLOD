# User guide

LOD is the simpler scenery Skyrim draws beyond the loaded area around the player. PenguLOD
generates it in four stages. Each stage writes to its own folder, which you install as a mod.

| Stage | What it produces |
| --- | --- |
| Grass cache | Saved grass placements |
| Textures | LOD textures and the images used for distant trees and grass |
| Terrain | Distant landscape and water |
| Object LOD | Distant objects, trees and grass, plus any generated plugins |

Run only the stages you need. The stages always run in the order above.

## 1. Install PenguLOD

Download a package from [Releases](https://github.com/opteron-x86/PenguLOD/releases) and
extract it into a folder of its own. Keep the extracted files together.

- **Windows:** run `win-x64\PenguLOD.Desktop.exe`.
- **Linux:** run `linux-x64/run-pengulod.sh`. To add a menu entry, run
  `linux-x64/install-desktop.sh` once the folder is where you want to keep it.
- **Linux AppImage:** run `chmod +x PenguLOD-*.AppImage`, then start the file. If it cannot
  mount itself, add `--appimage-extract-and-run`.

To check a download, put `SHA256SUMS` from the release page beside it and run
`sha256sum --ignore-missing -c SHA256SUMS`. On Windows, compare the output of
`Get-FileHash <file> -Algorithm SHA256` with the line in `SHA256SUMS`.

To update, extract the new version into a new folder. Your settings and output folders are
kept.

## 2. Prepare your load order

1. Finish installing and sorting your mods, including any LOD models, billboards and
   resource packs made for them.
2. Disable the object LOD mod you generated before, including its plugins. Keep the source
   mods and resource packs enabled.
3. Create one empty folder per stage, outside the game and mod folders, for example
   `PenguLOD-output/Grass`, `Textures`, `Terrain` and `LOD`.

To try different settings without losing a generation you like, use a second set of folders.

## 3. Choose the game source

Open **Game & resources** and choose the game edition and the source.

- **Mod Organizer 2:** browse to the instance folder, the one that holds
  `ModOrganizer.ini`. Click **Read profiles** and choose your profile.
- **Data folder / Vortex:** choose the game's Data folder and its `plugins.txt`. Only mods
  deployed to that folder are read.
- **Amethyst Mod Manager:** choose **Mod Organizer 2** and browse to Amethyst's staging folder
  for the game, the one that holds `mods`, `overwrite` and `profiles` (by default
  `~/Games/Amethyst/Skyrim Special Edition`). Click **Read profiles** and choose a profile.
  The game folder and Wine prefix come from Amethyst's settings, and Amethyst does not need
  to deploy first. See [Amethyst Mod Manager](SETTINGS.md#amethyst-mod-manager).

On Linux, if the mod manager stores Windows paths such as `C:\...`, open **Advanced source
paths** and set **Wine / Proton prefix** to the game's `pfx` folder.

## 4. Choose stages, folders and worldspaces

Open **Overview**.

1. Tick the stages to run and set the output folder for each.
2. Set the worldspaces for terrain and object LOD. Leave the field blank for every
   worldspace with LOD settings, or click **Choose…** to tick them from a list of your load
   order.

The grass cache has its own worldspace field. Textures always cover the whole load order.

## 5. Choose settings

Each stage has a **Settings** button. The defaults are a reasonable start. For a first run:

- On **Overview**, under **Presets**, click **Balanced**. It sets DynDOLOD's Medium mesh rules and the texture sizes TexGen and DynDOLOD choose for your game's resolution. **High** and **Performance** use the High and Low rules.
- Turn on **3D tree LOD** if you want 3D trees in the nearest LOD band. It costs more to draw.
- Leave **Grass billboard LOD** on only if you have a grass cache, from this run or an
  earlier one. To use a cache you already have installed as a mod, turn off **Use generated
  grass cache for LOD** on **Grass cache**; PenguLOD then reads the cache from your mods.
- Leave **Build missing object LOD models** off unless you want objects that have no LOD
  model to get one. It adds scenery and drawing cost.
- Turn on seasonal and PBR options only if your setup uses them.

Hover over any control for a short explanation, or look it up in the
[settings reference](SETTINGS.md). Use **Preferences → Export settings…** to save a
configuration you want to return to.

## 6. Generate

1. Click **Check setup** and fix anything it reports.
2. Click **Run selected stages**.
3. When the run finishes, read the warnings. **Open run folder** shows the full log and
   reports.

If a run fails or you cancel it, fix the reported problem and run the same selection again.
Finished stages and finished terrain tiles are reused. The output from your previous run is
left in place until a stage completes.

## 7. Install the output

1. Add each output folder to your mod manager as a separate mod. Replace the previous
   output of the same stage; do not merge old and new files.
2. Order the mods so that the texture output wins conflicts with the source textures, and
   the object LOD output wins conflicts with the texture output.
3. Enable the generated plugins. Which plugins are written depends on your settings and
   load order, and the run summary lists them. Put `Occlusion.esp` last.
4. For dynamic LOD and the in-game quality controls, install the PenguLOD SKSE plugin from
   the same release and pick your Skyrim version in its installer.

## 8. Check in game

Restart Skyrim after replacing generated files. To compare two generations, stand in the
same place, facing the same way, at the same time of day and in the same weather. Look at:

- the boundary where full models change to LOD, and each LOD distance beyond it
- shorelines and terrain seams
- tree shapes and trunk alignment
- grass and ground brightness in sun and under cloud

The SKSE plugin's in-game menu can hide individual layers. Show them all again before
judging a new generation, because saved layer settings survive an update.

## Running again

With **Reuse completed stages** on, a stage is skipped when its settings, your load order
and its output files have not changed.

- Enabling, disabling or updating any mod rebuilds every selected stage.
- Playing the game, or running Pandora, Nemesis or FNIS, does not.
- Choosing a different output folder starts that run from the beginning.

Each time a stage is replaced, the old folder is kept beside the new one with a
`.previous-` suffix. **Previous outputs to keep** under **Run options** sets how many are
kept; the default is 1. A kept folder is removed 14 days after it was replaced, when a run
starts or PenguLOD opens. **Days to keep previous outputs** changes that; 0 keeps the
folder until a newer one takes its place. To see what is kept, remove it or go back to a
previous output, see [Storage](REPORTS-AND-OUTPUTS.md#storage).

## What to rerun after a change

| You changed | Rerun |
| --- | --- |
| Object or tree LOD detail, brightness, atlases or model choices | Object LOD |
| Billboard size, texture brightness or baked materials | Textures, then object LOD |
| Terrain texture size, brightness, water or heightmaps | Terrain, then object LOD if it uses the generated terrain |
| Grass mods, landscape or grass cache settings | Grass cache, then object LOD. Textures too if grass models or textures changed |
| Object placement, tree removers or other plugins | Object LOD, and any other stage that reads the changed mods |
| Seasonal swaps | Every stage those swaps affect |

See [troubleshooting](TROUBLESHOOTING.md) if something goes wrong.
