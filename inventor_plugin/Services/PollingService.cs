using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inventor;
using Newtonsoft.Json;

namespace InventorMCPBridge.Services
{
    public class PollingService
    {
        private readonly Application _inventorApp;
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _userId;
        private readonly HttpClient _client;
        private CancellationTokenSource _cts;
        private bool _isRunning;

        public PollingService(Application inventorApp, string baseUrl, string apiKey, string userId)
        {
            _inventorApp = inventorApp;
            _baseUrl = baseUrl.TrimEnd('/');
            _apiKey = apiKey;
            _userId = userId;
            _client = new HttpClient();
            _client.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        }

        public bool TestConnection()
        {
            try
            {
                var response = _client.GetAsync($"{_baseUrl}/api/health")
                                      .GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            Task.Run(() => PollLoop(_cts.Token));
        }

        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
        }

        private async Task PollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var response = await _client.GetAsync($"{_baseUrl}/api/poll/{_userId}", token);
                    if (response.IsSuccessStatusCode)
                    {
                        string content = await response.Content.ReadAsStringAsync();
                        if (!string.IsNullOrEmpty(content))
                        {
                            var task = JsonConvert.DeserializeObject<McpTask>(content);
                            if (task != null && !string.IsNullOrEmpty(task.TaskId))
                                await HandleTask(task);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Silencioso — el servidor puede no estar disponible
                }

                await Task.Delay(2000, token);
            }
        }

