using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PenguLOD.Desktop.Services;

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
            QualityPresetButtons(),
            status,
            Text("High, Balanced and Performance set what DynDOLOD and TexGen ship with: the High, Medium or Low mesh rules, the texture sizes for a 2160p, 1440p or 1080p screen, TexGen's light and DynDOLOD's occlusion quality. A saved preset holds every generation setting. Neither changes paths, worldspaces or the game source.", true),
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
        Help(Button(QualityPresets.High, () => ApplyQualityPreset(QualityPresets.High)), "DynDOLOD's High mesh rules, and the texture sizes TexGen and DynDOLOD choose for a 2160p screen."),
        Help(Button(QualityPresets.Balanced, () => ApplyQualityPreset(QualityPresets.Balanced)), "DynDOLOD's Medium mesh rules, and the texture sizes TexGen and DynDOLOD choose for a 1440p screen."),
        Help(Button(QualityPresets.Performance, () => ApplyQualityPreset(QualityPresets.Performance)), "DynDOLOD's Low mesh rules, and the texture sizes TexGen and DynDOLOD choose for a 1080p screen."),
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
        _presetStatus.Text = QualityPresets.Matching(_settings) is { } name
            ? $"The settings match {name}." : "The settings match no built-in preset.";
    }

    internal void ApplyQualityPreset(string name)
    {
        QualityPresets.Apply(_settings, QualityPresets.BuiltIn(name)); ScheduleSave();
        Notice($"{name} preset applied to Textures and Object LOD. Paths, worldspaces and what is generated were kept.");
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
