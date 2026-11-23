using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using PenguLOD.Core;
using PenguLOD.Core.Pipeline;
using PenguLOD.Core.World;

namespace PenguLOD.Plugins;

internal sealed record TerrainTileReceipt(TerrainTileResult Tile, OutputSnapshot Output,
    IReadOnlyList<QuadKey> FallbackTiles, IReadOnlyList<TerrainMaterialReport> Materials, IReadOnlyList<string> Warnings);

/// <summary>
/// Private job cache. Complete mesh/diffuse/normal sets are committed together and verified while copying.
/// While a terrain stage is unfinished the cache holds each finished tile's files. Once the stage has published, the
/// output holds the same files, so <see cref="Trim"/> keeps only the small tile receipts and a repair reads the files
/// from the published output.
/// </summary>
internal sealed class TerrainTileCache
{
    /// <summary>In a cache folder whose tile files were removed by <see cref="Trim"/>.</summary>
    internal const string TrimmedMarker="trimmed";
    private const string Entry="entry.json";
    private readonly string _root;
    private readonly string? _published;
    private readonly bool _reuse;
    private readonly CancellationToken _ct;
    private readonly IProgress<string>? _progress;
    /// <param name="published">The stage's output folder. A tile whose files are no longer in the cache is restored from it.</param>
    internal TerrainTileCache(string folder,string fingerprint,bool reuse,IProgress<string>? progress,CancellationToken ct,string? published=null)
    {
        _root=Path.Combine(folder,OutputSnapshot.HashText(ProductInfo.Version+"|terrain-tiles-v1|"+fingerprint));
        _reuse=reuse;_ct=ct;_progress=progress;_published=published;
        // Resolve against the job folder so existing links at any cache level are rejected.
        OutputSnapshot.Resolve(folder,Path.GetFileName(_root)+"/probe");
        Directory.CreateDirectory(_root);
        Sweep(folder,_root,progress);
        // This run stores tile files again; they are removed when it has published.
        File.Delete(Path.Combine(_root,TrimmedMarker));
    }

    /// <summary>
    /// Removes the tile files of every cache in <paramref name="folder"/> and keeps the receipts. Call it when the stage's
    /// output is published and recorded: the output then holds the same files. Returns the bytes freed.
    /// </summary>
    internal static long Trim(string folder,IProgress<string>? progress=null,CancellationToken ct=default)
    {
        if(!Directory.Exists(folder) || new DirectoryInfo(folder).LinkTarget is not null)return 0;
        long bytes=0;int files=0,failed=0;
        foreach(var root in new DirectoryInfo(folder).EnumerateDirectories())
        {
            if(!IsHash(root.Name) || root.LinkTarget is not null || File.Exists(Path.Combine(root.FullName,TrimmedMarker)))continue;
            int failedBefore=failed;
            foreach(var tile in root.EnumerateDirectories())
            {
                if(tile.LinkTarget is not null)continue;
                foreach(var entry in tile.EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        // A link is removed itself; what it points to is not touched.
                        if(entry is DirectoryInfo dir)
                        {
                            if(dir.LinkTarget is null)
                                foreach(var file in dir.EnumerateFiles("*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}))
                                { bytes+=file.Length;files++; }
                            dir.Delete(dir.LinkTarget is null);
                        }
                        else if(!entry.Name.Equals(Entry,StringComparison.Ordinal))
                        {
                            if(entry.LinkTarget is null){bytes+=((FileInfo)entry).Length;files++;}
                            entry.Delete();
                        }
                    }
                    catch(Exception e) when(e is IOException or UnauthorizedAccessException){failed++;}
                }
            }
            if(failed==failedBefore)File.WriteAllBytes(Path.Combine(root.FullName,TrimmedMarker),[]);
        }
        if(files>0)progress?.Report($"Removed {files:N0} terrain tile checkpoint files ({bytes/1073741824.0:N1} GiB): the published terrain holds the same files.");
        if(failed>0)progress?.Report($"Could not remove {failed:N0} terrain tile checkpoint files; they are removed after the next terrain run.");
        return bytes;
    }

    /// <summary>
    /// Removes tile caches of earlier inputs (each is a full copy of the tiles) and entries a stopped process left half
    /// written. The job folder is locked by its pipeline, so nothing else is using them.
    /// </summary>
    internal static int Sweep(string folder,string current,IProgress<string>? progress)
    {
        int removed=0;
        void Remove(string dir)
        {
            if(new DirectoryInfo(dir).LinkTarget is not null)return;
            try{Directory.Delete(dir,true);removed++;}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){progress?.Report("Could not remove old terrain tile cache "+dir+": "+e.Message);}
        }
        foreach(var dir in Directory.EnumerateDirectories(folder))
            if(IsHash(Path.GetFileName(dir)) && !Path.GetFullPath(dir).Equals(Path.GetFullPath(current),StringComparison.Ordinal))Remove(dir);
        foreach(var dir in Directory.EnumerateDirectories(current,"*.pending-*"))Remove(dir);
        if(removed>0)progress?.Report($"Removed {removed} outdated or unfinished terrain tile cache folder(s).");
        return removed;
    }
    private static bool IsHash(string name)=>name.Length==64 && name.All(Uri.IsHexDigit);
    private string Folder(TerrainTileResult tile) => OutputSnapshot.Resolve(_root,
        OutputSnapshot.HashText($"{tile.Worldspace}|{tile.Level}|{tile.X}|{tile.Y}|{tile.Season}")+"/"+Entry);

