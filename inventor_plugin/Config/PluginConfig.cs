using System.IO;
using System.Text.Json;

namespace InventorMCPBridge.Config
{
    internal class PluginConfig
    {
        // Nombre del named pipe donde escucha el bridge. Solo hay que cambiarlo si se
        // ejecutan varias sesiones de Inventor a la vez: el servidor MCP se conectaría
        // a cualquiera de ellas indistintamente.
        public string PipeName { get; set; } = "InventorMCPBridge";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            // Newtonsoft era insensible a mayúsculas: los config.json existentes
            // (pipeName, PipeName) deben seguir cargando igual.
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling         = JsonCommentHandling.Skip,
            AllowTrailingCommas         = true,
        };

        internal static PluginConfig Load()
        {
            string configPath = Path.Combine(
                Path.GetDirectoryName(typeof(PluginConfig).Assembly.Location),
                "config.json");

            if (!File.Exists(configPath))
                return new PluginConfig();

            try
            {
                string json = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<PluginConfig>(json, Options) ?? new PluginConfig();
            }
            catch
            {
                return new PluginConfig();
            }
        }
    }
}
