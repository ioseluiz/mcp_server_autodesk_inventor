using System.IO;
using Newtonsoft.Json;

namespace InventorMCPBridge.Config
{
    internal class PluginConfig
    {
        public string ServerUrl { get; set; } = "https://your-app.azurewebsites.net";
        public string ApiKey   { get; set; } = "1234567890";
        public string UserId   { get; set; } = "user";

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
                return JsonConvert.DeserializeObject<PluginConfig>(json) ?? new PluginConfig();
            }
            catch
            {
                return new PluginConfig();
            }
        }
    }
}
