using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using LcscBridge.Core;

namespace LcscBridge.App;

public partial class MainWindow : Window
{
    private LibraryStore _store = null!;
    private FileAltiumBridge _bridge = null!;
    private string _libraryRoot = null!;
    private CancellationTokenSource? _searchCancellation;
    private readonly DispatcherTimer _responseTimer;
    private LibraryItem? Selected => Results.SelectedItem as LibraryItem;

    public MainWindow()
    {
        InitializeComponent();
        Configure(LoadConfiguredLibraryRoot() ?? DefaultLibraryRoot());
        BridgeLog.Info("startup", $"Companion 0.2.4 started. Library root: {_libraryRoot}");
        _responseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _responseTimer.Tick += ResponseTimer_Tick;
        _responseTimer.Start();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            var active = _bridge.FindActiveOnlineOperation();
            if (active is not null)
            {
                _activeOnlineRequestId = active.RequestId;
                _lastOnlineStage = active.Stage;
                SetOnlineImportRunning(true);
                StatusText.Text = $"Recovered active online import {active.RequestId}: {active.Stage}.";
            }
        };
    }

    private void Configure(string root)
    {
        _libraryRoot = Path.GetFullPath(root);
        _store = new LibraryStore(_libraryRoot);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _bridge = new FileAltiumBridge(Path.Combine(local, "AltiumLcscBridge", "Bridge"), _libraryRoot);
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
        PlaceButton.IsEnabled = item.Manifest.IsPlaceable;
        InstallButton.IsEnabled = item.Manifest.IsPlaceable;
        DatasheetButton.IsEnabled = Uri.TryCreate(m.DatasheetUrl, UriKind.Absolute, out _);
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportAsync(false);
    private async void ImportPlace_Click(object sender, RoutedEventArgs e) => await ImportAsync(true);

    private async Task ImportAsync(bool place)
    {
        try
        {
            var source = new ImportSource(EmptyToNull(SchLibPath.Text), EmptyToNull(PcbLibPath.Text), EmptyToNull(IntLibPath.Text));
            var metadata = new ComponentMetadata(Manufacturer.Text.Trim(), Mpn.Text.Trim(), SupplierPart.Text.Trim(), Description.Text.Trim(),
                Package.Text.Trim(), Datasheet.Text.Trim(), "local-import", "1", SymbolReference.Text.Trim(), FootprintName.Text.Trim());
            StatusText.Text = "Importing and checksumming native libraries…";
            var manifest = await _store.ImportAsync(source, metadata);
            if (place)
            {
                if (!_bridge.IsPlacementAdapterAvailable(out var diagnostic)) throw new InvalidOperationException(diagnostic);
                var id = await _bridge.QueueImportAsync(manifest, true);
                StatusText.Text = $"Placement request {id} queued. Keep Altium's LCSC Bridge listener open.";
            }
            else StatusText.Text = $"Imported {manifest.PartId}, revision {manifest.RevisionId}.";
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
            if (!_bridge.IsPlacementAdapterAvailable(out var diagnostic)) throw new InvalidOperationException(diagnostic);
            var id = await _bridge.QueueImportAsync(Selected.Manifest, place);
            StatusText.Text = $"Altium request {id} queued ({(place ? "install and place" : "install")}).";
        }
        catch (Exception ex) { ShowError(ex); }
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
            $"[Bridge]{Environment.NewLine}protocol=1{Environment.NewLine}libraryRoot={_libraryRoot}{Environment.NewLine}appPath={Environment.ProcessPath}{Environment.NewLine}",
            Encoding.Unicode);
    }

    private async void ResponseTimer_Tick(object? sender, EventArgs e)
    {
        await PollOnlineOperationAsync();
        var responseDirectory = Path.Combine(_bridge.BridgeRoot, "responses");
        foreach (var path in Directory.EnumerateFiles(responseDirectory, "*.response"))
        {
            var claimed = path + ".processing";
            try
            {
                File.Move(path, claimed);
                var values = File.ReadAllLines(claimed).Select(x => x.Split('=', 2))
                    .Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
                if (string.Equals(values.GetValueOrDefault("operation"), "onlineCreate", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessOnlineCreateResponseAsync(values);
                    File.Delete(claimed);
                    await RefreshAsync();
                    continue;
                }
                var requestId = values.GetValueOrDefault("requestId", string.Empty);
                BridgeLog.Info("response", $"Altium placement response status={values.GetValueOrDefault("status", "unknown")}: {values.GetValueOrDefault("message", "")}", requestId);
                var pendingPath = Path.Combine(_bridge.BridgeRoot, "pending", requestId + ".pending");
                var manifestPath = File.Exists(pendingPath) ? await File.ReadAllTextAsync(pendingPath) : values.GetValueOrDefault("manifestPath");
                if (values.GetValueOrDefault("modified") == "true" && !string.IsNullOrWhiteSpace(manifestPath))
                    await _store.ReconcileAsync(manifestPath);
                StatusText.Text = values.GetValueOrDefault("status") == "ok"
                    ? values.GetValueOrDefault("message", "Altium request completed.")
                    : "Altium: " + values.GetValueOrDefault("message", "Request failed.");
                File.Delete(claimed);
                if (File.Exists(pendingPath)) File.Delete(pendingPath);
                await RefreshAsync();
            }
            catch (IOException) { }
            catch (Exception ex)
            {
                BridgeLog.Error("response", ex);
                if (File.Exists(claimed))
                {
                    var failedValues = File.ReadAllLines(claimed).Select(x => x.Split('=', 2))
                        .Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
                    if (string.Equals(failedValues.GetValueOrDefault("operation"), "onlineCreate", StringComparison.OrdinalIgnoreCase))
                    {
                        var failedId = failedValues.GetValueOrDefault("requestId", string.Empty);
                        CatalogValidationText.Text = $"Import failed at publishing (request {failedId}): {ex.Message}";
                        CatalogValidationText.Foreground = System.Windows.Media.Brushes.DarkRed;
                        StatusText.Text = CatalogValidationText.Text;
                        try { await _bridge.WriteOnlineOperationStatusAsync(failedId, "failed", "publishing", ex.Message); }
                        catch (Exception statusError) { BridgeLog.Error("status", statusError, failedId); }
                        if (string.Equals(_activeOnlineRequestId, failedId, StringComparison.OrdinalIgnoreCase)) _activeOnlineRequestId = null;
                        _lastOnlineStage = null;
                        SetOnlineImportRunning(false);
                        await RefreshCatalogActionsAsync();
                        File.Delete(claimed);
                        continue;
                    }
                    File.Move(claimed, path, true);
                }
                StatusText.Text = "Could not process Altium response: " + ex.Message;
            }
        }
    }

    private async Task ProcessOnlineCreateResponseAsync(IReadOnlyDictionary<string, string> values)
    {
        var requestId = values.GetValueOrDefault("requestId", string.Empty);
        BridgeLog.Info("online-response", $"Native-library response status={values.GetValueOrDefault("status", "unknown")}; errorCode={values.GetValueOrDefault("errorCode", "")}; failedStage={values.GetValueOrDefault("failedStage", "")}; staging={values.GetValueOrDefault("stagingDirectory", "")}: {values.GetValueOrDefault("message", "")}", requestId);
        if (values.GetValueOrDefault("protocol") != FileAltiumBridge.OnlineProtocolVersion.ToString())
            throw new InvalidDataException("The online-import response uses an incompatible protocol. Install adapter 0.2.4 and restart Altium.");
        if (!string.Equals(values.GetValueOrDefault("status"), "ok", StringComparison.OrdinalIgnoreCase))
        {
            var stage = values.GetValueOrDefault("failedStage", "unknown stage");
            var code = values.GetValueOrDefault("errorCode", "ALTIUM_ERROR");
            var message = values.GetValueOrDefault("message", "Unknown adapter error.");
            CatalogValidationText.Text = $"Import failed at {stage} (request {requestId}, {code}): {message}";
            CatalogValidationText.Foreground = System.Windows.Media.Brushes.DarkRed;
            StatusText.Text = CatalogValidationText.Text;
            await _bridge.WriteOnlineOperationStatusAsync(requestId, "failed", stage, code + ": " + message);
            if (string.Equals(_activeOnlineRequestId, requestId, StringComparison.OrdinalIgnoreCase)) _activeOnlineRequestId = null;
            _lastOnlineStage = null;
            SetOnlineImportRunning(false);
            await RefreshCatalogActionsAsync();
            return;
        }

        var schPath = Path.GetFullPath(RequiredResponse(values, "schLibPath"));
        var pcbPath = Path.GetFullPath(RequiredResponse(values, "pcbLibPath"));
        var staging = Path.GetDirectoryName(schPath) ?? throw new InvalidDataException("The adapter returned an invalid staging path.");
        if (!IsWithin(staging, Path.Combine(_libraryRoot, ".staging")) ||
            !string.Equals(Path.GetDirectoryName(pcbPath), staging, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The adapter response escaped the online-import staging directory.");

        var sourcePayload = Path.Combine(staging, "easyeda-source.json");
        var metadata = new ComponentMetadata(
            values.GetValueOrDefault("manufacturer", string.Empty),
            values.GetValueOrDefault("manufacturerPartNumber", string.Empty),
            values.GetValueOrDefault("supplierPartNumber", string.Empty),
            values.GetValueOrDefault("description", string.Empty),
            values.GetValueOrDefault("package", string.Empty),
            values.GetValueOrDefault("datasheetUrl", string.Empty),
            "EasyEDA/LCSC",
            values.GetValueOrDefault("providerRevision", string.Empty),
            RequiredResponse(values, "symbolReference"),
            RequiredResponse(values, "footprintName"));

        await _bridge.WriteOnlineOperationStatusAsync(requestId, "running", "publishing", "Publishing verified libraries to the persistent cache.");
        StatusText.Text = $"Request {requestId}: publishing verified native libraries…";
        var manifest = await _store.ImportAsync(new ImportSource(schPath, pcbPath, null,
            File.Exists(sourcePayload) ? sourcePayload : null), metadata);
        try { Directory.Delete(staging, true); }
        catch (IOException) { /* The published revision is complete; stale staging can be repaired later. */ }
        catch (UnauthorizedAccessException) { }

        var place = string.Equals(values.GetValueOrDefault("placeAfterImport"), "true", StringComparison.OrdinalIgnoreCase);
        BridgeLog.Info("publish", $"Published {manifest.PartId}/{manifest.RevisionId}; placeAfterImport={place}.", requestId);
        if (!place)
        {
            await _bridge.WriteOnlineOperationStatusAsync(requestId, "succeeded", "complete", "Library published to the offline cache.");
            _activeOnlineRequestId = null;
            _lastOnlineStage = null;
            SetOnlineImportRunning(false);
            await RefreshCatalogActionsAsync();
            StatusText.Text = $"Published {manifest.PartId} revision {manifest.RevisionId} to the offline library.";
            return;
        }
        if (!_bridge.IsPlacementAdapterAvailable(out var placementDiagnostic))
        {
            await _bridge.WriteOnlineOperationStatusAsync(requestId, "succeeded", "published", "Library published; placement listener unavailable.");
            _activeOnlineRequestId = null;
            _lastOnlineStage = null;
            SetOnlineImportRunning(false);
            await RefreshCatalogActionsAsync();
            StatusText.Text = $"Published {manifest.PartId} revision {manifest.RevisionId}, but it was not placed. {placementDiagnostic}";
            BridgeLog.Info("placement", "Library published but placement was not queued: " + placementDiagnostic, requestId);
            return;
        }
        await _bridge.WriteOnlineOperationStatusAsync(requestId, "running", "linking", "Linking the final-path footprint and preparing placement.");
        var followUpId = await _bridge.QueueImportAsync(manifest, true, values.GetValueOrDefault("targetDocument"));
        await _bridge.WriteOnlineOperationStatusAsync(requestId, "succeeded", "placement-queued", "Placement request " + followUpId + " was queued.");
        _activeOnlineRequestId = null;
        _lastOnlineStage = null;
        SetOnlineImportRunning(false);
        await RefreshCatalogActionsAsync();
        StatusText.Text = $"Published {manifest.PartId} revision {manifest.RevisionId}; placement request {followUpId} queued.";
    }

    private static string RequiredResponse(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidDataException("The Altium adapter response is missing: " + key);

    private static bool IsWithin(string child, string parent)
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
    private void ShowError(Exception ex) { BridgeLog.Error("ui", ex); StatusText.Text = ex.Message; MessageBox.Show(this, ex.Message, "Altium LCSC Bridge", MessageBoxButton.OK, MessageBoxImage.Error); }

    private sealed record LibraryItem(LibraryManifest Manifest)
    {
        public string DisplayName => string.Join(" — ", new[] { Manifest.Metadata.SupplierPartNumber, Manifest.Metadata.ManufacturerPartNumber, Manifest.Metadata.Description }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
