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
        private PlanarSketch _activeSketch;
        private List<SketchEntity> _sketchEntities = new List<SketchEntity>();

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
                    case "create_sketch":
                        result = CreateSketch(task.Payload);
                        break;
                    case "draw_rectangle":
                        result = DrawRectangle(task.Payload);
                        break;
                    case "draw_arc":
                        result = DrawArc(task.Payload);
                        break;
                    case "draw_slot":
                        result = DrawSlot(task.Payload);
                        break;
                    case "add_sketch_dimension":
                        result = AddSketchDimension(task.Payload);
                        break;
                    case "add_sketch_constraint":
                        result = AddSketchConstraint(task.Payload);
                        break;
                    case "project_geometry":
                        result = ProjectGeometry(task.Payload);
                        break;
                    case "close_sketch":
                        result = CloseSketch(task.Payload);
                        break;
                    case "extrude_profile":
                        result = ExtrudeProfile(task.Payload);
                        break;
                    case "revolve_profile":
                        result = RevolveProfile(task.Payload);
                        break;
                    case "sweep_profile":
                        result = SweepProfile(task.Payload);
                        break;
                    case "loft_profiles":
                        result = LoftProfiles(task.Payload);
                        break;
                    case "create_hole":
                        result = CreateHole(task.Payload);
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

        // ── Grupo: Bocetado 2D / Sketching ───────────────────────────────

        private PlanarSketch GetActiveSketch()
        {
            if (_activeSketch == null)
                throw new Exception("No hay boceto activo. Usa 'create_sketch' primero.");
            return _activeSketch;
        }

        private int TrackEntity(SketchEntity entity)
        {
            _sketchEntities.Add(entity);
            return _sketchEntities.Count - 1;
        }

        private SketchEntity GetEntity(int index)
        {
            if (index < 0 || index >= _sketchEntities.Count)
                throw new Exception($"Entidad {index} fuera de rango (0–{_sketchEntities.Count - 1}).");
            return _sketchEntities[index];
        }

        private WorkPlane FindOriginPlane(PartComponentDefinition compDef, string planeName)
        {
            string search = planeName.ToUpper().Trim();
            foreach (WorkPlane wp in compDef.WorkPlanes)
            {
                if (wp.Name.ToUpper().Replace(" ", "").Contains(search))
                    return wp;
            }
            switch (search)
            {
                case "XY": return compDef.WorkPlanes[1];
                case "XZ": return compDef.WorkPlanes[2];
                case "YZ": return compDef.WorkPlanes[3];
                default:   throw new Exception($"Plano '{planeName}' no reconocido. Usa: XY, XZ, YZ");
            }
        }

        private object CreateSketch(Dictionary<string, object> payload)
        {
            var doc = _inventorApp.ActiveDocument;
            if (!(doc is PartDocument partDoc))
                throw new Exception("El documento activo debe ser una pieza (.ipt)");

            string planeName = payload != null && payload.ContainsKey("plane")
                ? payload["plane"].ToString().ToUpper() : "XY";
            string sketchName = payload != null && payload.ContainsKey("name")
                ? payload["name"].ToString() : "";

            var compDef = partDoc.ComponentDefinition;
            WorkPlane workPlane = FindOriginPlane(compDef, planeName);

            _activeSketch = compDef.Sketches.Add(workPlane);
            _sketchEntities.Clear();

            if (!string.IsNullOrWhiteSpace(sketchName))
                _activeSketch.Name = sketchName;

            return new { SketchName = _activeSketch.Name, Plane = planeName };
        }

        private object DrawRectangle(Dictionary<string, object> payload)
        {
            var sketch = GetActiveSketch();
            var tg = _inventorApp.TransientGeometry;

            string mode = payload != null && payload.ContainsKey("mode")
                ? payload["mode"].ToString().ToLower() : "twopoint";

            SketchEntitiesEnumerator lines;
            if (mode == "centered")
            {
                double cx = Convert.ToDouble(payload["cx"]);
                double cy = Convert.ToDouble(payload["cy"]);
                double px = Convert.ToDouble(payload["px"]);
                double py = Convert.ToDouble(payload["py"]);
                lines = sketch.SketchLines.AddAsTwoPointCenteredRectangle(
                    tg.CreatePoint2d(cx, cy), tg.CreatePoint2d(px, py));
            }
            else
            {
                double x1 = Convert.ToDouble(payload["x1"]);
                double y1 = Convert.ToDouble(payload["y1"]);
                double x2 = Convert.ToDouble(payload["x2"]);
                double y2 = Convert.ToDouble(payload["y2"]);
                lines = sketch.SketchLines.AddAsTwoPointRectangle(
                    tg.CreatePoint2d(x1, y1), tg.CreatePoint2d(x2, y2));
            }

            var indices = new List<int>();
            foreach (SketchEntity entity in lines)
                indices.Add(TrackEntity(entity));

            return new { EntityIndices = indices, Type = "rectangle", Count = indices.Count };
        }

        private object DrawArc(Dictionary<string, object> payload)
        {
            var sketch = GetActiveSketch();
            var tg = _inventorApp.TransientGeometry;

            string mode = payload != null && payload.ContainsKey("mode")
                ? payload["mode"].ToString().ToLower() : "threepoints";

            SketchArc arc;
            if (mode == "center")
            {
                double cx = Convert.ToDouble(payload["cx"]);
                double cy = Convert.ToDouble(payload["cy"]);
                double x1 = Convert.ToDouble(payload["x1"]);
                double y1 = Convert.ToDouble(payload["y1"]);
                double x2 = Convert.ToDouble(payload["x2"]);
                double y2 = Convert.ToDouble(payload["y2"]);
                bool clockwise = payload.ContainsKey("clockwise") && Convert.ToBoolean(payload["clockwise"]);
                arc = sketch.SketchArcs.AddByCenterStartEndPoint(
                    tg.CreatePoint2d(cx, cy),
                    tg.CreatePoint2d(x1, y1),
                    tg.CreatePoint2d(x2, y2),
                    clockwise);
            }
            else
            {
                double x1 = Convert.ToDouble(payload["x1"]);
                double y1 = Convert.ToDouble(payload["y1"]);
                double x2 = Convert.ToDouble(payload["x2"]);
                double y2 = Convert.ToDouble(payload["y2"]);
                double x3 = Convert.ToDouble(payload["x3"]);
                double y3 = Convert.ToDouble(payload["y3"]);
                arc = sketch.SketchArcs.AddByThreePoints(
                    tg.CreatePoint2d(x1, y1),
                    tg.CreatePoint2d(x2, y2),
                    tg.CreatePoint2d(x3, y3));
            }

            // SketchArc → SketchEntity via COM QueryInterface (double cast through object)
            int index = TrackEntity((SketchEntity)(object)arc);
            return new { EntityIndex = index, Type = "arc" };
        }

        private object DrawSlot(Dictionary<string, object> payload)
        {
            var sketch = GetActiveSketch();
            var tg = _inventorApp.TransientGeometry;

            double cx1   = Convert.ToDouble(payload["cx1"]);
            double cy1   = Convert.ToDouble(payload["cy1"]);
            double cx2   = Convert.ToDouble(payload["cx2"]);
            double cy2   = Convert.ToDouble(payload["cy2"]);
            double width = Convert.ToDouble(payload["width"]);

            var entities = sketch.AddStraightSlotByCenterToCenter(
                tg.CreatePoint2d(cx1, cy1), tg.CreatePoint2d(cx2, cy2), width);

            var indices = new List<int>();
            foreach (SketchEntity entity in entities)
                indices.Add(TrackEntity(entity));

            return new { EntityIndices = indices, Type = "slot", Count = indices.Count };
        }

        private object AddSketchDimension(Dictionary<string, object> payload)
        {
            var sketch = GetActiveSketch();
            var tg = _inventorApp.TransientGeometry;

            string dimType  = payload["type"].ToString().ToLower();
            double tx       = payload.ContainsKey("text_x") ? Convert.ToDouble(payload["text_x"]) : 1.0;
            double ty       = payload.ContainsKey("text_y") ? Convert.ToDouble(payload["text_y"]) : 1.0;
            Point2d textPt  = tg.CreatePoint2d(tx, ty);
            bool driven     = payload.ContainsKey("driven") && Convert.ToBoolean(payload["driven"]);
            string valExpr  = null;
            if (payload.ContainsKey("value"))
            {
                string units = payload.ContainsKey("units") ? payload["units"].ToString() : "mm";
                valExpr = payload["value"].ToString() + " " + units;
            }

            switch (dimType)
            {
                case "line":
                case "length":
                {
                    int idx = Convert.ToInt32(payload["entity_index"]);
                    if (!(GetEntity(idx) is SketchLine line))
                        throw new Exception($"La entidad {idx} no es una línea.");
                    var dim = sketch.DimensionConstraints.AddTwoPointDistance(
                        line.StartSketchPoint, line.EndSketchPoint,
                        DimensionOrientationEnum.kAlignedDim, textPt, driven);
                    if (valExpr != null) dim.Parameter.Expression = valExpr;
                    return $"Cota de longitud en línea {idx}" + (valExpr != null ? $" = {valExpr}" : "");
                }
                case "radius":
                {
                    int idx = Convert.ToInt32(payload["entity_index"]);
                    var dim = sketch.DimensionConstraints.AddRadius(GetEntity(idx), textPt, driven);
                    if (valExpr != null) dim.Parameter.Expression = valExpr;
                    return $"Cota de radio en entidad {idx}" + (valExpr != null ? $" = {valExpr}" : "");
                }
                case "diameter":
                {
                    int idx = Convert.ToInt32(payload["entity_index"]);
                    var dim = sketch.DimensionConstraints.AddDiameter(GetEntity(idx), textPt, driven);
                    if (valExpr != null) dim.Parameter.Expression = valExpr;
                    return $"Cota de diámetro en entidad {idx}" + (valExpr != null ? $" = {valExpr}" : "");
                }
                case "distance":
                {
                    int idx1 = Convert.ToInt32(payload["entity1"]);
                    int idx2 = Convert.ToInt32(payload["entity2"]);
                    if (!(GetEntity(idx1) is SketchLine l1))
                        throw new Exception($"Entidad {idx1} debe ser una línea.");
                    if (!(GetEntity(idx2) is SketchLine l2))
                        throw new Exception($"Entidad {idx2} debe ser una línea.");
                    string ori = payload.ContainsKey("orientation")
                        ? payload["orientation"].ToString().ToLower() : "aligned";
                    var orient = ori == "horizontal" ? DimensionOrientationEnum.kHorizontalDim
                        : ori == "vertical" ? DimensionOrientationEnum.kVerticalDim
                        : DimensionOrientationEnum.kAlignedDim;
                    var dim = sketch.DimensionConstraints.AddTwoPointDistance(
                        l1.StartSketchPoint, l2.StartSketchPoint, orient, textPt, driven);
                    if (valExpr != null) dim.Parameter.Expression = valExpr;
                    return $"Cota de distancia entre entidades {idx1} y {idx2}" + (valExpr != null ? $" = {valExpr}" : "");
                }
                default:
                    throw new Exception($"Tipo '{dimType}' no reconocido. Usa: line, radius, diameter, distance");
            }
        }

        private object AddSketchConstraint(Dictionary<string, object> payload)
        {
            var sketch = GetActiveSketch();
            string ctype = payload["type"].ToString().ToLower();

            switch (ctype)
            {
                case "horizontal":
                {
                    int idx = Convert.ToInt32(payload["entity_index"]);
                    sketch.GeometricConstraints.AddHorizontal(GetEntity(idx), false);
                    return $"Restricción horizontal en entidad {idx}.";
                }
                case "vertical":
                {
                    int idx = Convert.ToInt32(payload["entity_index"]);
                    sketch.GeometricConstraints.AddVertical(GetEntity(idx), false);
                    return $"Restricción vertical en entidad {idx}.";
                }
                case "tangent":
                {
                    int idx1 = Convert.ToInt32(payload["entity1"]);
                    int idx2 = Convert.ToInt32(payload["entity2"]);
                    sketch.GeometricConstraints.AddTangent(GetEntity(idx1), GetEntity(idx2), null);
                    return $"Restricción tangente entre entidades {idx1} y {idx2}.";
                }
                case "coincident":
                {
                    int idx1 = Convert.ToInt32(payload["entity1"]);
                    int idx2 = Convert.ToInt32(payload["entity2"]);
                    sketch.GeometricConstraints.AddCoincident(GetEntity(idx1), GetEntity(idx2));
                    return $"Restricción coincidente entre entidades {idx1} y {idx2}.";
                }
                case "parallel":
                {
                    int idx1 = Convert.ToInt32(payload["entity1"]);
                    int idx2 = Convert.ToInt32(payload["entity2"]);
                    sketch.GeometricConstraints.AddParallel(GetEntity(idx1), GetEntity(idx2), false, false);
                    return $"Restricción paralela entre entidades {idx1} y {idx2}.";
                }
                case "perpendicular":
                {
                    int idx1 = Convert.ToInt32(payload["entity1"]);
                    int idx2 = Convert.ToInt32(payload["entity2"]);
                    sketch.GeometricConstraints.AddPerpendicular(GetEntity(idx1), GetEntity(idx2), false, false);
                    return $"Restricción perpendicular entre entidades {idx1} y {idx2}.";
                }
                case "equal":
                case "equal_length":
                {
                    int idx1 = Convert.ToInt32(payload["entity1"]);
                    int idx2 = Convert.ToInt32(payload["entity2"]);
                    if (!(GetEntity(idx1) is SketchLine l1))
                        throw new Exception($"Entidad {idx1} debe ser una línea.");
                    if (!(GetEntity(idx2) is SketchLine l2))
                        throw new Exception($"Entidad {idx2} debe ser una línea.");
                    sketch.GeometricConstraints.AddEqualLength(l1, l2);
                    return $"Restricción igual longitud entre entidades {idx1} y {idx2}.";
                }
                case "concentric":
                {
                    int idx1 = Convert.ToInt32(payload["entity1"]);
                    int idx2 = Convert.ToInt32(payload["entity2"]);
                    sketch.GeometricConstraints.AddConcentric(GetEntity(idx1), GetEntity(idx2));
                    return $"Restricción concéntrica entre entidades {idx1} y {idx2}.";
                }
                default:
                    throw new Exception(
                        $"Restricción '{ctype}' no reconocida. Usa: horizontal, vertical, tangent, coincident, parallel, perpendicular, equal_length, concentric");
            }
        }

        private object ProjectGeometry(Dictionary<string, object> payload)
        {
            var sketch = GetActiveSketch();
            var doc = _inventorApp.ActiveDocument;
            if (!(doc is PartDocument partDoc))
                throw new Exception("El documento activo debe ser una pieza (.ipt)");

            var compDef = partDoc.ComponentDefinition;
            string source = payload != null && payload.ContainsKey("source")
                ? payload["source"].ToString().ToLower() : "origin";

            int count = 0;
            if (source == "model")
            {
                if (compDef.SurfaceBodies.Count == 0)
                    throw new Exception("La pieza no tiene cuerpo sólido para proyectar.");
                foreach (Edge edge in compDef.SurfaceBodies[1].Edges)
                {
                    try { sketch.AddByProjectingEntity(edge); count++; } catch { }
                }
            }
            else // "origin"
            {
                try { sketch.AddByProjectingEntity(compDef.WorkPoints[1]); count++; } catch { }
                try { sketch.AddByProjectingEntity(compDef.WorkAxes[1]);   count++; } catch { }
                try { sketch.AddByProjectingEntity(compDef.WorkAxes[2]);   count++; } catch { }
            }

            return $"{count} elemento(s) proyectados desde '{source}' al boceto.";
        }

        private object CloseSketch(Dictionary<string, object> payload)
        {
            if (_activeSketch == null)
                throw new Exception("No hay boceto activo.");

            string name = _activeSketch.Name;
            try { _activeSketch.ExitEdit(); } catch { }
            _activeSketch = null;
            _sketchEntities.Clear();

            return $"Boceto '{name}' cerrado y listo para operaciones 3D.";
        }

        // ── Grupo: Sólidos base (extrusión / revolución / barrido / loft / agujero) ─

        private PartComponentDefinition GetPartCompDef()
        {
            var doc = _inventorApp.ActiveDocument;
            if (!(doc is PartDocument partDoc))
                throw new Exception("El documento activo debe ser una pieza (.ipt)");
            return partDoc.ComponentDefinition;
        }

        private PartFeatureOperationEnum ParseOperation(Dictionary<string, object> payload)
        {
            string op = payload != null && payload.ContainsKey("operation")
                ? payload["operation"].ToString().ToLower() : "join";
            switch (op)
            {
                case "cut":       return PartFeatureOperationEnum.kCutOperation;
                case "intersect": return PartFeatureOperationEnum.kIntersectOperation;
                default:          return PartFeatureOperationEnum.kJoinOperation;
            }
        }

        private string BuildExpr(Dictionary<string, object> payload, string key, string defaultVal, string defaultUnit = "mm")
        {
            if (payload == null || !payload.ContainsKey(key)) return defaultVal;
            string unit = payload.ContainsKey("units") ? payload["units"].ToString() : defaultUnit;
            return payload[key].ToString() + " " + unit;
        }

        private object ExtrudeProfile(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();
            var sketch  = GetActiveSketch();

            Profile profile = sketch.Profiles._AddForSolid();

            string distExpr = BuildExpr(payload, "distance", "10 mm");
            var operation   = ParseOperation(payload);

            string dirStr = payload != null && payload.ContainsKey("direction")
                ? payload["direction"].ToString().ToLower() : "positive";
            var direction = dirStr == "negative"  ? PartFeatureExtentDirectionEnum.kNegativeExtentDirection
                : dirStr == "symmetric"           ? PartFeatureExtentDirectionEnum.kSymmetricExtentDirection
                : PartFeatureExtentDirectionEnum.kPositiveExtentDirection;

            var feature = compDef.Features.ExtrudeFeatures.AddByDistanceExtent(
                profile, distExpr, direction, operation, 0);

            return new { FeatureName = feature.Name, Distance = distExpr, Operation = dirStr };
        }

        private object RevolveProfile(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();
            var sketch  = GetActiveSketch();

            Profile profile = sketch.Profiles._AddForSolid();
            var operation   = ParseOperation(payload);

            string axisName = payload != null && payload.ContainsKey("axis")
                ? payload["axis"].ToString().ToUpper() : "X";
            object axis;
            switch (axisName)
            {
                case "X": axis = compDef.WorkAxes[1]; break;
                case "Y": axis = compDef.WorkAxes[2]; break;
                case "Z": axis = compDef.WorkAxes[3]; break;
                default:  throw new Exception($"Eje '{axisName}' no reconocido. Usa: X, Y, Z");
            }

            double angleDeg = payload != null && payload.ContainsKey("angle")
                ? Convert.ToDouble(payload["angle"]) : 360.0;
            bool full = Math.Abs(angleDeg - 360.0) < 0.001;

            RevolveFeature feature;
            if (full)
            {
                feature = compDef.Features.RevolveFeatures.AddFull(profile, axis, operation);
            }
            else
            {
                string angleExpr = angleDeg.ToString("F4") + " deg";
                string dirStr = payload != null && payload.ContainsKey("direction")
                    ? payload["direction"].ToString().ToLower() : "positive";
                var dir = dirStr == "negative"
                    ? PartFeatureExtentDirectionEnum.kNegativeExtentDirection
                    : PartFeatureExtentDirectionEnum.kPositiveExtentDirection;
                feature = compDef.Features.RevolveFeatures.AddByAngle(
                    profile, axis, angleExpr, dir, operation);
            }

            return new { FeatureName = feature.Name, Angle = angleDeg, Axis = axisName };
        }

        private object SweepProfile(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();
            var sketch  = GetActiveSketch();

            Profile profile = sketch.Profiles._AddForSolid();
            var operation   = ParseOperation(payload);

            // Find path sketch by name (different from the active/profile sketch)
            string pathSketchName = payload != null && payload.ContainsKey("path_sketch")
                ? payload["path_sketch"].ToString() : "";
            PlanarSketch pathSketch = null;

            foreach (PlanarSketch s in compDef.Sketches)
            {
                if (!string.IsNullOrEmpty(pathSketchName))
                {
                    if (s.Name == pathSketchName) { pathSketch = s; break; }
                }
                else if (s != _activeSketch)
                {
                    pathSketch = s; // use last non-active sketch as fallback
                }
            }

            if (pathSketch == null)
                throw new Exception(
                    "No se encontró boceto de trayectoria. Indica 'path_sketch' con el nombre del boceto.");

            // Collect non-reference entities from the path sketch
            var pathEntities = _inventorApp.TransientObjects.CreateObjectCollection();
            foreach (SketchEntity entity in pathSketch.SketchEntities)
            {
                if (!entity.Reference) pathEntities.Add(entity);
            }

            if (pathEntities.Count == 0)
                throw new Exception($"El boceto de trayectoria '{pathSketch.Name}' no contiene entidades válidas.");

            var sweepFeatures = compDef.Features.SweepFeatures;
            Path sweepPath = sweepFeatures.CreatePath(pathEntities);

            var feature = sweepFeatures.AddUsingPath(
                profile, sweepPath, operation,
                SweepProfileOrientationEnum.kNormalToPath, 0);

            return new { FeatureName = feature.Name, PathSketch = pathSketch.Name };
        }

        private object LoftProfiles(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();

            // Parse sketch names (accepts JArray or comma-separated string)
            var sketchNames = new List<string>();
            if (payload != null && payload.ContainsKey("sketches"))
            {
                var val = payload["sketches"];
                if (val is Newtonsoft.Json.Linq.JArray arr)
                {
                    foreach (var item in arr) sketchNames.Add(item.ToString());
                }
                else
                {
                    foreach (var part in val.ToString().Split(','))
                        sketchNames.Add(part.Trim());
                }
            }

            if (sketchNames.Count < 2)
                throw new Exception(
                    "Se necesitan al menos 2 bocetos. Usa 'sketches': [\"boceto1\", \"boceto2\"]");

            var sections = _inventorApp.TransientObjects.CreateObjectCollection();
            foreach (string name in sketchNames)
            {
                PlanarSketch found = null;
                foreach (PlanarSketch s in compDef.Sketches)
                    if (s.Name == name) { found = s; break; }

                if (found == null)
                    throw new Exception($"Boceto '{name}' no encontrado en el documento.");

                sections.Add(found.Profiles._AddForSolid());
            }

            var operation   = ParseOperation(payload);
            var loftFeatures = compDef.Features.LoftFeatures;
            var loftDef     = loftFeatures.CreateLoftDefinition(sections, operation);
            var feature     = loftFeatures.Add(loftDef);

            return new { FeatureName = feature.Name, Sketches = sketchNames, Operation = operation.ToString() };
        }

        private object CreateHole(Dictionary<string, object> payload)
        {
            var compDef     = GetPartCompDef();
            var sketch      = GetActiveSketch();
            var holeFeatures = compDef.Features.HoleFeatures;

            string holeType  = payload != null && payload.ContainsKey("hole_type")
                ? payload["hole_type"].ToString().ToLower() : "drilled";
            string diamExpr  = BuildExpr(payload, "diameter", "10 mm");
            string depthExpr = BuildExpr(payload, "depth", "20 mm");
            bool throughAll  = payload != null && payload.ContainsKey("through")
                && Convert.ToBoolean(payload["through"]);
            var dir = PartFeatureExtentDirectionEnum.kNegativeExtentDirection;

            // Collect placement points: prefer circle centers, else sketch points
            var pts = _inventorApp.TransientObjects.CreateObjectCollection();
            foreach (SketchCircle circle in sketch.SketchCircles)
                if (!circle.Reference) pts.Add(circle.CenterSketchPoint);

            if (pts.Count == 0)
            {
                // Skip index 1 (default origin point of the sketch)
                for (int i = 2; i <= sketch.SketchPoints.Count; i++)
                    pts.Add(sketch.SketchPoints[i]);
            }

            if (pts.Count == 0)
                throw new Exception(
                    "El boceto activo necesita círculos o puntos para ubicar los agujeros.");

            var placement = holeFeatures.CreateSketchPlacementDefinition(pts);
            HoleFeature hole;

            if (holeType == "cbore")
            {
                string cboreDiam  = BuildExpr(payload, "cbore_diameter", "16 mm");
                string cboreDepth = BuildExpr(payload, "cbore_depth", "5 mm");
                hole = throughAll
                    ? holeFeatures.AddCBoreByThroughAllExtent(placement, diamExpr, dir, cboreDiam, cboreDepth)
                    : holeFeatures.AddCBoreByDistanceExtent(placement, diamExpr, depthExpr, dir, cboreDiam, cboreDepth, false, null);
            }
            else if (holeType == "csink")
            {
                string csinkDiam  = BuildExpr(payload, "csink_diameter", "18 mm");
                string csinkAngle = BuildExpr(payload, "csink_angle", "90 deg", "deg");
                hole = throughAll
                    ? holeFeatures.AddCSinkByThroughAllExtent(placement, diamExpr, dir, csinkDiam, csinkAngle)
                    : holeFeatures.AddCSinkByDistanceExtent(placement, diamExpr, depthExpr, dir, csinkDiam, csinkAngle, false, null);
            }
            else // drilled
            {
                hole = throughAll
                    ? holeFeatures.AddDrilledByThroughAllExtent(placement, diamExpr, dir)
                    : holeFeatures.AddDrilledByDistanceExtent(placement, diamExpr, depthExpr, dir, false, null);
            }

            return new { FeatureName = hole.Name, HoleType = holeType, Diameter = diamExpr };
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
