using System.Buffers.Binary;
using PenguLOD.Core.Pipeline;

namespace PenguLOD.Plugins;

/// <summary>R16_UNORM DDS and bounds understood by Community Shaders TerrainShadows.</summary>
public static class TerrainHeightmap
{
    public sealed record Result(string Path,byte[] Bytes,int Width,int Height,float Minimum,float Maximum);
    /// <param name="perWorker">Gives each worker thread its own height sampler; rows are then sampled in parallel.
    /// Without it <paramref name="sample"/> is called from one thread.</param>
    public static Result Export(string world,int minX,int minY,int maxX,int maxY,int maxSize,Func<float,float,float?> sample,CancellationToken ct=default,
        Func<Func<float,float,float?>>? perWorker=null, float? fallbackHeight=null, Action<string>? warn=null)
    {
        int cellsX=checked(maxX-minX+1),cellsY=checked(maxY-minY+1);
        if(cellsX<=0 || cellsY<=0 || maxSize is <1 or >16384)throw new ArgumentException("Invalid heightmap bounds/size.");
        float reduction=Math.Max(1,Math.Max(cellsX*32f,cellsY*32f)/maxSize);
        int width=Math.Max(1,(int)MathF.Ceiling(cellsX*32/reduction)),height=Math.Max(1,(int)MathF.Ceiling(cellsY*32/reduction));
        byte[] bytes=new byte[checked(148+width*height*2)];
        void U(int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset,4),value);
        U(0,0x20534444);U(4,124);U(8,0x100f);U(12,(uint)height);U(16,(uint)width);U(20,(uint)width*2);
        U(76,32);U(80,4);U(84,0x30315844);U(108,0x1000);U(128,56);U(132,3);U(140,1); // DXGI R16_UNORM, texture2D, one slice
        var rowLow=new float[height];var rowHigh=new float[height];
        long clamped=0, filled=0;
        void Row(int y,Func<float,float,float?> heights)
        {
            ct.ThrowIfCancellationRequested();
            float lo=float.MaxValue,hi=float.MinValue;
            long rowClamped=0, rowFilled=0;
            for(int x=0;x<width;x++)
            {
                float wx=(minX+(x+.5f)*cellsX/width)*4096,wy=(maxY+1-(y+.5f)*cellsY/height)*4096;
                float? sampled=heights(wx,wy);
                if(sampled is null)
                {
                    if(fallbackHeight is not {} fb || !float.IsFinite(fb))
                        throw new InvalidDataException($"Incomplete heightmap coverage for {world} at ({wx},{wy}). Disable heightmap export for nonrectangular coverage, or supply existing terrain for the gaps.");
                    sampled=fb; rowFilled++;
                }
                float h=sampled.Value;
                if(!float.IsFinite(h))throw new InvalidDataException("Heightmap contains a non-finite height.");
                if(h is < -262136f or >262136f)
                {
                    if(warn is null) throw new InvalidDataException("Heightmap exceeds the supported xLODGen height range.");
                    h=Math.Clamp(h,-262136f,262136f); rowClamped++;
                }
                lo=Math.Min(lo,h);hi=Math.Max(hi,h);
                ushort value=(ushort)Math.Clamp(MathF.Round((h+262136)/524272*65535),0,65535);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(148+(y*width+x)*2,2),value);
            }
            rowLow[y]=lo;rowHigh[y]=hi;
            if(rowClamped>0) Interlocked.Add(ref clamped,rowClamped);
            if(rowFilled>0) Interlocked.Add(ref filled,rowFilled);
        }
        if(perWorker is null) for(int y=0;y<height;y++)Row(y,sample);
        else OrderedParallel.For(height,perWorker,(y,heights)=>Row(y,heights),ct);
        if(clamped>0) warn?.Invoke($"{world}: clamped {clamped:N0} terrain-shadow heightmap sample(s) to the supported [-262136, 262136] height range.");
        if(filled>0) warn?.Invoke($"{world}: filled {filled:N0} uncovered heightmap sample(s) at the default land height.");
        float lo=rowLow.Min(),hi=rowHigh.Max();
        int bottom=(int)MathF.Floor(lo/8),top=Math.Max(bottom+1,(int)MathF.Ceiling(hi/8));
        string path=$"textures\\terrain\\{world}\\{world}.Terrain.HeightMap.{minX}.{minY}.{maxX}.{maxY}.{bottom}.{top}.dds";
        return new(path,bytes,width,height,lo,hi);
    }
}
