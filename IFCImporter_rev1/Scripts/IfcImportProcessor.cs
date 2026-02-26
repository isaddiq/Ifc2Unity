using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.AI;
#endif

namespace IFCImporter
{
    /// <summary>
    /// Core IFC import functionality that handles the conversion and scene building process.
    /// Robotics-aware: creates semantic GameObjects for IfcSpace / IfcOpeningElement,
    /// builds an IfcRegistry, assigns layers, and attaches relationship components.
    /// </summary>
    public class IfcImportProcessor
    {
        // Import options
        private IfcImportOptions _options;

        // Statistics tracking
        private IfcImportStatistics _stats;

        // Loaded data
        private List<IfcElementData> _elements;
        private Dictionary<string, ObjMeshLoader.MultiMaterialMeshData> _meshes;
        private Dictionary<string, Material> _materials;
        private IfcSpatialHierarchy _hierarchy;
        private IfcRelationshipBundle _relationships;

        // Material cache by color
        private Dictionary<Color, Material> _colorMaterialCache;

        // Progress callback
        private Action<float, string> _progressCallback;

        // Stopwatches for timing
        private Stopwatch _totalTimer;
        private Stopwatch _phaseTimer;

        public IfcImportStatistics Statistics => _stats;

        public IfcImportProcessor(IfcImportOptions options = null)
        {
            _options = options ?? new IfcImportOptions();
            _stats = new IfcImportStatistics();
            _colorMaterialCache = new Dictionary<Color, Material>();
        }

        /// <summary>
        /// Run the Python script to convert IFC to OBJ/CSV.
        /// </summary>
        public bool RunPythonConversion(string ifcFilePath, Action<float, string> progressCallback = null)
        {
            _progressCallback = progressCallback;

            // Ensure we have an absolute path for the IFC file
            string absoluteIfcPath = Path.GetFullPath(ifcFilePath);

            _stats.IfcFilePath = absoluteIfcPath;
            _stats.IfcFileName = Path.GetFileName(absoluteIfcPath);
            _stats.ImportStartTime = DateTime.Now;

            _progressCallback?.Invoke(0f, "Starting Python conversion...");

            // Verify the IFC file exists
            if (!File.Exists(absoluteIfcPath))
            {
                Debug.LogError($"IFC file not found: {absoluteIfcPath}");
                return false;
            }

            // Find Python script
            string scriptPath = FindPythonScript();
            if (string.IsNullOrEmpty(scriptPath))
            {
                Debug.LogError("Python script not found: ifc_to_unity_export.py");
                return false;
            }

            // Ensure script path is also absolute
            scriptPath = Path.GetFullPath(scriptPath);

            // Build command with absolute paths
            string arguments = $"\"{scriptPath}\" \"{absoluteIfcPath}\"";

            _progressCallback?.Invoke(0.05f, $"Running: {_options.PythonExecutable} {arguments}");

            _phaseTimer = Stopwatch.StartNew();

            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = _options.PythonExecutable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(absoluteIfcPath)
                };

                var outputBuilder = new System.Text.StringBuilder();
                var errorBuilder = new System.Text.StringBuilder();

                using (var process = new Process())
                {
                    process.StartInfo = processInfo;

                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            outputBuilder.AppendLine(e.Data);

                            // Parse progress from Python output
                            if (e.Data.Contains("Processing:") || e.Data.Contains("Metadata:"))
                            {
                                ParsePythonProgress(e.Data);
                            }
                        }
                    };

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            errorBuilder.AppendLine(e.Data);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();

                    _phaseTimer.Stop();
                    _stats.PythonConversionTimeSeconds = _phaseTimer.Elapsed.TotalSeconds;

                    if (process.ExitCode != 0)
                    {
                        Debug.LogError($"Python conversion failed with exit code {process.ExitCode}");
                        Debug.LogError($"Python executable: {_options.PythonExecutable}");
                        Debug.LogError($"Script path: {scriptPath}");
                        Debug.LogError($"Arguments: {arguments}");
                        Debug.LogError($"Working directory: {Path.GetDirectoryName(ifcFilePath)}");
                        Debug.LogError($"Standard output: {outputBuilder}");
                        Debug.LogError($"Error output: {errorBuilder}");
                        return false;
                    }

                    // Log output
                    Debug.Log($"Python output:\n{outputBuilder}");

