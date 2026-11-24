using PenguLOD.Textures;
using PenguLOD.Textures.Bc;
using Xunit;

namespace PenguLOD.Core.Tests;

public class TextureComputeTests
{
    [Fact]
    public void CpuModePreservesOutputAndRejectsInvalidDimensions()
    {
        var pixels=Enumerable.Repeat((byte)255,64).ToArray();var expected=Bc7Encoder.EncodeImage(pixels,4,4);
        using(TextureCompute.Enter("Cpu"))Assert.Equal(expected,Bc7Encoder.EncodeImage(pixels,4,4));
        Assert.Throws<ArgumentException>(()=>Bc7Encoder.EncodeImage(pixels,8,8));
        Assert.Throws<ArgumentException>(()=>TextureCompute.Enter("unknown"));
    }
    [Fact]
    public void AlphaAndOpaqueImagesDecodeCleanlyUnderVulkanScope()
    {
        var pixels=new byte[256*256*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=190;pixels[i+1]=80;pixels[i+3]=(byte)(i%32==0?0:255);}
        var expected=Bc7Encoder.EncodeImageCpu(pixels,256,256);
        using var compute=TextureCompute.Enter("Vulkan");
        var actual=Bc7Encoder.EncodeImage(pixels,256,256);
        Assert.Equal(expected.Length,actual.Length);
        var decA=new byte[64];var decB=new byte[64];
        for(int o=0;o<expected.Length;o+=16)
        {
            Bc7Decoder.DecodeBlock(expected.AsSpan(o,16),decA);
            Bc7Decoder.DecodeBlock(actual.AsSpan(o,16),decB);
            for(int k=0;k<64;k+=4) Assert.InRange(Math.Abs(decA[k+3]-decB[k+3]),0,4);
        }
    }
    [VulkanFact]
    public void RealVulkanDispatchPassesDecodedQualityCheckIncludingSoftwareDriver()
    {
        var result=TextureCompute.Benchmark(allowSoftware:true);
        Assert.True(result.Available,result.Recommendation);Assert.True(result.QualityPassed,result.ToString());
        Assert.True(result.CpuMilliseconds>0);Assert.True(result.VulkanMilliseconds>0);
    }
    private sealed class VulkanFactAttribute : FactAttribute
    {public VulkanFactAttribute(){if(Environment.GetEnvironmentVariable("PENGULOD_TEST_VULKAN")!="1")Skip="Set PENGULOD_TEST_VULKAN=1 after building the optional native compute backend.";}}
}
