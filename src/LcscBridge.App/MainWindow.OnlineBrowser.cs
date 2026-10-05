using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using LcscBridge.Core;

namespace LcscBridge.App;

public partial class MainWindow
{
    private const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";
    private bool _onlineInitialized;
    private bool _onlineInitializing;
    private bool _onlineNavigating;

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Tabs) || !IsLoaded || !ReferenceEquals(Tabs.SelectedItem, WebsiteTab)) return;
        await InitializeOnlineBrowserAsync();
    }

    private async Task<bool> InitializeOnlineBrowserAsync()
    {
        if (_onlineInitialized) return true;
        if (_onlineInitializing) return false;

        _onlineInitializing = true;
        OnlineMessageTitle.Text = "Starting LCSC online catalog…";
        OnlineMessageText.Text = "The first launch can take a moment while the browser profile is prepared.";
        OnlineMessagePanel.Visibility = Visibility.Visible;
        OnlineWebView.Visibility = Visibility.Hidden;
        OnlineRetryButton.Visibility = Visibility.Collapsed;
        RuntimeDownloadButton.Visibility = Visibility.Collapsed;

        try
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profilePath = Path.Combine(localData, "AltiumLcscBridge", "WebView2");
            Directory.CreateDirectory(profilePath);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profilePath);
            await OnlineWebView.EnsureCoreWebView2Async(environment);

            var browser = OnlineWebView.CoreWebView2;
            browser.Settings.AreDevToolsEnabled = false;
            browser.Settings.AreHostObjectsAllowed = false;
            browser.Settings.IsWebMessageEnabled = false;
            browser.Settings.IsStatusBarEnabled = true;
            browser.Settings.IsBuiltInErrorPageEnabled = true;
            browser.NavigationStarting += Online_NavigationStarting;
            browser.NavigationCompleted += Online_NavigationCompleted;
            browser.HistoryChanged += Online_HistoryChanged;
            browser.SourceChanged += Online_SourceChanged;
            browser.NewWindowRequested += Online_NewWindowRequested;
            browser.ProcessFailed += Online_ProcessFailed;

            _onlineInitialized = true;
            OnlineWebView.Visibility = Visibility.Visible;
            OnlineMessagePanel.Visibility = Visibility.Collapsed;
            OnlineReloadButton.IsEnabled = true;
            NavigateOnline(OnlineCatalogNavigation.HomeUri);
            return true;
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            ShowOnlineFailure(
                "WebView2 Runtime is required",
                "Install the Microsoft Edge WebView2 Evergreen Runtime, then select Retry. Offline search and local imports remain available.\n\n" + ex.Message,
                runtimeMissing: true);
            return false;
        }
        catch (Exception ex)
        {
            ShowOnlineFailure(
                "The embedded browser could not start",
                "You can retry or open LCSC in your default browser. Offline search and local imports remain available.\n\n" + ex.Message,
                runtimeMissing: false);
            return false;
        }
        finally
        {
            _onlineInitializing = false;
        }
    }

    private async void OnlineSearch_Click(object sender, RoutedEventArgs e)
    {
        if (await InitializeOnlineBrowserAsync()) NavigateOnline(OnlineCatalogNavigation.CreateSearchUri(OnlineSearchBox.Text));
    }

    private async void OnlineSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (await InitializeOnlineBrowserAsync()) NavigateOnline(OnlineCatalogNavigation.CreateSearchUri(OnlineSearchBox.Text));
    }

    private void OnlineBack_Click(object sender, RoutedEventArgs e)
    {
        if (_onlineInitialized && OnlineWebView.CanGoBack) OnlineWebView.GoBack();
    }

    private void OnlineForward_Click(object sender, RoutedEventArgs e)
    {
        if (_onlineInitialized && OnlineWebView.CanGoForward) OnlineWebView.GoForward();
    }

    private async void OnlineHome_Click(object sender, RoutedEventArgs e)
    {
        if (await InitializeOnlineBrowserAsync()) NavigateOnline(OnlineCatalogNavigation.HomeUri);
    }

    private void OnlineReload_Click(object sender, RoutedEventArgs e)
    {
        if (!_onlineInitialized) return;
        if (_onlineNavigating) OnlineWebView.CoreWebView2.Stop();
        else OnlineWebView.Reload();
    }

    private void OnlineExternal_Click(object sender, RoutedEventArgs e)
    {
        OpenExternal(CurrentOnlineUri().AbsoluteUri);
    }

    private async void OnlineRetry_Click(object sender, RoutedEventArgs e)
    {
        if (!_onlineInitialized)
        {
            await InitializeOnlineBrowserAsync();
            return;
        }

        OnlineMessagePanel.Visibility = Visibility.Collapsed;
        OnlineWebView.Visibility = Visibility.Visible;
        NavigateOnline(CurrentOnlineUri());
    }

    private void RuntimeDownload_Click(object sender, RoutedEventArgs e) => OpenExternal(WebView2DownloadUrl);

    private void NavigateOnline(Uri target)
    {
        if (!_onlineInitialized || !OnlineCatalogNavigation.IsAllowedWebUri(target.AbsoluteUri)) return;
        OnlineMessagePanel.Visibility = Visibility.Collapsed;
        OnlineWebView.Visibility = Visibility.Visible;
        OnlineWebView.CoreWebView2.Navigate(target.AbsoluteUri);
    }

    private Uri CurrentOnlineUri()
    {
        var source = OnlineWebView.Source;
        return _onlineInitialized && source is not null && OnlineCatalogNavigation.IsAllowedWebUri(source.AbsoluteUri)
            ? source
            : OnlineCatalogNavigation.HomeUri;
    }

    private void Online_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!OnlineCatalogNavigation.IsAllowedWebUri(e.Uri))
        {
            e.Cancel = true;
            OnlineStatusText.Text = "Blocked navigation to a non-web address.";
            return;
        }

        _onlineNavigating = true;
        OnlineReloadButton.Content = "Stop";
        OnlineStatusText.Text = "Loading " + e.Uri;
    }

    private void Online_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _onlineNavigating = false;
        OnlineReloadButton.Content = "Reload";
        UpdateOnlineNavigationButtons();
        if (e.IsSuccess)
        {
            OnlineStatusText.Text = "Live LCSC browsing. Import supported models from the Online components tab.";
            return;
        }

        ShowOnlineFailure(
            "The page could not be loaded",
            $"WebView2 reported {e.WebErrorStatus}. Check the network connection, retry, or open LCSC externally. Offline features remain available.",
            runtimeMissing: false);
    }

    private void Online_HistoryChanged(object? sender, object e) => UpdateOnlineNavigationButtons();

    private void Online_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        OnlineAddress.Text = OnlineWebView.Source?.AbsoluteUri ?? string.Empty;
    }

    private void Online_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (OnlineCatalogNavigation.IsAllowedWebUri(e.Uri)) OpenExternal(e.Uri);
        else OnlineStatusText.Text = "Blocked a new window using a non-web address.";
    }

    private void Online_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        ShowOnlineFailure(
            "The embedded browser stopped",
            $"The WebView2 process ended ({e.ProcessFailedKind}). Retry to reload it. Offline features remain available.",
            runtimeMissing: false);
    }

    private void UpdateOnlineNavigationButtons()
    {
        OnlineBackButton.IsEnabled = _onlineInitialized && OnlineWebView.CanGoBack;
        OnlineForwardButton.IsEnabled = _onlineInitialized && OnlineWebView.CanGoForward;
        OnlineReloadButton.IsEnabled = _onlineInitialized;
    }

    private void ShowOnlineFailure(string title, string message, bool runtimeMissing)
    {
        _onlineNavigating = false;
        OnlineReloadButton.Content = "Reload";
        OnlineWebView.Visibility = Visibility.Hidden;
        OnlineMessageTitle.Text = title;
        OnlineMessageText.Text = message;
        OnlineRetryButton.Visibility = Visibility.Visible;
        RuntimeDownloadButton.Visibility = runtimeMissing ? Visibility.Visible : Visibility.Collapsed;
        OnlineMessagePanel.Visibility = Visibility.Visible;
        OnlineStatusText.Text = title;
    }

    private static void OpenExternal(string address)
    {
        if (!OnlineCatalogNavigation.IsAllowedWebUri(address)) return;
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CancelCatalogOperation();
        if (_nativeOperationActive && !_shutdownRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _shutdownRequested = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelCatalogOperation();
        _catalogProvider.Dispose();
        CatalogSymbolPreview.Dispose();
        CatalogFootprintPreview.Dispose();
        OnlineWebView.Dispose();
        base.OnClosed(e);
    }
}