                    _progressCallback?.Invoke(0.4f, $"Python conversion complete ({_stats.PythonConversionTimeSeconds:F1}s)");
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to run Python script: {e.Message}");
                return false;
            }
        }

        private void ParsePythonProgress(string line)
        {
            // Try to parse progress like "Processing: 100/3717"
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)/(\d+)");
                if (match.Success)
                {
                    int current = int.Parse(match.Groups[1].Value);
                    int total = int.Parse(match.Groups[2].Value);
                    float progress = 0.05f + (0.35f * current / total);
                    _progressCallback?.Invoke(progress, line.Trim());
                }
            }
            catch { }
        }

        /// <summary>
        /// Load and process the exported files.
        /// </summary>
        public bool LoadExportedFiles(string ifcFilePath, Action<float, string> progressCallback = null)
        {
            _progressCallback = progressCallback;

            string basePath = Path.ChangeExtension(ifcFilePath, null);
            string objPath = basePath + ".obj";
            string mtlPath = basePath + ".mtl";
            string csvPath = basePath + "_metadata.csv";
            string relPath = basePath + "_relationships.json";

            // Track memory
            _stats.MemoryAtStartBytes = GC.GetTotalMemory(false);
            _stats.PeakMemoryBytes = _stats.MemoryAtStartBytes;

            // Load CSV metadata
            _progressCallback?.Invoke(0.42f, "Loading CSV metadata...");
            _phaseTimer = Stopwatch.StartNew();

            _elements = CsvMetadataParser.ParseCsvFile(csvPath);

            _phaseTimer.Stop();
            _stats.CsvParseTimeSeconds = _phaseTimer.Elapsed.TotalSeconds;
            _stats.CsvElementCount = _elements.Count;

            if (_elements.Count == 0)
            {
                Debug.LogError("No elements found in CSV file");
                return false;
            }

            _progressCallback?.Invoke(0.45f, $"Loaded {_elements.Count} elements from CSV");

            // Build spatial hierarchy
            _hierarchy = CsvMetadataParser.BuildSpatialHierarchy(_elements);
            _stats.ElementsByClass = CsvMetadataParser.GetElementClassCounts(_elements);
            _stats.ElementsByStorey = CsvMetadataParser.GetElementStoreyCounts(_elements);

            // Debug: Log hierarchy structure
            Debug.Log($"[Hierarchy] Project: {_hierarchy.ProjectName}");
            Debug.Log($"[Hierarchy] Sites: {_hierarchy.Sites.Count}");
            foreach (var site in _hierarchy.Sites)
            {
                Debug.Log($"  Site: '{site.Name}' with {site.Buildings.Count} buildings");
                foreach (var building in site.Buildings)
                {
                    Debug.Log($"    Building: '{building.Name}' with {building.Storeys.Count} storeys");
                    foreach (var storey in building.Storeys)
                    {
                        Debug.Log($"      Storey: '{storey.Name}' with {storey.ElementIds.Count} elements");
                    }
                }
            }

            // Debug: Sample element hierarchy data
            Debug.Log($"[Elements] Sample Site/Building/Storey data:");
            int sampleCount = 0;
            foreach (var elem in _elements)
            {
                if (!elem.IsSpatialElement())
                {
                    Debug.Log($"  {elem.IfcClass}: Site='{elem.Site}' Building='{elem.Building}' Storey='{elem.Storey}'");
                    if (++sampleCount >= 5) break;
                }
            }

            // Load OBJ meshes with multi-material support
            _progressCallback?.Invoke(0.5f, "Loading OBJ meshes with multi-material support...");
            _phaseTimer = Stopwatch.StartNew();

            _meshes = ObjMeshLoader.LoadObjFileMultiMaterial(objPath, (p, msg) =>
            {
                float overallProgress = 0.5f + (0.3f * p);
                _progressCallback?.Invoke(overallProgress, msg);
                UpdatePeakMemory();
            });

            _phaseTimer.Stop();
            _stats.MeshLoadTimeSeconds = _phaseTimer.Elapsed.TotalSeconds;
            _stats.ObjMeshCount = _meshes.Count;

            // Debug: Log some mesh keys to verify what was loaded
            if (_meshes.Count > 0)
            {
                Debug.Log($"Loaded {_meshes.Count} meshes. Sample keys:");
                int count = 0;
                int multiMatCount = 0;
                foreach (var kvp in _meshes)
                {
                    if (count < 5)
                    {
                        Debug.Log($"  Mesh key: '{kvp.Key}' (submeshes: {kvp.Value.SubmeshCount})");
                    }
                    if (kvp.Value.HasMultipleMaterials)
                        multiMatCount++;
                    count++;
                }
                Debug.Log($"Multi-material meshes: {multiMatCount}");
            }
            else
            {
                Debug.LogWarning("No meshes were loaded from OBJ file!");
            }

            // Debug: Log some element GlobalIds to compare
            if (_elements.Count > 0)
            {
                Debug.Log($"Loaded {_elements.Count} elements from CSV. Sample GlobalIds:");
                int count = 0;
                foreach (var elem in _elements)
                {
                    if (!elem.IsSpatialElement())
                    {
                        Debug.Log($"  Element GlobalId: '{elem.GlobalId}'");
                        if (++count >= 5) break;
                    }
                }
            }

            _progressCallback?.Invoke(0.8f, $"Loaded {_meshes.Count} meshes");

            // Load materials
            _progressCallback?.Invoke(0.82f, "Loading materials...");

            _materials = ObjMeshLoader.LoadMtlFile(mtlPath);
            _stats.MaterialsCreated = _materials.Count;

            _progressCallback?.Invoke(0.85f, $"Loaded {_materials.Count} materials");

            // Load relationships JSON
            _relationships = LoadRelationships(relPath);
            if (_relationships != null)
            {
                Debug.Log($"[Relationships] Voids: {_relationships.Voids.Count}, Fills: {_relationships.Fills.Count}, " +
                          $"Containments: {_relationships.Containments.Count}, SpaceBoundaries: {_relationships.SpaceBoundaries.Count}, " +
                          $"UnitScale: {_relationships.UnitScaleToMetres}, Schema: {_relationships.Schema}");
            }

            UpdatePeakMemory();
            return true;
        }

        // ════════════════════════════════════════════════════════
        //  Relationship JSON loader (typed, spec-compliant parser)
        // ════════════════════════════════════════════════════════

        private IfcRelationshipBundle LoadRelationships(string jsonPath)
        {
            var bundle = new IfcRelationshipBundle();
            if (!File.Exists(jsonPath))
            {
                Debug.LogWarning($"Relationships JSON not found: {jsonPath}");
                return bundle;
            }

            try
            {
                string json = File.ReadAllText(jsonPath);
                var parseResult = IfcRelationshipJsonParser.Parse(json);

                // Log validation results
                foreach (var warning in parseResult.Warnings)
                    Debug.LogWarning($"[IfcRelJsonParser] {warning}");
                foreach (var error in parseResult.Errors)
                    Debug.LogError($"[IfcRelJsonParser] {error}");

                if (parseResult.Success)
                {
                    bundle = parseResult.Bundle;
                    Debug.Log($"[IfcRelJsonParser] Parsed {parseResult.TotalRelationshipsParsed} relationships with {parseResult.Warnings.Count} warnings.");
                }
                else
                {
                    Debug.LogError($"[IfcRelJsonParser] Failed with {parseResult.Errors.Count} errors. Using empty bundle.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to load relationships JSON: {e.Message}");
            }
            return bundle;
        }

        // ════════════════════════════════════════════════════════
        //  Build Scene Hierarchy (robotics-aware)
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// Build the Unity scene hierarchy from loaded data.
        /// Creates: IFC_Root > Project > Site > Building > Storey > { Spaces, Elements }
        /// Also creates semantic-only GameObjects for IfcSpace / IfcOpeningElement,
        /// attaches IfcRelations and IfcPassage components, builds the IfcRegistry,
        /// and assigns robotics layers.
        /// </summary>
        public GameObject BuildSceneHierarchy(string rootName, Action<float, string> progressCallback = null)
        {
            _progressCallback = progressCallback;
            _totalTimer = Stopwatch.StartNew();
            _phaseTimer = Stopwatch.StartNew();

            _progressCallback?.Invoke(0.86f, "Building IFC hierarchy...");

            // Dictionary to store all created GameObjects by GlobalId
            var objectsByGlobalId = new Dictionary<string, GameObject>();

            // ─── Ensure layers exist (editor only) ───
#if UNITY_EDITOR
            if (_options.AssignLayers)
                RoboticsLayerSetup.EnsureLayersExist();
#endif
            RoboticsLayerSetup.RefreshLayerIndices();

            // ─── Create root ───
            var rootObject = new GameObject(rootName);
            var registry = rootObject.AddComponent<IfcRegistry>();
            int hierarchyNodeCount = 0;

            // ═══ Step 1: Spatial structure backbone ═══

            // IfcProject
            GameObject projectObj = null;
            var projectElement = _elements.FirstOrDefault(e => e.IfcClass == "IfcProject");
            if (projectElement != null)
            {
                projectObj = CreateHierarchyNode(
                    string.IsNullOrEmpty(projectElement.Name) ? "IfcProject" : projectElement.Name,
                    rootObject.transform, "IfcProject");
                objectsByGlobalId[projectElement.GlobalId] = projectObj;
                hierarchyNodeCount++;
                AssignMetadata(projectObj, projectElement);
                registry.Register(projectElement.GlobalId, projectObj,
                    projectObj.GetComponent<IfcMetadata>());
            }
            else
            {
                projectObj = CreateHierarchyNode("IfcProject", rootObject.transform, "IfcProject");
                hierarchyNodeCount++;
            }

            // IfcSite(s) — deduplicate by name to merge duplicate sites
            var siteElements = _elements.Where(e => e.IfcClass == "IfcSite").ToList();
            var siteByName = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var site in siteElements)
            {
                string siteName = string.IsNullOrEmpty(site.Name) ? "IfcSite" : site.Name;

                if (siteByName.TryGetValue(siteName, out var existingSiteObj))
                {
                    objectsByGlobalId[site.GlobalId] = existingSiteObj;
                    registry.Register(site.GlobalId, existingSiteObj,
                        existingSiteObj.GetComponent<IfcMetadata>());
                    continue;
                }

                var siteObj = CreateHierarchyNode(siteName, projectObj.transform, "IfcSite");
                objectsByGlobalId[site.GlobalId] = siteObj;
                siteByName[siteName] = siteObj;
                hierarchyNodeCount++;
                _stats.SitesCreated++;
                AssignMetadata(siteObj, site);
                registry.Register(site.GlobalId, siteObj, siteObj.GetComponent<IfcMetadata>());
            }
            if (siteElements.Count == 0)
            {
                var defaultSite = CreateHierarchyNode("Default Site", projectObj.transform, "IfcSite");
                objectsByGlobalId["_default_site_"] = defaultSite;
                hierarchyNodeCount++;
                _stats.SitesCreated++;
            }

            // IfcBuilding(s) — deduplicate by name to merge duplicate buildings
            var buildingElements = _elements.Where(e => e.IfcClass == "IfcBuilding").ToList();
            var buildingByName = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var building in buildingElements)
            {
                string buildingName = string.IsNullOrEmpty(building.Name) ? "IfcBuilding" : building.Name;

                if (buildingByName.TryGetValue(buildingName, out var existingBuildingObj))
                {
                    objectsByGlobalId[building.GlobalId] = existingBuildingObj;
                    registry.Register(building.GlobalId, existingBuildingObj,
                        existingBuildingObj.GetComponent<IfcMetadata>());
                    continue;
                }

                Transform parentTransform = FindSiteParent(building, siteElements, objectsByGlobalId, projectObj);
                var buildingObj = CreateHierarchyNode(buildingName, parentTransform, "IfcBuilding");
                objectsByGlobalId[building.GlobalId] = buildingObj;
                buildingByName[buildingName] = buildingObj;
                hierarchyNodeCount++;
                _stats.BuildingsCreated++;
                AssignMetadata(buildingObj, building);
                registry.Register(building.GlobalId, buildingObj, buildingObj.GetComponent<IfcMetadata>());
            }
            if (buildingElements.Count == 0)
            {
                Transform defaultBuildingParent = ResolveParent(objectsByGlobalId,
                    "_default_site_", siteElements, projectObj);
                var defaultBuilding = CreateHierarchyNode("Default Building",
                    defaultBuildingParent, "IfcBuilding");
                objectsByGlobalId["_default_building_"] = defaultBuilding;
                hierarchyNodeCount++;
                _stats.BuildingsCreated++;
            }

            // IfcBuildingStorey(s) — deduplicate by name so that storeys with identical
            // names (common when Revit exports separate SL/FL/CL levels per discipline)
            // are merged into a single hierarchy node.
            var storeyElements = _elements.Where(e => e.IfcClass == "IfcBuildingStorey").ToList();
            var storeyByName = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var storey in storeyElements)
            {
                string storeyName = string.IsNullOrEmpty(storey.Name) ? "IfcBuildingStorey" : storey.Name;

                // Reuse an existing storey node with the same name
                if (storeyByName.TryGetValue(storeyName, out var existingStoreyObj))
                {
                    // Map this GlobalId to the already-created node
                    objectsByGlobalId[storey.GlobalId] = existingStoreyObj;
                    registry.Register(storey.GlobalId, existingStoreyObj,
                        existingStoreyObj.GetComponent<IfcMetadata>());
                    registry.Categorise(storey.GlobalId, "IfcBuildingStorey", "");
                    continue;
                }

                Transform parentTransform = FindBuildingParent(storey, buildingElements, objectsByGlobalId, projectObj);
                var storeyObj = CreateHierarchyNode(storeyName, parentTransform, "IfcBuildingStorey");

                // Set elevation as Y position hint
                if (storey.Elevation != 0f)
                {
                    float elevMetres = storey.Elevation * (_relationships?.UnitScaleToMetres ?? 1f);
                    // Don't move transform (geometry is in world coords), just store metadata
                    var meta = storeyObj.GetComponent<IfcMetadata>();
                    if (meta != null) meta.Elevation = elevMetres;
                }

                objectsByGlobalId[storey.GlobalId] = storeyObj;
                storeyByName[storeyName] = storeyObj;
                hierarchyNodeCount++;
                _stats.StoreysCreated++;
                AssignMetadata(storeyObj, storey);
                registry.Register(storey.GlobalId, storeyObj, storeyObj.GetComponent<IfcMetadata>());
                registry.Categorise(storey.GlobalId, "IfcBuildingStorey", "");

                // Create "Spaces" and "Elements" sub-groups under each storey
                var spacesGroup = new GameObject("Spaces");
                spacesGroup.transform.SetParent(storeyObj.transform, false);
                var elementsGroup = new GameObject("Elements");
                elementsGroup.transform.SetParent(storeyObj.transform, false);
            }
            if (storeyElements.Count == 0)
            {
                Transform defaultStoreyParent = ResolveParent(objectsByGlobalId,
                    "_default_building_", buildingElements, projectObj);
                var defaultStorey = CreateHierarchyNode("Default Level",
                    defaultStoreyParent, "IfcBuildingStorey");
                objectsByGlobalId["_default_storey_"] = defaultStorey;
                var spacesGroup = new GameObject("Spaces");
                spacesGroup.transform.SetParent(defaultStorey.transform, false);
                var elementsGroup = new GameObject("Elements");
                elementsGroup.transform.SetParent(defaultStorey.transform, false);
                hierarchyNodeCount++;
                _stats.StoreysCreated++;
            }

            Debug.Log($"[IFC Hierarchy] Sites: {_stats.SitesCreated}, Buildings: {_stats.BuildingsCreated}, Storeys: {_stats.StoreysCreated}");

            // ═══ Step 2: Create product elements ═══
            int processedCount = 0;
            var productElements = _elements.Where(e => !e.IsSpatialElement()).ToList();
            int totalElements = productElements.Count;

            foreach (var element in productElements)
            {
                processedCount++;
                if (processedCount % 100 == 0)
                {
                    float progress = 0.86f + (0.10f * processedCount / totalElements);
                    _progressCallback?.Invoke(progress, $"Creating elements: {processedCount}/{totalElements}");
                    UpdatePeakMemory();
                }

                // Determine parent: storey > Elements sub-group, or Spaces sub-group for IfcSpace
                Transform parentTransform = FindStoreyParent(element, storeyElements, objectsByGlobalId, projectObj);
                bool isSpace = element.IfcClass == "IfcSpace";
                if (isSpace)
                {
                    // Put spaces under Spaces sub-group
                    var spacesChild = parentTransform.Find("Spaces");
                    if (spacesChild != null) parentTransform = spacesChild;
                }
                else
                {
                    var elemChild = parentTransform.Find("Elements");
                    if (elemChild != null) parentTransform = elemChild;

                    // Group by IFC class: create/find sub-group named after the IFC class
                    if (_options.GroupByClass && !string.IsNullOrEmpty(element.IfcClass))
                    {
                        string className = element.IfcClass;
                        Transform classGroup = parentTransform.Find(className);
                        if (classGroup == null)
                        {
                            var classObj = new GameObject(className);
                            classObj.transform.SetParent(parentTransform, false);
                            classGroup = classObj.transform;
                        }
                        parentTransform = classGroup;
                    }
                }

                var elementObj = CreateElement(element, parentTransform);
                if (elementObj != null)
                {
                    objectsByGlobalId[element.GlobalId] = elementObj;
                    _stats.SuccessfullyImported++;
                    hierarchyNodeCount++;

                    bool hasGeo = elementObj.GetComponent<MeshFilter>() != null &&
                                  elementObj.GetComponent<MeshFilter>().sharedMesh != null;
                    if (hasGeo) _stats.ElementsWithGeometry++;
                    else _stats.LogicalNodes++;

                    // Register in registry
                    var meta = elementObj.GetComponent<IfcMetadata>();
                    registry.Register(element.GlobalId, elementObj, meta);
                    registry.Categorise(element.GlobalId, element.IfcClass, element.PredefinedType);

                    // Assign layer
                    if (_options.AssignLayers)
                        RoboticsLayerSetup.AssignLayer(elementObj, element.IfcClass, element.PredefinedType);

                    // Door dimensions: compute from bounds if missing
                    if (IsDoorLike(element.IfcClass) && meta != null)
                    {
                        float w = element.OverallWidth;
                        float h = element.OverallHeight;

                        // Apply unit scale if values look raw
                        float unitScale = _relationships?.UnitScaleToMetres ?? 1f;
                        bool fromBounds = false;
                        if (w > 0) w *= unitScale;
                        if (h > 0) h *= unitScale;

                        // Fallback: compute from mesh bounds
                        if ((w <= 0 || h <= 0) && hasGeo)
                        {
                            var bounds = elementObj.GetComponent<MeshFilter>().sharedMesh.bounds;
                            if (w <= 0) w = Mathf.Max(bounds.size.x, bounds.size.z);
                            if (h <= 0) h = bounds.size.y;
                            fromBounds = true;
                        }

                        meta.SetDoorDimensions(w, h, element.OperationType);

                        // Annotate door dimension source confidence
                        var dimAnnotation = fromBounds
                            ? IfcSourceAnnotation.Geometry("Fallback from mesh bounds")
                            : IfcSourceAnnotation.Standard("IfcDoor.OverallWidth/Height");
                        meta.AddProperty("Door.OverallWidth", w.ToString("F4", System.Globalization.CultureInfo.InvariantCulture), dimAnnotation);
                        meta.AddProperty("Door.OverallHeight", h.ToString("F4", System.Globalization.CultureInfo.InvariantCulture), dimAnnotation);
                        if (!string.IsNullOrEmpty(element.OperationType))
                            meta.AddProperty("Door.OperationType", element.OperationType, IfcSourceAnnotation.TypeObj("IfcDoorType.OperationType"));
                    }
                }
                else
                {
                    _stats.FailedToImport++;
                }
            }

            // ═══ Step 3: Wire relationships ═══
            _progressCallback?.Invoke(0.96f, "Wiring relationships...");
            WireRelationships(objectsByGlobalId, registry);

            // ═══ Step 3b: Build space adjacency NavGraph ═══
            _progressCallback?.Invoke(0.97f, "Building space adjacency graph...");
            BuildSpaceAdjacencyGraph(rootObject, registry);

            // ═══ Step 4: Create IfcPassage components on doors/openings ═══
            _progressCallback?.Invoke(0.98f, "Creating passage descriptors...");
            CreatePassageDescriptors(registry);

            // ═══ Step 5: Colliders ═══
            if (_options.CreateColliders)
            {
                _progressCallback?.Invoke(0.985f, "Adding colliders...");
                AddColliders(objectsByGlobalId, registry);
            }

            // ═══ Finalise ═══
            _phaseTimer.Stop();
            _stats.SceneBuildTimeSeconds = _phaseTimer.Elapsed.TotalSeconds;
            _stats.TotalHierarchyNodes = hierarchyNodeCount;

            _totalTimer.Stop();
            _stats.TotalTimeSeconds = _totalTimer.Elapsed.TotalSeconds;
            _stats.ImportEndTime = DateTime.Now;
            _stats.MemoryAtEndBytes = GC.GetTotalMemory(false);

            Debug.Log($"[IfcRegistry] {registry.GetSummary()}");
            _progressCallback?.Invoke(1f, "Import complete!");

            // ═══ Save incremental import manifest ═══
            if (_options.IncrementalImport && !string.IsNullOrEmpty(_options.SourceFileReference))
            {
                var manifest = IfcIncrementalImport.BuildManifest(
                    _options.SourceFileReference, _elements, _relationships?.Schema ?? "");
                IfcIncrementalImport.SaveManifest(_options.SourceFileReference, manifest);
                Debug.Log($"[IncrementalImport] Manifest saved ({_elements.Count} element fingerprints)");
            }

            return rootObject;
        }

        // ════════════════════════════════════════════════════════
        //  Incremental Import  (delta-update strategy)
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// Perform an incremental import.  If a previous manifest exists and the IFC
        /// file has changed, computes a delta and applies selective updates (add, modify,
        /// remove) rather than rebuilding the entire scene.  Returns the delta summary
        /// if incremental mode was used, or null when a full import was performed instead.
        /// </summary>
        public IfcImportDelta TryIncrementalImport(string ifcFilePath, Action<float, string> progressCallback = null)
        {
            _progressCallback = progressCallback;

            // 1. Load the previous manifest
            var previousManifest = IfcIncrementalImport.LoadManifest(ifcFilePath);
            if (previousManifest == null)
            {
                Debug.Log("[IncrementalImport] No previous manifest found — full import required.");
                return null; // caller should fall back to full import
            }

            // 2. Quick file-level check
            if (!IfcIncrementalImport.HasFileChanged(ifcFilePath, previousManifest))
            {
                Debug.Log("[IncrementalImport] IFC file unchanged (same hash). Nothing to do.");
                return new IfcImportDelta(); // empty delta
            }

            // 3. Ensure exported artefacts are current
            _progressCallback?.Invoke(0.1f, "Incremental: loading exported files...");
            if (!LoadExportedFiles(ifcFilePath, progressCallback))
            {
                Debug.LogError("[IncrementalImport] Failed to load exported files for delta comparison.");
                return null;
            }

            // 4. Compute delta
            _progressCallback?.Invoke(0.5f, "Computing element delta...");
            var delta = IfcIncrementalImport.ComputeDelta(_elements, previousManifest);
            Debug.Log($"[IncrementalImport] {delta.GetSummary()}");

            if (!delta.HasChanges)
            {
                Debug.Log("[IncrementalImport] No element-level changes detected.");
                // Save updated manifest (timestamp)
                SaveCurrentManifest(ifcFilePath);
                return delta;
            }

            // 5. Find existing IfcRegistry in scene
            var existingRegistries = UnityEngine.Object.FindObjectsByType<IfcRegistry>(FindObjectsSortMode.None);
            IfcRegistry registry = null;
            foreach (var reg in existingRegistries)
            {
                // Match by SourceFile field stored directly on the root IfcMetadata component
                var rootMeta = reg.GetComponent<IfcMetadata>();
                if (rootMeta != null &&
                    !string.IsNullOrEmpty(rootMeta.SourceFile) &&
                    rootMeta.SourceFile.Equals(ifcFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    registry = reg;
                    break;
                }
            }

            if (registry == null)
            {
                Debug.LogWarning("[IncrementalImport] Could not find matching IfcRegistry in scene. Falling back to full import.");
                return null;
            }

            // 6. Apply deletions
            _progressCallback?.Invoke(0.6f, $"Removing {delta.RemovedIds.Count} deleted elements...");
            foreach (var removedId in delta.RemovedIds)
            {
                var go = registry.GetGameObject(removedId);
                if (go != null)
                {
                    Debug.Log($"[IncrementalImport] Removed: {removedId}");
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }

            // 7. Build set of ids that need full (re-)creation
            var idsToProcess = new HashSet<string>(delta.AddedIds);
            foreach (var modId in delta.ModifiedIds)
                idsToProcess.Add(modId);

            // 8. Re-create modified elements (destroy old, then treat as new)
            foreach (var modId in delta.ModifiedIds)
            {
                var go = registry.GetGameObject(modId);
                if (go != null)
                {
                    Debug.Log($"[IncrementalImport] Updating modified: {modId}");
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }

            // 9. Create added + modified elements via the normal pipeline
            var elementsToImport = _elements.Where(e => idsToProcess.Contains(e.GlobalId)).ToList();
            _progressCallback?.Invoke(0.7f, $"Importing {elementsToImport.Count} changed elements...");

            int imported = 0;
            foreach (var element in elementsToImport)
            {
                imported++;
                float progress = 0.7f + (0.25f * imported / Math.Max(elementsToImport.Count, 1));
                _progressCallback?.Invoke(progress, $"Incremental: {imported}/{elementsToImport.Count}");

                // Find an appropriate parent in the existing hierarchy
                Transform parent = FindIncrementalParent(element, registry);
                var elementObj = CreateElement(element, parent);
                if (elementObj != null)
                {
                    var meta = elementObj.GetComponent<IfcMetadata>();
                    registry.Register(element.GlobalId, elementObj, meta);
                    registry.Categorise(element.GlobalId, element.IfcClass, element.PredefinedType);

                    if (_options.AssignLayers)
                        RoboticsLayerSetup.AssignLayer(elementObj, element.IfcClass, element.PredefinedType);
                }
            }

            // 10. Save updated manifest
            SaveCurrentManifest(ifcFilePath);

            _progressCallback?.Invoke(1f, "Incremental import complete!");
            Debug.Log($"[IncrementalImport] Done. Processed {delta.ChangedCount} changes, " +
                      $"skipped {delta.UnchangedIds.Count} unchanged elements.");

            return delta;
        }

        /// <summary>
        /// Find a suitable parent transform for an element during incremental import.
        /// Searches the existing hierarchy for the matching storey → class group.
        /// </summary>
        private Transform FindIncrementalParent(IfcElementData element, IfcRegistry registry)
        {
            Transform root = registry.transform;

            // Try to find the storey node
            if (!string.IsNullOrEmpty(element.Storey))
            {
                var storeyNode = FindChildRecursive(root, StripIfcClassPrefix(element.Storey));
                if (storeyNode != null)
                {
                    bool isSpace = element.IfcClass == "IfcSpace";
                    string subGroup = isSpace ? "Spaces" : "Elements";
                    var sub = storeyNode.Find(subGroup);
                    if (sub != null)
                    {
                        // Optionally find class group
                        if (!isSpace && _options.GroupByClass && !string.IsNullOrEmpty(element.IfcClass))
                        {
                            var classGroup = sub.Find(element.IfcClass);
                            if (classGroup != null) return classGroup;
                            // Create the class group if it doesn't exist
                            var newGroup = new GameObject(element.IfcClass);
                            newGroup.transform.SetParent(sub, false);
                            return newGroup.transform;
                        }
                        return sub;
                    }
                    return storeyNode;
                }
            }
            return root;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Transform child in parent)
            {
                string childName = StripIfcClassPrefix(child.name);
                if (string.Equals(childName, name, StringComparison.OrdinalIgnoreCase))
                    return child;
                var found = FindChildRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private void SaveCurrentManifest(string ifcFilePath)
        {
            var manifest = IfcIncrementalImport.BuildManifest(
                ifcFilePath, _elements, _relationships?.Schema ?? "");
            IfcIncrementalImport.SaveManifest(ifcFilePath, manifest);
        }

        // ════════════════════════════════════════════════════════
        //  Parent Resolution Helpers
        // ════════════════════════════════════════════════════════

        private Transform FindSiteParent(IfcElementData building,
            List<IfcElementData> siteElements,
            Dictionary<string, GameObject> objectsByGlobalId,
            GameObject projectObj)
        {
            if (!string.IsNullOrEmpty(building.Site))
            {
                var parentSite = siteElements.FirstOrDefault(s => s.Name == building.Site);
                if (parentSite != null && objectsByGlobalId.TryGetValue(parentSite.GlobalId, out var siteObj))
                    return siteObj.transform;

                // Fallback: strip IFC class prefix and try again
                string siteClean = StripIfcClassPrefix(building.Site);
                parentSite = siteElements.FirstOrDefault(s =>
                    string.Equals(StripIfcClassPrefix(s.Name), siteClean, StringComparison.OrdinalIgnoreCase));
                if (parentSite != null && objectsByGlobalId.TryGetValue(parentSite.GlobalId, out siteObj))
                    return siteObj.transform;
            }
            if (objectsByGlobalId.TryGetValue("_default_site_", out var def))
                return def.transform;
            if (siteElements.Count > 0 && objectsByGlobalId.TryGetValue(siteElements[0].GlobalId, out var first))
                return first.transform;
            return projectObj.transform;
        }

        private Transform FindBuildingParent(IfcElementData storey,
            List<IfcElementData> buildingElements,
            Dictionary<string, GameObject> objectsByGlobalId,
            GameObject projectObj)
        {
            if (!string.IsNullOrEmpty(storey.Building))
            {
                var parentBuilding = buildingElements.FirstOrDefault(b => b.Name == storey.Building);
                if (parentBuilding != null && objectsByGlobalId.TryGetValue(parentBuilding.GlobalId, out var buildingObj))
                    return buildingObj.transform;

                // Fallback: strip IFC class prefix and try again
                string buildingClean = StripIfcClassPrefix(storey.Building);
                parentBuilding = buildingElements.FirstOrDefault(b =>
                    string.Equals(StripIfcClassPrefix(b.Name), buildingClean, StringComparison.OrdinalIgnoreCase));
                if (parentBuilding != null && objectsByGlobalId.TryGetValue(parentBuilding.GlobalId, out buildingObj))
                    return buildingObj.transform;
            }
            if (objectsByGlobalId.TryGetValue("_default_building_", out var def))
                return def.transform;
            if (buildingElements.Count > 0 && objectsByGlobalId.TryGetValue(buildingElements[0].GlobalId, out var first))
                return first.transform;
            return projectObj.transform;
        }

        private Transform FindStoreyParent(IfcElementData element,
            List<IfcElementData> storeyElements,
            Dictionary<string, GameObject> objectsByGlobalId,
            GameObject projectObj)
        {
            if (!string.IsNullOrEmpty(element.Storey))
            {
                // Exact match first
                var containingStorey = storeyElements.FirstOrDefault(s => s.Name == element.Storey);
                if (containingStorey != null && objectsByGlobalId.TryGetValue(containingStorey.GlobalId, out var storeyObj))
                    return storeyObj.transform;

                // Try case-insensitive match
                containingStorey = storeyElements.FirstOrDefault(s =>
                    string.Equals(s.Name, element.Storey, StringComparison.OrdinalIgnoreCase));
                if (containingStorey != null && objectsByGlobalId.TryGetValue(containingStorey.GlobalId, out storeyObj))
                    return storeyObj.transform;

                // Try matching after stripping "IfcBuildingStorey: " prefix from either side
                string elementStoreyClean = StripIfcClassPrefix(element.Storey);
                foreach (var s in storeyElements)
                {
                    string storeyNameClean = StripIfcClassPrefix(s.Name);
                    if (string.Equals(storeyNameClean, elementStoreyClean, StringComparison.OrdinalIgnoreCase))
                    {
                        if (objectsByGlobalId.TryGetValue(s.GlobalId, out storeyObj))
                            return storeyObj.transform;
                    }
                }
            }
            if (objectsByGlobalId.TryGetValue("_default_storey_", out var def))
                return def.transform;
            if (storeyElements.Count > 0 && objectsByGlobalId.TryGetValue(storeyElements[0].GlobalId, out var first))
                return first.transform;
            return projectObj.transform;
        }

        /// <summary>
        /// Strip IFC class prefixes like "IfcBuildingStorey: " from hierarchy names
        /// to enable robust matching between different naming conventions.
        /// </summary>
        private static string StripIfcClassPrefix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            // Handles patterns like "IfcBuildingStorey: Name", "IfcBuilding: Name", etc.
            int colonIdx = name.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx > 0 && name.StartsWith("Ifc", StringComparison.OrdinalIgnoreCase))
                return name.Substring(colonIdx + 2);
            return name;
        }

        private Transform ResolveParent(Dictionary<string, GameObject> objectsByGlobalId,
            string defaultKey, List<IfcElementData> fallbackList, GameObject projectObj)
        {
            if (objectsByGlobalId.TryGetValue(defaultKey, out var def))
                return def.transform;
            if (fallbackList.Count > 0 && objectsByGlobalId.TryGetValue(fallbackList[0].GlobalId, out var first))
                return first.transform;
            return projectObj.transform;
        }

        // ════════════════════════════════════════════════════════
        //  Relationship Wiring
        // ════════════════════════════════════════════════════════

        private void WireRelationships(Dictionary<string, GameObject> objectsByGlobalId, IfcRegistry registry)
        {
            if (_relationships == null) return;

            // Build lookup helpers
            var voidsByOpening = new Dictionary<string, string>(); // openingId -> hostId
            var voidsByHost = new Dictionary<string, List<string>>(); // hostId -> [openingIds]
            foreach (var v in _relationships.Voids)
            {
                if (!string.IsNullOrEmpty(v.RelatedOpeningElement))
                    voidsByOpening[v.RelatedOpeningElement] = v.RelatingBuildingElement;
                if (!string.IsNullOrEmpty(v.RelatingBuildingElement))
                {
                    if (!voidsByHost.TryGetValue(v.RelatingBuildingElement, out var list))
                    {
                        list = new List<string>();
                        voidsByHost[v.RelatingBuildingElement] = list;
                    }
                    list.Add(v.RelatedOpeningElement);
                }
            }

            var fillByOpening = new Dictionary<string, string>(); // openingId -> fillingElementId
            var fillByElement = new Dictionary<string, string>(); // fillingElementId -> openingId
            foreach (var f in _relationships.Fills)
            {
                if (!string.IsNullOrEmpty(f.RelatingOpeningElement))
                    fillByOpening[f.RelatingOpeningElement] = f.RelatedBuildingElement;
                if (!string.IsNullOrEmpty(f.RelatedBuildingElement))
                    fillByElement[f.RelatedBuildingElement] = f.RelatingOpeningElement;
            }

            // Containment: relatedElement -> structureId
            var containment = new Dictionary<string, string>();
            foreach (var c in _relationships.Containments)
            {
                if (!string.IsNullOrEmpty(c.RelatedElement))
                    containment[c.RelatedElement] = c.RelatingStructure;
            }

            // SpaceBoundary: spaceId -> [elementIds], elementId -> [spaceIds]
            var sbBySpace = new Dictionary<string, List<string>>();
            var sbByElement = new Dictionary<string, List<string>>();
            foreach (var sb in _relationships.SpaceBoundaries)
            {
                if (!string.IsNullOrEmpty(sb.RelatingSpace))
                {
                    if (!sbBySpace.TryGetValue(sb.RelatingSpace, out var sList))
                    {
                        sList = new List<string>();
                        sbBySpace[sb.RelatingSpace] = sList;
                    }
                    sList.Add(sb.RelatedBuildingElement);
                }
                if (!string.IsNullOrEmpty(sb.RelatedBuildingElement))
                {
                    if (!sbByElement.TryGetValue(sb.RelatedBuildingElement, out var eList))
                    {
                        eList = new List<string>();
                        sbByElement[sb.RelatedBuildingElement] = eList;
                    }
                    eList.Add(sb.RelatingSpace);
                }
            }

            // Aggregates
            var aggByChild = new Dictionary<string, string>();
            var aggByParent = new Dictionary<string, List<string>>();
            foreach (var a in _relationships.Aggregates)
            {
                if (!string.IsNullOrEmpty(a.RelatedObject))
                    aggByChild[a.RelatedObject] = a.RelatingObject;
                if (!string.IsNullOrEmpty(a.RelatingObject))
                {
                    if (!aggByParent.TryGetValue(a.RelatingObject, out var list))
                    {
                        list = new List<string>();
                        aggByParent[a.RelatingObject] = list;
                    }
                    list.Add(a.RelatedObject);
                }
            }

            // Attach IfcRelations components
            foreach (var kvp in objectsByGlobalId)
            {
                string gid = kvp.Key;
                if (gid.StartsWith("_default_")) continue;

                var go = kvp.Value;
                var rels = go.GetComponent<IfcRelations>();
                if (rels == null) rels = go.AddComponent<IfcRelations>();

                // Containment
                if (containment.TryGetValue(gid, out var structId))
                    rels.ContainedInStructureId = structId;

                // Voids: this element is an opening that voids a host
                if (voidsByOpening.TryGetValue(gid, out var hostId))
                    rels.HostElementId = hostId;

                // Voids: this element is a host that has openings
                if (voidsByHost.TryGetValue(gid, out var openingList))
                    rels.OpeningElementIds = new List<string>(openingList);

                // Fills: this door/window fills an opening
                if (fillByElement.TryGetValue(gid, out var fillsOpeningId))
                    rels.FillsOpeningId = fillsOpeningId;

                // Fills: this opening is filled by a door/window
                if (fillByOpening.TryGetValue(gid, out var filledById))
                    rels.FilledByElementId = filledById;

                // Space boundaries
                if (sbBySpace.TryGetValue(gid, out var sbElements))
                    rels.SpaceBoundaryElementIds = sbElements;
                if (sbByElement.TryGetValue(gid, out var sbSpaces))
                    rels.SpaceBoundarySpaceIds = sbSpaces;

                // Aggregates
                if (aggByChild.TryGetValue(gid, out var parentAgg))
                    rels.AggregateParentId = parentAgg;
                if (aggByParent.TryGetValue(gid, out var childrenAgg))
                    rels.AggregateChildIds = childrenAgg;

                // Update registry with rels
                registry.Register(gid, go, go.GetComponent<IfcMetadata>(), rels);
            }
        }

        // ════════════════════════════════════════════════════════
        //  Passage Descriptors
        // ════════════════════════════════════════════════════════

        private void CreatePassageDescriptors(IfcRegistry registry)
        {
            // For each door, create an IfcPassage
            foreach (var doorId in registry.DoorIds)
            {
                var go = registry.GetGameObject(doorId);
                if (go == null) continue;

                var meta = registry.GetMetadata(doorId);
                var rels = registry.GetRelations(doorId);

                var passage = go.AddComponent<IfcPassage>();
                passage.Width = meta != null ? meta.OverallWidth : 0f;
                passage.Height = meta != null ? meta.OverallHeight : 0f;
                passage.DoorId = doorId;
                passage.OperationType = meta != null ? meta.OperationType : "";

                if (rels != null)
                {
                    passage.OpeningId = rels.FillsOpeningId;
                    // Resolve host wall via opening
                    if (!string.IsNullOrEmpty(rels.FillsOpeningId))
                    {
                        var openingRels = registry.GetRelations(rels.FillsOpeningId);
                        if (openingRels != null)
                            passage.HostWallId = openingRels.HostElementId;
                    }
                }
            }

            // For each opening without a filling door, also create a passage
            foreach (var openingId in registry.OpeningIds)
            {
                var go = registry.GetGameObject(openingId);
                if (go == null) continue;

                var rels = registry.GetRelations(openingId);
                if (rels != null && !string.IsNullOrEmpty(rels.FilledByElementId))
                    continue; // Already covered by the door above

                var passage = go.AddComponent<IfcPassage>();
                passage.OpeningId = openingId;
                passage.HostWallId = rels?.HostElementId ?? "";

                // Try to compute width/height from bounds
                var meshFilter = go.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    var bounds = meshFilter.sharedMesh.bounds;
                    passage.Width = Mathf.Max(bounds.size.x, bounds.size.z);
                    passage.Height = bounds.size.y;
                }
            }
        }

        // ════════════════════════════════════════════════════════
        //  Space Adjacency NavGraph
        // ════════════════════════════════════════════════════════

        private void BuildSpaceAdjacencyGraph(GameObject rootObject, IfcRegistry registry)
        {
            if (_relationships == null || registry.SpaceIds.Count == 0)
            {
                Debug.Log("[IfcSpaceAdjacencyGraph] No spaces or relationships — skipping NavGraph.");
                return;
            }

            var graph = rootObject.AddComponent<IfcSpaceAdjacencyGraph>();
            graph.BuildFromRelationships(_relationships, registry);
            Debug.Log($"[IfcSpaceAdjacencyGraph] {graph.GetSummary()}");
        }

        // ════════════════════════════════════════════════════════
        //  Collider Strategy
        // ════════════════════════════════════════════════════════

        private void AddColliders(Dictionary<string, GameObject> objectsByGlobalId, IfcRegistry registry)
        {
            foreach (var kvp in objectsByGlobalId)
            {
                if (kvp.Key.StartsWith("_default_")) continue;
                var go = kvp.Value;
                var mf = go.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var mesh = mf.sharedMesh;

                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                // Convex MeshColliders are required for Rigidbody interaction;
                // non-convex is preferred for static environment accuracy.
                mc.convex = false;
            }
        }

        private static bool IsDoorLike(string ifcClass)
        {
            if (string.IsNullOrEmpty(ifcClass)) return false;
            return ifcClass.Contains("IfcDoor");
        }

        /// <summary>
        /// Create a hierarchy node (Site, Building, Storey).
        /// </summary>
        private GameObject CreateHierarchyNode(string name, Transform parent, string ifcClass)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            // Add IfcMetadata component
            if (_options.AssignMetadata)
            {
                var metadata = obj.AddComponent<IfcMetadata>();
                metadata.SetIdentification(
                    globalId: System.Guid.NewGuid().ToString(),
                    ifcClass: ifcClass,
                    elementName: name
                );
            }

            return obj;
        }

        /// <summary>
        /// Create an IFC element with mesh and metadata.
        /// Creates element in hierarchy even without geometry to preserve IFC structure.
        /// Supports multi-material meshes with submeshes.
        /// </summary>
        private GameObject CreateElement(IfcElementData elementData, Transform parent)
        {
            // Create object - ALWAYS create even without mesh
            string objectName = BuildElementName(elementData);

            var obj = new GameObject(objectName);
            obj.transform.SetParent(parent, false);

            // Try to get mesh - don't fail if mesh not found
            if (_meshes.TryGetValue(elementData.GlobalId, out var meshData) && meshData != null && meshData.Mesh != null && meshData.Mesh.vertexCount > 0)
            {
                elementData.HasGeometry = true;

                var meshFilter = obj.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = meshData.Mesh;

                var meshRenderer = obj.AddComponent<MeshRenderer>();

                // Apply materials based on submesh count
                if (meshData.HasMultipleMaterials)
                {
                    // Multi-material: create material array for each submesh
                    var materials = new Material[meshData.MaterialNames.Count];
                    for (int i = 0; i < meshData.MaterialNames.Count; i++)
                    {
                        string matName = meshData.MaterialNames[i];
                        materials[i] = GetMaterialByName(matName, elementData);
                    }
                    meshRenderer.sharedMaterials = materials;
                    _stats.ElementsWithColor++;
                }
                else
                {
                    // Single material
                    Material material = GetMaterialForElement(elementData);
                    meshRenderer.sharedMaterial = material;
                }

                // Colliders are now added centrally by AddColliders() after all elements exist,
                // to apply the smart box-vs-mesh strategy.  Do NOT add here.
            }
            else
            {
                elementData.HasGeometry = false;
                _stats.MeshesNotFound++;
            }

            // Add metadata
            if (_options.AssignMetadata)
            {
                AssignMetadata(obj, elementData);
            }

            return obj;
        }

        /// <summary>
        /// Get material by name from loaded MTL materials, or create from element color.
        /// </summary>
        private Material GetMaterialByName(string materialName, IfcElementData elementData)
        {
            // Try exact match from MTL
            if (_materials.TryGetValue(materialName, out var mtlMaterial))
            {
                return mtlMaterial;
            }

            // Try case-insensitive match
            foreach (var kvp in _materials)
            {
                if (string.Equals(kvp.Key, materialName, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }

            // Fallback to element's color-based material
            return GetMaterialForElement(elementData);
        }

        /// <summary>
        /// Get or create material for an element.
        /// Priority: MTL file material > Color-based material > Default material
        /// </summary>
        private Material GetMaterialForElement(IfcElementData elementData)
        {
            // First priority: Try to use material from MTL file by exact name
            if (!string.IsNullOrEmpty(elementData.Material))
            {
                if (_materials.TryGetValue(elementData.Material, out var mtlMaterial))
                {
                    _stats.ElementsWithColor++;
                    return mtlMaterial;
                }

                // Try case-insensitive match
                foreach (var kvp in _materials)
                {
                    if (string.Equals(kvp.Key, elementData.Material, StringComparison.OrdinalIgnoreCase))
                    {
                        _stats.ElementsWithColor++;
                        return kvp.Value;
                    }
                }
            }

            // Second priority: Use color-based material from CSV data
            if (_options.ApplyColors && elementData.Color.a > 0)
            {
                _stats.ElementsWithColor++;
                return GetOrCreateColorMaterial(elementData.Color);
            }

            // Default material
            return GetDefaultMaterial();
        }

        /// <summary>
        /// Get or create a material for a specific color.
        /// </summary>
        private Material GetOrCreateColorMaterial(Color color)
        {
            // Round color to reduce unique materials
            Color roundedColor = new Color(
                Mathf.Round(color.r * 100) / 100f,
                Mathf.Round(color.g * 100) / 100f,
                Mathf.Round(color.b * 100) / 100f,
                Mathf.Round(color.a * 100) / 100f
            );

            if (_colorMaterialCache.TryGetValue(roundedColor, out var material))
            {
                return material;
            }

            // Create new material - try URP first
            var shader = Shader.Find(_options.DefaultShaderName);
            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Lit");
            }
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            material = new Material(shader);
            material.name = $"IFC_Color_{ColorUtility.ToHtmlStringRGBA(roundedColor)}";

            // Set color based on shader type
            SetMaterialColor(material, roundedColor);

            // Enable transparency if needed
            if (roundedColor.a < 1f && _options.EnableTransparency)
            {
                SetMaterialTransparent(material);
            }

            _colorMaterialCache[roundedColor] = material;
            _stats.MaterialsCreated++;

            return material;
        }

        /// <summary>
        /// Get default material.
        /// </summary>
        private Material GetDefaultMaterial()
        {
            return GetOrCreateColorMaterial(new Color(0.8f, 0.8f, 0.8f, 1f));
        }

        /// <summary>
        /// Set material to transparent mode (supports both URP and Standard).
        /// </summary>
        private void SetMaterialTransparent(Material material)
        {
            // URP Lit shader transparency
            if (material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 1); // 0 = Opaque, 1 = Transparent
                material.SetFloat("_Blend", 0); // 0 = Alpha, 1 = Premultiply, 2 = Additive, 3 = Multiply
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            // Standard shader transparency
            else if (material.HasProperty("_Mode"))
            {
                material.SetFloat("_Mode", 3); // Transparent mode
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
        }

        /// <summary>
        /// Set material color (supports both URP and Standard shaders).
        /// </summary>
        private void SetMaterialColor(Material material, Color color)
        {
            // URP Lit shader uses _BaseColor
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            // Standard shader uses _Color
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
            // Also set the main color property
            material.color = color;
        }

        /// <summary>
        /// Assign IfcMetadata to a GameObject.
        /// Assigns ALL properties and attributes regardless of whether they're empty.
        /// Uses GetComponent-or-AddComponent to avoid duplicates when CreateHierarchyNode
        /// already added an IfcMetadata stub.
        /// Attaches per-field source-confidence annotations.
        /// </summary>
        private void AssignMetadata(GameObject obj, IfcElementData elementData)
        {
            var metadata = obj.GetComponent<IfcMetadata>();
            if (metadata == null)
                metadata = obj.AddComponent<IfcMetadata>();

            // Set core identification (always set, even if empty)
            metadata.SetIdentification(
                globalId: elementData.GlobalId ?? "",
                ifcClass: elementData.IfcClass ?? "",
                elementName: elementData.Name ?? "",
                description: elementData.Description ?? "",
                objectType: elementData.ObjectType ?? "",
                elementId: elementData.Tag ?? "",
                predefinedType: elementData.PredefinedType ?? "",
                sourceFile: _options.SourceFileReference ?? ""
            );

            // Set spatial hierarchy (always set, even if empty)
            string spatialPath = BuildSpatialPath(elementData);
            metadata.SetHierarchy(
                project: elementData.Project ?? "",
                site: elementData.Site ?? "",
                building: elementData.Building ?? "",
                storey: elementData.Storey ?? "",
                parentGlobalId: "",
                spatialPath: spatialPath,
                elevation: elementData.Elevation,
                longName: elementData.LongName ?? ""
            );

            // Set material information (always set)
            metadata.SetMaterial(
                materialName: elementData.Material ?? "",
                color: elementData.Color
            );

            // Set classification (derive discipline from IFC class) — heuristic
            string discipline = DetermineDiscipline(elementData.IfcClass);
            string category = (elementData.IfcClass ?? "").Replace("Ifc", "");
            metadata.SetClassification(discipline, category);

            // Type information as properties with TypeObject source annotation
            metadata.AddProperty(
                $"{IfcMetadata.Categories.Type}.TypeName",
                elementData.TypeName ?? "",
                IfcSourceAnnotation.TypeObj("IfcTypeObject.Name"));
            metadata.AddProperty(
                $"{IfcMetadata.Categories.Type}.TypeId",
                elementData.TypeId ?? "",
                IfcSourceAnnotation.TypeObj("IfcTypeObject.GlobalId"));

            // Spatial annotation
            metadata.AddProperty(
                $"{IfcMetadata.Categories.Location}.Space",
                elementData.Space ?? "",
                IfcSourceAnnotation.Spatial("IfcRelContainedInSpatialStructure"));

            // Classification fields — importer heuristic
            metadata.AddProperty(
                $"{IfcMetadata.Categories.Classifications}.Discipline",
                discipline,
                IfcSourceAnnotation.Heuristic("DetermineDiscipline()"));
            metadata.AddProperty(
                $"{IfcMetadata.Categories.Classifications}.Category",
                category,
                IfcSourceAnnotation.Heuristic("IfcClass prefix strip"));

            // Add all properties from property sets and quantity sets with proper annotations
            foreach (var kvp in elementData.Properties)
            {
                string propKey = kvp.Key;
                IfcSourceAnnotation annotation;

                if (propKey.StartsWith("Pset_", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract Pset name from key like "Pset_WallCommon_IsExternal"
                    int secondUnderscore = propKey.IndexOf('_', 5);
                    string psetName = secondUnderscore > 0 ? propKey.Substring(0, secondUnderscore) : propKey;
                    annotation = IfcSourceAnnotation.Pset(psetName);
                }
                else if (propKey.StartsWith("Qto_", StringComparison.OrdinalIgnoreCase))
                {
                    int secondUnderscore = propKey.IndexOf('_', 4);
                    string qtoName = secondUnderscore > 0 ? propKey.Substring(0, secondUnderscore) : propKey;
                    annotation = IfcSourceAnnotation.Qto(qtoName);
                }
                else
                {
                    annotation = IfcSourceAnnotation.Standard(propKey);
                }

                metadata.AddProperty(propKey, kvp.Value ?? "", annotation);
            }
        }

        /// <summary>
        /// Build spatial path string from element data.
        /// </summary>
        private string BuildSpatialPath(IfcElementData elementData)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(elementData.Site)) parts.Add(elementData.Site);
            if (!string.IsNullOrEmpty(elementData.Building)) parts.Add(elementData.Building);
            if (!string.IsNullOrEmpty(elementData.Storey)) parts.Add(elementData.Storey);
            if (!string.IsNullOrEmpty(elementData.Space)) parts.Add(elementData.Space);
            return string.Join(" > ", parts);
        }

        /// <summary>
        /// Build element name with format: "ElementName_Tag" or "IfcClass_GlobalId" as fallback.
        /// Example: "Basic Wall" with tag "123456" -> "Basic Wall_123456"
        /// </summary>
        private string BuildElementName(IfcElementData elementData)
        {
            string baseName = elementData.Name;
            string tag = elementData.Tag;

            // If we have a name, clean it first (remove any existing tag suffix) then add tag with underscore
            if (!string.IsNullOrEmpty(baseName))
            {
                // Remove any existing tag suffix (could be :tag, _tag, or space+tag)
                baseName = RemoveExistingTagSuffix(baseName, tag);

                // Add tag with underscore separator if tag exists
                if (!string.IsNullOrEmpty(tag))
                {
                    return $"{baseName}_{tag}";
                }
                return baseName;
            }

            // Fallback: use IFC class and GlobalId
            string shortGuid = elementData.GlobalId.Length >= 8
                ? elementData.GlobalId.Substring(0, 8)
                : elementData.GlobalId;
            return $"{elementData.IfcClass}_{shortGuid}";
        }

        /// <summary>
        /// Remove existing tag suffix from name (handles :tag, _tag, space+tag patterns).
        /// </summary>
        private string RemoveExistingTagSuffix(string name, string tag)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            // If tag is present, try to remove it with various separators
            if (!string.IsNullOrEmpty(tag))
            {
                // Check for ":tag" at the end
                string tagSuffix = ":" + tag;
                if (name.EndsWith(tagSuffix))
                {
                    return name.Substring(0, name.Length - tagSuffix.Length).Trim();
                }

                // Check for "_tag" at the end
                tagSuffix = "_" + tag;
                if (name.EndsWith(tagSuffix))
                {
                    return name.Substring(0, name.Length - tagSuffix.Length).Trim();
                }

                // Check for " tag" at the end (space separator)
                tagSuffix = " " + tag;
                if (name.EndsWith(tagSuffix))
                {
                    return name.Substring(0, name.Length - tagSuffix.Length).Trim();
                }
            }

            // Also try to remove trailing numeric ID patterns like ":123456" or "_123456"
            var match = System.Text.RegularExpressions.Regex.Match(name, @"[:_]\s*\d+$");
            if (match.Success)
            {
                return name.Substring(0, match.Index).Trim();
            }

            return name;
        }

        /// <summary>
        /// Determine discipline from IFC class name.
        /// </summary>
        private string DetermineDiscipline(string ifcClass)
        {
            if (string.IsNullOrEmpty(ifcClass)) return "Unknown";

            // Structural elements
            if (ifcClass.Contains("Beam") || ifcClass.Contains("Column") ||
                ifcClass.Contains("Footing") || ifcClass.Contains("Pile") ||
                ifcClass.Contains("Member") || ifcClass.Contains("Plate"))
                return "Structural";

            // MEP elements
            if (ifcClass.Contains("Pipe") || ifcClass.Contains("Duct") ||
                ifcClass.Contains("Cable") || ifcClass.Contains("Flow") ||
                ifcClass.Contains("Distribution") || ifcClass.Contains("Energy") ||
                ifcClass.Contains("Sanitary") || ifcClass.Contains("Light"))
                return "MEP";

            // Architectural elements
            return "Architectural";
        }

        /// <summary>
        /// Update peak memory tracking.
        /// </summary>
        private void UpdatePeakMemory()
        {
            long currentMemory = GC.GetTotalMemory(false);
            if (currentMemory > _stats.PeakMemoryBytes)
            {
                _stats.PeakMemoryBytes = currentMemory;
            }
        }

        /// <summary>
        /// Find the Python script path.
        /// </summary>
        private string FindPythonScript()
        {
            // Check standard locations
            string[] searchPaths = new[]
            {
                Path.Combine(Application.dataPath, "BIMUniXchange", "IFCImporter", "Python", "ifc_to_unity_export.py"),
                Path.Combine(Application.dataPath, "IFCImporter", "Python", "ifc_to_unity_export.py"),
                Path.Combine(Application.dataPath, "Scripts", "IFCImporter", "ifc_to_unity_export.py")
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // Search Assets folder
#if UNITY_EDITOR
            var guids = AssetDatabase.FindAssets("ifc_to_unity_export");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".py"))
                {
                    return Path.GetFullPath(path);
                }
            }
#endif

            return null;
        }
    }
}