        private async Task HandleTask(McpTask task)
        {
            object result = null;
            string error = null;

            try
            {
                switch (task.Command)
                {
                    case "get_active_doc_info":
                        result = GetActiveDocInfo();
                        break;
                    case "list_parameters":
                        result = ListParameters();
                        break;
                    case "update_parameter":
                        result = UpdateParameter(task.Payload);
                        break;
                    case "export_step":
                        result = ExportStep();
                        break;
                    case "create_line":
                        result = CreateLine(task.Payload);
                        break;
                    case "create_circle":
                        result = CreateCircle(task.Payload);
                        break;
                    case "create_new_part":
                        result = CreateNewPart(task.Payload);
                        break;
                    case "create_new_assembly":
                        result = CreateNewAssembly(task.Payload);
                        break;
                    case "open_document":
                        result = OpenDocument(task.Payload);
                        break;
                    case "save_document":
                        result = SaveDocument(task.Payload);
                        break;
                    case "change_units":
                        result = ChangeUnits(task.Payload);
                        break;
                    case "set_material":
                        result = SetMaterial(task.Payload);
                        break;
                    default:
                        error = $"Comando no reconocido: {task.Command}";
                        break;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            await SendResult(task.TaskId, result, error);
        }

        private async Task SendResult(string taskId, object result, string error)
        {
            string json = JsonConvert.SerializeObject(new { task_id = taskId, result, error });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _client.PostAsync($"{_baseUrl}/api/result/{taskId}", content);
        }

        private PlanarSketch GetOrCreateSketchOnXY()
        {
            Document doc = _inventorApp.ActiveDocument;
            if (!(doc is PartDocument partDoc))
                throw new Exception("El documento activo debe ser una pieza (.ipt)");

            PartComponentDefinition compDef = partDoc.ComponentDefinition;
            foreach (PlanarSketch s in compDef.Sketches)
            {
                if (s.Name == "MCP Sketch") return s;
            }

            WorkPlane xyPlane = compDef.WorkPlanes[1];
            PlanarSketch sketch = compDef.Sketches.Add(xyPlane);
            sketch.Name = "MCP Sketch";
            return sketch;
        }

        private object CreateLine(Dictionary<string, object> payload)
        {
            double x1 = Convert.ToDouble(payload["x1"]);
            double y1 = Convert.ToDouble(payload["y1"]);
            double x2 = Convert.ToDouble(payload["x2"]);
            double y2 = Convert.ToDouble(payload["y2"]);

            PlanarSketch sketch = GetOrCreateSketchOnXY();
            TransientGeometry tg = _inventorApp.TransientGeometry;
            sketch.SketchLines.AddByTwoPoints(tg.CreatePoint2d(x1, y1), tg.CreatePoint2d(x2, y2));
            return $"Línea creada de ({x1},{y1}) a ({x2},{y2})";
        }

        private object CreateCircle(Dictionary<string, object> payload)
        {
            double cx = Convert.ToDouble(payload["center_x"]);
            double cy = Convert.ToDouble(payload["center_y"]);
            double r  = Convert.ToDouble(payload["radius"]);

            PlanarSketch sketch = GetOrCreateSketchOnXY();
            TransientGeometry tg = _inventorApp.TransientGeometry;
            sketch.SketchCircles.AddByCenterRadius(tg.CreatePoint2d(cx, cy), r);
            return $"Círculo con centro ({cx},{cy}) y radio {r}";
        }

        private object GetActiveDocInfo()
        {
            Document doc = _inventorApp.ActiveDocument;
            if (doc == null) return new { error = "No hay documento activo" };

            return new
            {
                DisplayName   = doc.DisplayName,
                FullFileName  = doc.FullFileName,
                DocumentType  = doc.DocumentType.ToString(),
                UnitsOfMeasure = doc.UnitsOfMeasure.LengthUnits.ToString()
            };
        }

        private object ListParameters()
        {
            Document doc = _inventorApp.ActiveDocument;
            if (doc == null) return new List<object>();

            Parameters docParams = null;
            if (doc is PartDocument partDoc)         docParams = partDoc.ComponentDefinition.Parameters;
            else if (doc is AssemblyDocument asmDoc) docParams = asmDoc.ComponentDefinition.Parameters;

            var list = new List<object>();
            if (docParams != null)
            {
                foreach (Inventor.Parameter p in docParams)
                    list.Add(new { Name = p.Name, Value = p.Value, Expression = p.Expression, Units = p.get_Units() });
            }
            return list;
        }

        private object UpdateParameter(Dictionary<string, object> payload)
        {
            if (!payload.ContainsKey("name") || !payload.ContainsKey("value"))
                throw new Exception("Faltan parámetros 'name' o 'value'");

            string name  = payload["name"].ToString();
            string value = payload["value"].ToString();

            Document doc = _inventorApp.ActiveDocument;
            Parameters docParams = null;
            if (doc is PartDocument partDoc)         docParams = partDoc.ComponentDefinition.Parameters;
            else if (doc is AssemblyDocument asmDoc) docParams = asmDoc.ComponentDefinition.Parameters;

            if (docParams == null) throw new Exception("El documento no soporta parámetros");

            foreach (Inventor.Parameter p in docParams)
            {
                if (p.Name == name)
                {
                    p.Expression = value;
                    doc.Update();
                    return $"Parámetro '{name}' actualizado a {value}";
                }
            }

            throw new Exception($"Parámetro '{name}' no encontrado");
        }

        private object ExportStep()
        {
            Document doc = _inventorApp.ActiveDocument;
            if (doc == null) throw new Exception("No hay documento activo");

            string tempPath  = System.IO.Path.GetTempPath();
            string fileName  = System.IO.Path.GetFileNameWithoutExtension(doc.FullFileName);
            if (string.IsNullOrEmpty(fileName)) fileName = "Export";
            string stepPath  = System.IO.Path.Combine(tempPath, fileName + ".step");

            doc.SaveAs(stepPath, true);
            return $"Exportado a {stepPath}";
        }

        // ── Grupo: Inicialización y gestión de entorno ────────────────────

        private string GetTemplatePath(DocumentTypeEnum docType, bool metric)
        {
            try
            {
                var sys = metric
                    ? SystemOfMeasureEnum.kMetricSystemOfMeasure
                    : SystemOfMeasureEnum.kEnglishSystemOfMeasure;
                return _inventorApp.FileManager.GetTemplateFile(
                    docType, sys, DraftingStandardEnum.kISO_DraftingStandard);
            }
            catch
            {
                return string.Empty;
            }
        }

        private object CreateNewPart(Dictionary<string, object> payload)
        {
            string units = payload != null && payload.ContainsKey("units")
                ? payload["units"].ToString().ToLower() : "metric";
            bool metric = units != "imperial";

            string tpl = GetTemplatePath(DocumentTypeEnum.kPartDocumentObject, metric);
            var doc = (PartDocument)_inventorApp.Documents.Add(
                DocumentTypeEnum.kPartDocumentObject, tpl, true);

            return new { DisplayName = doc.DisplayName, FullFileName = doc.FullFileName, Units = units };
        }

        private object CreateNewAssembly(Dictionary<string, object> payload)
        {
            string units = payload != null && payload.ContainsKey("units")
                ? payload["units"].ToString().ToLower() : "metric";
            bool metric = units != "imperial";

            string tpl = GetTemplatePath(DocumentTypeEnum.kAssemblyDocumentObject, metric);
            var doc = (AssemblyDocument)_inventorApp.Documents.Add(
                DocumentTypeEnum.kAssemblyDocumentObject, tpl, true);

            return new { DisplayName = doc.DisplayName, FullFileName = doc.FullFileName, Units = units };
        }

        private object OpenDocument(Dictionary<string, object> payload)
        {
            if (payload == null || !payload.ContainsKey("path"))
                throw new Exception("Falta el parámetro 'path'");

            string filePath = payload["path"].ToString();
            if (!System.IO.File.Exists(filePath))
                throw new Exception($"El archivo no existe: {filePath}");

            var doc = _inventorApp.Documents.Open(filePath, true);
            return new
            {
                DisplayName  = doc.DisplayName,
                FullFileName = doc.FullFileName,
                DocumentType = doc.DocumentType.ToString()
            };
        }

        private object SaveDocument(Dictionary<string, object> payload)
        {
            var doc = _inventorApp.ActiveDocument;
            if (doc == null) throw new Exception("No hay documento activo");

            bool hasPath = payload != null && payload.ContainsKey("path")
                && !string.IsNullOrWhiteSpace(payload["path"]?.ToString());

            if (hasPath)
            {
                string savePath = payload["path"].ToString();
                doc.SaveAs(savePath, false);
                return $"Documento guardado en: {savePath}";
            }

            if (string.IsNullOrEmpty(doc.FullFileName))
                throw new Exception(
                    "El documento es nuevo y no tiene ruta. Proporciona el parámetro 'path'.");

            doc.Save();
            return $"Documento guardado: {doc.FullFileName}";
        }

        private object ChangeUnits(Dictionary<string, object> payload)
        {
            if (payload == null || !payload.ContainsKey("units"))
                throw new Exception("Falta el parámetro 'units'. Válidos: mm, cm, m, in, ft");

            var doc = _inventorApp.ActiveDocument;
            if (doc == null) throw new Exception("No hay documento activo");

            string units = payload["units"].ToString().ToLower().Trim();
            UnitsTypeEnum unitType;
            switch (units)
            {
                case "mm": unitType = UnitsTypeEnum.kMillimeterLengthUnits; break;
                case "cm": unitType = UnitsTypeEnum.kCentimeterLengthUnits; break;
                case "m":  unitType = UnitsTypeEnum.kMeterLengthUnits;      break;
                case "in": unitType = UnitsTypeEnum.kInchLengthUnits;       break;
                case "ft": unitType = UnitsTypeEnum.kFootLengthUnits;       break;
                default:   throw new Exception($"Unidad '{units}' no válida. Usa: mm, cm, m, in, ft");
            }

            doc.UnitsOfMeasure.LengthUnits = unitType;
            return $"Unidades de longitud cambiadas a '{units}'.";
        }

        private object SetMaterial(Dictionary<string, object> payload)
        {
            if (payload == null || !payload.ContainsKey("material_name"))
                throw new Exception("Falta el parámetro 'material_name'");

            var doc = _inventorApp.ActiveDocument;
            if (!(doc is PartDocument partDoc))
                throw new Exception("El documento activo debe ser una pieza (.ipt)");

            string materialName = payload["material_name"].ToString();

            // Search document-local materials first (fastest path)
            foreach (Material mat in partDoc.Materials)
            {
                if (string.Compare(mat.Name, materialName, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    partDoc.ComponentDefinition.Material = mat;
                    return $"Material '{mat.Name}' asignado.";
                }
            }

            // Fall back to global styles library
            foreach (Material mat in _inventorApp.StylesManager.Materials)
            {
                if (string.Compare(mat.Name, materialName, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    Material localMat = mat.ConvertToLocal();
                    partDoc.ComponentDefinition.Material = localMat ?? mat;
                    return $"Material '{mat.Name}' asignado desde biblioteca global.";
                }
            }

            throw new Exception(
                $"Material '{materialName}' no encontrado. Verifica el nombre exacto en Inventor.");
        }
    }

    public class McpTask
    {
        [JsonProperty("task_id")]
        public string TaskId { get; set; }

        [JsonProperty("command")]
        public string Command { get; set; }

        [JsonProperty("payload")]
        public Dictionary<string, object> Payload { get; set; }
    }
}
