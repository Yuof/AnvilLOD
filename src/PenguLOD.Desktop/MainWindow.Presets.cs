using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PenguLOD.Desktop.Models;
using PenguLOD.Desktop.Services;
using PenguLOD.Plugins;

namespace PenguLOD.Desktop;

public sealed partial class MainWindow
{
    private TextBlock? _presetStatus;
    private ComboBox? _savedPresets;
    private TextBox? _presetName;

    private Control PresetsCard()
    {
        var status = _presetStatus = Text("", true);
        var saved = _savedPresets = new ComboBox { Name = "SavedPresets", MinWidth = 240, PlaceholderText = "Saved presets", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var name = _presetName = new TextBox { Name = "PresetName", Width = 240, PlaceholderText = "Name for a new preset", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        RefreshSavedPresets(); UpdatePresetStatus();
        return Card("Presets", Stack(12,
            QualityPresetButtons(
                Help(Button(QualityPresets.DynDolodDefaults, () => ApplyQualityPreset(QualityPresets.DynDolodDefaults)),
                    "The state of a new DynDOLOD and TexGen: Balanced, and what the two have ticked. Tree, dynamic and occlusion on; grass LOD, 3D trees, LOD 32, candles, glow rules and window glow off."),
                Help(Choice("Screen", nameof(DesktopSettings.PresetScreen), QualityPresetScreen.Choices),
                    "The screen the presets size textures for. From the game reads the resolution in the game's own settings (SkyrimPrefs.ini, SkyrimCustom.ini or SSE Display Tweaks), as TexGen does.")),
            status,
            Text("High, Balanced and Performance set what DynDOLOD and TexGen ship with: DynDOLOD's High, Medium or Low mesh rules, the texture sizes the two choose for the screen, TexGen's light and DynDOLOD's occlusion quality. They leave what is generated as it is; DynDOLOD defaults also sets that. A saved preset holds every generation setting. None of them changes paths, worldspaces or the game source.", true),
            Row(saved,
                Button("Load preset", () => LoadSavedPreset(Selected())),
                Button("Delete preset", () => DeleteSavedPreset(Selected()))),
            Row(name, Help(Button("Save current settings", () => SavePreset(name.Text ?? "")), "Saves the generation settings under this name. An existing preset of the same name is replaced.")),
            Row(Help(Button("Load preset file…", async () => { if (await Pick(true, "Load a PenguLOD preset") is { } file) LoadPresetFile(file); }),
                    "A preset saved here, or an exported settings file. From a settings file only the generation settings are taken."),
                Button("Open presets folder", () => { Directory.CreateDirectory(_store.PresetsFolder); Open(_store.PresetsFolder); }),
                Help(Button("Import DynDOLOD settings…", ChooseDynDolodPreset),
                    "Reads a preset saved by DynDOLOD or TexGen, from Edit Scripts\\DynDOLOD\\Presets in the DynDOLOD folder, and sets the PenguLOD settings that mean the same."))));

        string Selected() => saved.SelectedItem as string ?? throw new ArgumentException("Choose a saved preset first.");
    }

    /// <summary>The three built-in presets, followed by a page's own buttons. They set texture settings as well as Object LOD options.</summary>
    private Control QualityPresetButtons(params Control[] more) => Row(
    [
        Help(Button(QualityPresets.High, () => ApplyQualityPreset(QualityPresets.High)), "DynDOLOD's High mesh rules, with the texture sizes TexGen and DynDOLOD choose for the screen."),
        Help(Button(QualityPresets.Balanced, () => ApplyQualityPreset(QualityPresets.Balanced)), "DynDOLOD's Medium mesh rules, with the texture sizes TexGen and DynDOLOD choose for the screen."),
        Help(Button(QualityPresets.Performance, () => ApplyQualityPreset(QualityPresets.Performance)), "DynDOLOD's Low mesh rules, with the texture sizes TexGen and DynDOLOD choose for the screen."),
        .. more,
    ]);

    private void RefreshSavedPresets(string? select = null)
    {
        if (_savedPresets is null) return;
        var names = QualityPresets.Saved(_store.PresetsFolder);
        _savedPresets.ItemsSource = names;
        _savedPresets.SelectedItem = select is null ? null : names.FirstOrDefault(n => n.Equals(select, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdatePresetStatus()
    {
        if (_presetStatus is null) return;
        var heights = _detectedHeight is { } detected ? QualityPresets.ScreenHeights.Prepend(detected) : QualityPresets.ScreenHeights;
        _presetStatus.Text = QualityPresets.Matching(_settings, heights) is { } match
            ? $"The settings match {match.Name} for a {match.ScreenHeight}p screen." : "The settings match no built-in preset.";
    }

    // The game's resolution as last read for a preset, so the status line can name a screen that is not one of the listed heights.
    private int? _detectedHeight;

    /// <summary>The screen height the presets are made for: the chosen one, or the game's own resolution.</summary>
    internal (int Height, string? Source) PresetScreenHeight()
    {
        if (QualityPresetScreen.Height(_settings.PresetScreen) is { } chosen) return (chosen, null);
        var found = GameResolution.Detect(RequestFactory.Game(_settings))
            ?? throw new InvalidOperationException("The game's resolution was not found in SkyrimPrefs.ini, SkyrimCustom.ini or SSE Display Tweaks. Choose a screen beside the presets.");
        _detectedHeight = found.Height;
        return (found.Height, found.Source);
    }

    internal void ApplyQualityPreset(string name)
    {
        var (height, source) = PresetScreenHeight();
        bool shipped = name == QualityPresets.DynDolodDefaults;
        var changes = QualityPresets.Apply(_settings, shipped ? QualityPresets.Shipped(height) : QualityPresets.BuiltIn(name, height));
        ScheduleSave(); UpdatePresetStatus();
        _messages.Enqueue($"{name} applied for a {height}p screen" + (source is null ? "" : $" (read from {source})") + (changes.Count == 0 ? ": nothing to change." : ":"));
        foreach (string change in changes) _messages.Enqueue("  " + change);
        Notice($"{name} applied for a {height}p screen" + (source is null ? "" : $", read from {source}") + $": {changes.Count} settings changed"
            + (changes.Count > 0 ? ", listed in the activity log. " : ". ") + (shipped ? "Paths and worldspaces were kept." : "Paths, worldspaces and what is generated were kept."));
    }

    internal void SavePreset(string name)
    {
        string file = QualityPresets.SavedFile(_store.PresetsFolder, name);
        name = Path.GetFileNameWithoutExtension(file);
        QualityPresets.Write(file, QualityPresets.Capture(_settings, name));
        RefreshSavedPresets(name);
        Notice($"Preset \"{name}\" saved to {file}");
    }

    internal void LoadSavedPreset(string name) => LoadPresetFile(QualityPresets.SavedFile(_store.PresetsFolder, name));

    internal void LoadPresetFile(string file)
    {
        var preset = QualityPresets.Read(file);
        QualityPresets.Apply(_settings, preset); ScheduleSave();
        Notice($"Preset \"{preset.Name}\" applied. Paths, worldspaces and the game source were kept.");
    }

    internal void DeleteSavedPreset(string name)
    {
        File.Delete(QualityPresets.SavedFile(_store.PresetsFolder, name));
        RefreshSavedPresets();
        Notice($"Preset \"{name}\" deleted.");
    }

    private async Task ChooseDynDolodPreset()
    {
        // Open the file dialog in DynDOLOD's preset folder when the primary resource folder is the DynDOLOD folder.
        IStorageFolder? start = null;
        if (RequestFactory.Optional(_settings.DynDolodFolder) is { } folder)
        {
            string presets = Path.Combine(RequestFactory.PathValue(folder, _settings), "Edit Scripts", "DynDOLOD", "Presets");
            if (Directory.Exists(presets)) start = await StorageProvider.TryGetFolderFromPathAsync(presets);
        }
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a DynDOLOD or TexGen preset", SuggestedStartLocation = start,
            FileTypeFilter = [new FilePickerFileType("DynDOLOD presets") { Patterns = ["*.ini", "*.INI"] }],
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } file) ImportDynDolodSettings(file);
    }

    internal DynDolodImport.Result ImportDynDolodSettings(string file)
    {
        var result = DynDolodImport.Apply(_settings, file); ScheduleSave();
        string name = Path.GetFileName(file);
        _messages.Enqueue($"Imported {result.Kind} preset {file}");
        foreach (string change in result.Changes) _messages.Enqueue("  " + change);
        if (result.NotImported.Count > 0) _messages.Enqueue("  No PenguLOD setting for: " + string.Join(", ", result.NotImported));
        string text = $"{result.Kind} preset {name} imported: {result.Changes.Count} settings changed, {result.Unchanged} already matched.";
        if (result.NotImported.Count > 0) text += $" {result.NotImported.Count} of its choices have no PenguLOD setting.";
        text += " The activity log lists them.";
        if (result.MeshRules > 0)
            text += $" Its {result.MeshRules:N0} mesh rules were not imported, because PenguLOD reads DynDOLOD's rule files itself. To use the preset's own rules, choose it as Custom rule file on Game & resources.";
        Notice(text);
        return result;
    }
}
