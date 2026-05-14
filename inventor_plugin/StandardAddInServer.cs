using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Inventor;
using InventorMCPBridge.Services;

namespace InventorMCPBridge
{
    [Guid("E1A5A5A5-A5A5-A5A5-A5A5-A5A5A5A5A5A5")]
    [ComVisible(true)]
    public class StandardAddInServer : ApplicationAddInServer
    {
        private Inventor.Application _inventorApp;
        private ButtonDefinition _btnOn;
        private ButtonDefinition _btnOff;
        private readonly List<CommandControl> _onControls  = new List<CommandControl>();
        private readonly List<CommandControl> _offControls = new List<CommandControl>();
        private PollingService _pollingService;
        private bool _isBridgeActive = false;

        public void Activate(ApplicationAddInSite addInSiteObject, bool firstTime)
        {
            try
            {
                _inventorApp = addInSiteObject.Application;

                _pollingService = new PollingService(
                    _inventorApp,
                    "https://your-app.azurewebsites.net",
                    "1234567890",
                    "jlmunoz");

                ControlDefinitions controlDefs = _inventorApp.CommandManager.ControlDefinitions;
                string clientId = "{E1A5A5A5-A5A5-A5A5-A5A5-A5A5A5A5A5A5}";

                _btnOff = controlDefs.AddButtonDefinition(
                    "MCP Bridge: OFF", "InventorMCPBridge:TurnOn",
                    CommandTypesEnum.kNonShapeEditCmdType, clientId,
                    "Activar MCP Bridge", "Click para conectar al servidor MCP");

                _btnOn = controlDefs.AddButtonDefinition(
                    "MCP Bridge: ON", "InventorMCPBridge:TurnOff",
                    CommandTypesEnum.kNonShapeEditCmdType, clientId,
                    "Desactivar MCP Bridge", "Click para desconectar del servidor MCP");

                _btnOff.OnExecute += (NameValueMap ctx) => ToggleBridge();
                _btnOn.OnExecute  += (NameValueMap ctx) => ToggleBridge();

                SetupRibbon(_inventorApp.UserInterfaceManager);
            }
            catch (Exception ex)
            {
                Exception inner = ex;
                while (inner.InnerException != null)
                    inner = inner.InnerException;

                MessageBox.Show(
                    "Tipo: " + inner.GetType().FullName +
                    "\n\nMensaje: " + inner.Message +
                    "\n\nStack:\n" + inner.StackTrace,
                    "InventorMCPBridge — Error de activación",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void SetupRibbon(UserInterfaceManager uiManager)
        {
            string[] targetRibbons = { "ZeroDoc", "Part", "Assembly" };

            foreach (string ribbonName in targetRibbons)
            {
                try
                {
                    Ribbon ribbon  = uiManager.Ribbons[ribbonName];
                    string tabId   = ribbonName == "ZeroDoc" ? "id_TabMCP" : "id_TabMCP_" + ribbonName;
                    string panelId = ribbonName == "ZeroDoc" ? "id_PanelMCP" : "id_PanelMCP_" + ribbonName;

                    RibbonTab   tab   = GetOrCreateTab(ribbon, "MCP", tabId);
                    RibbonPanel panel = GetOrCreatePanel(tab, "Bridge", panelId);

                    CommandControl ctrlOff = panel.CommandControls.AddButton(_btnOff, true);
                    CommandControl ctrlOn  = panel.CommandControls.AddButton(_btnOn,  true);
                    ctrlOn.Visible = false;

                    _offControls.Add(ctrlOff);
                    _onControls.Add(ctrlOn);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error al crear ribbon '{ribbonName}':\n\n{ex.Message}",
                        "InventorMCPBridge — Ribbon",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }

        private void ToggleBridge()
        {
            if (!_isBridgeActive)
            {
                if (!_pollingService.TestConnection())
                {
                    MessageBox.Show(
                        "No se pudo conectar al servidor MCP.\n\nVerifica que el servidor esté corriendo y que la URL y API Key sean correctas.",
                        "MCP Bridge — Sin conexión",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
            }

            _isBridgeActive = !_isBridgeActive;

            foreach (CommandControl c in _offControls)
                c.Visible = !_isBridgeActive;
            foreach (CommandControl c in _onControls)
                c.Visible = _isBridgeActive;

            if (_isBridgeActive)
                _pollingService.Start();
            else
                _pollingService.Stop();
        }

        private RibbonTab GetOrCreateTab(Ribbon ribbon, string displayName, string internalName)
        {
            foreach (RibbonTab t in ribbon.RibbonTabs)
                if (t.InternalName == internalName) return t;
            return ribbon.RibbonTabs.Add(displayName, internalName, "{E1A5A5A5-A5A5-A5A5-A5A5-A5A5A5A5A5A5}");
        }

        private RibbonPanel GetOrCreatePanel(RibbonTab tab, string displayName, string internalName)
        {
            foreach (RibbonPanel p in tab.RibbonPanels)
                if (p.InternalName == internalName) return p;
            return tab.RibbonPanels.Add(displayName, internalName, "{E1A5A5A5-A5A5-A5A5-A5A5-A5A5A5A5A5A5}");
        }

        public void Deactivate()
        {
            try
            {
                _pollingService?.Stop();
                Marshal.ReleaseComObject(_inventorApp);
            }
            finally
            {
                _inventorApp = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        public void ExecuteCommand(int commandID) { }

        public object Automation => null;
    }
}
