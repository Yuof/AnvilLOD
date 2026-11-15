using System.Runtime.InteropServices;
using System.Text.Json;
using PenguLOD.Core.World;
using PenguLOD.Desktop.Services;
using PenguLOD.Plugins;
using PenguLOD.Textures;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PenguLOD.Desktop;

public sealed partial class MainWindow
{
    private readonly DataGrid _textureGenResults = Table(("Kind", "Kind"), ("Output", "Output"), ("Status", "Status"), ("Details", "Detail"));
    private readonly TextBlock _textureGenSummary = Text("No textures generated in this session.", true);
    private TextureGenerationResult? _lastTextureGeneration;

    private Control TextureGenerationPage() => Scroll(Stack(16,
        Heading("Textures"),
        Card("Output", Stack(12,
            PathField("Texture output", nameof(_settings.TextureGenOutput)),
            Check("Use generated textures for LOD", nameof(_settings.UseGeneratedTextures)),
            Button("Open texture output", () => Open(RequestFactory.RequiredPath(_settings.TextureGenOutput, "a texture output folder", _settings))))),
        Card("Texture types", Stack(10,
            Row(Check("Stitched object textures", nameof(_settings.TextureGenStitched)), Check("Tree billboards", nameof(_settings.TextureGenTrees)), Check("Grass billboards", nameof(_settings.TextureGenGrass))),
            Help(Check("Hybrid tree trunk textures", nameof(_settings.TextureGenTrunks)), "For installed hybrid tree LOD models. Requires matching render resources and 3D tree LOD enabled on Object LOD."),
            Help(Check("Rendered object textures (experimental)", nameof(_settings.TextureGenObjects)), "Updates installed object LOD textures where supported. Otherwise, keeps the supplied texture pair."))),
        Card("Quality", Stack(12,
            Help(Check("HD front and side billboards", nameof(_settings.TextureGenHdViews)), "Uses separate front and side images for crossed cards."),
            Check("Size tree textures for the largest placed reference", nameof(_settings.TextureGenReferenceScale)),
            Row(Help(Number("Unplaced tree resolution scale", nameof(_settings.TextureGenUnplacedTreeScale), .01m, 100, .1m), "Changes texture resolution for trees without placed references. Does not change tree size."),
                Help(Choice("Grass texture layout", nameof(_settings.TextureGenGrassTextures), ["Auto", "Basic", "Packed"]), "Auto detects packed grass textures. Basic and Packed override detection.")),
            Number("Complex grass detection threshold", nameof(_settings.TextureGenComplexThreshold), .001m, .5m, .01m),
            Row(TextureSizeChoice("Stitch base size", nameof(_settings.TextureGenBaseSize), [64, 128, 256, 512, 1024]),
                TextureSizeChoice("Maximum billboard size", nameof(_settings.TextureGenMaxSize), [32, 64, 128, 256, 512, 1024, 2048])),
            Row(Help(Number("Tree world units per pixel", nameof(_settings.TextureGenUnitsPerPixel), .25m, 128, .25m), "Lower values increase texture resolution."),
                Help(Number("Grass world units per pixel", nameof(_settings.TextureGenGrassUnitsPerPixel), .25m, 128, .25m), "Lower values increase texture resolution. Grass is small, so it needs a much lower value than trees."),
                Help(Number("Trunk world units per pixel", nameof(_settings.TextureGenTrunkUnitsPerPixel), .25m, 128, .25m), "For hybrid tree trunk textures. Lower values increase texture resolution.")),
            Number("Brightness multiplier", nameof(_settings.TextureGenBrightness), .1m, 4, .05m),
            Row(Number("Minimum tree height", nameof(_settings.TextureGenMinTreeHeight), 0, 65536, 16), Number("Minimum grass height", nameof(_settings.TextureGenMinGrassHeight), 0, 65536, 4)),
            Field("Model path filter", nameof(_settings.TextureGenFilter), "For previews of matching models. Leave blank for a complete run. Stitched textures are always included."))),
        new Expander { Header = "Material calibration", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Stack(12,
            Check("Bake source normal maps", nameof(_settings.BakeSourceNormals)),
            Check("Calibrate PBR albedo", nameof(_settings.BakePbr)),
            Row(Number("PBR scale", nameof(_settings.BakePbrScale), .1m, 4, .05m), Number("PBR gamma", nameof(_settings.BakePbrGamma), .1m, 4, .05m)),
            Number("PBR occlusion strength", nameof(_settings.BakeAo), 0, 1, .05m),
            Help(Row(Number("Tree ambient light", nameof(_settings.TextureGenTreeAmbient), 0, 4, .05m), Number("Tree direct light", nameof(_settings.TextureGenTreeDirect), 0, 4, .05m)),
                "Trees and trunks are lit from above. Ambient reaches every surface; direct light adds to the surfaces that face up."),
            Row(Number("Trunk ambient light", nameof(_settings.TextureGenTrunkAmbient), 0, 4, .05m), Number("Trunk direct light", nameof(_settings.TextureGenTrunkDirect), 0, 4, .05m)),
            Help(Row(Number("Grass ambient light", nameof(_settings.TextureGenGrassAmbient), 0, 4, .05m), Number("Grass direct light", nameof(_settings.TextureGenGrassDirect), 0, 4, .05m)),
                "Grass is lit softly from the front and above, on both sides of a blade.")) },
        Card("Results", Stack(12, _textureGenSummary, _textureGenResults, Button("Preview selected texture", PreviewGeneratedTexture)))));

    private Control TextureSizeChoice(string label, string property, decimal[] sizes)
    {
        var input = new ComboBox { Name = property, ItemsSource = sizes, MinWidth = 160 };
        Bind(input, SelectingItemsControl.SelectedItemProperty, _settings, property);
        return Labeled(label, input);
    }

    internal Task RunTextureGeneration(bool thenLod)
    {
        var snapshot = _settings.Snapshot();
        return RunPersistentPipeline(thenLod ? "Textures and LOD" : "Texture generation", snapshot,
            s => RequestFactory.Stages(s, textures: true, lod: thenLod));
    }

    private void PreviewGeneratedTexture()
    {
        if (_lastTextureGeneration is not { Published: true } result || _textureGenResults.SelectedItem is not TextureGenerationItem { Status: "Written" or "Retained" } item)
            throw new InvalidOperationException("Select a written texture from a successful run first.");
        var decoded = TextureImages.Decode(File.ReadAllBytes(GamePath.ToFileSystemPath(result.OutputFolder, item.Output)));
        var bitmap = new WriteableBitmap(new PixelSize(decoded.Width, decoded.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (var buffer = bitmap.Lock())
            for (int y = 0; y < decoded.Height; y++) Marshal.Copy(decoded.Pixels, y * decoded.Width * 4, buffer.Address + y * buffer.RowBytes, decoded.Width * 4);
        var preview = new Window
        {
            Title = $"{item.Output} · {decoded.Width} × {decoded.Height}", Width = 760, Height = 650,
            Background = Brush.Parse("#343b44"), Content = new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(20) }
        };
        preview.Closed += (_, _) => bitmap.Dispose();
        preview.Show(this);
    }
}
