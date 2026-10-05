using System.Windows;

namespace LcscBridge.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LcscBridge.Core.BridgeLog.Error("unhandled-ui", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                LcscBridge.Core.BridgeLog.Error("unhandled-process", exception);
        };
        base.OnStartup(e);
    }
}
