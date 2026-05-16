using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Inventor;
using InventorMCPBridge.Config;
using InventorMCPBridge.Services;
using Color = System.Drawing.Color;

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

                var cfg = PluginConfig.Load();
                _pollingService = new PollingService(
                    _inventorApp,
                    cfg.ServerUrl,
                    cfg.ApiKey,
                    cfg.UserId);

                ControlDefinitions controlDefs = _inventorApp.CommandManager.ControlDefinitions;
                string clientId = "{E1A5A5A5-A5A5-A5A5-A5A5-A5A5A5A5A5A5}";

                var redSmall  = MakeIcon(16, Color.FromArgb(210, 50, 50));
                var redLarge  = MakeIcon(32, Color.FromArgb(210, 50, 50));
                var greenSmall = MakeIcon(16, Color.FromArgb(45, 185, 75));
                var greenLarge = MakeIcon(32, Color.FromArgb(45, 185, 75));

                _btnOff = controlDefs.AddButtonDefinition(
                    "MCP Bridge: OFF", "InventorMCPBridge:TurnOn",
                    CommandTypesEnum.kNonShapeEditCmdType, clientId,
                    "Activar MCP Bridge", "Click para conectar al servidor MCP",
                    redSmall, redLarge);

                _btnOn = controlDefs.AddButtonDefinition(
                    "MCP Bridge: ON", "InventorMCPBridge:TurnOff",
                    CommandTypesEnum.kNonShapeEditCmdType, clientId,
                    "Desactivar MCP Bridge", "Click para desconectar del servidor MCP",
                    greenSmall, greenLarge);

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

        // Crea un ícono LED circular del tamaño y color indicados y lo convierte
        // al formato COM (IPictureDisp) que acepta la API de ribbon de Inventor.
        // Devuelve null si la creación falla, lo que hace que Inventor omita el ícono
        // sin lanzar una excepción.
        private static object MakeIcon(int size, Color ledColor)
        {
            try
            {
                using (var bmp = new Bitmap(size, size))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    int pad = Math.Max(2, size / 8);
                    var r = new Rectangle(pad, pad, size - 2 * pad - 1, size - 2 * pad - 1);

                    // Sombra tenue para dar profundidad
                    using (var b = new SolidBrush(Color.FromArgb(55, 0, 0, 0)))
                        g.FillEllipse(b, r.X + 1, r.Y + 1, r.Width, r.Height);

                    // Relleno principal del LED
                    using (var b = new SolidBrush(ledColor))
                        g.FillEllipse(b, r);

                    // Reflejo especular en la parte superior izquierda
                    if (size >= 16)
                    {
                        var hr = new RectangleF(
                            r.X + r.Width  * 0.18f,
                            r.Y + r.Height * 0.10f,
                            r.Width  * 0.38f,
                            r.Height * 0.30f);
                        using (var b = new SolidBrush(Color.FromArgb(140, 255, 255, 255)))
                            g.FillEllipse(b, hr);
                    }

                    // Borde oscuro
                    using (var p = new Pen(Color.FromArgb(150, 0, 0, 0), 1f))
                        g.DrawEllipse(p, r);

                    return IconConverter.ToIPictureDisp(bmp);
                }
            }
            catch
            {
                return null;
            }
        }

        // Expone el método protegido de AxHost que convierte un Bitmap a IPictureDisp
        // (el tipo COM que usa la API de Inventor para los íconos del ribbon).
        private class IconConverter : AxHost
        {
            private IconConverter() : base(string.Empty) { }
            public static object ToIPictureDisp(Image image) => GetIPictureDispFromPicture(image);
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
