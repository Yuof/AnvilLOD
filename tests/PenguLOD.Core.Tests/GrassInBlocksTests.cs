using PenguLOD.Plugins;

namespace PenguLOD.Core.Tests;

public sealed class GrassInBlocksTests
{
    [Theory]
    [InlineData(true, false, false, true)]   // no Grass Optimizations: grass stays in the blocks
    [InlineData(true, false, true, true)]    // the option does nothing without the feature
    [InlineData(true, true, false, true)]    // the feature alone does not take grass out (the default)
    [InlineData(true, true, true, false)]    // the feature and the option: left to the feature
    [InlineData(false, true, false, false)]  // grass LOD off
    public void Grass_leaves_the_blocks_only_when_Grass_Optimizations_is_installed_and_the_option_is_on(bool grassLod, bool feature, bool option, bool expected)
        => Assert.Equal(expected, ScanPipeline.GrassInBlocks(grassLod, feature, option));

}
