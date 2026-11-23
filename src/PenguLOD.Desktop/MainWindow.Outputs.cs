using System.Text.Json;
using PenguLOD.Core.Pipeline;
using PenguLOD.Desktop.Models;
using PenguLOD.Desktop.Services;
using PenguLOD.Plugins;
using Avalonia.Controls;
using Avalonia;

namespace PenguLOD.Desktop;

public sealed partial class MainWindow
{
    private readonly TextBox _costFolder = new() { Name = "CostFolder" };
    private readonly TextBox _costBefore = new() { Name = "CostBefore" };
    private readonly TextBox _costAfter = new() { Name = "CostAfter" };
    private readonly TextBox _outputFolders = new() { Name = "OutputFolders", AcceptsReturn = true, MinHeight = 80 };
    private readonly TextBox _packageFolder = new() { Name = "PackageFolder" };
    private readonly TextBlock _outputSummary = Text("No output report yet.", true);
    private readonly DataGrid _costGroups = Table(("World", "World"), ("LOD", "Level"), ("Season", "Season"), ("Layer", "Layer"), ("Triangles", "Triangles"), ("Mesh groups", "MeshGroups"));
    private readonly DataGrid _costBlocks = Table(("Block", "File"), ("Bytes", "Bytes"), ("Triangles", "Triangles"), ("Mesh groups", "MeshGroups"), ("Unavailable", "Unavailable"));
    private readonly DataGrid _settingChanges = Table(("Setting", "Setting"), ("Before", "Before"), ("After", "After"));
    private readonly DataGrid _storage = Table(("Folder", "Name"), ("What it is", "Kind"), ("Size", "Size"), ("Generated", "Generated"), ("Replaced", "Replaced"), ("Version", "Version"), ("In", "Parent"));
    private readonly TextBlock _storageSummary = Text("Not measured yet.", true);
    private StorageItem[] _armedRemoval = [];
    internal IReadOnlyList<StorageItem> StorageItems { get; private set; } = [];
    private OutputUpdatePlan? _reviewedOutput;
    private object? _outputReport;
    private string[] _outputReportInputs = [];
    private Button? _publishOutput;

    private Control ReportsPage()
    {
        _publishOutput = Help(Primary("Publish reviewed package", PublishOutput), "Applies the reviewed update and keeps a backup of the previous package."); _publishOutput.IsEnabled = false;
        _outputFolders.PropertyChanged -= OutputFieldsChanged; _outputFolders.PropertyChanged += OutputFieldsChanged;
        _packageFolder.PropertyChanged -= OutputFieldsChanged; _packageFolder.PropertyChanged += OutputFieldsChanged;
        return Scroll(Stack(16, Heading("Reports & outputs"),
            Card("PERFORMANCE REPORTS", Stack(10,
                BrowseBox("Output folder to measure", _costFolder, false), Button("Measure output", MeasureOutput),
                BrowseBox("Before report", _costBefore, true), BrowseBox("After report", _costAfter, true), Help(Button("Compare cost reports", CompareOutputCosts), "Compares geometry and texture storage for matching output. These figures do not measure FPS or VRAM use."))),
            Card("OUTPUT MANAGEMENT", Stack(10, Labeled("Stage output folders · one per line", _outputFolders),
                Button("Add stage output", async () => { if (await Pick(false, "Select generated stage output") is { } p) _outputFolders.Text = ((_outputFolders.Text ?? "").TrimEnd() + "\n" + p).Trim(); }),
                BrowseBox("Package destination", _packageFolder, false),
                Row(Button("Verify output set", VerifyOutputs), Button("Preview package update", PreviewOutputUpdate), _publishOutput))),
            Card("RESULTS", Stack(10, _outputSummary, Button("Export report", ExportOutputReport), _costGroups, _costBlocks, _settingChanges)),
            Card("STORAGE", Stack(10,
                Text("What the stage output folders in your settings hold on disk, with their previous copies, unfinished builds and the cache's checkpoints.", true),
                Row(Button("Measure storage", MeasureStorage),
                    Help(Button("Restore selected", () => RestoreStorage(SelectedStorage())), "Puts the selected previous output back in place. The output it replaces is kept as a previous output, so this can be undone."),
                    Help(Button("Remove selected", () => RemoveStorage(SelectedStorage())), "Deletes the selected previous outputs, builds and checkpoints. An output itself is never removed here. Click twice to confirm.")),
                _storageSummary, _storage))));
    }

