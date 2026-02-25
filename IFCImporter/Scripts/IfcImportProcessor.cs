using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace IFCImporter
{
    /// <summary>
    /// Core IFC import functionality that handles the conversion and scene building process.
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

            UpdatePeakMemory();
            return true;
        }

        /// <summary>
        /// Build the Unity scene hierarchy from loaded data.
        /// Uses IFC schema hierarchy: Project > Site > Building > Storey > Elements
        /// Each element is placed under its containing storey based on IFC containment.
        /// </summary>
        public GameObject BuildSceneHierarchy(string rootName, Action<float, string> progressCallback = null)
        {
            _progressCallback = progressCallback;
            _totalTimer = Stopwatch.StartNew();
            _phaseTimer = Stopwatch.StartNew();

            _progressCallback?.Invoke(0.86f, "Building IFC hierarchy...");

            // Dictionary to store all created GameObjects by GlobalId
            var objectsByGlobalId = new Dictionary<string, GameObject>();

            // Create root object
            var rootObject = new GameObject(rootName);
            int hierarchyNodeCount = 0;

            // Step 1: Create all spatial structure elements (Project, Site, Building, Storey)
            // These form the backbone of the IFC hierarchy

            // Find and create IfcProject
            GameObject projectObj = null;
            var projectElement = _elements.FirstOrDefault(e => e.IfcClass == "IfcProject");
            if (projectElement != null)
            {
                projectObj = CreateHierarchyNode(
                    string.IsNullOrEmpty(projectElement.Name) ? "IfcProject" : projectElement.Name,
                    rootObject.transform,
                    "IfcProject");
                objectsByGlobalId[projectElement.GlobalId] = projectObj;
                hierarchyNodeCount++;
                AssignMetadata(projectObj, projectElement);
            }
            else
            {
                // Create default project if none exists
                projectObj = CreateHierarchyNode("IfcProject", rootObject.transform, "IfcProject");
                hierarchyNodeCount++;
            }

            // Find and create IfcSite(s)
            var siteElements = _elements.Where(e => e.IfcClass == "IfcSite").ToList();
            foreach (var site in siteElements)
            {
                var siteObj = CreateHierarchyNode(
                    string.IsNullOrEmpty(site.Name) ? "IfcSite" : site.Name,
                    projectObj.transform,
                    "IfcSite");
                objectsByGlobalId[site.GlobalId] = siteObj;
                hierarchyNodeCount++;
                _stats.SitesCreated++;
                AssignMetadata(siteObj, site);
            }

            // If no sites, create a default one
            if (siteElements.Count == 0)
            {
                var defaultSite = CreateHierarchyNode("Default Site", projectObj.transform, "IfcSite");
                objectsByGlobalId["_default_site_"] = defaultSite;
                hierarchyNodeCount++;
                _stats.SitesCreated++;
            }

            // Find and create IfcBuilding(s)
            var buildingElements = _elements.Where(e => e.IfcClass == "IfcBuilding").ToList();
            foreach (var building in buildingElements)
            {
                // Find parent site
                Transform parentTransform = projectObj.transform;
                if (!string.IsNullOrEmpty(building.Site))
                {
                    var parentSite = siteElements.FirstOrDefault(s => s.Name == building.Site);
                    if (parentSite != null && objectsByGlobalId.TryGetValue(parentSite.GlobalId, out var siteObj))
                    {
                        parentTransform = siteObj.transform;
                    }
                }
                else if (objectsByGlobalId.TryGetValue("_default_site_", out var defaultSite))
                {
                    parentTransform = defaultSite.transform;
                }
                else if (siteElements.Count > 0 && objectsByGlobalId.TryGetValue(siteElements[0].GlobalId, out var firstSite))
                {
                    parentTransform = firstSite.transform;
                }

                var buildingObj = CreateHierarchyNode(
                    string.IsNullOrEmpty(building.Name) ? "IfcBuilding" : building.Name,
                    parentTransform,
                    "IfcBuilding");
                objectsByGlobalId[building.GlobalId] = buildingObj;
                hierarchyNodeCount++;
                _stats.BuildingsCreated++;
                AssignMetadata(buildingObj, building);
            }

            // If no buildings, create a default one
            if (buildingElements.Count == 0)
            {
                Transform defaultBuildingParent = projectObj.transform;
                if (objectsByGlobalId.TryGetValue("_default_site_", out var defaultSite))
                    defaultBuildingParent = defaultSite.transform;
                else if (siteElements.Count > 0 && objectsByGlobalId.TryGetValue(siteElements[0].GlobalId, out var firstSite))
                    defaultBuildingParent = firstSite.transform;

                var defaultBuilding = CreateHierarchyNode("Default Building", defaultBuildingParent, "IfcBuilding");
                objectsByGlobalId["_default_building_"] = defaultBuilding;
                hierarchyNodeCount++;
                _stats.BuildingsCreated++;
            }

            // Find and create IfcBuildingStorey(s)
            var storeyElements = _elements.Where(e => e.IfcClass == "IfcBuildingStorey").ToList();
            foreach (var storey in storeyElements)
            {
                // Find parent building
                Transform parentTransform = projectObj.transform;
                if (!string.IsNullOrEmpty(storey.Building))
                {
                    var parentBuilding = buildingElements.FirstOrDefault(b => b.Name == storey.Building);
                    if (parentBuilding != null && objectsByGlobalId.TryGetValue(parentBuilding.GlobalId, out var buildingObj))
                    {
                        parentTransform = buildingObj.transform;
                    }
                }
                else if (objectsByGlobalId.TryGetValue("_default_building_", out var defaultBuilding))
                {
                    parentTransform = defaultBuilding.transform;
                }
                else if (buildingElements.Count > 0 && objectsByGlobalId.TryGetValue(buildingElements[0].GlobalId, out var firstBuilding))
                {
                    parentTransform = firstBuilding.transform;
                }

                var storeyObj = CreateHierarchyNode(
                    string.IsNullOrEmpty(storey.Name) ? "IfcBuildingStorey" : storey.Name,
                    parentTransform,
                    "IfcBuildingStorey");
                objectsByGlobalId[storey.GlobalId] = storeyObj;
                hierarchyNodeCount++;
                _stats.StoreysCreated++;
                AssignMetadata(storeyObj, storey);
            }

            // If no storeys, create a default one
            if (storeyElements.Count == 0)
            {
                Transform defaultStoreyParent = projectObj.transform;
                if (objectsByGlobalId.TryGetValue("_default_building_", out var defaultBuilding))
                    defaultStoreyParent = defaultBuilding.transform;
                else if (buildingElements.Count > 0 && objectsByGlobalId.TryGetValue(buildingElements[0].GlobalId, out var firstBuilding))
                    defaultStoreyParent = firstBuilding.transform;

                var defaultStorey = CreateHierarchyNode("Default Level", defaultStoreyParent, "IfcBuildingStorey");
                objectsByGlobalId["_default_storey_"] = defaultStorey;
                hierarchyNodeCount++;
                _stats.StoreysCreated++;
            }

            // Debug log the spatial hierarchy
            Debug.Log($"[IFC Hierarchy] Project: {projectObj?.name}");
            Debug.Log($"[IFC Hierarchy] Sites: {_stats.SitesCreated}, Buildings: {_stats.BuildingsCreated}, Storeys: {_stats.StoreysCreated}");

            // Step 2: Create all product elements under their containing storey
            int processedCount = 0;
            var productElements = _elements.Where(e => !e.IsSpatialElement()).ToList();
            int totalElements = productElements.Count;

            foreach (var element in productElements)
            {
                processedCount++;

                if (processedCount % 100 == 0)
                {
                    float progress = 0.86f + (0.12f * processedCount / totalElements);
                    _progressCallback?.Invoke(progress, $"Creating elements: {processedCount}/{totalElements}");
                    UpdatePeakMemory();
                }

                // Find the parent storey for this element
                Transform parentTransform = projectObj.transform;

                // Try to find the storey by name from the element's Storey field
                if (!string.IsNullOrEmpty(element.Storey))
                {
                    var containingStorey = storeyElements.FirstOrDefault(s => s.Name == element.Storey);
                    if (containingStorey != null && objectsByGlobalId.TryGetValue(containingStorey.GlobalId, out var storeyObj))
                    {
                        parentTransform = storeyObj.transform;
                    }
                    else if (objectsByGlobalId.TryGetValue("_default_storey_", out var defaultStorey))
                    {
                        parentTransform = defaultStorey.transform;
                    }
                }
                else if (objectsByGlobalId.TryGetValue("_default_storey_", out var defaultStorey))
                {
                    parentTransform = defaultStorey.transform;
                }
                else if (storeyElements.Count > 0 && objectsByGlobalId.TryGetValue(storeyElements[0].GlobalId, out var firstStorey))
                {
                    parentTransform = firstStorey.transform;
                }

                // Create the element
                var elementObj = CreateElement(element, parentTransform);
                if (elementObj != null)
                {
                    objectsByGlobalId[element.GlobalId] = elementObj;
                    _stats.SuccessfullyImported++;
                    hierarchyNodeCount++;

                    // Check if element has geometry
                    var meshFilter = elementObj.GetComponent<MeshFilter>();
                    if (meshFilter != null && meshFilter.sharedMesh != null)
                    {
                        _stats.ElementsWithGeometry++;
                    }
                    else
                    {
                        _stats.LogicalNodes++;
                    }
                }
                else
                {
                    _stats.FailedToImport++;
                }
            }

            _phaseTimer.Stop();
            _stats.SceneBuildTimeSeconds = _phaseTimer.Elapsed.TotalSeconds;
            _stats.TotalHierarchyNodes = hierarchyNodeCount;

            _totalTimer.Stop();
            _stats.TotalTimeSeconds = _totalTimer.Elapsed.TotalSeconds;
            _stats.ImportEndTime = DateTime.Now;
            _stats.MemoryAtEndBytes = GC.GetTotalMemory(false);

            _progressCallback?.Invoke(1f, "Import complete!");

            return rootObject;
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

                // Add collider if requested
                if (_options.CreateColliders && meshData.Mesh.vertexCount > 0)
                {
                    var collider = obj.AddComponent<MeshCollider>();
                    collider.sharedMesh = meshData.Mesh;
                }
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
        /// </summary>
        private void AssignMetadata(GameObject obj, IfcElementData elementData)
        {
            var metadata = obj.AddComponent<IfcMetadata>();

            // Set core identification (always set, even if empty)
            metadata.SetIdentification(
                globalId: elementData.GlobalId ?? "",
                ifcClass: elementData.IfcClass ?? "",
                elementName: elementData.Name ?? "",
                description: elementData.Description ?? "",
                objectType: elementData.ObjectType ?? "",
                elementId: elementData.Tag ?? ""
            );

            // Set spatial hierarchy (always set, even if empty)
            string spatialPath = BuildSpatialPath(elementData);
            metadata.SetHierarchy(
                project: elementData.Project ?? "",
                site: elementData.Site ?? "",
                building: elementData.Building ?? "",
                storey: elementData.Storey ?? "",
                parentGlobalId: "",
                spatialPath: spatialPath
            );

            // Set material information (always set)
            metadata.SetMaterial(
                materialName: elementData.Material ?? "",
                color: elementData.Color
            );

            // Set classification (derive discipline from IFC class)
            string discipline = DetermineDiscipline(elementData.IfcClass);
            string category = (elementData.IfcClass ?? "").Replace("Ifc", "");
            metadata.SetClassification(discipline, category);

            // Always add type information as properties (even if empty)
            metadata.AddProperty($"{IfcMetadata.Categories.Type}.TypeName", elementData.TypeName ?? "");
            metadata.AddProperty($"{IfcMetadata.Categories.Type}.TypeId", elementData.TypeId ?? "");

            // Add Space information
            metadata.AddProperty($"{IfcMetadata.Categories.Location}.Space", elementData.Space ?? "");

            // Add all properties from property sets and quantity sets
            foreach (var kvp in elementData.Properties)
            {
                // Properties already have format like "Pset_WallCommon.IsExternal" or "Qto_WallBaseQuantities.GrossVolume"
                metadata.AddProperty(kvp.Key, kvp.Value ?? "");
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
