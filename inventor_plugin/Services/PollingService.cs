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
                        result = ExportStep(task.Payload);
                        break;
                    case "render_screenshot":
                        result = RenderScreenshot(task.Payload);
                        break;
                    case "export_to_stl":
                        result = ExportToStl(task.Payload);
                        break;
                    case "export_to_dxf":
                        result = ExportToDxf(task.Payload);
                        break;
                    case "check_interference":
                        result = CheckInterference(task.Payload);
                        break;
                    case "get_mass_properties":
                        result = GetMassProperties(task.Payload);
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
                    case "add_fillet":
                        result = AddFillet(task.Payload);
                        break;
                    case "add_chamfer":
                        result = AddChamfer(task.Payload);
                        break;
                    case "shell_solid":
                        result = ShellSolid(task.Payload);
                        break;
                    case "thread_feature":
                        result = AddThreadFeature(task.Payload);
                        break;
                    case "split_body":
                        result = SplitBody(task.Payload);
                        break;
                    case "combine_bodies":
                        result = CombineBodies(task.Payload);
                        break;
                    case "create_rectangular_pattern":
                        result = CreateRectangularPattern(task.Payload);
                        break;
                    case "create_circular_pattern":
                        result = CreateCircularPattern(task.Payload);
                        break;
                    case "mirror_feature":
                        result = MirrorFeature(task.Payload);
                        break;
                    case "mirror_solid":
                        result = MirrorSolid(task.Payload);
                        break;
                    case "get_parameters":
                        result = GetParametersRich();
                        break;
                    case "set_parameter_value":
                        result = SetParameterValue(task.Payload);
                        break;
                    case "add_custom_parameter":
                        result = AddCustomParameter(task.Payload);
                        break;
                    case "update_iproperties":
                        result = UpdateIProperties(task.Payload);
                        break;
                    case "create_work_plane":
                        result = CreateWorkPlane(task.Payload);
                        break;
                    case "create_work_axis":
                        result = CreateWorkAxis(task.Payload);
                        break;
                    case "create_work_point":
                        result = CreateWorkPoint(task.Payload);
                        break;
                    case "insert_component":
                        result = InsertComponent(task.Payload);
                        break;
                    case "ground_component":
                        result = GroundComponent(task.Payload);
                        break;
                    case "add_assembly_constraint":
                        result = AddAssemblyConstraint(task.Payload);
                        break;
                    case "add_assembly_joint":
                        result = AddAssemblyJoint(task.Payload);
                        break;
                    case "get_assembly_bom":
                        result = GetAssemblyBOM(task.Payload);
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

            PlanarSketch sketch = GetActiveSketch();
            TransientGeometry tg = _inventorApp.TransientGeometry;
            SketchLine line = sketch.SketchLines.AddByTwoPoints(
                tg.CreatePoint2d(x1, y1), tg.CreatePoint2d(x2, y2));
            int index = TrackEntity((SketchEntity)(object)line);
            return new
            {
                Status           = "ok",
                Type             = "line",
                EntityIndex      = index,
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                SketchName       = sketch.Name,
                TotalEntityCount = _sketchEntities.Count
            };
        }

        private object CreateCircle(Dictionary<string, object> payload)
        {
            double cx = Convert.ToDouble(payload["center_x"]);
            double cy = Convert.ToDouble(payload["center_y"]);
            double r  = Convert.ToDouble(payload["radius"]);

            PlanarSketch sketch = GetActiveSketch();
            TransientGeometry tg = _inventorApp.TransientGeometry;
            SketchCircle circle = sketch.SketchCircles.AddByCenterRadius(
                tg.CreatePoint2d(cx, cy), r);
            int index = TrackEntity((SketchEntity)(object)circle);
            return new
            {
                Status           = "ok",
                Type             = "circle",
                EntityIndex      = index,
                CenterX          = cx,
                CenterY          = cy,
                Radius           = r,
                SketchName       = sketch.Name,
                TotalEntityCount = _sketchEntities.Count
            };
        }

        private object GetActiveDocInfo()
        {
            Document doc = _inventorApp.ActiveDocument;
            if (doc == null) return new { Status = "error", Error = "No hay documento activo" };

            int sketchCount = 0, bodyCount = 0;
            string materialName = "";
            if (doc is PartDocument pd)
            {
                var cd = pd.ComponentDefinition;
                sketchCount = cd.Sketches.Count;
                bodyCount = cd.SurfaceBodies.Count;
                try { materialName = cd.Material.Name; } catch { }
            }

            return new
            {
                Status       = "ok",
                DisplayName  = doc.DisplayName,
                FullFileName = doc.FullFileName,
                DocumentType = doc.DocumentType.ToString(),
                Units        = doc.UnitsOfMeasure.LengthUnits.ToString(),
                SketchCount  = sketchCount,
                BodyCount    = bodyCount,
                Material     = materialName,
                ActiveSketch = _activeSketch != null ? _activeSketch.Name : null
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
                    return new { Status = "ok", Name = name, Value = value, DocumentName = doc.DisplayName };
                }
            }

            throw new Exception($"Parámetro '{name}' no encontrado");
        }

        private object ExportStep(Dictionary<string, object> payload)
        {
            Document doc = _inventorApp.ActiveDocument;
            if (doc == null) throw new Exception("No hay documento activo");

            string fileName = System.IO.Path.GetFileNameWithoutExtension(doc.FullFileName);
            if (string.IsNullOrEmpty(fileName)) fileName = "Export";

            string savePath = payload != null && payload.ContainsKey("path")
                && !string.IsNullOrWhiteSpace(payload["path"]?.ToString())
                ? payload["path"].ToString()
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName + ".step");

            doc.SaveAs(savePath, true);

            long fileSize = 0;
            try { fileSize = new System.IO.FileInfo(savePath).Length; } catch { }

            return new
            {
                Status        = "ok",
                FilePath      = savePath,
                FileSizeBytes = fileSize,
                DocumentName  = doc.DisplayName,
                DocumentType  = doc.DocumentType.ToString()
            };
        }

        private object RenderScreenshot(Dictionary<string, object> payload)
        {
            Document doc = _inventorApp.ActiveDocument;
            if (doc == null) throw new Exception("No hay documento activo");

            int width  = payload != null && payload.ContainsKey("width")  ? Convert.ToInt32(payload["width"])  : 1280;
            int height = payload != null && payload.ContainsKey("height") ? Convert.ToInt32(payload["height"]) : 720;

            string tempBmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"inv_{Guid.NewGuid():N}.bmp");
            string tempPng = System.IO.Path.ChangeExtension(tempBmp, ".png");

            Color white = _inventorApp.TransientObjects.CreateColor(255, 255, 255);
            Color black = _inventorApp.TransientObjects.CreateColor(0, 0, 0);
            _inventorApp.ActiveView.Camera.SaveAsBitmap(tempBmp, width, height, white, black);

            // Convert BMP → PNG for smaller base64 payload
            using (var bmpImg = new System.Drawing.Bitmap(tempBmp))
                bmpImg.Save(tempPng, System.Drawing.Imaging.ImageFormat.Png);

            byte[] imageBytes = System.IO.File.ReadAllBytes(tempPng);
            string base64    = Convert.ToBase64String(imageBytes);
            try { System.IO.File.Delete(tempBmp); } catch { }
            try { System.IO.File.Delete(tempPng); } catch { }

            return new
            {
                Status       = "ok",
                MimeType     = "image/png",
                Width        = width,
                Height       = height,
                Base64Data   = base64,
                DocumentName = doc.DisplayName,
                FullFileName = doc.FullFileName
            };
        }

        private object ExportToStl(Dictionary<string, object> payload)
        {
            Document doc = _inventorApp.ActiveDocument;
            if (doc == null) throw new Exception("No hay documento activo");
            if (!(doc is PartDocument) && !(doc is AssemblyDocument))
                throw new Exception("STL solo se exporta desde piezas (.ipt) o ensambles (.iam).");

            string fileName = System.IO.Path.GetFileNameWithoutExtension(doc.FullFileName);
            if (string.IsNullOrEmpty(fileName)) fileName = "Export";

            string savePath = payload != null && payload.ContainsKey("path")
                && !string.IsNullOrWhiteSpace(payload["path"]?.ToString())
                ? payload["path"].ToString()
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName + ".stl");

            doc.SaveAs(savePath, true);

            long fileSize = 0;
            try { fileSize = new System.IO.FileInfo(savePath).Length; } catch { }

            return new
            {
                Status        = "ok",
                FilePath      = savePath,
                FileSizeBytes = fileSize,
                DocumentName  = doc.DisplayName,
                DocumentType  = doc.DocumentType.ToString()
            };
        }

        private object ExportToDxf(Dictionary<string, object> payload)
        {
            Document doc = _inventorApp.ActiveDocument;
            if (doc == null) throw new Exception("No hay documento activo");

            string fileName = System.IO.Path.GetFileNameWithoutExtension(doc.FullFileName);
            if (string.IsNullOrEmpty(fileName)) fileName = "Export";

            string savePath = payload != null && payload.ContainsKey("path")
                && !string.IsNullOrWhiteSpace(payload["path"]?.ToString())
                ? payload["path"].ToString()
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName + ".dxf");

            string exportSource;

            if (doc is PartDocument partDoc)
            {
                if (!(partDoc.ComponentDefinition is SheetMetalComponentDefinition smDef))
                    throw new Exception("DXF desde piezas 3D requiere crear un plano (Drawing). Para chapa metálica, usa piezas con patrón plano.");

                if (!smDef.HasFlatPattern)
                    throw new Exception("La pieza de chapa metálica no tiene patrón plano. Activa el patrón plano antes de exportar.");

                doc.SaveAs(savePath, true);
                exportSource = "flat_pattern";
            }
            else if (doc is DrawingDocument)
            {
                doc.SaveAs(savePath, true);
                exportSource = "drawing";
            }
            else
            {
                throw new Exception("DXF solo se exporta desde planos (.idw/.dwg) o piezas de chapa metálica (.ipt) con patrón plano.");
            }

            long fileSize = 0;
            try { fileSize = new System.IO.FileInfo(savePath).Length; } catch { }

            return new
            {
                Status        = "ok",
                FilePath      = savePath,
                ExportSource  = exportSource,
                FileSizeBytes = fileSize,
                DocumentName  = doc.DisplayName
            };
        }

        private object CheckInterference(Dictionary<string, object> payload)
        {
            Document doc = _inventorApp.ActiveDocument;
            if (!(doc is AssemblyDocument asmDoc))
                throw new Exception("La comprobación de interferencias requiere un ensamble (.iam) activo.");

            AssemblyComponentDefinition compDef = asmDoc.ComponentDefinition;

            var allOccs = _inventorApp.TransientObjects.CreateObjectCollection();
            for (int i = 1; i <= compDef.Occurrences.Count; i++)
                allOccs.Add(compDef.Occurrences[i]);

            if (allOccs.Count < 2)
                throw new Exception("El ensamble necesita al menos 2 componentes para verificar interferencias.");

            InterferenceResults results = compDef.AnalyzeInterference(allOccs, Type.Missing);

            var pairs = new List<object>();
            foreach (InterferenceResult r in results)
            {
                pairs.Add(new
                {
                    Component1 = r.OccurrenceOne.Name,
                    Component2 = r.OccurrenceTwo.Name,
                    Volume_cm3 = r.Volume,
                    Centroid   = new { X = r.Centroid.X, Y = r.Centroid.Y, Z = r.Centroid.Z }
                });
            }

            return new
            {
                Status            = "ok",
                HasInterferences  = results.Count > 0,
                InterferenceCount = results.Count,
                Interferences     = pairs,
                DocumentName      = asmDoc.DisplayName,
                ComponentCount    = compDef.Occurrences.Count
            };
        }

        private object GetMassProperties(Dictionary<string, object> payload)
        {
            Document doc = _inventorApp.ActiveDocument;
            if (doc == null) throw new Exception("No hay documento activo");

            MassProperties massProp;
            string docType;

            if (doc is PartDocument partDoc)
            {
                massProp = partDoc.ComponentDefinition.MassProperties;
                docType  = "part";
            }
            else if (doc is AssemblyDocument asmDoc)
            {
                massProp = asmDoc.ComponentDefinition.MassProperties;
                docType  = "assembly";
            }
            else
            {
                throw new Exception("Propiedades de masa disponibles solo para piezas (.ipt) y ensambles (.iam).");
            }

            Point com = massProp.CenterOfMass;

            return new
            {
                Status          = "ok",
                DocumentName    = doc.DisplayName,
                DocumentType    = docType,
                Mass_kg         = massProp.Mass,
                Volume_cm3      = massProp.Volume,
                SurfaceArea_cm2 = massProp.Area,
                CenterOfMass    = new { X = com.X, Y = com.Y, Z = com.Z },
                Units           = "mass:kg, volume:cm³, area:cm²"
            };
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

            return new
            {
                Status       = "ok",
                DisplayName  = doc.DisplayName,
                FullFileName = doc.FullFileName,
                DocumentType = "PartDocument",
                Units        = units,
                SketchCount  = 0,
                BodyCount    = 0
            };
        }

        private object CreateNewAssembly(Dictionary<string, object> payload)
        {
            string units = payload != null && payload.ContainsKey("units")
                ? payload["units"].ToString().ToLower() : "metric";
            bool metric = units != "imperial";

            string tpl = GetTemplatePath(DocumentTypeEnum.kAssemblyDocumentObject, metric);
            var doc = (AssemblyDocument)_inventorApp.Documents.Add(
                DocumentTypeEnum.kAssemblyDocumentObject, tpl, true);

            return new
            {
                Status       = "ok",
                DisplayName  = doc.DisplayName,
                FullFileName = doc.FullFileName,
                DocumentType = "AssemblyDocument",
                Units        = units
            };
        }

        private object OpenDocument(Dictionary<string, object> payload)
        {
            if (payload == null || !payload.ContainsKey("path"))
                throw new Exception("Falta el parámetro 'path'");

            string filePath = payload["path"].ToString();
            if (!System.IO.File.Exists(filePath))
                throw new Exception($"El archivo no existe: {filePath}");

            var doc = _inventorApp.Documents.Open(filePath, true);
            int sketchCount = 0, bodyCount = 0;
            if (doc is PartDocument pdOpen)
            {
                sketchCount = pdOpen.ComponentDefinition.Sketches.Count;
                bodyCount   = pdOpen.ComponentDefinition.SurfaceBodies.Count;
            }
            return new
            {
                Status       = "ok",
                DisplayName  = doc.DisplayName,
                FullFileName = doc.FullFileName,
                DocumentType = doc.DocumentType.ToString(),
                Units        = doc.UnitsOfMeasure.LengthUnits.ToString(),
                SketchCount  = sketchCount,
                BodyCount    = bodyCount
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
                return new { Status = "ok", FullFileName = savePath, DisplayName = doc.DisplayName };
            }

            if (string.IsNullOrEmpty(doc.FullFileName))
                throw new Exception(
                    "El documento es nuevo y no tiene ruta. Proporciona el parámetro 'path'.");

            doc.Save();
            return new { Status = "ok", FullFileName = doc.FullFileName, DisplayName = doc.DisplayName };
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
            return new { Status = "ok", Units = units, DocumentName = doc.DisplayName };
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
                    return new { Status = "ok", MaterialName = mat.Name, Source = "document", DocumentName = partDoc.DisplayName };
                }
            }

            // Fall back to global styles library
            foreach (Material mat in _inventorApp.StylesManager.Materials)
            {
                if (string.Compare(mat.Name, materialName, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    Material localMat = mat.ConvertToLocal();
                    partDoc.ComponentDefinition.Material = localMat ?? mat;
                    return new { Status = "ok", MaterialName = mat.Name, Source = "global_library", DocumentName = partDoc.DisplayName };
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
            WorkPlane workPlane = FindWorkPlaneGeneral(compDef, planeName);

            _activeSketch = compDef.Sketches.Add(workPlane);
            _sketchEntities.Clear();

            if (!string.IsNullOrWhiteSpace(sketchName))
                _activeSketch.Name = sketchName;

            return new
            {
                Status             = "ok",
                SketchName         = _activeSketch.Name,
                SketchIndex        = compDef.Sketches.Count,
                Plane              = planeName,
                EntityCount        = 0,
                IsFullyConstrained = false
            };
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

            return new
            {
                Status           = "ok",
                Type             = "rectangle",
                EntityIndices    = indices,
                LineCount        = indices.Count,
                SketchName       = sketch.Name,
                TotalEntityCount = _sketchEntities.Count
            };
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
            return new
            {
                Status           = "ok",
                Type             = "arc",
                EntityIndex      = index,
                SketchName       = sketch.Name,
                TotalEntityCount = _sketchEntities.Count
            };
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

            return new
            {
                Status           = "ok",
                Type             = "slot",
                EntityIndices    = indices,
                EntityCount      = indices.Count,
                SketchName       = sketch.Name,
                TotalEntityCount = _sketchEntities.Count
            };
        }

        private object AddSketchDimension(Dictionary<string, object> payload)
        {
            var sketch = GetActiveSketch();
            var tg = _inventorApp.TransientGeometry;

            string dimType = payload["type"].ToString().ToLower();
            double tx      = payload.ContainsKey("text_x") ? Convert.ToDouble(payload["text_x"]) : 1.0;
            double ty      = payload.ContainsKey("text_y") ? Convert.ToDouble(payload["text_y"]) : 1.0;
            Point2d textPt = tg.CreatePoint2d(tx, ty);
            bool driven    = payload.ContainsKey("driven") && Convert.ToBoolean(payload["driven"]);
            string valExpr = null;
            if (payload.ContainsKey("value"))
            {
                string units = payload.ContainsKey("units") ? payload["units"].ToString() : "mm";
                valExpr = payload["value"].ToString() + " " + units;
            }

            string dimName   = "";
            int entityIdx    = -1, entity1Idx = -1, entity2Idx = -1;

            switch (dimType)
            {
                case "line":
                case "length":
                {
                    entityIdx = Convert.ToInt32(payload["entity_index"]);
                    if (!(GetEntity(entityIdx) is SketchLine line))
                        throw new Exception($"La entidad {entityIdx} no es una línea.");
                    var dim = sketch.DimensionConstraints.AddTwoPointDistance(
                        line.StartSketchPoint, line.EndSketchPoint,
                        DimensionOrientationEnum.kAlignedDim, textPt, driven);
                    if (valExpr != null) dim.Parameter.Expression = valExpr;
                    dimName = dim.Parameter.Name;
                    break;
                }
                case "radius":
                {
                    entityIdx = Convert.ToInt32(payload["entity_index"]);
                    var dim = sketch.DimensionConstraints.AddRadius(GetEntity(entityIdx), textPt, driven);
                    if (valExpr != null) dim.Parameter.Expression = valExpr;
                    dimName = dim.Parameter.Name;
                    break;
                }
                case "diameter":
                {
                    entityIdx = Convert.ToInt32(payload["entity_index"]);
                    var dim = sketch.DimensionConstraints.AddDiameter(GetEntity(entityIdx), textPt, driven);
                    if (valExpr != null) dim.Parameter.Expression = valExpr;
                    dimName = dim.Parameter.Name;
                    break;
                }
                case "distance":
                {
                    entity1Idx = Convert.ToInt32(payload["entity1"]);
                    entity2Idx = Convert.ToInt32(payload["entity2"]);
                    if (!(GetEntity(entity1Idx) is SketchLine l1))
                        throw new Exception($"Entidad {entity1Idx} debe ser una línea.");
                    if (!(GetEntity(entity2Idx) is SketchLine l2))
                        throw new Exception($"Entidad {entity2Idx} debe ser una línea.");
                    string ori = payload.ContainsKey("orientation")
                        ? payload["orientation"].ToString().ToLower() : "aligned";
                    var orient = ori == "horizontal" ? DimensionOrientationEnum.kHorizontalDim
                        : ori == "vertical" ? DimensionOrientationEnum.kVerticalDim
                        : DimensionOrientationEnum.kAlignedDim;
                    var dim = sketch.DimensionConstraints.AddTwoPointDistance(
                        l1.StartSketchPoint, l2.StartSketchPoint, orient, textPt, driven);
                    if (valExpr != null) dim.Parameter.Expression = valExpr;
                    dimName = dim.Parameter.Name;
                    break;
                }
                default:
                    throw new Exception($"Tipo '{dimType}' no reconocido. Usa: line, radius, diameter, distance");
            }

            return new
            {
                Status        = "ok",
                DimensionName = dimName,
                Type          = dimType,
                Value         = valExpr ?? "reference",
                EntityIndex   = entityIdx >= 0 ? (object)entityIdx : null,
                Entity1       = entity1Idx >= 0 ? (object)entity1Idx : null,
                Entity2       = entity2Idx >= 0 ? (object)entity2Idx : null,
                Driven        = driven,
                SketchName    = sketch.Name
            };
        }

        private object AddSketchConstraint(Dictionary<string, object> payload)
        {
            var sketch = GetActiveSketch();
            string ctype = payload["type"].ToString().ToLower();
            int entityIdx = -1, entity1Idx = -1, entity2Idx = -1;

            switch (ctype)
            {
                case "horizontal":
                {
                    entityIdx = Convert.ToInt32(payload["entity_index"]);
                    sketch.GeometricConstraints.AddHorizontal(GetEntity(entityIdx), false);
                    break;
                }
                case "vertical":
                {
                    entityIdx = Convert.ToInt32(payload["entity_index"]);
                    sketch.GeometricConstraints.AddVertical(GetEntity(entityIdx), false);
                    break;
                }
                case "tangent":
                {
                    entity1Idx = Convert.ToInt32(payload["entity1"]);
                    entity2Idx = Convert.ToInt32(payload["entity2"]);
                    sketch.GeometricConstraints.AddTangent(GetEntity(entity1Idx), GetEntity(entity2Idx), null);
                    break;
                }
                case "coincident":
                {
                    entity1Idx = Convert.ToInt32(payload["entity1"]);
                    entity2Idx = Convert.ToInt32(payload["entity2"]);
                    sketch.GeometricConstraints.AddCoincident(GetEntity(entity1Idx), GetEntity(entity2Idx));
                    break;
                }
                case "parallel":
                {
                    entity1Idx = Convert.ToInt32(payload["entity1"]);
                    entity2Idx = Convert.ToInt32(payload["entity2"]);
                    sketch.GeometricConstraints.AddParallel(GetEntity(entity1Idx), GetEntity(entity2Idx), false, false);
                    break;
                }
                case "perpendicular":
                {
                    entity1Idx = Convert.ToInt32(payload["entity1"]);
                    entity2Idx = Convert.ToInt32(payload["entity2"]);
                    sketch.GeometricConstraints.AddPerpendicular(GetEntity(entity1Idx), GetEntity(entity2Idx), false, false);
                    break;
                }
                case "equal":
                case "equal_length":
                {
                    entity1Idx = Convert.ToInt32(payload["entity1"]);
                    entity2Idx = Convert.ToInt32(payload["entity2"]);
                    if (!(GetEntity(entity1Idx) is SketchLine l1))
                        throw new Exception($"Entidad {entity1Idx} debe ser una línea.");
                    if (!(GetEntity(entity2Idx) is SketchLine l2))
                        throw new Exception($"Entidad {entity2Idx} debe ser una línea.");
                    sketch.GeometricConstraints.AddEqualLength(l1, l2);
                    break;
                }
                case "concentric":
                {
                    entity1Idx = Convert.ToInt32(payload["entity1"]);
                    entity2Idx = Convert.ToInt32(payload["entity2"]);
                    sketch.GeometricConstraints.AddConcentric(GetEntity(entity1Idx), GetEntity(entity2Idx));
                    break;
                }
                default:
                    throw new Exception(
                        $"Restricción '{ctype}' no reconocida. Usa: horizontal, vertical, tangent, coincident, parallel, perpendicular, equal_length, concentric");
            }

            return new
            {
                Status      = "ok",
                Type        = ctype,
                EntityIndex = entityIdx >= 0 ? (object)entityIdx : null,
                Entity1     = entity1Idx >= 0 ? (object)entity1Idx : null,
                Entity2     = entity2Idx >= 0 ? (object)entity2Idx : null,
                SketchName  = sketch.Name
            };
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

            return new { Status = "ok", Source = source, ProjectedCount = count, SketchName = sketch.Name };
        }

        private object CloseSketch(Dictionary<string, object> payload)
        {
            if (_activeSketch == null)
                throw new Exception("No hay boceto activo.");

            string name            = _activeSketch.Name;
            int trackedEntityCount = _sketchEntities.Count;
            int totalEntityCount   = 0;
            try { totalEntityCount = _activeSketch.SketchEntities.Count; } catch { }
            try { _activeSketch.ExitEdit(); } catch { }
            _activeSketch = null;
            _sketchEntities.Clear();

            return new
            {
                Status             = "ok",
                SketchName         = name,
                TrackedEntityCount = trackedEntityCount,
                TotalEntityCount   = totalEntityCount
            };
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

            int bodyCount = compDef.SurfaceBodies.Count;
            int faceCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int edgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;
            return new
            {
                Status      = "ok",
                FeatureName = feature.Name,
                Distance    = distExpr,
                Direction   = dirStr,
                BodyCount   = bodyCount,
                FaceCount   = faceCount,
                EdgeCount   = edgeCount
            };
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

            int bodyCount = compDef.SurfaceBodies.Count;
            int faceCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int edgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;
            return new
            {
                Status      = "ok",
                FeatureName = feature.Name,
                Angle       = angleDeg,
                Axis        = axisName,
                BodyCount   = bodyCount,
                FaceCount   = faceCount,
                EdgeCount   = edgeCount
            };
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

            int bodyCount = compDef.SurfaceBodies.Count;
            int faceCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int edgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;
            return new
            {
                Status      = "ok",
                FeatureName = feature.Name,
                PathSketch  = pathSketch.Name,
                BodyCount   = bodyCount,
                FaceCount   = faceCount,
                EdgeCount   = edgeCount
            };
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

            int bodyCount = compDef.SurfaceBodies.Count;
            int faceCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int edgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;
            return new
            {
                Status      = "ok",
                FeatureName = feature.Name,
                Sketches    = sketchNames,
                Operation   = operation.ToString(),
                BodyCount   = bodyCount,
                FaceCount   = faceCount,
                EdgeCount   = edgeCount
            };
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

            int bodyCount = compDef.SurfaceBodies.Count;
            int faceCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int edgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;
            return new
            {
                Status      = "ok",
                FeatureName = hole.Name,
                HoleType    = holeType,
                Diameter    = diamExpr,
                HoleCount   = pts.Count,
                BodyCount   = bodyCount,
                FaceCount   = faceCount,
                EdgeCount   = edgeCount
            };
        }
        // ── Grupo: Modificación de sólidos ─────────────────────────────────

        private List<int> ParseIntList(object val)
        {
            var result = new List<int>();
            if (val is Newtonsoft.Json.Linq.JArray arr)
            {
                foreach (var item in arr) result.Add(Convert.ToInt32(item));
            }
            else
            {
                foreach (var part in val.ToString().Split(','))
                {
                    string trimmed = part.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        result.Add(Convert.ToInt32(trimmed));
                }
            }
            return result;
        }

        private SurfaceBody GetFirstBody(PartComponentDefinition compDef)
        {
            if (compDef.SurfaceBodies.Count == 0)
                throw new Exception("La pieza no tiene cuerpo sólido.");
            return compDef.SurfaceBodies[1];
        }

        private object AddFillet(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();

            if (payload == null || (!payload.ContainsKey("edge_indices") && !payload.ContainsKey("edge_index")))
                throw new Exception("Debes indicar 'edge_indices' con los índices de las aristas a redondear.");

            string radiusExpr = BuildExpr(payload, "radius", "1 mm");
            var body = GetFirstBody(compDef);

            var edgeIndices = payload.ContainsKey("edge_indices")
                ? ParseIntList(payload["edge_indices"])
                : new List<int> { Convert.ToInt32(payload["edge_index"]) };

            var edgeColl = _inventorApp.TransientObjects.CreateEdgeCollection();
            foreach (int idx in edgeIndices)
            {
                if (idx < 1 || idx > body.Edges.Count)
                    throw new Exception($"Índice de arista {idx} fuera de rango (1–{body.Edges.Count}).");
                edgeColl.Add(body.Edges[idx]);
            }

            var filletFeatures = compDef.Features.FilletFeatures;
            var feature = filletFeatures.AddSimple(edgeColl, radiusExpr, false, false, false, false, false, false);

            int bodyCount   = compDef.SurfaceBodies.Count;
            int faceCount   = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int newEdgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;
            return new
            {
                Status            = "ok",
                FeatureName       = feature.Name,
                Radius            = radiusExpr,
                FilletedEdgeCount = edgeIndices.Count,
                BodyCount         = bodyCount,
                FaceCount         = faceCount,
                EdgeCount         = newEdgeCount
            };
        }

        private object AddChamfer(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();

            if (payload == null || (!payload.ContainsKey("edge_indices") && !payload.ContainsKey("edge_index")))
                throw new Exception("Debes indicar 'edge_indices' con los índices de las aristas a chaflanar.");

            string distExpr = BuildExpr(payload, "distance", "1 mm");
            var body = GetFirstBody(compDef);

            var edgeIndices = payload.ContainsKey("edge_indices")
                ? ParseIntList(payload["edge_indices"])
                : new List<int> { Convert.ToInt32(payload["edge_index"]) };

            var edgeColl = _inventorApp.TransientObjects.CreateEdgeCollection();
            foreach (int idx in edgeIndices)
            {
                if (idx < 1 || idx > body.Edges.Count)
                    throw new Exception($"Índice de arista {idx} fuera de rango (1–{body.Edges.Count}).");
                edgeColl.Add(body.Edges[idx]);
            }

            var chamferFeatures = compDef.Features.ChamferFeatures;
            ChamferFeature feature = chamferFeatures.AddUsingDistance(edgeColl, distExpr, false, false, false);

            int bodyCount    = compDef.SurfaceBodies.Count;
            int faceCount    = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int newEdgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;
            return new
            {
                Status             = "ok",
                FeatureName        = feature.Name,
                Distance           = distExpr,
                ChamferedEdgeCount = edgeIndices.Count,
                BodyCount          = bodyCount,
                FaceCount          = faceCount,
                EdgeCount          = newEdgeCount
            };
        }

        private object ShellSolid(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();
            string thicknessExpr = BuildExpr(payload, "thickness", "1 mm");

            string dir = payload != null && payload.ContainsKey("direction")
                ? payload["direction"].ToString().ToLower() : "inside";
            bool shellOutside = dir == "outside";

            var body = GetFirstBody(compDef);

            var faceColl = _inventorApp.TransientObjects.CreateObjectCollection();
            int openFaceCount = 0;

            if (payload != null && (payload.ContainsKey("face_indices") || payload.ContainsKey("face_index")))
            {
                var faceIndices = payload.ContainsKey("face_indices")
                    ? ParseIntList(payload["face_indices"])
                    : new List<int> { Convert.ToInt32(payload["face_index"]) };

                foreach (int idx in faceIndices)
                {
                    if (idx < 1 || idx > body.Faces.Count)
                        throw new Exception($"Índice de cara {idx} fuera de rango (1–{body.Faces.Count}).");
                    faceColl.Add(body.Faces[idx]);
                }
                openFaceCount = faceIndices.Count;
            }

            var shellDir = shellOutside
                ? ShellDirectionEnum.kOutsideShellDirection
                : ShellDirectionEnum.kInsideShellDirection;
            var shellDef = compDef.Features.ShellFeatures.CreateShellDefinition(faceColl, thicknessExpr, shellDir);
            var feature = compDef.Features.ShellFeatures.Add(shellDef);

            int bodyCount = compDef.SurfaceBodies.Count;
            int faceCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int edgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;
            return new
            {
                Status      = "ok",
                FeatureName = feature.Name,
                Thickness   = thicknessExpr,
                OpenFaces   = openFaceCount,
                Direction   = dir,
                BodyCount   = bodyCount,
                FaceCount   = faceCount,
                EdgeCount   = edgeCount
            };
        }

        private object AddThreadFeature(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();

            int faceIdx = payload != null && payload.ContainsKey("face_index")
                ? Convert.ToInt32(payload["face_index"]) : 1;

            var body = GetFirstBody(compDef);
            if (faceIdx < 1 || faceIdx > body.Faces.Count)
                throw new Exception($"Índice de cara {faceIdx} fuera de rango (1–{body.Faces.Count}).");

            Face face = body.Faces[faceIdx];
            if (face.SurfaceType != SurfaceTypeEnum.kCylinderSurface)
                throw new Exception($"La cara {faceIdx} no es cilíndrica. Las roscas requieren una superficie cilíndrica.");

            bool rightHanded = !(payload != null && payload.ContainsKey("right_handed") && !Convert.ToBoolean(payload["right_handed"]));
            bool cosmetic = payload == null || !payload.ContainsKey("cosmetic") || Convert.ToBoolean(payload["cosmetic"]);
            bool fullLength = payload == null || !payload.ContainsKey("full_length") || Convert.ToBoolean(payload["full_length"]);

            string designation = payload != null && payload.ContainsKey("designation")
                ? payload["designation"].ToString() : "M6x1";
            string threadType = payload != null && payload.ContainsKey("thread_type")
                ? payload["thread_type"].ToString() : "ANSI Metric M Profile";

            var threadFeatures = compDef.Features.ThreadFeatures;
            // StandardThreadInfo → ThreadInfo via COM QueryInterface (double cast through object)
            ThreadInfo threadInfo = (ThreadInfo)(object)threadFeatures.CreateStandardThreadInfo(
                true, rightHanded, threadType, designation, "6H");

            object threadDepth = Type.Missing;
            object threadOffset = Type.Missing;
            if (!fullLength && payload != null && payload.ContainsKey("length"))
            {
                string units = payload.ContainsKey("units") ? payload["units"].ToString() : "mm";
                threadDepth = payload["length"].ToString() + " " + units;
            }

            var feature = threadFeatures.Add(face, null, threadInfo, false, fullLength, threadDepth, threadOffset);

            return new
            {
                Status      = "ok",
                FeatureName = feature.Name,
                Cosmetic    = cosmetic,
                FullLength  = fullLength,
                Designation = designation,
                ThreadType  = threadType,
                FaceIndex   = faceIdx
            };
        }

        private object SplitBody(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();

            string planeName = payload != null && payload.ContainsKey("plane")
                ? payload["plane"].ToString().ToUpper() : "XY";

            WorkPlane splittingPlane = FindOriginPlane(compDef, planeName);

            bool keepBoth = payload == null || !payload.ContainsKey("keep_both")
                || Convert.ToBoolean(payload["keep_both"]);

            if (compDef.SurfaceBodies.Count == 0)
                throw new Exception("La pieza no tiene cuerpo sólido para dividir.");

            var splitFeatures = compDef.Features.SplitFeatures;
            var targetBody = compDef.SurfaceBodies[1];
            if (keepBoth)
                splitFeatures.SplitBody(splittingPlane, targetBody);
            else
                splitFeatures.TrimSolid(splittingPlane, targetBody, false);

            int bodyCount = compDef.SurfaceBodies.Count;
            int faceCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int edgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;
            return new
            {
                Status      = "ok",
                Operation   = keepBoth ? "split" : "trim",
                Plane       = planeName,
                BodyCount   = bodyCount,
                FaceCount   = faceCount,
                EdgeCount   = edgeCount
            };
        }

        private object CombineBodies(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();

            int baseIdx = payload != null && payload.ContainsKey("base_body")
                ? Convert.ToInt32(payload["base_body"]) : 1;

            if (baseIdx < 1 || baseIdx > compDef.SurfaceBodies.Count)
                throw new Exception($"Índice de cuerpo base {baseIdx} fuera de rango (1–{compDef.SurfaceBodies.Count}).");

            var toolIndices = payload != null && payload.ContainsKey("tool_bodies")
                ? ParseIntList(payload["tool_bodies"])
                : new List<int> { 2 };

            SurfaceBody baseBody = compDef.SurfaceBodies[baseIdx];
            var toolColl = _inventorApp.TransientObjects.CreateObjectCollection();

            foreach (int idx in toolIndices)
            {
                if (idx < 1 || idx > compDef.SurfaceBodies.Count)
                    throw new Exception($"Índice de cuerpo herramienta {idx} fuera de rango (1–{compDef.SurfaceBodies.Count}).");
                if (idx == baseIdx)
                    throw new Exception($"El cuerpo herramienta {idx} no puede ser el mismo que el cuerpo base.");
                toolColl.Add(compDef.SurfaceBodies[idx]);
            }

            var operation = ParseOperation(payload);
            var feature = compDef.Features.CombineFeatures.Add(baseBody, toolColl, operation);

            int bodyCount = compDef.SurfaceBodies.Count;
            int faceCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int edgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;
            return new
            {
                Status      = "ok",
                FeatureName = feature.Name,
                Operation   = operation.ToString(),
                BodyCount   = bodyCount,
                FaceCount   = faceCount,
                EdgeCount   = edgeCount
            };
        }
        // ── Grupo: Patrones y simetría ───────────────────────────────────────

        private ObjectCollection CollectFeatures(PartComponentDefinition compDef, string featureNamesStr)
        {
            var coll = _inventorApp.TransientObjects.CreateObjectCollection();
            int total = compDef.Features.Count;
            if (total == 0) throw new Exception("La pieza no tiene operaciones.");

            if (string.IsNullOrWhiteSpace(featureNamesStr))
            {
                // Default: last feature in the tree
                int idx = 0;
                foreach (PartFeature f in compDef.Features)
                {
                    idx++;
                    if (idx == total) { coll.Add(f); break; }
                }
            }
            else
            {
                foreach (string token in featureNamesStr.Split(','))
                {
                    string t = token.Trim();
                    if (string.IsNullOrEmpty(t)) continue;

                    PartFeature found = null;
                    if (int.TryParse(t, out int targetIdx))
                    {
                        int i = 0;
                        foreach (PartFeature f in compDef.Features)
                        {
                            i++;
                            if (i == targetIdx) { found = f; break; }
                        }
                    }
                    else
                    {
                        foreach (PartFeature f in compDef.Features)
                        {
                            if (string.Compare(f.Name, t, StringComparison.OrdinalIgnoreCase) == 0)
                            { found = f; break; }
                        }
                    }

                    if (found == null)
                        throw new Exception($"Operación '{t}' no encontrada en el árbol de operaciones.");
                    coll.Add(found);
                }
            }

            if (coll.Count == 0)
                throw new Exception("No se encontraron operaciones para procesar.");
            return coll;
        }

        private WorkAxis AutoYAxis(PartComponentDefinition compDef, string xAxisName)
        {
            string u = xAxisName.ToUpper().Trim();
            string autoName = (u == "Y") ? "X" : "Y";
            return FindWorkAxisGeneral(compDef, autoName);
        }

        private object CreateRectangularPattern(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();
            string units = payload != null && payload.ContainsKey("units") ? payload["units"].ToString() : "mm";

            string featureNames = payload != null && payload.ContainsKey("feature_names")
                ? payload["feature_names"].ToString() : "";
            var features = CollectFeatures(compDef, featureNames);

            string xAxisName = payload != null && payload.ContainsKey("x_axis")
                ? payload["x_axis"].ToString() : "X";
            int xCount = payload != null && payload.ContainsKey("x_count")
                ? Convert.ToInt32(payload["x_count"]) : 2;
            double xSpacing = payload != null && payload.ContainsKey("x_spacing")
                ? Convert.ToDouble(payload["x_spacing"]) : 10.0;
            int yCount = payload != null && payload.ContainsKey("y_count")
                ? Convert.ToInt32(payload["y_count"]) : 1;
            double ySpacing = payload != null && payload.ContainsKey("y_spacing")
                ? Convert.ToDouble(payload["y_spacing"]) : 10.0;

            string xSpacingExpr = xSpacing.ToString("F4") + " " + units;
            string ySpacingExpr = ySpacing.ToString("F4") + " " + units;

            WorkAxis xAxis = FindWorkAxisGeneral(compDef, xAxisName);
            WorkAxis yAxis = payload != null && payload.ContainsKey("y_axis")
                ? FindWorkAxisGeneral(compDef, payload["y_axis"].ToString())
                : AutoYAxis(compDef, xAxisName);

            var feature = compDef.Features.RectangularPatternFeatures.Add(
                features,
                xAxis, true,
                xCount.ToString(), xSpacingExpr, PatternSpacingTypeEnum.kDefault, Type.Missing,
                yAxis, true,
                yCount.ToString(), ySpacingExpr, PatternSpacingTypeEnum.kDefault, Type.Missing,
                PatternComputeTypeEnum.kIdenticalCompute,
                PatternOrientationEnum.kIdentical);

            int bodyCount = compDef.SurfaceBodies.Count;
            int faceCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int edgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;

            return new
            {
                Status         = "ok",
                FeatureName    = feature.Name,
                XAxis          = xAxisName,
                XCount         = xCount,
                XSpacing       = xSpacingExpr,
                YAxis          = yAxis.Name,
                YCount         = yCount,
                YSpacing       = ySpacingExpr,
                TotalInstances = xCount * yCount,
                BodyCount      = bodyCount,
                FaceCount      = faceCount,
                EdgeCount      = edgeCount
            };
        }

        private object CreateCircularPattern(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();

            string featureNames = payload != null && payload.ContainsKey("feature_names")
                ? payload["feature_names"].ToString() : "";
            var features = CollectFeatures(compDef, featureNames);

            string axisName = payload != null && payload.ContainsKey("axis")
                ? payload["axis"].ToString() : "Z";
            int count = payload != null && payload.ContainsKey("count")
                ? Convert.ToInt32(payload["count"]) : 4;
            double angle = payload != null && payload.ContainsKey("angle")
                ? Convert.ToDouble(payload["angle"]) : 360.0;
            // FitWithinAngle=true: 'angle' is total arc, instances equally spaced inside
            // FitWithinAngle=false: 'angle' is spacing between consecutive instances
            bool fitWithinAngle = payload == null || !payload.ContainsKey("fit_within_angle")
                || Convert.ToBoolean(payload["fit_within_angle"]);

            string angleExpr = angle.ToString("F4") + " deg";
            WorkAxis axis = FindWorkAxisGeneral(compDef, axisName);

            var feature = compDef.Features.CircularPatternFeatures.Add(
                features, axis, true,
                count.ToString(), angleExpr, fitWithinAngle,
                PatternComputeTypeEnum.kIdenticalCompute);

            int bodyCount = compDef.SurfaceBodies.Count;
            int faceCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int edgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;

            return new
            {
                Status         = "ok",
                FeatureName    = feature.Name,
                Axis           = axisName,
                Count          = count,
                TotalAngle     = angleExpr,
                FitWithinAngle = fitWithinAngle,
                BodyCount      = bodyCount,
                FaceCount      = faceCount,
                EdgeCount      = edgeCount
            };
        }

        private object MirrorFeature(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();

            string featureNames = payload != null && payload.ContainsKey("feature_names")
                ? payload["feature_names"].ToString() : "";
            var features = CollectFeatures(compDef, featureNames);

            string planeName = payload != null && payload.ContainsKey("plane")
                ? payload["plane"].ToString() : "XZ";
            WorkPlane mirrorPlane = FindWorkPlaneGeneral(compDef, planeName);

            var feature = compDef.Features.MirrorFeatures.Add(
                features, mirrorPlane, false,
                PatternComputeTypeEnum.kAdjustToModelCompute);

            int bodyCount = compDef.SurfaceBodies.Count;
            int faceCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int edgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;

            return new
            {
                Status       = "ok",
                FeatureName  = feature.Name,
                Plane        = planeName,
                FeaturesCount = features.Count,
                BodyCount    = bodyCount,
                FaceCount    = faceCount,
                EdgeCount    = edgeCount
            };
        }

        private object MirrorSolid(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();

            string planeName = payload != null && payload.ContainsKey("plane")
                ? payload["plane"].ToString() : "XZ";
            WorkPlane mirrorPlane = FindWorkPlaneGeneral(compDef, planeName);

            // Collect all solid features from the model tree
            var features = _inventorApp.TransientObjects.CreateObjectCollection();
            foreach (PartFeature f in compDef.Features)
            {
                try { features.Add(f); } catch { }
            }

            if (features.Count == 0)
                throw new Exception("No se encontraron operaciones para espejar.");

            var feature = compDef.Features.MirrorFeatures.Add(
                features, mirrorPlane, false,
                PatternComputeTypeEnum.kAdjustToModelCompute);

            int bodyCount = compDef.SurfaceBodies.Count;
            int faceCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Faces.Count : 0;
            int edgeCount = bodyCount > 0 ? compDef.SurfaceBodies[1].Edges.Count : 0;

            return new
            {
                Status              = "ok",
                FeatureName         = feature.Name,
                Plane               = planeName,
                MirroredFeatureCount = features.Count,
                BodyCount           = bodyCount,
                FaceCount           = faceCount,
                EdgeCount           = edgeCount
            };
        }

        // ── Grupo: Parámetros e iProperties ─────────────────────────────────

        private string GetParamValueType(string units)
        {
            if (string.IsNullOrEmpty(units)) return "numeric";
            switch (units.Trim().ToLower())
            {
                case "text":    return "text";
                case "boolean": return "boolean";
                default:        return "numeric";
            }
        }

        private PropertySets GetDocPropertySets()
        {
            var doc = _inventorApp.ActiveDocument;
            if (doc is PartDocument pd)     return pd.PropertySets;
            if (doc is AssemblyDocument ad) return ad.PropertySets;
            if (doc is DrawingDocument dd)  return dd.PropertySets;
            throw new Exception("El tipo de documento no soporta iProperties.");
        }

        private void SetIPropertySafe(PropertySet ps, string propName, string value,
            List<string> updated, List<string> errors)
        {
            if (ps == null) { errors.Add($"PropertySet no encontrado para '{propName}'"); return; }
            try   { ps[propName].Value = value; updated.Add(propName); }
            catch (Exception ex) { errors.Add($"{propName}: {ex.Message}"); }
        }

        private object GetParametersRich()
        {
            var doc = _inventorApp.ActiveDocument;
            if (doc == null) return new { Status = "error", Error = "No hay documento activo" };

            Inventor.Parameters docParams = null;
            if (doc is PartDocument partDoc)         docParams = partDoc.ComponentDefinition.Parameters;
            else if (doc is AssemblyDocument asmDoc) docParams = asmDoc.ComponentDefinition.Parameters;
            if (docParams == null)
                return new { Status = "error", Error = "El documento no soporta parámetros." };

            var modelList = new List<object>();
            var userList  = new List<object>();
            var refList   = new List<object>();

            foreach (ModelParameter p in docParams.ModelParameters)
            {
                string u = p.get_Units();
                modelList.Add(new { Name = p.Name, Value = p.Value, Expression = p.Expression,
                    Units = u, InUse = p.InUse, ValueType = GetParamValueType(u) });
            }
            foreach (UserParameter p in docParams.UserParameters)
            {
                string u = p.get_Units();
                userList.Add(new { Name = p.Name, Value = p.Value, Expression = p.Expression,
                    Units = u, ValueType = GetParamValueType(u) });
            }
            foreach (Inventor.Parameter p in docParams.ReferenceParameters)
            {
                string u = p.get_Units();
                refList.Add(new { Name = p.Name, Value = p.Value, Expression = p.Expression,
                    Units = u, ValueType = GetParamValueType(u) });
            }

            return new
            {
                Status                  = "ok",
                DocumentName            = doc.DisplayName,
                TotalCount              = modelList.Count + userList.Count + refList.Count,
                ModelParameterCount     = modelList.Count,
                UserParameterCount      = userList.Count,
                ReferenceParameterCount = refList.Count,
                ModelParameters         = modelList,
                UserParameters          = userList,
                ReferenceParameters     = refList
            };
        }

        private object SetParameterValue(Dictionary<string, object> payload)
        {
            if (payload == null || !payload.ContainsKey("name"))
                throw new Exception("Falta el parámetro 'name'.");

            string name = payload["name"].ToString();

            // Build expression from 'expression' or 'value' + optional 'units'
            string newExpr;
            if (payload.ContainsKey("expression") && !string.IsNullOrWhiteSpace(payload["expression"]?.ToString()))
            {
                newExpr = payload["expression"].ToString();
            }
            else if (payload.ContainsKey("value"))
            {
                string raw       = payload["value"].ToString();
                string valueType = payload.ContainsKey("value_type") ? payload["value_type"].ToString().ToLower() : "numeric";
                if (valueType == "text")
                    newExpr = "\"" + raw.Replace("\"", "\\\"") + "\"";
                else if (valueType == "boolean")
                    newExpr = (raw.ToLower() == "true" || raw == "1") ? "True" : "False";
                else // numeric
                {
                    string u = payload.ContainsKey("units") ? payload["units"].ToString() : "";
                    newExpr = string.IsNullOrEmpty(u) ? raw : raw + " " + u;
                }
            }
            else
            {
                throw new Exception("Falta 'expression' o 'value'.");
            }

            var doc = _inventorApp.ActiveDocument;
            Inventor.Parameters docParams = null;
            if (doc is PartDocument partDoc)         docParams = partDoc.ComponentDefinition.Parameters;
            else if (doc is AssemblyDocument asmDoc) docParams = asmDoc.ComponentDefinition.Parameters;
            if (docParams == null) throw new Exception("El documento no soporta parámetros.");

            Inventor.Parameter found = null;
            string paramTypeName = "";
            foreach (ModelParameter p in docParams.ModelParameters)
                if (p.Name == name) { found = (Inventor.Parameter)(object)p; paramTypeName = "Model"; break; }
            if (found == null)
                foreach (UserParameter p in docParams.UserParameters)
                    if (p.Name == name) { found = (Inventor.Parameter)(object)p; paramTypeName = "User"; break; }
            if (found == null)
                foreach (Inventor.Parameter p in docParams.ReferenceParameters)
                    if (p.Name == name) { found = p; paramTypeName = "Reference"; break; }

            if (found == null)
                throw new Exception($"Parámetro '{name}' no encontrado en el documento.");

            string oldExpr  = found.Expression;
            object oldValue = found.Value;
            string units    = found.get_Units();

            found.Expression = newExpr;
            doc.Update();

            return new
            {
                Status        = "ok",
                Name          = name,
                ParameterType = paramTypeName,
                OldExpression = oldExpr,
                NewExpression = found.Expression,
                OldValue      = oldValue,
                NewValue      = found.Value,
                Units         = units,
                DocumentName  = doc.DisplayName
            };
        }

        private object AddCustomParameter(Dictionary<string, object> payload)
        {
            if (payload == null || !payload.ContainsKey("name"))
                throw new Exception("Falta el parámetro 'name'.");

            string name      = payload["name"].ToString();
            string valueType = payload.ContainsKey("value_type") ? payload["value_type"].ToString().ToLower() : "numeric";

            var doc = _inventorApp.ActiveDocument;
            Inventor.Parameters docParams = null;
            if (doc is PartDocument partDoc)         docParams = partDoc.ComponentDefinition.Parameters;
            else if (doc is AssemblyDocument asmDoc) docParams = asmDoc.ComponentDefinition.Parameters;
            if (docParams == null) throw new Exception("El documento no soporta parámetros.");

            UserParameter newParam;
            string expression, units;

            switch (valueType)
            {
                case "text":
                {
                    string val = payload.ContainsKey("value") ? payload["value"].ToString() : "";
                    expression = "\"" + val.Replace("\"", "\\\"") + "\"";
                    units      = "Text";
                    newParam   = docParams.UserParameters.AddByExpression(name, expression, UnitsTypeEnum.kTextUnits);
                    break;
                }
                case "boolean":
                {
                    bool boolVal = payload.ContainsKey("value") &&
                        (payload["value"].ToString().ToLower() == "true" || payload["value"].ToString() == "1");
                    expression = boolVal ? "True" : "False";
                    units      = "Boolean";
                    newParam   = docParams.UserParameters.AddByExpression(name, expression, UnitsTypeEnum.kBooleanUnits);
                    break;
                }
                default: // numeric
                {
                    units      = payload.ContainsKey("units") ? payload["units"].ToString() : "mm";
                    string val = payload.ContainsKey("value") ? payload["value"].ToString() : "0";
                    expression = val + " " + units;
                    newParam   = docParams.UserParameters.AddByExpression(name, expression, units);
                    break;
                }
            }

            return new
            {
                Status       = "ok",
                Name         = newParam.Name,
                Value        = newParam.Value,
                Expression   = newParam.Expression,
                Units        = newParam.get_Units(),
                ValueType    = valueType,
                DocumentName = doc.DisplayName
            };
        }

        private object UpdateIProperties(Dictionary<string, object> payload)
        {
            if (payload == null || payload.Count == 0)
                throw new Exception("No se proporcionaron campos para actualizar.");

            var doc = _inventorApp.ActiveDocument;
            if (doc == null) throw new Exception("No hay documento activo.");

            PropertySets allPS    = GetDocPropertySets();
            PropertySet summaryPS  = null; // Title, Author, Subject, Keywords, Comments
            PropertySet trackingPS = null; // Description, Part Number, Revision Number…

            foreach (PropertySet ps in allPS)
            {
                string dn = ps.DisplayName ?? "";
                if (dn.IndexOf("Summary", StringComparison.OrdinalIgnoreCase) >= 0)
                    summaryPS = ps;
                else if (dn.IndexOf("Design Tracking", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dn.IndexOf("Tracking", StringComparison.OrdinalIgnoreCase) >= 0)
                    trackingPS = ps;
            }

            var updated = new List<string>();
            var errors  = new List<string>();

            if (payload.ContainsKey("title") && payload["title"] != null)
                SetIPropertySafe(summaryPS, "Title", payload["title"].ToString(), updated, errors);
            if (payload.ContainsKey("author") && payload["author"] != null)
                SetIPropertySafe(summaryPS, "Author", payload["author"].ToString(), updated, errors);
            if (payload.ContainsKey("subject") && payload["subject"] != null)
                SetIPropertySafe(summaryPS, "Subject", payload["subject"].ToString(), updated, errors);
            if (payload.ContainsKey("keywords") && payload["keywords"] != null)
                SetIPropertySafe(summaryPS, "Keywords", payload["keywords"].ToString(), updated, errors);
            if (payload.ContainsKey("description") && payload["description"] != null)
                SetIPropertySafe(trackingPS, "Description", payload["description"].ToString(), updated, errors);
            if (payload.ContainsKey("part_number") && payload["part_number"] != null)
                SetIPropertySafe(trackingPS, "Part Number", payload["part_number"].ToString(), updated, errors);

            if (errors.Count == 0 && updated.Count == 0)
                throw new Exception("Ningún campo fue actualizado. Revisa los nombres de los campos.");

            return new
            {
                Status        = errors.Count == 0 ? "ok" : "partial",
                UpdatedFields = updated,
                Errors        = errors,
                DocumentName  = doc.DisplayName,
                FullFileName  = doc.FullFileName
            };
        }

        // ── Grupo: Geometría de trabajo ──────────────────────────────────────

        private WorkPlane FindWorkPlaneGeneral(PartComponentDefinition compDef, string name)
        {
            string upper = name.ToUpper().Trim();
            if (upper == "XY") return compDef.WorkPlanes[1];
            if (upper == "XZ") return compDef.WorkPlanes[2];
            if (upper == "YZ") return compDef.WorkPlanes[3];
            foreach (WorkPlane wp in compDef.WorkPlanes)
                if (string.Compare(wp.Name, name, StringComparison.OrdinalIgnoreCase) == 0)
                    return wp;
            if (int.TryParse(name, out int idx) && idx >= 1 && idx <= compDef.WorkPlanes.Count)
                return compDef.WorkPlanes[idx];
            throw new Exception($"Plano de trabajo '{name}' no encontrado. Usa: XY, XZ, YZ, nombre o índice.");
        }

        private WorkAxis FindWorkAxisGeneral(PartComponentDefinition compDef, string name)
        {
            string upper = name.ToUpper().Trim();
            if (upper == "X") return compDef.WorkAxes[1];
            if (upper == "Y") return compDef.WorkAxes[2];
            if (upper == "Z") return compDef.WorkAxes[3];
            foreach (WorkAxis wa in compDef.WorkAxes)
                if (string.Compare(wa.Name, name, StringComparison.OrdinalIgnoreCase) == 0)
                    return wa;
            if (int.TryParse(name, out int idx) && idx >= 1 && idx <= compDef.WorkAxes.Count)
                return compDef.WorkAxes[idx];
            throw new Exception($"Eje de trabajo '{name}' no encontrado. Usa: X, Y, Z, nombre o índice.");
        }

        private double ToInventorCm(double value, string units)
        {
            switch (units.ToLower().Trim())
            {
                case "m":  return value * 100.0;
                case "cm": return value;
                case "in": return value * 2.54;
                default:   return value * 0.1; // mm
            }
        }

        private object CreateWorkPlane(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();
            string mode = payload != null && payload.ContainsKey("mode")
                ? payload["mode"].ToString().ToLower() : "offset";
            string units = payload != null && payload.ContainsKey("units")
                ? payload["units"].ToString() : "mm";

            WorkPlane workPlane;
            string description;

            switch (mode)
            {
                case "offset":
                {
                    // AddByPlaneAndOffset(Object Plane, Object Offset [cm], Boolean Construction)
                    string planeName = payload != null && payload.ContainsKey("plane")
                        ? payload["plane"].ToString() : "XY";
                    double offset = payload != null && payload.ContainsKey("offset")
                        ? Convert.ToDouble(payload["offset"]) : 10.0;
                    double offsetCm = ToInventorCm(offset, units);
                    var refPlane = FindWorkPlaneGeneral(compDef, planeName);
                    workPlane = compDef.WorkPlanes.AddByPlaneAndOffset(refPlane, offsetCm, false);
                    description = $"Offset {offset} {units} from {planeName}";
                    break;
                }
                case "angle":
                {
                    // AddByLinePlaneAndAngle(Object Line, Object Plane, Object Angle [rad], Boolean Construction)
                    string planeName = payload != null && payload.ContainsKey("plane")
                        ? payload["plane"].ToString() : "XY";
                    string axisName = payload != null && payload.ContainsKey("axis")
                        ? payload["axis"].ToString() : "X";
                    double angleDeg = payload != null && payload.ContainsKey("angle")
                        ? Convert.ToDouble(payload["angle"]) : 45.0;
                    double angleRad = angleDeg * Math.PI / 180.0;
                    var refPlane = FindWorkPlaneGeneral(compDef, planeName);
                    var refAxis  = FindWorkAxisGeneral(compDef, axisName);
                    workPlane = compDef.WorkPlanes.AddByLinePlaneAndAngle(refAxis, refPlane, angleRad, false);
                    description = $"{angleDeg}° from {planeName} about axis {axisName}";
                    break;
                }
                case "three_points":
                {
                    // AddByThreePoints(Object Point1, Object Point2, Object Point3, Boolean Construction)
                    int p1Idx = payload != null && payload.ContainsKey("point1") ? Convert.ToInt32(payload["point1"]) : 1;
                    int p2Idx = payload != null && payload.ContainsKey("point2") ? Convert.ToInt32(payload["point2"]) : 2;
                    int p3Idx = payload != null && payload.ContainsKey("point3") ? Convert.ToInt32(payload["point3"]) : 3;
                    int total = compDef.WorkPoints.Count;
                    if (p1Idx < 1 || p1Idx > total) throw new Exception($"work_point {p1Idx} fuera de rango (1–{total}).");
                    if (p2Idx < 1 || p2Idx > total) throw new Exception($"work_point {p2Idx} fuera de rango (1–{total}).");
                    if (p3Idx < 1 || p3Idx > total) throw new Exception($"work_point {p3Idx} fuera de rango (1–{total}).");
                    workPlane = compDef.WorkPlanes.AddByThreePoints(
                        compDef.WorkPoints[p1Idx], compDef.WorkPoints[p2Idx], compDef.WorkPoints[p3Idx], false);
                    description = $"Through work points {p1Idx}, {p2Idx}, {p3Idx}";
                    break;
                }
                default:
                    throw new Exception($"Modo '{mode}' no reconocido. Usa: offset, angle, three_points");
            }

            int totalPlanes = compDef.WorkPlanes.Count;
            return new
            {
                Status          = "ok",
                WorkPlaneName   = workPlane.Name,
                WorkPlaneIndex  = totalPlanes,
                WorkPlaneCount  = totalPlanes,
                Mode            = mode,
                Description     = description
            };
        }

        private object CreateWorkAxis(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();
            string mode = payload != null && payload.ContainsKey("mode")
                ? payload["mode"].ToString().ToLower() : "cylinder";

            WorkAxis workAxis;
            string description;

            switch (mode)
            {
                case "cylinder":
                {
                    // AddByRevolvedFace(Face Face, Boolean Construction)
                    int faceIdx = payload != null && payload.ContainsKey("face_index")
                        ? Convert.ToInt32(payload["face_index"]) : 1;
                    var body = GetFirstBody(compDef);
                    if (faceIdx < 1 || faceIdx > body.Faces.Count)
                        throw new Exception($"Índice de cara {faceIdx} fuera de rango (1–{body.Faces.Count}).");
                    Face face = body.Faces[faceIdx];
                    if (face.SurfaceType != SurfaceTypeEnum.kCylinderSurface &&
                        face.SurfaceType != SurfaceTypeEnum.kConeSurface)
                        throw new Exception($"La cara {faceIdx} debe ser cilíndrica o cónica. Tipo actual: {face.SurfaceType}");
                    workAxis = compDef.WorkAxes.AddByRevolvedFace(face, false);
                    description = $"Axis of cylindrical face {faceIdx}";
                    break;
                }
                case "two_planes":
                {
                    // AddByTwoPlanes(Object Plane1, Object Plane2, Boolean Construction)
                    string plane1Name = payload != null && payload.ContainsKey("plane1")
                        ? payload["plane1"].ToString() : "XY";
                    string plane2Name = payload != null && payload.ContainsKey("plane2")
                        ? payload["plane2"].ToString() : "YZ";
                    var p1 = FindWorkPlaneGeneral(compDef, plane1Name);
                    var p2 = FindWorkPlaneGeneral(compDef, plane2Name);
                    workAxis = compDef.WorkAxes.AddByTwoPlanes(p1, p2, false);
                    description = $"Intersection of {plane1Name} and {plane2Name}";
                    break;
                }
                case "two_points":
                {
                    // AddByTwoPoints(Object Point1, Object Point2, Boolean Construction)
                    int p1Idx = payload != null && payload.ContainsKey("point1")
                        ? Convert.ToInt32(payload["point1"]) : 1;
                    int p2Idx = payload != null && payload.ContainsKey("point2")
                        ? Convert.ToInt32(payload["point2"]) : 2;
                    int total = compDef.WorkPoints.Count;
                    if (p1Idx < 1 || p1Idx > total) throw new Exception($"work_point {p1Idx} fuera de rango (1–{total}).");
                    if (p2Idx < 1 || p2Idx > total) throw new Exception($"work_point {p2Idx} fuera de rango (1–{total}).");
                    workAxis = compDef.WorkAxes.AddByTwoPoints(
                        compDef.WorkPoints[p1Idx], compDef.WorkPoints[p2Idx], false);
                    description = $"Through work points {p1Idx} and {p2Idx}";
                    break;
                }
                default:
                    throw new Exception($"Modo '{mode}' no reconocido. Usa: cylinder, two_planes, two_points");
            }

            int totalAxes = compDef.WorkAxes.Count;
            return new
            {
                Status        = "ok",
                WorkAxisName  = workAxis.Name,
                WorkAxisIndex = totalAxes,
                WorkAxisCount = totalAxes,
                Mode          = mode,
                Description   = description
            };
        }

        private object CreateWorkPoint(Dictionary<string, object> payload)
        {
            var compDef = GetPartCompDef();
            string mode = payload != null && payload.ContainsKey("mode")
                ? payload["mode"].ToString().ToLower() : "fixed";
            string units = payload != null && payload.ContainsKey("units")
                ? payload["units"].ToString() : "mm";

            WorkPoint workPoint;
            string description;

            switch (mode)
            {
                case "fixed":
                {
                    // AddFixed(Point Point, Boolean Construction)
                    double x = payload != null && payload.ContainsKey("x") ? Convert.ToDouble(payload["x"]) : 0.0;
                    double y = payload != null && payload.ContainsKey("y") ? Convert.ToDouble(payload["y"]) : 0.0;
                    double z = payload != null && payload.ContainsKey("z") ? Convert.ToDouble(payload["z"]) : 0.0;
                    Point pt = _inventorApp.TransientGeometry.CreatePoint(
                        ToInventorCm(x, units), ToInventorCm(y, units), ToInventorCm(z, units));
                    workPoint = compDef.WorkPoints.AddFixed(pt, false);
                    description = $"Fixed at ({x}, {y}, {z}) {units}";
                    break;
                }
                case "midpoint":
                {
                    // AddByMidPoint(Edge Edge, Boolean Construction)
                    int edgeIdx = payload != null && payload.ContainsKey("edge_index")
                        ? Convert.ToInt32(payload["edge_index"]) : 1;
                    var body = GetFirstBody(compDef);
                    if (edgeIdx < 1 || edgeIdx > body.Edges.Count)
                        throw new Exception($"Índice de arista {edgeIdx} fuera de rango (1–{body.Edges.Count}).");
                    workPoint = compDef.WorkPoints.AddByMidPoint(body.Edges[edgeIdx], false);
                    description = $"Midpoint of edge {edgeIdx}";
                    break;
                }
                case "three_planes":
                {
                    // AddByThreePlanes(Object Plane1, Object Plane2, Object Plane3, Boolean Construction)
                    string plane1Name = payload != null && payload.ContainsKey("plane1") ? payload["plane1"].ToString() : "XY";
                    string plane2Name = payload != null && payload.ContainsKey("plane2") ? payload["plane2"].ToString() : "XZ";
                    string plane3Name = payload != null && payload.ContainsKey("plane3") ? payload["plane3"].ToString() : "YZ";
                    var p1 = FindWorkPlaneGeneral(compDef, plane1Name);
                    var p2 = FindWorkPlaneGeneral(compDef, plane2Name);
                    var p3 = FindWorkPlaneGeneral(compDef, plane3Name);
                    workPoint = compDef.WorkPoints.AddByThreePlanes(p1, p2, p3, false);
                    description = $"Intersection of {plane1Name}, {plane2Name}, {plane3Name}";
                    break;
                }
                default:
                    throw new Exception($"Modo '{mode}' no reconocido. Usa: fixed, midpoint, three_planes");
            }

            int totalPoints = compDef.WorkPoints.Count;
            return new
            {
                Status          = "ok",
                WorkPointName   = workPoint.Name,
                WorkPointIndex  = totalPoints,
                WorkPointCount  = totalPoints,
                Mode            = mode,
                Description     = description
            };
        }
        // ──────────────────────────────────────────────────────────────
        // Grupo: Ensambles
        // ──────────────────────────────────────────────────────────────

        private ComponentOccurrence FindOccurrence(AssemblyComponentDefinition compDef, string nameOrIndex)
        {
            if (int.TryParse(nameOrIndex, out int idx))
                return compDef.Occurrences[idx];

            foreach (ComponentOccurrence occ in compDef.Occurrences)
                if (string.Compare(occ.Name, nameOrIndex, StringComparison.OrdinalIgnoreCase) == 0)
                    return occ;

            throw new Exception($"Componente '{nameOrIndex}' no encontrado. Verifica el nombre con get_active_document_info.");
        }

        private object InsertComponent(Dictionary<string, object> payload)
        {
            Document doc = _inventorApp.ActiveDocument;
            if (!(doc is AssemblyDocument asmDoc))
                throw new Exception("El documento activo debe ser un ensamble (.iam).");
            AssemblyComponentDefinition compDef = asmDoc.ComponentDefinition;

            if (!payload.ContainsKey("file_path"))
                throw new Exception("file_path es requerido (ruta completa al archivo .ipt o .iam).");
            string filePath = payload["file_path"].ToString().Replace('/', '\\');

            Matrix matrix = _inventorApp.TransientGeometry.CreateMatrix();
            ComponentOccurrence occurrence = compDef.Occurrences.Add(filePath, matrix);
            int total = compDef.Occurrences.Count;

            // Find index of the newly added occurrence
            int occIdx = 1;
            foreach (ComponentOccurrence o in compDef.Occurrences)
            {
                if (o.Name == occurrence.Name) break;
                occIdx++;
            }

            return new
            {
                Status           = "ok",
                OccurrenceName   = occurrence.Name,
                OccurrenceIndex  = occIdx,
                FilePath         = filePath,
                Grounded         = occurrence.Grounded,
                TotalOccurrences = total,
                Note             = "Componente en el origen (0,0,0). Usa ground_component para fijarlo como base o add_assembly_constraint / add_assembly_joint para posicionarlo respecto a otro componente."
            };
        }

        private object GroundComponent(Dictionary<string, object> payload)
        {
            Document doc = _inventorApp.ActiveDocument;
            if (!(doc is AssemblyDocument asmDoc))
                throw new Exception("El documento activo debe ser un ensamble (.iam).");
            AssemblyComponentDefinition compDef = asmDoc.ComponentDefinition;

            if (!payload.ContainsKey("occurrence"))
                throw new Exception("occurrence es requerido (nombre o índice 1-based del componente).");
            string occName = payload["occurrence"].ToString();
            bool ground = !payload.ContainsKey("ground") || Convert.ToBoolean(payload["ground"]);

            ComponentOccurrence occ = FindOccurrence(compDef, occName);
            occ.Grounded = ground;

            int total = compDef.Occurrences.Count;
            int idx = 1;
            foreach (ComponentOccurrence o in compDef.Occurrences)
            {
                if (o.Name == occ.Name) break;
                idx++;
            }

            return new
            {
                Status           = "ok",
                OccurrenceName   = occ.Name,
                OccurrenceIndex  = idx,
                Grounded         = occ.Grounded,
                TotalOccurrences = total
            };
        }

        private object AddAssemblyConstraint(Dictionary<string, object> payload)
        {
            Document doc = _inventorApp.ActiveDocument;
            if (!(doc is AssemblyDocument asmDoc))
                throw new Exception("El documento activo debe ser un ensamble (.iam).");
            AssemblyComponentDefinition compDef = asmDoc.ComponentDefinition;

            string constraintType = payload.ContainsKey("constraint_type")
                ? payload["constraint_type"].ToString().ToLower().Trim() : "mate";

            string occ1Name = payload.ContainsKey("occurrence1") ? payload["occurrence1"].ToString()
                : throw new Exception("occurrence1 es requerido.");
            int face1Idx = payload.ContainsKey("face1") ? Convert.ToInt32(payload["face1"]) : 1;
            string occ2Name = payload.ContainsKey("occurrence2") ? payload["occurrence2"].ToString()
                : throw new Exception("occurrence2 es requerido.");
            int face2Idx = payload.ContainsKey("face2") ? Convert.ToInt32(payload["face2"]) : 1;

            string units  = payload.ContainsKey("units") ? payload["units"].ToString().ToLower() : "mm";
            double rawVal = payload.ContainsKey("value") ? Convert.ToDouble(payload["value"]) : 0.0;

            ComponentOccurrence occ1 = FindOccurrence(compDef, occ1Name);
            ComponentOccurrence occ2 = FindOccurrence(compDef, occ2Name);

            // Face proxies — 1-based index on first solid body of each occurrence
            object face1 = occ1.SurfaceBodies[1].Faces[face1Idx];
            object face2 = occ2.SurfaceBodies[1].Faces[face2Idx];

            double offsetCm  = ToInventorCm(rawVal, units);
            double angleRad  = rawVal * Math.PI / 180.0;

            string constraintName = "";
            switch (constraintType)
            {
                case "mate":
                    var mate = compDef.Constraints.AddMateConstraint(face1, face2, offsetCm);
                    constraintName = mate.Name;
                    break;
                case "flush":
                    var flush = compDef.Constraints.AddFlushConstraint(face1, face2, offsetCm);
                    constraintName = flush.Name;
                    break;
                case "angle":
                    var angle = compDef.Constraints.AddAngleConstraint(face1, face2, angleRad);
                    constraintName = angle.Name;
                    break;
                case "tangent":
                    bool inside = payload.ContainsKey("inside") && Convert.ToBoolean(payload["inside"]);
                    var tangent = compDef.Constraints.AddTangentConstraint(face1, face2, inside, offsetCm);
                    constraintName = tangent.Name;
                    break;
                case "insert":
                    bool axesOpposed = !payload.ContainsKey("axes_opposed") || Convert.ToBoolean(payload["axes_opposed"]);
                    var insert = compDef.Constraints.AddInsertConstraint(face1, face2, axesOpposed, offsetCm);
                    constraintName = insert.Name;
                    break;
                default:
                    throw new Exception($"Tipo de restricción '{constraintType}' no válido. Usa: mate, flush, angle, tangent, insert.");
            }

            return new
            {
                Status          = "ok",
                ConstraintType  = constraintType,
                ConstraintName  = constraintName,
                EntityOne       = new { Occurrence = occ1.Name, FaceIndex = face1Idx },
                EntityTwo       = new { Occurrence = occ2.Name, FaceIndex = face2Idx },
                Value           = rawVal,
                Units           = units,
                TotalConstraints = compDef.Constraints.Count
            };
        }

        private object AddAssemblyJoint(Dictionary<string, object> payload)
        {
            Document doc = _inventorApp.ActiveDocument;
            if (!(doc is AssemblyDocument asmDoc))
                throw new Exception("El documento activo debe ser un ensamble (.iam).");
            AssemblyComponentDefinition compDef = asmDoc.ComponentDefinition;

            string jointTypeName = payload.ContainsKey("joint_type")
                ? payload["joint_type"].ToString().ToLower().Trim() : "rigid";
            string occ1Name = payload.ContainsKey("occurrence1") ? payload["occurrence1"].ToString()
                : throw new Exception("occurrence1 es requerido.");
            int face1Idx = payload.ContainsKey("face1") ? Convert.ToInt32(payload["face1"]) : 1;
            string occ2Name = payload.ContainsKey("occurrence2") ? payload["occurrence2"].ToString()
                : throw new Exception("occurrence2 es requerido.");
            int face2Idx = payload.ContainsKey("face2") ? Convert.ToInt32(payload["face2"]) : 1;

            ComponentOccurrence occ1 = FindOccurrence(compDef, occ1Name);
            ComponentOccurrence occ2 = FindOccurrence(compDef, occ2Name);

            object face1 = occ1.SurfaceBodies[1].Faces[face1Idx];
            object face2 = occ2.SurfaceBodies[1].Faces[face2Idx];

            GeometryIntent intentOne = compDef.CreateGeometryIntent(face1, Type.Missing);
            GeometryIntent intentTwo = compDef.CreateGeometryIntent(face2, Type.Missing);

            AssemblyJointTypeEnum jointType;
            switch (jointTypeName)
            {
                case "rigid":        jointType = AssemblyJointTypeEnum.kRigidJointType;       break;
                case "rotational":   jointType = AssemblyJointTypeEnum.kRotationalJointType;  break;
                case "sliding":      jointType = AssemblyJointTypeEnum.kSlideJointType;       break;
                case "cylindrical":  jointType = AssemblyJointTypeEnum.kCylindricalJointType; break;
                case "planar":       jointType = AssemblyJointTypeEnum.kPlanarJointType;      break;
                case "ball":         jointType = AssemblyJointTypeEnum.kBallJointType;        break;
                default: throw new Exception($"Tipo de joint '{jointTypeName}' no válido. Usa: rigid, rotational, sliding, cylindrical, planar, ball.");
            }

            AssemblyJointDefinition jointDef = compDef.Joints.CreateAssemblyJointDefinition(
                jointType, intentOne, intentTwo);
            AssemblyJoint joint = compDef.Joints.Add(jointDef);

            return new
            {
                Status       = "ok",
                JointType    = jointTypeName,
                JointName    = joint.Name,
                ComponentOne = new { Occurrence = occ1.Name, FaceIndex = face1Idx },
                ComponentTwo = new { Occurrence = occ2.Name, FaceIndex = face2Idx },
                TotalJoints  = compDef.Joints.Count
            };
        }

        private object GetAssemblyBOM(Dictionary<string, object> payload)
        {
            Document doc = _inventorApp.ActiveDocument;
            if (!(doc is AssemblyDocument asmDoc))
                throw new Exception("El documento activo debe ser un ensamble (.iam).");
            AssemblyComponentDefinition compDef = asmDoc.ComponentDefinition;

            BOM bom = compDef.BOM;

            // Prefer structured view, then parts-only, then first available
            BOMView targetView = null;
            foreach (BOMView v in bom.BOMViews)
                if (v.ViewType == BOMViewTypeEnum.kStructuredBOMViewType) { targetView = v; break; }
            if (targetView == null)
                foreach (BOMView v in bom.BOMViews)
                    if (v.ViewType == BOMViewTypeEnum.kPartsOnlyBOMViewType) { targetView = v; break; }
            if (targetView == null && bom.BOMViews.Count > 0)
                targetView = bom.BOMViews[1];
            if (targetView == null)
                throw new Exception("No se encontraron vistas de BOM en el ensamble.");

            var items = new System.Collections.Generic.List<object>();
            foreach (BOMRow row in targetView.BOMRows)
            {
                string partName = "";
                string fileName = "";
                try
                {
                    ComponentDefinition cd = row.ComponentDefinitions[1];
                    Document cdDoc = (Document)cd.Document;
                    partName = cdDoc.DisplayName;
                    try { fileName = System.IO.Path.GetFileName(cdDoc.FullFileName); } catch { }
                }
                catch { }

                items.Add(new
                {
                    ItemNumber = row.ItemNumber,
                    Quantity   = row.ItemQuantity,
                    PartName   = partName,
                    FileName   = fileName
                });
            }

            string viewTypeName = targetView.ViewType == BOMViewTypeEnum.kStructuredBOMViewType
                ? "Structured" : "PartsOnly";

            return new
            {
                Status       = "ok",
                DocumentName = asmDoc.DisplayName,
                ViewType     = viewTypeName,
                TotalItems   = items.Count,
                BOMItems     = items
            };
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
