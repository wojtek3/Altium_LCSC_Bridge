using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LcscBridge.Core;

namespace LcscBridge.App;

public partial class MainWindow : Window
{
    private readonly IAltiumIntegration _integration;
    private LibraryStore _store = null!;
    private string _libraryRoot = null!;
    private CancellationTokenSource? _searchCancellation;
    private bool _nativeOperationActive;
    private bool _shutdownRequested;
    private LibraryItem? Selected => Results.SelectedItem as LibraryItem;

    public MainWindow(IAltiumIntegration integration)
    {
        _integration = integration ?? throw new ArgumentNullException(nameof(integration));
        InitializeComponent();
        Configure(LoadConfiguredLibraryRoot() ?? DefaultLibraryRoot());
        BridgeLog.Info("startup", $"EasyEDA Loader 0.3.0 opened in Altium. Library root: {_libraryRoot}");
        Loaded += async (_, _) => await RefreshAsync();
    }

    public void ActivateExisting()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void RequestShutdown()
    {
        _shutdownRequested = true;
        CancelCatalogOperation();
        Close();
    }

    private void Configure(string root)
    {
        _libraryRoot = Path.GetFullPath(root);
        _store = new LibraryStore(_libraryRoot);
        RemoveExpiredFailures();
        SaveSettings();
        StatusText.Text = $"Library: {_libraryRoot}";
    }

