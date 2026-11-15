# Command line

The command-line program runs the same stages as the desktop, from settings you save in the
desktop. It is `PenguLOD` on Linux, `PenguLOD.exe` on Windows, and
`PenguLOD-<version>-x86_64.AppImage --cli` for the AppImage.

`PenguLOD --help` lists every command and option.

## Run a generation

1. In the desktop, set everything up, then click **Preferences → Export settings…** and
   save a preset.
2. Run the stages you want:

```sh
PenguLOD pipeline --settings my-preset.json
PenguLOD pipeline --settings my-preset.json --stages grass,textures,terrain,lod
```

Without `--stages`, the stages ticked in the desktop are run. Run the same command again to
resume after a failure or cancellation.

To run one stage:

```sh
PenguLOD grass --settings my-preset.json
PenguLOD texgen --settings my-preset.json
PenguLOD terrain --settings my-preset.json
PenguLOD generate --settings my-preset.json
```

The preset defines the generation. Options that would change it, such as `--output` or
`--worldspace`, are refused next to `--settings`; change the preset in the desktop instead.
Relative paths in a preset are resolved against the preset's own folder.

## Run options

These can be added to a `--settings` or `--request` run:

| Option | Effect |
| --- | --- |
| `--stages <list>` | Stages to run: `grass`, `textures`, `terrain`, `lod` |
| `--threads <n>` | Worker limit. 0 uses the available processors minus one |
| `--cache-folder <path>` | Where saved jobs and reusable work are kept |
| `--no-resume` | Run every stage again instead of reusing finished ones |
| `--no-preflight` | Skip the setup check |
| `--verify-source-contents` | Read every source file in full when deciding what can be reused |
| `--expand-output-scope` | Keep the worldspaces and levels already in an output folder |
| `--keep-previous <n>` | Previous copies of each output to keep, 0 to 100 (default 1) |
| `--texture-compute Cpu\|Auto\|Vulkan` | Texture encoder |
| `--wine-prefix <path>` | Wine or Proton prefix used to resolve Windows paths |
| `--report <file>` | Write a JSON report of the run |

## Look before generating

```sh
PenguLOD worldspaces --settings my-preset.json
PenguLOD scan --settings my-preset.json --report scan.json
PenguLOD diagnose --settings my-preset.json --report diagnostics.json
```

- `worldspaces` lists every worldspace in the load order with its ID, name, plugin, parent
  and LOD grid size. It also accepts `--mo2` and `--profile`, or `--data` and `--plugins`,
  in place of a preset.
- `scan` reports which objects and trees can get LOD, without writing any output.
- `diagnose` runs the setup check. Add `--deep-scan` to check models and billboards,
  `--verify-outputs` to check every generated file, or `--inspect-asset <path>` to see
  which mod supplies a file. It exits with 1 when it finds a blocking problem.

## Saved jobs

```sh
PenguLOD export-job --settings my-preset.json --stages textures,terrain,lod --output job.json
PenguLOD pipeline --request job.json --report run.json
PenguLOD job-status --job-folder /path/to/cache/jobs/job-id
```

`export-job` writes a self-contained request file, which `pipeline --request` runs.
`--request` and `--settings` cannot be combined.

## Other commands

| Command | Purpose |
| --- | --- |
| `cost-report`, `compare-costs`, `verify-output`, `plan-update`, `publish-update`, `plan-generation`, `clean` | [Reports and outputs](REPORTS-AND-OUTPUTS.md) |
| `lodmaker`, `author`, `convert-lod`, `fix-textures` | The [tools](TOOLS.md), from the command line |
| `compute-info` | Test optional Vulkan texture encoding on this machine |

`scan` and `generate` can also be run from options alone, without a preset, for example
`--mo2 <instance> --profile <name> --output <folder> --worldspace Tamriel`. Numeric options
take plain numbers, such as `--grass-density 15`.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | `diagnose` found a blocking problem, or `clean` could not remove a folder |
| 2 | Invalid arguments or a failed generation |
| 130 | Cancelled |
