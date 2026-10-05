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
    private string? _activeOnlineRequestId;
    private string? _lastOnlineStage;
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
            CatalogValidationText.Foreground = errors.Count == 0 ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.DarkRed;
            await RefreshCatalogActionsAsync(errors.Count == 0);
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
        if (_selectedBundle is null || !_selectedBundle.IsImportable) return;
        try
        {
            if (!_bridge.IsOnlineAdapterAvailable(out var adapterDiagnostic)) throw new InvalidOperationException(adapterDiagnostic);
            if (place && !_bridge.IsPlacementAdapterAvailable(out var placementDiagnostic)) throw new InvalidOperationException(placementDiagnostic);
            var requestId = await _bridge.QueueOnlineImportAsync(_selectedBundle, place);
            _activeOnlineRequestId = requestId;
            _lastOnlineStage = "queued";
            BridgeLog.Info("online-import", $"Queued {_selectedBundle.Component.Metadata.SupplierPartNumber}; placeAfterImport={place}.", requestId);
            StatusText.Text = $"Native library request {requestId} queued. Keep the Altium LCSC Bridge adapter active.";
            SetOnlineImportRunning(true);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void CatalogPlaceCached_Click(object sender, RoutedEventArgs e)
    {
        var supplier = SelectedCatalogItem?.Component.Metadata.SupplierPartNumber;
        if (string.IsNullOrWhiteSpace(supplier)) return;
        var cached = (await _store.SearchAsync(supplier)).FirstOrDefault(x => x.IsPlaceable);
        if (cached is null) return;
        try
        {
            if (!_bridge.IsPlacementAdapterAvailable(out var diagnostic)) throw new InvalidOperationException(diagnostic);
            var id = await _bridge.QueueImportAsync(cached, true);
            StatusText.Text = $"Cached placement request {id} queued.";
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
        CatalogCancelButton.IsEnabled = busy;
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
        var adapterAvailable = _bridge.IsOnlineAdapterAvailable(out var adapterDiagnostic);
        var placementAvailable = _bridge.IsPlacementAdapterAvailable(out var placementDiagnostic);
        var active = !string.IsNullOrWhiteSpace(_activeOnlineRequestId);
        CatalogImportButton.IsEnabled = _selectedBundle.IsImportable && adapterAvailable && !active;
        CatalogImportPlaceButton.IsEnabled = _selectedBundle.IsImportable && adapterAvailable && placementAvailable && !active;
        CatalogPlaceCachedButton.IsEnabled = placementAvailable && !active &&
            (await _store.SearchAsync(_selectedBundle.Component.Metadata.SupplierPartNumber)).Any(x => x.IsPlaceable);
        if (appendDiagnostics && !adapterAvailable)
        {
            CatalogValidationText.Text += "\n\n" + adapterDiagnostic;
            CatalogValidationText.Foreground = System.Windows.Media.Brushes.DarkRed;
        }
        if (appendDiagnostics && !placementAvailable && _selectedBundle.IsImportable)
            CatalogValidationText.Text += "\n\nPlacement unavailable: " + placementDiagnostic;
        if (!active) StatusText.Text = !_selectedBundle.IsImportable
            ? "The selected model contains unsupported data and was not enabled for import."
            : adapterAvailable ? "Source models validated and ready for native Altium creation." : adapterDiagnostic;
    }

    private async Task PollOnlineOperationAsync()
    {
        if (string.IsNullOrWhiteSpace(_activeOnlineRequestId)) return;
        var status = _bridge.ReadOnlineOperationStatus(_activeOnlineRequestId);
        if (status is null) return;
        if (!string.Equals(_lastOnlineStage, status.Stage, StringComparison.Ordinal))
        {
            _lastOnlineStage = status.Stage;
            BridgeLog.Info("online-status", $"state={status.State}; stage={status.Stage}; detail={status.Detail}", status.RequestId);
        }
        if (status.IsTerminal)
        {
            if (status.State == "interrupted")
            {
                CatalogValidationText.Text = $"Import interrupted at {status.Stage} (request {status.RequestId}): {status.Detail}";
                CatalogValidationText.Foreground = System.Windows.Media.Brushes.DarkRed;
                _activeOnlineRequestId = null;
                _lastOnlineStage = null;
                SetOnlineImportRunning(false);
                await RefreshCatalogActionsAsync();
            }
            return;
        }
        SetOnlineImportRunning(true);
        var age = DateTimeOffset.UtcNow - status.UpdatedAt;
        var hasArtifacts = _bridge.HasOnlineOperationArtifacts(status.RequestId);
        var adapterAlive = _bridge.IsOnlineAdapterAvailable(out var diagnostic);
        if (age > TimeSpan.FromMinutes(2) && (!hasArtifacts || !adapterAlive))
        {
            diagnostic = hasArtifacts ? diagnostic : "No request, processing marker, or response remains for this operation.";
            await _bridge.WriteOnlineOperationStatusAsync(status.RequestId, "interrupted", status.Stage,
                "The adapter stopped while this request was active. " + diagnostic);
            CatalogValidationText.Text = $"Import interrupted at {status.Stage} (request {status.RequestId}): {diagnostic}";
            CatalogValidationText.Foreground = System.Windows.Media.Brushes.DarkRed;
            _activeOnlineRequestId = null;
            _lastOnlineStage = null;
            SetOnlineImportRunning(false);
            await RefreshCatalogActionsAsync();
            return;
        }
        StatusText.Text = age > TimeSpan.FromMinutes(2)
            ? $"Request {status.RequestId} is still active in Altium at {status.Stage}, but status updates are delayed."
            : $"Request {status.RequestId}: {status.Stage} — {status.Detail}";
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

    private sealed record CatalogItem(CatalogComponent Component)
    {
        public string DisplayName => string.Join(" — ", new[]
            { Component.Metadata.SupplierPartNumber, Component.Metadata.ManufacturerPartNumber, Component.Metadata.Description }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