    private async Task RefreshAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = new CancellationTokenSource();
        try
        {
            var manifests = await _store.SearchAsync(SearchBox.Text, _searchCancellation.Token);
            Results.ItemsSource = manifests.Select(x => new LibraryItem(x)).ToList();
            StatusText.Text = $"{manifests.Count} cached revision(s) — {_libraryRoot}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await Task.Delay(180);
        await RefreshAsync();
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = Selected;
        if (item is null) return;
        var m = item.Manifest.Metadata;
        PartTitle.Text = item.DisplayName;
        PartDescription.Text = m.Description;
        SymbolPreview.Text = string.IsNullOrWhiteSpace(m.SymbolReference) ? "Symbol name missing" : m.SymbolReference;
        FootprintPreview.Text = string.IsNullOrWhiteSpace(m.FootprintName) ? "Footprint name missing" : m.FootprintName;
        PartDetails.Text = $"Manufacturer: {m.Manufacturer}\nSupplier part: {m.SupplierPartNumber}\nPackage: {m.Package}\nSource: {m.Source} {m.SourceRevision}\nRevision: {item.Manifest.RevisionId}";
        UpdateActionState();
        DatasheetButton.IsEnabled = Uri.TryCreate(m.DatasheetUrl, UriKind.Absolute, out _);
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportAsync(false);
    private async void ImportPlace_Click(object sender, RoutedEventArgs e) => await ImportAsync(true);

    private async Task ImportAsync(bool place)
    {
        var target = _integration.CaptureActiveSchematic();
        try
        {
            EnsureIntegrationAvailable();
            var source = new ImportSource(EmptyToNull(SchLibPath.Text), EmptyToNull(PcbLibPath.Text), EmptyToNull(IntLibPath.Text));
            var metadata = new ComponentMetadata(Manufacturer.Text.Trim(), Mpn.Text.Trim(), SupplierPart.Text.Trim(), Description.Text.Trim(),
                Package.Text.Trim(), Datasheet.Text.Trim(), "local-import", "1", SymbolReference.Text.Trim(), FootprintName.Text.Trim());
            StatusText.Text = "Importing and checksumming native libraries…";
            var manifest = await _store.ImportAsync(source, metadata);
            await InstallManifestAsync(manifest, place, target, Guid.NewGuid().ToString("N"));
            await RefreshAsync();
            Tabs.SelectedIndex = 0;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Place_Click(object sender, RoutedEventArgs e) => await QueueSelectedAsync(true);
    private async void Install_Click(object sender, RoutedEventArgs e) => await QueueSelectedAsync(false);

    private async Task QueueSelectedAsync(bool place)
    {
        if (Selected is null) return;
        try
        {
            EnsureIntegrationAvailable();
            await InstallManifestAsync(Selected.Manifest, place, _integration.CaptureActiveSchematic(), Guid.NewGuid().ToString("N"));
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task InstallManifestAsync(LibraryManifest manifest, bool place, string? target, string requestId)
    {
        _nativeOperationActive = true;
        UpdateActionState();
        try
        {
            var progress = OperationProgress();
            if (place) Hide();
            await _integration.InstallAndPlaceAsync(manifest, place, target, requestId, progress);
            var manifestPath = Path.Combine(manifest.LibraryDirectory, "manifest.json");
            if (File.Exists(manifestPath)) await _store.ReconcileAsync(manifestPath);
            StatusText.Text = place
                ? $"Placement started for {manifest.Metadata.SymbolReference}. Escape cancels placement; Altium undo remains available."
                : $"Installed {manifest.PartId}, revision {manifest.RevisionId}.";
        }
        finally
        {
            _nativeOperationActive = false;
            if (!IsVisible) Show();
            Activate();
            UpdateActionState();
        }
    }

    private Progress<AltiumOperationProgress> OperationProgress() => new(p =>
    {
        StatusText.Text = $"{p.Stage} — {p.Detail}";
        BridgeLog.Info("operation", $"{p.Stage}: {p.Detail}", p.RequestId);
    });

    private void UpdateActionState()
    {
        var available = !_nativeOperationActive && _integration.IsAvailable(out _);
        PlaceButton.IsEnabled = available && Selected?.Manifest.IsPlaceable == true;
        InstallButton.IsEnabled = available && Selected?.Manifest.IsPlaceable == true;
    }

    private void EnsureIntegrationAvailable()
    {
        if (!_integration.IsAvailable(out var diagnostic)) throw new InvalidOperationException(diagnostic);
    }

    private void Datasheet_Click(object sender, RoutedEventArgs e)
    {
        var url = Selected?.Manifest.Metadata.DatasheetUrl;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        try { await _store.RebuildIndexAsync(); await RefreshAsync(); StatusText.Text = "Index rebuilt from manifests."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the persistent LCSC library folder", InitialDirectory = _libraryRoot };
        if (dialog.ShowDialog(this) == true) { Configure(dialog.FolderName); _ = RefreshAsync(); }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(BridgeLog.LogDirectory);
        Process.Start(new ProcessStartInfo(BridgeLog.LogDirectory) { UseShellExecute = true });
    }

    private void BrowseSch_Click(object sender, RoutedEventArgs e) => ChooseFile(SchLibPath, "Altium schematic library (*.SchLib)|*.SchLib");
    private void BrowsePcb_Click(object sender, RoutedEventArgs e) => ChooseFile(PcbLibPath, "Altium PCB library (*.PcbLib)|*.PcbLib");
    private void BrowseInt_Click(object sender, RoutedEventArgs e) => ChooseFile(IntLibPath, "Altium integrated library (*.IntLib)|*.IntLib");

    private void ChooseFile(TextBox target, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName;
    }

    private void SaveSettings()
    {
        var settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltiumLcscBridge");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "settings.ini"),
            $"[Bridge]{Environment.NewLine}protocol=in-process{Environment.NewLine}libraryRoot={_libraryRoot}{Environment.NewLine}host=EasyEDA-Loader{Environment.NewLine}", Encoding.Unicode);
    }

    private void RemoveExpiredFailures()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(14);
        var operations = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltiumLcscBridge", "Operations");
        var stagingRoot = Path.Combine(_libraryRoot, ".staging");
        if (Directory.Exists(stagingRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(stagingRoot, "online-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) >= cutoff) continue;
                    var id = Path.GetFileName(directory)["online-".Length..];
                    var journal = Path.Combine(operations, id + ".json");
                    if (!File.Exists(journal)) continue;
                    var text = File.ReadAllText(journal);
                    if (!text.Contains("\"state\": \"failed\"", StringComparison.Ordinal) &&
                        !text.Contains("\"state\": \"interrupted\"", StringComparison.Ordinal)) continue;
                    Directory.Delete(directory, true);
                    BridgeLog.Info("maintenance", "Removed failed staging data retained for more than 14 days.", id);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { BridgeLog.Error("maintenance", ex); }
            }
        }
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AltiumLcscBridge", "Bridge", "online-requests");
        if (!Directory.Exists(legacy)) return;
        foreach (var failed in Directory.EnumerateFiles(legacy, "*.processing.failed"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(failed) >= cutoff) continue;
                var values = File.ReadAllLines(failed, Encoding.Unicode).Select(x => x.Split('=', 2))
                    .Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
                if (values.TryGetValue("stagingDirectory", out var staging) && IsInside(staging, stagingRoot) && Directory.Exists(staging))
                    Directory.Delete(staging, true);
                File.Delete(failed);
                BridgeLog.Info("maintenance", "Removed a legacy failed request retained for more than 14 days.", values.GetValueOrDefault("requestId"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { BridgeLog.Error("maintenance", ex); }
        }
    }

    private static bool IsInside(string child, string parent)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(child));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static string DefaultLibraryRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LCSC");

    private static string? LoadConfiguredLibraryRoot()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltiumLcscBridge", "settings.ini");
        if (!File.Exists(path)) return null;
        try
        {
            var line = File.ReadLines(path, Encoding.Unicode).FirstOrDefault(x => x.StartsWith("libraryRoot=", StringComparison.OrdinalIgnoreCase));
            var value = line?["libraryRoot=".Length..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void ShowError(Exception ex)
    {
        BridgeLog.Error("ui", ex);
        StatusText.Text = ex.Message;
        MessageBox.Show(this, ex.Message, "EasyEDA Loader", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private Brush ThemeBrush(string key) => (Brush)FindResource(key);

    private sealed record LibraryItem(LibraryManifest Manifest)
    {
        public string DisplayName => string.Join(" — ", new[] { Manifest.Metadata.SupplierPartNumber, Manifest.Metadata.ManufacturerPartNumber, Manifest.Metadata.Description }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
