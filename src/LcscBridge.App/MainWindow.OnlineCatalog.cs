using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using LcscBridge.Core;

namespace LcscBridge.App;

public partial class MainWindow
{
    private readonly EasyEdaProvider _catalogProvider = new();
    private CancellationTokenSource? _catalogCancellation;
    private SourceModelBundle? _selectedBundle;
    private CatalogItem? SelectedCatalogItem => CatalogResults.SelectedItem as CatalogItem;
    private bool _catalogPreviewsInitialized;

    private async void CatalogSearch_Click(object sender, RoutedEventArgs e) => await SearchCatalogAsync();

    private async void CatalogSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchCatalogAsync();
    }

    private async Task SearchCatalogAsync()
    {
        CancelCatalogOperation();
        _catalogCancellation = new CancellationTokenSource();
        SetCatalogBusy(true, "Searching online catalog…");
        ClearCatalogSelection();
        try
        {
            var results = await _catalogProvider.SearchAsync(CatalogSearchBox.Text, 1, _catalogCancellation.Token);
            CatalogResults.ItemsSource = results.Select(x => new CatalogItem(x)).ToList();
            StatusText.Text = $"Found {results.Count} online component(s). Select one to retrieve and validate its models.";
            if (results.Count == 1) CatalogResults.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { StatusText.Text = "Online search cancelled."; }
        catch (Exception ex) { ShowError(ex); }
        finally { SetCatalogBusy(false, null); }
    }

    private async void CatalogResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = SelectedCatalogItem;
        ClearCatalogSelection(clearDetails: false);
        if (item is null) return;
        var metadata = item.Component.Metadata;
        CatalogPartTitle.Text = item.DisplayName;
        CatalogPartDescription.Text = metadata.Description;
        CatalogPartDetails.Text = $"Manufacturer: {metadata.Manufacturer}\nMPN: {metadata.ManufacturerPartNumber}\nLCSC: {metadata.SupplierPartNumber}\nPackage: {metadata.Package}\nSymbol: {item.Component.SymbolAvailability}\nFootprint: {item.Component.FootprintAvailability}";

        CancelCatalogOperation();
        _catalogCancellation = new CancellationTokenSource();
        SetCatalogBusy(true, $"Retrieving and validating {metadata.SupplierPartNumber}…");
        try
        {
            _selectedBundle = await _catalogProvider.DownloadAsync(item.Component, _catalogCancellation.Token);
            await ShowCatalogPreviewsAsync(_selectedBundle, _catalogCancellation.Token);
            var errors = _selectedBundle.Diagnostics.Where(x => x.IsError).ToList();
            CatalogValidationText.Text = errors.Count == 0
                ? $"Validated source model: {_selectedBundle.SymbolPrimitiveCount} symbol primitives, {_selectedBundle.FootprintPrimitiveCount} footprint primitives."
                : "Import blocked:\n" + string.Join("\n", errors.Select(x => "• " + x.Message));
            CatalogValidationText.Foreground = errors.Count == 0 ? ThemeBrush("SuccessForegroundBrush") : ThemeBrush("ErrorForegroundBrush");
            await RefreshCatalogActionsAsync(appendDiagnostics: true);
        }
        catch (OperationCanceledException) { StatusText.Text = "Model retrieval cancelled."; }
        catch (Exception ex) { ShowError(ex); }
        finally { SetCatalogBusy(false, null); }
    }

    private async Task ShowCatalogPreviewsAsync(SourceModelBundle bundle, CancellationToken cancellationToken)
    {
        if (!_catalogPreviewsInitialized)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(local, "AltiumLcscBridge", "PreviewWebView2"));
            await Task.WhenAll(CatalogSymbolPreview.EnsureCoreWebView2Async(environment), CatalogFootprintPreview.EnsureCoreWebView2Async(environment));
            foreach (var view in new[] { CatalogSymbolPreview, CatalogFootprintPreview })
            {
                view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                view.CoreWebView2.Settings.AreHostObjectsAllowed = false;
                view.CoreWebView2.Settings.IsWebMessageEnabled = false;
                view.CoreWebView2.Settings.IsZoomControlEnabled = true;
            }
            _catalogPreviewsInitialized = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        CatalogSymbolPreview.NavigateToString(PreviewHtml(bundle.PreviewSvgs.ElementAtOrDefault(0)));
        CatalogFootprintPreview.NavigateToString(PreviewHtml(bundle.PreviewSvgs.ElementAtOrDefault(1)));
    }

    private static string PreviewHtml(string? svg)
    {
        if (string.IsNullOrWhiteSpace(svg)) return "<html><body style='font-family:Segoe UI;color:#555;display:flex;align-items:center;justify-content:center'>Preview unavailable</body></html>";
        return "<html><head><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline'\"></head><body style='margin:0;display:flex;align-items:center;justify-content:center;overflow:hidden'>" + svg + "</body></html>";
    }

    private async void CatalogImport_Click(object sender, RoutedEventArgs e) => await QueueOnlineCreationAsync(false);
    private async void CatalogImportPlace_Click(object sender, RoutedEventArgs e) => await QueueOnlineCreationAsync(true);

    private async Task QueueOnlineCreationAsync(bool place)
    {
        if (_selectedBundle is null || !_selectedBundle.IsImportable || _nativeOperationActive) return;
        var requestId = Guid.NewGuid().ToString("N");
        var target = _integration.CaptureActiveSchematic();
        _nativeOperationActive = true;
        SetOnlineImportRunning(true);
        try
        {
            EnsureIntegrationAvailable();
            BridgeLog.Info("online-import", $"Starting {_selectedBundle.Component.Metadata.SupplierPartNumber}; placeAfterImport={place}.", requestId);
            var created = await _integration.CreateNativeLibrariesAsync(_selectedBundle, _libraryRoot, requestId, target, OperationProgress());
            StatusText.Text = $"Request {requestId}: publishing verified native libraries…";
            var metadata = _selectedBundle.Component.Metadata with
            {
                Source = "EasyEDA/LCSC",
                SourceRevision = _selectedBundle.ProviderRevision,
                SymbolReference = created.SymbolReference,
                FootprintName = created.FootprintName
            };
            var payload = Path.Combine(created.StagingDirectory, "easyeda-source.json");
            var manifest = await _store.ImportAsync(new ImportSource(created.SchLibPath, created.PcbLibPath, null,
                File.Exists(payload) ? payload : null), metadata);
            TryDeleteStaging(created.StagingDirectory);
            if (place) Hide();
            await _integration.InstallAndPlaceAsync(manifest, place, target, requestId, OperationProgress());
            var manifestPath = Path.Combine(manifest.LibraryDirectory, "manifest.json");
            if (File.Exists(manifestPath)) await _store.ReconcileAsync(manifestPath);
            await RefreshAsync();
            StatusText.Text = place
                ? $"Published {manifest.PartId} revision {manifest.RevisionId}; interactive placement started."
                : $"Published and installed {manifest.PartId} revision {manifest.RevisionId}.";
            CatalogValidationText.Text = StatusText.Text;
            CatalogValidationText.Foreground = ThemeBrush("SuccessForegroundBrush");
        }
        catch (Exception ex)
        {
            BridgeLog.Error("online-import", ex, requestId);
            CatalogValidationText.Text = $"Import failed (request {requestId}): {ex.Message}";
            CatalogValidationText.Foreground = ThemeBrush("ErrorForegroundBrush");
            StatusText.Text = CatalogValidationText.Text;
        }
        finally
        {
            _nativeOperationActive = false;
            if (!IsVisible) Show();
            Activate();
            SetOnlineImportRunning(false);
            await RefreshCatalogActionsAsync();
        }
    }

    private async void CatalogPlaceCached_Click(object sender, RoutedEventArgs e)
    {
        var supplier = SelectedCatalogItem?.Component.Metadata.SupplierPartNumber;
        if (string.IsNullOrWhiteSpace(supplier) || _nativeOperationActive) return;
        var cached = (await _store.SearchAsync(supplier)).FirstOrDefault(x => x.IsPlaceable);
        if (cached is null) return;
        try
        {
            EnsureIntegrationAvailable();
            await InstallManifestAsync(cached, true, _integration.CaptureActiveSchematic(), Guid.NewGuid().ToString("N"));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void CatalogCancel_Click(object sender, RoutedEventArgs e) => CancelCatalogOperation();

    private void CancelCatalogOperation()
    {
        _catalogCancellation?.Cancel();
        _catalogCancellation?.Dispose();
        _catalogCancellation = null;
    }

    private void SetCatalogBusy(bool busy, string? message)
    {
        CatalogProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CatalogCancelButton.IsEnabled = busy && !_nativeOperationActive;
        if (message is not null) StatusText.Text = message;
    }

    private void SetOnlineImportRunning(bool running)
    {
        CatalogProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CatalogCancelButton.IsEnabled = false;
        if (running)
        {
            CatalogImportButton.IsEnabled = false;
            CatalogImportPlaceButton.IsEnabled = false;
            CatalogPlaceCachedButton.IsEnabled = false;
        }
    }

    private async Task RefreshCatalogActionsAsync(bool appendDiagnostics = false)
    {
        if (_selectedBundle is null) return;
        var available = _integration.IsAvailable(out var diagnostic);
        CatalogImportButton.IsEnabled = _selectedBundle.IsImportable && available && !_nativeOperationActive;
        CatalogImportPlaceButton.IsEnabled = _selectedBundle.IsImportable && available && !_nativeOperationActive;
        CatalogPlaceCachedButton.IsEnabled = available && !_nativeOperationActive &&
            (await _store.SearchAsync(_selectedBundle.Component.Metadata.SupplierPartNumber)).Any(x => x.IsPlaceable);
        if (appendDiagnostics && !available)
        {
            CatalogValidationText.Text += "\n\n" + diagnostic;
            CatalogValidationText.Foreground = ThemeBrush("ErrorForegroundBrush");
        }
        if (!_nativeOperationActive) StatusText.Text = !_selectedBundle.IsImportable
            ? "The selected model contains unsupported data and was not enabled for import."
            : available ? "Source models validated and ready for native Altium creation." : diagnostic;
    }

    private void ClearCatalogSelection(bool clearDetails = true)
    {
        _selectedBundle = null;
        CatalogImportButton.IsEnabled = false;
        CatalogImportPlaceButton.IsEnabled = false;
        CatalogPlaceCachedButton.IsEnabled = false;
        CatalogValidationText.Text = string.Empty;
        if (!clearDetails) return;
        CatalogPartTitle.Text = "Search the EasyEDA/LCSC catalog";
        CatalogPartDescription.Text = string.Empty;
        CatalogPartDetails.Text = string.Empty;
    }

    private static void TryDeleteStaging(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record CatalogItem(CatalogComponent Component)
    {
        public string DisplayName => string.Join(" — ", new[]
            { Component.Metadata.SupplierPartNumber, Component.Metadata.ManufacturerPartNumber, Component.Metadata.Description }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
