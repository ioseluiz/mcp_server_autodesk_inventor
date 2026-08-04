using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using InventorMCPBridge.Services;

namespace BridgeTest
{
    // Pruebas del add-in que no necesitan Inventor: la capa JSON y el transporte por
    // named pipe. Los comandos COM en sí solo se pueden probar con Inventor abierto.
    internal static class Program
    {
        private const string TestPipe = "InventorMCPBridgeTests_CS";

        private static int _failed;

        private static void Check(bool ok, string label, string detail = null)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + label +
                              (string.IsNullOrEmpty(detail) ? "" : $"  [{detail}]"));
            if (!ok) _failed++;
        }

        private static async Task<int> Main()
        {
            Console.WriteLine("== JsonPayload: normalización del payload ==");
            McpRequest req = JsonPayload.ParseRequest(
                "{\"id\":\"7\",\"command\":\"draw_rectangle\",\"payload\":{" +
                "\"x1\":0,\"y1\":-2.5,\"name\":\"caja\",\"ok\":true,\"nada\":null," +
                "\"ids\":[1,2,3],\"nested\":{\"k\":1.5},\"sketches\":[\"a\",\"b\"]}}");

            Check(req.Id == "7", "id leído");
            Check(req.Command == "draw_rectangle", "command leído");

            Dictionary<string, object> p = req.Payload;
            Check(p["x1"] is long, "entero -> long (igual que Newtonsoft)");
            Check(p["y1"] is double, "real -> double");
            Check(p["name"] is string, "string -> string");
            Check(p["ok"] is bool, "true -> bool");
            Check(p["nada"] == null, "null -> null");
            Check(p["ids"] is List<object>, "array -> List<object>");
            Check(p["nested"] is Dictionary<string, object>, "objeto -> Dictionary<string,object>");

            Console.WriteLine("== Patrones que usan los ~194 accesos a payload[...] ==");
            Check(Math.Abs(Convert.ToDouble(p["y1"]) + 2.5) < 1e-9, "Convert.ToDouble(payload[..])");
            Check(Convert.ToInt32(p["x1"]) == 0, "Convert.ToInt32(payload[..])");
            Check(p["name"].ToString() == "caja", "payload[..].ToString()");
            Check(p.ContainsKey("nested") && !p.ContainsKey("NESTED"), "claves sensibles a mayúsculas");

            var ids = (List<object>)p["ids"];
            Check(Convert.ToInt32(ids[2]) == 3, "ParseIntList: Convert.ToInt32 sobre List<object>");
            var sketches = (List<object>)p["sketches"];
            Check(sketches[1].ToString() == "b", "LoftProfiles: item.ToString() sobre List<object>");

            Console.WriteLine("== JsonPayload: serialización de la respuesta ==");
            string json = JsonPayload.SerializeResponse(
                "7",
                new { Status = "ok", Nombre = "posición límite", Valor = 1.5, Items = new[] { 1, 2 } },
                null);
            Check(json.IndexOf('\n') < 0 && json.IndexOf('\r') < 0, "respuesta en una sola línea");
            Check(json.Contains("posición límite"), "acentos sin escapar como \\uXXXX");
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement root = doc.RootElement;
                Check(root.GetProperty("error").ValueKind == JsonValueKind.Null, "error = null");
                Check(root.GetProperty("id").GetString() == "7", "id devuelto");
                Check(root.GetProperty("result").GetProperty("Status").GetString() == "ok",
                      "result serializa el tipo anónimo en PascalCase");
                Check(root.GetProperty("result").GetProperty("Items").GetArrayLength() == 2,
                      "arrays dentro del result");
            }

            string multiline = JsonPayload.SerializeResponse("8", null, "línea1\nlínea2");
            Check(multiline.IndexOf('\n') < 0, "un error multilínea no rompe el framing");

            Console.WriteLine("== BridgeService: named pipe de extremo a extremo ==");
            // inventorApp = null: no se ejecuta ningún handler, solo el transporte.
            var bridge = new BridgeService(null, TestPipe, work => work());
            bridge.Start();
            Check(bridge.IsRunning, "Start() abre el pipe");

            try
            {
                using (var client = new NamedPipeClientStream(
                    ".", TestPipe, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    await client.ConnectAsync(5000);
                    Check(client.IsConnected, "el cliente conecta");

                    var utf8 = new UTF8Encoding(false);
                    using (var writer = new StreamWriter(client, utf8, 4096, true))
                    using (var reader = new StreamReader(client, utf8, false, 4096, true))
                    {
                        writer.NewLine = "\n";
                        writer.AutoFlush = true;

                        await writer.WriteLineAsync(
                            "{\"id\":\"9\",\"command\":\"__desconocido__\",\"payload\":{}}");
                        string r1 = await reader.ReadLineAsync();
                        Check(r1 != null && r1.Contains("Comando no reconocido: __desconocido__"),
                              "comando desconocido -> error legible");
                        Check(r1 != null && r1.Contains("\"id\":\"9\""), "el id se devuelve tal cual");

                        // Segunda petición en la misma conexión: valida el bucle de framing.
                        await writer.WriteLineAsync("esto no es json");
                        string r2 = await reader.ReadLineAsync();
                        bool jsonErrorHandled = false;
                        if (r2 != null)
                        {
                            using (JsonDocument doc = JsonDocument.Parse(r2))
                                jsonErrorHandled =
                                    doc.RootElement.GetProperty("error").ValueKind == JsonValueKind.String;
                        }
                        Check(jsonErrorHandled, "JSON inválido -> respuesta de error, sin cortar la sesión");

                        // Tercera: el handler lanza (inventorApp es null) y debe capturarse.
                        await writer.WriteLineAsync("{\"id\":\"11\",\"command\":\"get_active_doc_info\"}");
                        string r3 = await reader.ReadLineAsync();
                        bool handlerErrorCaught = false;
                        if (r3 != null)
                        {
                            using (JsonDocument doc = JsonDocument.Parse(r3))
                                handlerErrorCaught =
                                    doc.RootElement.GetProperty("error").ValueKind == JsonValueKind.String &&
                                    doc.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null;
                        }
                        Check(handlerErrorCaught, "excepción del handler -> campo error, la sesión sigue viva");
                    }
                }

                // Reconexión: simula el reinicio del proceso servidor por Claude Desktop.
                using (var client2 = new NamedPipeClientStream(
                    ".", TestPipe, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    await client2.ConnectAsync(5000);
                    var utf8 = new UTF8Encoding(false);
                    using (var writer = new StreamWriter(client2, utf8, 4096, true))
                    using (var reader = new StreamReader(client2, utf8, false, 4096, true))
                    {
                        writer.NewLine = "\n";
                        writer.AutoFlush = true;
                        await writer.WriteLineAsync("{\"id\":\"12\",\"command\":\"__otro__\"}");
                        string r4 = await reader.ReadLineAsync();
                        Check(r4 != null && r4.Contains("\"id\":\"12\""),
                              "tras desconectar, el bucle acepta una conexión nueva");
                    }
                }
            }
            finally
            {
                bridge.Stop();
            }

            await Task.Delay(300);
            Check(!bridge.IsRunning, "Stop() cierra el bucle de accept");

            Console.WriteLine();
            Console.WriteLine(_failed == 0 ? "TODO OK" : $"{_failed} COMPROBACIONES FALLIDAS");
            return _failed == 0 ? 0 : 1;
        }
    }
}
