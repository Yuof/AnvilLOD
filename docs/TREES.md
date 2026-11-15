# Trees

Distant trees are billboards by default: flat images made by the
[texture stage](TEXTURES.md). The object LOD stage can also use 3D tree models in the
nearest LOD bands. They look better and cost more to draw.

All settings on this page are under **Object LOD → Trees**.

## Use 3D trees

1. Turn on **3D tree LOD**. This puts 3D trees in the nearest band, LOD 4.
2. Optionally turn on **3D trees at LOD 8** to extend them one band further. This costs
   noticeably more.
3. Keep **Tree billboards** on. Farther bands, and any tree without a usable 3D model, use
   billboards.
4. Run textures, then object LOD.

Tree LOD models installed by your mods are used first. With **Build missing 3D tree
models** on (the default), a tree without one gets a simplified copy of its full model.

## Control the cost

| Setting | Default | Effect |
| --- | --- | --- |
| LOD 4 tree triangle budget | 4000 | Maximum triangles per near tree model. 0 removes the limit. |
| LOD 8 tree triangle budget | 1600 | The same for the next band. |
| Tree geometry target (%) | 65 | Share of the source triangles to aim for. 100 reduces only as far as the budget requires. |
| Reduce redundant leaf cards | On | Removes overlapping leaf surfaces while limiting visible gaps. |
| LOD 4 / LOD 8 foliage coverage loss (%) | 1 / 3 | How much foliage thinning is allowed. 0 removes only fully hidden cards. |

A tree that cannot meet its budget within the coverage limits uses a billboard at that
level.

## Hybrid trunks

A hybrid tree has a 3D crown and a flat trunk, which is cheaper than a fully 3D tree.

- **Installed hybrid models:** turn on **Hybrid tree trunk textures** on the Textures page
  and **3D tree LOD** here, then run textures and object LOD.
- **Trees without one:** turn on **Author native hybrid trunks**. PenguLOD builds the trunk
  cards and their textures during object LOD. The tree needs separate trunk and foliage
  shapes; a tree that does not qualify gets an ordinary simplified model.

Check where trunk and crown meet, and the side views, in game before using hybrid trunks
across a whole tree mod.

## See what each tree uses

Click **Scan load order**, then open **Scan results → Tree coverage**. For every tree it
shows the model chosen at each level, its triangle count, and the reason whenever a
billboard was used instead. The same table is saved as `scan-report.json` in the run folder.

## Limits

- 3D tree LOD is static. It has no wind or other animation.
- Simplified trees keep their full-size textures, so less geometry does not mean smaller
  textures.
- When a mod changes a tree's textures per record or per season, the 3D version is built
  from the full model with those textures, which needs **Build missing 3D tree models**.
  If that is not possible, the tree uses a billboard and the report gives the reason.