    private string StorageCache(DesktopSettings s) => RequestFactory.Optional(s.CacheFolder) is { } c ? RequestFactory.PathValue(c, s) : SettingsStore.CacheRoot;
    private StorageItem[] SelectedStorage() => _storage.SelectedItems.OfType<StorageRow>().Select(r => r.Item).ToArray();
    private void ShowStorage(IReadOnlyList<StorageItem> items)
    {
        StorageItems = items; _armedRemoval = []; _storage.ItemsSource = items.Select(i => new StorageRow(i)).ToArray();
        // Outputs always; the rest only when there is some.
        var parts = new (string Label, string[] Kinds)[] { ("Outputs", [StorageKinds.Output]), ("previous outputs", [StorageKinds.Previous]),
            ("unfinished builds", [StorageKinds.Build, StorageKinds.Unpublished]), ("left by other output folders", [StorageKinds.OtherPrevious, StorageKinds.OtherBuild]),
            ("cache checkpoints", [StorageKinds.Checkpoints]) }
            .Select(p => (p.Label, Bytes: items.Where(i => p.Kinds.Contains(i.Kind)).Sum(i => i.Bytes))).Where(p => p.Bytes > 0 || p.Label == "Outputs");
        _storageSummary.Text = items.Count == 0 ? "No stage output folder in the settings exists yet."
            : string.Join(" · ", parts.Select(p => p.Label + " " + StorageRow.Bytes(p.Bytes)));
    }

    internal Task MeasureStorage()
    {
        var s = _settings.Snapshot(); string[] outputs = RequestFactory.StageOutputs(s); string cache = StorageCache(s);
        return RunJob("Measure storage", s, (_, ct) => OutputStorage.Survey(outputs, cache, ct), (items, _, _) => ShowStorage(items), saveSettings: false);
    }

    /// <summary>The first call names what would be deleted; the same selection again deletes it.</summary>
    internal Task RemoveStorage(StorageItem[] selected)
    {
        var chosen = selected.Where(i => i.Removable).ToArray();
        if (chosen.Length == 0)
        { Notice("Select previous outputs, builds or checkpoints in the table first. An output itself is not removed here."); return Task.CompletedTask; }
        if (!_armedRemoval.SequenceEqual(chosen))
        {
            _armedRemoval = chosen;
            Notice($"Click Remove selected again to delete {chosen.Length} folder(s), {StorageRow.Bytes(chosen.Sum(i => i.Bytes))}. They are deleted, not moved to a recycle bin.");
            return Task.CompletedTask;
        }
        var s = _settings.Snapshot(); string[] outputs = RequestFactory.StageOutputs(s); string cache = StorageCache(s);
        return RunJob("Remove stored folders", s, (progress, ct) => (Removed: OutputStorage.Remove(chosen, progress, ct), Items: OutputStorage.Survey(outputs, cache, ct)),
            (result, _, _) => { ShowStorage(result.Items); Notice($"Removed {result.Removed} of {chosen.Length} folder(s). The log says why a folder was left."); }, saveSettings: false);
    }

