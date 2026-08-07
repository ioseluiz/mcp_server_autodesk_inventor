using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InventorMCPBridge.Services
{
    // Petición que llega por el named pipe (una línea de JSON):
    //   {"id": "opcional", "command": "get_active_doc_info", "payload": {...}}
    internal class McpRequest
    {
        public string Id { get; set; }
        public string Command { get; set; }
        public Dictionary<string, object> Payload { get; set; }
    }

    // Respuesta que devuelve el bridge (una línea de JSON):
    //   {"id": "opcional", "result": {...}, "error": null}
    internal class McpResponse
    {
        [JsonPropertyName("id")]     public string Id     { get; set; }
        [JsonPropertyName("result")] public object Result  { get; set; }
        [JsonPropertyName("error")]  public string Error   { get; set; }
    }

    // Conversión JSON del bridge sobre System.Text.Json.
    //
    // Se usa la librería del framework en lugar de Newtonsoft.Json a propósito:
    // Inventor 2025/2026 carga los add-ins en el AssemblyLoadContext por defecto,
    // compartido con sus propios ensamblados, así que cualquier paquete NuGet que
    // despleguemos puede quedar ignorado o chocar con la versión que cargue Inventor.
    // Sin dependencias externas, la carpeta del add-in es un solo DLL.
    internal static class JsonPayload
    {
        private static readonly JsonSerializerOptions SerializeOptions = new JsonSerializerOptions
        {
            // Sin escapes \uXXXX: los mensajes de error del plugin están en español
            // y así el JSON que ve el servidor MCP es legible.
            Encoder       = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        };

        public static McpRequest ParseRequest(string line)
        {
            using (JsonDocument doc = JsonDocument.Parse(line))
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new Exception("La petición debe ser un objeto JSON.");

                var request = new McpRequest();
                JsonElement el;

                if (root.TryGetProperty("id", out el) && el.ValueKind != JsonValueKind.Null)
                    request.Id = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();

                if (root.TryGetProperty("command", out el) && el.ValueKind == JsonValueKind.String)
                    request.Command = el.GetString();

                if (root.TryGetProperty("payload", out el) && el.ValueKind == JsonValueKind.Object)
                    request.Payload = (Dictionary<string, object>)Normalize(el);

                return request;
            }
        }

        public static string SerializeResponse(string id, object result, string error)
        {
            return JsonSerializer.Serialize(
                new McpResponse { Id = id, Result = result, Error = error },
                SerializeOptions);
        }

        // Convierte un JsonElement a los tipos primitivos que esperan los handlers.
        //
        // Replica lo que hacía Newtonsoft al deserializar en Dictionary<string, object>:
        // enteros → long, reales → double, arrays → List<object>, objetos →
        // Dictionary<string, object>. Sin esta normalización los ~194 accesos a
        // payload[...] y los ~55 Convert.To*(payload[...]) de BridgeService fallarían,
        // porque JsonElement no implementa IConvertible.
        public static object Normalize(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (JsonProperty property in element.EnumerateObject())
                        dict[property.Name] = Normalize(property.Value);
                    return dict;

                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray())
                        list.Add(Normalize(item));
                    return list;

                case JsonValueKind.String:
                    return element.GetString();

                case JsonValueKind.Number:
                    long integer;
                    if (element.TryGetInt64(out integer)) return integer;
                    return element.GetDouble();

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                default:
                    return null;
            }
        }
    }
}
