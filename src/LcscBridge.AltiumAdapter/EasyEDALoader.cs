using DXP;
using LcscBridge.App;
using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace EasyEDA_Loader
{
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class EasyEDALoaderModule : ServerModule
    {
        private readonly bool noGuiMode;
        private readonly InProcessAltiumIntegration integration;
        private MainWindow window;

        public EasyEDALoaderModule(IClient argClient) : base(argClient, "EasyEDA-Loader")
        {
            noGuiMode = argClient.ProductInfo().SupportsUIFeature("NoGUI", false);
            integration = new InProcessAltiumIntegration();
            AdapterLog.Info("startup", "EasyEDA Loader 0.3.0 loaded with the in-process UI and Altium integration.");
        }

        protected override IServerDocument NewDocumentInstance(string argKind, string argFileName) => null;

        protected override void InitializeCommands() => RegisterCommand("EasyEDARun", new CommandProc(Run));

        private void RegisterCommand(string commandId, CommandProc command) =>
            ((CommandLauncher)CommandLauncher).RegisterCommand(commandId,
                (IServerDocumentView view, ref string parameters) =>
                {
                    try { command(view, ref parameters); }
                    catch (Exception ex)
                    {
                        AdapterLog.Error("command", ex);
                        if (noGuiMode) throw;
                        Forms.MessageBox.Show(ex.ToString(), "EasyEDA Loader Error", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                    }
                });

        private void Run(IServerDocumentView context, ref string parameters)
        {
            if (!integration.IsAvailable(out var diagnostic)) throw new InvalidOperationException(diagnostic);
            if (window == null)
            {
                window = new MainWindow(integration);
                new WindowInteropHelper(window).Owner = new IntPtr(unchecked((long)AltiumApi.GlobalVars.Client.GetMainWindowHandle()));
                window.Closed += (_, _) => window = null;
            }
            window.ActivateExisting();
        }
    }
}