    internal Task RestoreStorage(StorageItem[] selected)
    {
        if (selected is not [{ Restorable: true, Output: { } output } item])
        { Notice("Select one previous output in the table to restore."); return Task.CompletedTask; }
        var s = _settings.Snapshot(); string[] outputs = RequestFactory.StageOutputs(s); string cache = StorageCache(s);
        return RunJob("Restore previous output", s, (progress, ct) => (Replaced: StagedOutput.Restore(output, item.Path, progress, ct), Items: OutputStorage.Survey(outputs, cache, ct)),
            (result, _, _) =>
            {
                ShowStorage(result.Items);
                Notice($"Restored {Path.GetFileName(item.Path)} as {output}." + (result.Replaced is null ? "" : $" The output it replaced is kept in {result.Replaced}.")
                    + " Restart Skyrim before checking it. Running this stage again generates a new output.");
            }, saveSettings: false);
    }
    private Control BrowseBox(string label, TextBox box, bool file) => Stack(5, Text(label), box,
        Button("Browse " + label.ToLowerInvariant(), async () => { if (await Pick(file, label) is { } p) box.Text = p; }));
    private void OutputFieldsChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property == TextBox.TextProperty) InvalidateOutputPlan(); }
    private void InvalidateOutputPlan() { _reviewedOutput = null; if (_publishOutput is not null) _publishOutput.IsEnabled = false; }
    private string[] SelectedOutputs() => RequestFactory.Lines(_outputFolders.Text ?? "");

    internal Task MeasureOutput()
    {
        string folder = (_costFolder.Text ?? "").Trim();
        return RunJob("Output costs", _settings.Snapshot(), (progress, ct) =>
        {
            string saved = Path.Combine(folder, OutputCostReport.FileName);
            return OutputCostReport.Analyze(folder, File.Exists(saved) ? OutputCostReport.Read(saved).Settings : null, progress, ct);
        }, (report, run, _) =>
        {
            string file = Path.Combine(run, OutputCostReport.FileName); report.Write(file);
            _costAfter.Text = file; _outputReport = report; _outputReportInputs = [folder]; _outputSummary.Text = report.Summary();
            _costGroups.ItemsSource = report.Groups; _costBlocks.ItemsSource = report.LargestBlocks; _settingChanges.ItemsSource = null;
        }, saveSettings: false);
    }
    internal Task CompareOutputCosts()
    {
        string before = _costBefore.Text ?? "", after = _costAfter.Text ?? "";
        return RunJob("Compare output costs", _settings.Snapshot(), (_, _) => OutputCostReport.Compare(OutputCostReport.Read(before), OutputCostReport.Read(after)),
            (report, _, _) => { _outputReport = report; _outputReportInputs = [before, after]; _outputSummary.Text = report.Summary(); _costGroups.ItemsSource = report.CommonGroups; _costBlocks.ItemsSource = report.CommonBlocks; _settingChanges.ItemsSource = report.Settings; }, saveSettings: false);
    }
    internal Task VerifyOutputs()
    {
        var folders = SelectedOutputs(); InvalidateOutputPlan();
        return RunJob("Verify output set", _settings.Snapshot(), (_, ct) => OutputManagement.Verify(folders, ct),
            (report, _, _) => { _outputReport = report; _outputReportInputs = folders; _outputSummary.Text = report.Summary(); }, saveSettings: false);
    }
    internal Task PreviewOutputUpdate()
    {
        var folders = SelectedOutputs(); string destination = _packageFolder.Text ?? ""; InvalidateOutputPlan();
        return RunJob("Plan output update", _settings.Snapshot(), (_, ct) => OutputManagement.Plan(folders, destination, ct),
            (plan, _, _) => { _outputReport = plan; _outputReportInputs = folders.Append(destination).ToArray(); _outputSummary.Text = plan.Summary();
                if (folders.SequenceEqual(SelectedOutputs()) && destination == _packageFolder.Text) { _reviewedOutput = plan; _publishOutput!.IsEnabled = plan.CanPublish; } }, saveSettings: false);
    }
    private Task PublishOutput()
    {
        var plan = _reviewedOutput ?? throw new InvalidOperationException("Preview the update first."); InvalidateOutputPlan();
        return RunJob("Publish output package", _settings.Snapshot(), (progress, ct) => OutputManagement.Publish(plan, progress, ct),
            (previous, _, _) => _outputSummary.Text = "Published package: " + plan.Destination + "\nPrevious output: " + (previous ?? "none"), saveSettings: false);
    }
    private Task ReviewOutputScope()
    {
        var snapshot = _settings.Snapshot();
        return RunJob("Review output scope", snapshot, (_, ct) => GenerationScope.ReviewOutputs(RequestFactory.OutputScope(snapshot), ct),
            (plan, _, _) => { _pipelineSummary.Text = plan.Summary(); Notice(plan.Summary()); }, saveSettings: false);
    }
    private async Task ExportOutputReport()
    {
        if (_outputReport is null) { Notice("Create a report or preview an update first."); return; }
        if (await SavePath("Export output report", "PenguLOD-report.json", "json") is { } path)
        {
            OutputManagement.ValidateReportPath(path, _outputReportInputs);
            File.WriteAllText(path, JsonSerializer.Serialize(_outputReport, PersistentPipeline.Json));
        }
    }
}

/// <summary>A row of the storage table.</summary>
public sealed class StorageRow(StorageItem item)
{
    public StorageItem Item { get; } = item;
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Item.Path)) is { Length: > 0 } name
        ? Item.Kind == StorageKinds.Checkpoints ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Item.Path)) + "/" + name : name : Item.Path;
    public string Kind => Item.Kind;
    public string Size => Bytes(Item.Bytes);
    public string Generated => Item.Generated?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public string Replaced => Item.Replaced?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public string Version => Item.Version ?? "";
    public string Parent => System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(Item.Path)) ?? "";
    public static string Bytes(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824.0:N1} GiB" : bytes >= 1048576 ? $"{bytes / 1048576.0:N0} MiB" : $"{Math.Ceiling(bytes / 1024.0):N0} KiB";
}