    internal TerrainTileReceipt? Restore(TerrainTileResult tile,IReadOnlyCollection<string> expectedFiles,string destination)
    {
        if(!_reuse)return null;
        string metadata=Folder(tile);
        if(!File.Exists(metadata))return null;
        try
        {
            _ct.ThrowIfCancellationRequested();
            var receipt=JsonSerializer.Deserialize<TerrainTileReceipt>(File.ReadAllText(metadata),PersistentPipeline.Json);
            if(receipt is null || receipt.Tile is null || receipt.Output?.Files is null || receipt.FallbackTiles is null || receipt.Materials is null || receipt.Warnings is null
                || receipt.Tile.Worldspace!=tile.Worldspace || receipt.Tile.Level!=tile.Level || receipt.Tile.X!=tile.X || receipt.Tile.Y!=tile.Y || receipt.Tile.Season!=tile.Season
                || !receipt.Output.Files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedFiles))
                throw new InvalidDataException("Tile checkpoint does not match the requested files.");
            var sources=new List<(string Source,string Relative,string Hash)>();
            foreach(var (relative,hash) in receipt.Output.Files)
            {
                string source=OutputSnapshot.Resolve(Path.GetDirectoryName(metadata)!,relative);
                if(_published is not null && !File.Exists(source))
                {
                    // After publication the cache keeps only the receipt, and the published output is the copy to verify.
                    source=OutputSnapshot.Resolve(_published,relative);
                    if(!File.Exists(source))return null;
                }
                sources.Add((source,relative,hash));
            }
            foreach(var (source,relative,hash) in sources)Copy(source,OutputSnapshot.Resolve(destination,relative),hash);
            return receipt;
        }
        catch(Exception e) when(e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { _progress?.Report($"Rebuilding terrain tile {tile.Worldspace} LOD{tile.Level} ({tile.X},{tile.Y}) {tile.Season}: checkpoint verification failed ({e.Message}).");return null; }
    }

    internal void Store(TerrainTileReceipt receipt,string source)
    {
        string final=Path.GetDirectoryName(Folder(receipt.Tile))!;
        string pending=final+".pending-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(pending);
        try
        {
            foreach(var (relative,hash) in receipt.Output.Files)
                Copy(OutputSnapshot.Resolve(source,relative),OutputSnapshot.Resolve(pending,relative),hash);
            _ct.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(pending,Entry),JsonSerializer.Serialize(receipt,PersistentPipeline.Json));
            if(Directory.Exists(final))Directory.Delete(final,true);
            Directory.Move(pending,final);
        }
        finally {if(Directory.Exists(pending))Directory.Delete(pending,true);}
    }

    private void Copy(string source,string destination,string expected)
    {
        _ct.ThrowIfCancellationRequested();Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input=File.OpenRead(source);using var output=File.Create(destination);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer=ArrayPool<byte>.Shared.Rent(128*1024);
        try
        {
            int length;while((length=input.Read(buffer))>0)
            { _ct.ThrowIfCancellationRequested();hash.AppendData(buffer,0,length);output.Write(buffer,0,length); }
            if(!Convert.ToHexString(hash.GetHashAndReset()).Equals(expected,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Terrain tile content hash changed: "+source);
        }
        finally {ArrayPool<byte>.Shared.Return(buffer);}
    }
}
