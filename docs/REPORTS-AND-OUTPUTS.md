# Reports and outputs

The **Reports & outputs** page measures a generation, compares two of them, checks that a
set of output folders is intact, and combines them into one package to install. None of
these tools change your generation settings or your mod manager.

Keep a generation you are happy with in its own folders and generate experiments into
different ones.

## Measure a generation

1. Set **Output folder to measure**.
2. Click **Measure output**. The folder is only read.
3. Click **Export report** to save a copy.

The report lists triangles and mesh groups by worldspace, LOD level, season and layer, the
largest blocks, and the size and format of the textures in the folder. A successful object
LOD run also writes this report to its output as `PenguLOD.costs.json`.

These numbers help find expensive areas. They do not predict frame rate, and texture sizes
are sizes on disk, not video memory.

## Compare two generations

1. Choose a **Before report** and an **After report**.
2. Click **Compare cost reports**.

Negative numbers mean fewer triangles, mesh groups or bytes. Only blocks present in both
reports are compared; added and removed blocks are listed separately.

For a fair frame-rate comparison in game, change one setting at a time and use the same
place, direction, weather and resolution.

## Verify output folders

1. Add the folders under **Stage output folders**. Include the texture, grass and terrain
   folders that the object LOD used.
2. Click **Verify output set**.

This reads every file and can take several minutes. It reports changed or missing files,
missing dependencies and duplicate stages. It checks the folders you select, not what your
mod manager has deployed.

Output made by older versions can be measured but not verified. Generate into a new folder
to get verifiable output.

## Build one package to install

1. Add one complete output folder for each stage you want in the package.
2. Set **Package destination** to a new folder, or to a package made here before.
3. Click **Preview package update** and read what will be replaced.
4. Click **Publish reviewed package**.
5. Install the package with your mod manager and enable the generated plugins.

The stage folders are not changed. A stage you leave out of an update stays in the package.
The update is refused if it would pair object LOD with textures it was not built against,
or if the package contains files PenguLOD does not recognise.

`meta.ini` and `SKSE/Plugins/PenguLOD.ini` in an output or package folder are yours.
PenguLOD never replaces them.

## Generate only some worldspaces

Generated plugins and some texture sheets are shared by all worldspaces, so an existing
output folder cannot be narrowed to fewer worldspaces than it already holds.

- **For a quick test:** generate into a new, empty folder and install it as a separate mod
  with the complete one disabled.
- **To keep using the same folder:** under **Overview → Run options**, click **Review
  output scope**, then turn on **Keep existing worlds and levels**. The run then covers the
  worldspaces already in the folder as well as the ones you selected.

## Remove old copies

Each time a stage is replaced, the old folder is kept beside it with a `.previous-` suffix.
**Previous outputs to keep** sets how many are kept. To list the extra copies and any
unfinished build folders, run this while nothing is generating:

```sh
PenguLOD clean --input "/path/to/output folder"
```

Add `--apply` to delete them.

## Command line

```sh
PenguLOD cost-report --input "Object LOD" --report costs.json
PenguLOD compare-costs --before old-costs.json --after new-costs.json --report comparison.json
PenguLOD verify-output --input "Textures" --input "Object LOD" --report verification.json
PenguLOD plan-update --input "Textures" --input "Object LOD" --output "Install Package" --report plan.json
PenguLOD publish-update --plan plan.json
PenguLOD plan-generation --settings preset.json --report scope.json
PenguLOD pipeline --settings preset.json --expand-output-scope
```

Reports and plans contain paths from your computer. Read them before sharing.
