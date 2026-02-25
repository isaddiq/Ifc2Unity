#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Debug = UnityEngine.Debug;

namespace IFCImporter.Editor
{
    /// <summary>
    /// Editor window for importing IFC files into Unity using Python IfcOpenShell.
    /// </summary>
    public class IfcImporterWindow : EditorWindow
    {
        // Input fields
        private string _ifcFilePath = "";
        private string _pythonExecutable = "python";

        // Import options
        private bool _createHierarchy = true;
        private bool _assignMetadata = true;
        private bool _applyColors = true;
        private bool _createColliders = false;
        private bool _groupByStorey = true;
        private bool _groupByClass = false;
        private float _scaleFactor = 1.0f;

        // Advanced options
        private bool _showAdvancedOptions = false;
        private bool _optimizeMeshes = true;
        private bool _generateUVs = true;
        private bool _enableTransparency = true;
        private string _defaultShaderName = "Universal Render Pipeline/Lit";

        // State
        private bool _isProcessing = false;
        private float _progress = 0f;
        private string _progressMessage = "";
        private IfcImportStatistics _lastImportStats;

        // Log
        private Vector2 _logScrollPosition;
        private List<string> _logMessages = new List<string>();
        private bool _autoScrollLog = true;

        // Statistics scroll
        private Vector2 _statsScrollPosition;
        private string _statsText = "";

        // Scroll position
        private Vector2 _mainScrollPosition;

        // Drag and drop state
        private bool _isDraggingOver = false;

        [MenuItem("Window/BIMUniXchange/IFC Importer (IfcOpenShell)", false, 0)]
        public static void ShowWindow()
        {
            var window = GetWindow<IfcImporterWindow>("IFC Importer");
            window.minSize = new Vector2(450, 600);
        }

        private void OnGUI()
        {
            _mainScrollPosition = EditorGUILayout.BeginScrollView(_mainScrollPosition);

            DrawHeader();
            EditorGUILayout.Space();

            DrawFileSelection();
            EditorGUILayout.Space();

            DrawImportOptions();
            EditorGUILayout.Space();

            DrawAdvancedOptions();
            EditorGUILayout.Space();

            DrawImportButton();
            EditorGUILayout.Space();

            if (_isProcessing)
            {
                DrawProgressBar();
                EditorGUILayout.Space();
            }

            if (_lastImportStats != null)
            {
                DrawStatistics();
                EditorGUILayout.Space();
            }

            DrawLogSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter
            };

            EditorGUILayout.LabelField("IFC Importer (IfcOpenShell)", titleStyle);
            EditorGUILayout.LabelField("Import IFC files with geometry, materials, and metadata",
                EditorStyles.centeredGreyMiniLabel);

            EditorGUILayout.EndVertical();
        }

        private void DrawFileSelection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("File Selection", EditorStyles.boldLabel);

            // Enhanced drag and drop area with IFC icon style
            Rect dropArea = GUILayoutUtility.GetRect(0, 80, GUILayout.ExpandWidth(true));

            // Draw background based on drag state
            Color bgColor = _isDraggingOver ? new Color(0.3f, 0.6f, 0.9f, 0.3f) : new Color(0.2f, 0.2f, 0.2f, 0.3f);
            EditorGUI.DrawRect(dropArea, bgColor);

            // Draw border
            Color borderColor = _isDraggingOver ? new Color(0.3f, 0.6f, 0.9f, 1f) : new Color(0.5f, 0.5f, 0.5f, 0.5f);
            DrawBorder(dropArea, borderColor, _isDraggingOver ? 2 : 1);

            // Draw IFC icon/logo area
            var iconStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 24,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = _isDraggingOver ? new Color(0.3f, 0.6f, 0.9f) : new Color(0.6f, 0.6f, 0.6f) }
            };

            var subtitleStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                fontSize = 11,
                normal = { textColor = _isDraggingOver ? new Color(0.4f, 0.7f, 1f) : Color.gray }
            };

            // Icon area
            Rect iconRect = new Rect(dropArea.x, dropArea.y + 10, dropArea.width, 35);
            GUI.Label(iconRect, "📁 IFC", iconStyle);

            // Subtitle
            Rect subtitleRect = new Rect(dropArea.x, dropArea.y + 45, dropArea.width, 20);
            string dropText = _isDraggingOver ? "Release to Import" : "Drag & Drop IFC File Here";
            GUI.Label(subtitleRect, dropText, subtitleStyle);

            // Show selected file name if any
            if (!string.IsNullOrEmpty(_ifcFilePath))
            {
                Rect fileNameRect = new Rect(dropArea.x + 5, dropArea.y + 62, dropArea.width - 10, 15);
                var fileStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = new Color(0.4f, 0.8f, 0.4f) }
                };
                GUI.Label(fileNameRect, $"✓ {Path.GetFileName(_ifcFilePath)}", fileStyle);
            }

            HandleDragAndDrop(dropArea);

            EditorGUILayout.Space(5);

            // IFC File
            EditorGUILayout.BeginHorizontal();
            _ifcFilePath = EditorGUILayout.TextField("IFC File", _ifcFilePath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string path = EditorUtility.OpenFilePanel("Select IFC File", "", "ifc");
                if (!string.IsNullOrEmpty(path))
                {
                    _ifcFilePath = path;
                }
            }
            EditorGUILayout.EndHorizontal();

            // Python executable
            EditorGUILayout.BeginHorizontal();
            _pythonExecutable = EditorGUILayout.TextField("Python Executable", _pythonExecutable);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string path = EditorUtility.OpenFilePanel("Select Python Executable", "", "exe");
                if (!string.IsNullOrEmpty(path))
                {
                    _pythonExecutable = path;
                }
            }
            EditorGUILayout.EndHorizontal();

            // Validate Python
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Test Python", GUILayout.Width(100)))
            {
                TestPythonInstallation();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void HandleDragAndDrop(Rect dropArea)
        {
            Event evt = Event.current;

            switch (evt.type)
            {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    if (!dropArea.Contains(evt.mousePosition))
                    {
                        _isDraggingOver = false;
                        return;
                    }

                    // Check if any dragged file is an IFC file
                    bool hasIfcFile = false;
                    foreach (string path in DragAndDrop.paths)
                    {
                        if (path.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
                        {
                            hasIfcFile = true;
                            break;
                        }
                    }

                    if (hasIfcFile)
                    {
                        _isDraggingOver = true;
                        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                        if (evt.type == EventType.DragPerform)
                        {
                            DragAndDrop.AcceptDrag();
                            _isDraggingOver = false;

                            foreach (string path in DragAndDrop.paths)
                            {
                                if (path.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
                                {
                                    _ifcFilePath = path;
                                    Log($"IFC file selected: {Path.GetFileName(path)}");
                                    break;
                                }
                            }
                        }
                    }

                    evt.Use();
                    Repaint();
                    break;

                case EventType.DragExited:
                    _isDraggingOver = false;
                    Repaint();
                    break;
            }
        }

        private void DrawBorder(Rect rect, Color color, int thickness)
        {
            // Top
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            // Bottom
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            // Left
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            // Right
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private void DrawImportOptions()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Import Options", EditorStyles.boldLabel);

            _createHierarchy = EditorGUILayout.Toggle("Create IFC Hierarchy", _createHierarchy);
            _assignMetadata = EditorGUILayout.Toggle("Assign Metadata", _assignMetadata);
            _applyColors = EditorGUILayout.Toggle("Apply Colors", _applyColors);
            _createColliders = EditorGUILayout.Toggle("Create Mesh Colliders", _createColliders);

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Hierarchy Organization", EditorStyles.boldLabel);
            _groupByStorey = EditorGUILayout.Toggle("Group by Storey", _groupByStorey);
            _groupByClass = EditorGUILayout.Toggle("Group by IFC Class", _groupByClass);

            EditorGUILayout.EndVertical();
        }

        private void DrawAdvancedOptions()
        {
            _showAdvancedOptions = EditorGUILayout.Foldout(_showAdvancedOptions, "Advanced Options", true);

            if (_showAdvancedOptions)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                _scaleFactor = EditorGUILayout.FloatField("Scale Factor", _scaleFactor);
                _optimizeMeshes = EditorGUILayout.Toggle("Optimize Meshes", _optimizeMeshes);
                _generateUVs = EditorGUILayout.Toggle("Generate UVs", _generateUVs);
                _enableTransparency = EditorGUILayout.Toggle("Enable Transparency", _enableTransparency);
                _defaultShaderName = EditorGUILayout.TextField("Default Shader", _defaultShaderName);

                EditorGUILayout.EndVertical();
            }
        }

        private void DrawImportButton()
        {
            using (new EditorGUI.DisabledScope(_isProcessing || string.IsNullOrEmpty(_ifcFilePath)))
            {
                var buttonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 14,
                    fontStyle = FontStyle.Bold,
                    fixedHeight = 35
                };

                if (GUILayout.Button("Import IFC File", buttonStyle))
                {
                    StartImport();
                }
            }

            if (string.IsNullOrEmpty(_ifcFilePath))
            {
                EditorGUILayout.HelpBox("Please select an IFC file to import.", MessageType.Info);
            }
        }

        private void DrawProgressBar()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUI.ProgressBar(
                EditorGUILayout.GetControlRect(GUILayout.Height(20)),
                _progress,
                _progressMessage
            );

            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                _isProcessing = false;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawStatistics()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Header with copy button
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Last Import Statistics", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Copy", GUILayout.Width(50)))
            {
                GUIUtility.systemCopyBuffer = BuildStatisticsText(_lastImportStats);
                Log("Statistics copied to clipboard");
            }
            EditorGUILayout.EndHorizontal();

            var stats = _lastImportStats;

            // Scrollable statistics area
            _statsScrollPosition = EditorGUILayout.BeginScrollView(_statsScrollPosition, GUILayout.Height(180));

            // File info
            EditorGUILayout.LabelField("File Information", EditorStyles.miniBoldLabel);
            DrawStatLine("File", stats.IfcFileName);
            DrawStatLine("Import Date", stats.ImportStartTime.ToString("yyyy-MM-dd HH:mm:ss"));

            EditorGUILayout.Space(5);

            // Timing section
            EditorGUILayout.LabelField("Timing", EditorStyles.miniBoldLabel);
            DrawStatLine("Total Time", $"{stats.TotalTimeSeconds:F2}s");
            DrawStatLine("  Python Conversion", $"{stats.PythonConversionTimeSeconds:F2}s");
            DrawStatLine("  CSV Parse", $"{stats.CsvParseTimeSeconds:F2}s");
            DrawStatLine("  Mesh Load", $"{stats.MeshLoadTimeSeconds:F2}s");
            DrawStatLine("  Scene Build", $"{stats.SceneBuildTimeSeconds:F2}s");

            EditorGUILayout.Space(5);

            // Counts section
            EditorGUILayout.LabelField("Element Counts", EditorStyles.miniBoldLabel);
            DrawStatLine("Total Elements", $"{stats.CsvElementCount:N0}");
            DrawStatLine("Successfully Imported", $"{stats.SuccessfullyImported:N0}");
            DrawStatLine("Failed to Import", $"{stats.FailedToImport:N0}");
            DrawStatLine("Meshes Loaded", $"{stats.ObjMeshCount:N0}");
            DrawStatLine("Elements with Geometry", $"{stats.ElementsWithGeometry:N0}");
            DrawStatLine("Logical Nodes", $"{stats.LogicalNodes:N0}");
            DrawStatLine("Materials Created", $"{stats.MaterialsCreated:N0}");
            DrawStatLine("Elements with Color", $"{stats.ElementsWithColor:N0}");

            EditorGUILayout.Space(5);

            // Hierarchy section
            EditorGUILayout.LabelField("Hierarchy", EditorStyles.miniBoldLabel);
            DrawStatLine("Sites", $"{stats.SitesCreated:N0}");
            DrawStatLine("Buildings", $"{stats.BuildingsCreated:N0}");
            DrawStatLine("Storeys", $"{stats.StoreysCreated:N0}");
            DrawStatLine("Total Nodes", $"{stats.TotalHierarchyNodes:N0}");

            EditorGUILayout.Space(5);

            // Memory section
            EditorGUILayout.LabelField("Memory", EditorStyles.miniBoldLabel);
            DrawStatLine("Memory Used", FormatBytes(stats.NetMemoryChange));
            DrawStatLine("Peak Memory", FormatBytes(stats.PeakMemoryBytes));

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawStatLine(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(150));
            EditorGUILayout.SelectableLabel(value, EditorStyles.label, GUILayout.Height(16));
            EditorGUILayout.EndHorizontal();
        }

        private string BuildStatisticsText(IfcImportStatistics stats)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== IFC Import Statistics ===");
            sb.AppendLine($"File: {stats.IfcFileName}");
            sb.AppendLine($"Import Date: {stats.ImportStartTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("--- Timing ---");
            sb.AppendLine($"Total Time: {stats.TotalTimeSeconds:F2}s");
            sb.AppendLine($"Python Conversion: {stats.PythonConversionTimeSeconds:F2}s");
            sb.AppendLine($"CSV Parse: {stats.CsvParseTimeSeconds:F2}s");
            sb.AppendLine($"Mesh Load: {stats.MeshLoadTimeSeconds:F2}s");
            sb.AppendLine($"Scene Build: {stats.SceneBuildTimeSeconds:F2}s");
            sb.AppendLine();
            sb.AppendLine("--- Elements ---");
            sb.AppendLine($"Total Elements: {stats.CsvElementCount:N0}");
            sb.AppendLine($"Successfully Imported: {stats.SuccessfullyImported:N0}");
            sb.AppendLine($"Failed to Import: {stats.FailedToImport:N0}");
            sb.AppendLine($"Meshes Loaded: {stats.ObjMeshCount:N0}");
            sb.AppendLine($"Elements with Geometry: {stats.ElementsWithGeometry:N0}");
            sb.AppendLine($"Materials Created: {stats.MaterialsCreated:N0}");
            sb.AppendLine();
            sb.AppendLine("--- Hierarchy ---");
            sb.AppendLine($"Sites: {stats.SitesCreated:N0}");
            sb.AppendLine($"Buildings: {stats.BuildingsCreated:N0}");
            sb.AppendLine($"Storeys: {stats.StoreysCreated:N0}");
            sb.AppendLine($"Total Nodes: {stats.TotalHierarchyNodes:N0}");
            sb.AppendLine();
            sb.AppendLine("--- Memory ---");
            sb.AppendLine($"Memory Used: {FormatBytes(stats.NetMemoryChange)}");
            sb.AppendLine($"Peak Memory: {FormatBytes(stats.PeakMemoryBytes)}");
            return sb.ToString();
        }

        private void DrawLogSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            // Auto-scroll toggle
            _autoScrollLog = GUILayout.Toggle(_autoScrollLog, "Auto-scroll", GUILayout.Width(80));

            if (_logMessages.Count > 0 && GUILayout.Button("Copy", GUILayout.Width(50)))
            {
                GUIUtility.systemCopyBuffer = string.Join("\n", _logMessages);
                Log("Log copied to clipboard");
            }
            if (_logMessages.Count > 0 && GUILayout.Button("Clear", GUILayout.Width(50)))
            {
                _logMessages.Clear();
            }
            EditorGUILayout.EndHorizontal();

            // Calculate scroll height - auto scroll to bottom if enabled
            float scrollHeight = 150;

            _logScrollPosition = EditorGUILayout.BeginScrollView(_logScrollPosition, GUILayout.Height(scrollHeight));

            var logStyle = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                richText = true,
                fontSize = 11
            };

            foreach (var message in _logMessages)
            {
                // Color code different message types
                string coloredMessage = message;
                if (message.Contains("ERROR"))
                {
                    coloredMessage = $"<color=#ff6666>{message}</color>";
                }
                else if (message.Contains("COMPLETE") || message.Contains("✓"))
                {
                    coloredMessage = $"<color=#66ff66>{message}</color>";
                }
                else if (message.Contains("═"))
                {
                    coloredMessage = $"<color=#66ccff>{message}</color>";
                }

                EditorGUILayout.LabelField(coloredMessage, logStyle);
            }

            EditorGUILayout.EndScrollView();

            // Auto-scroll to bottom when new messages added
            if (_autoScrollLog && _logMessages.Count > 0)
            {
                _logScrollPosition.y = float.MaxValue;
            }

            EditorGUILayout.EndVertical();
        }

        private void StartImport()
        {
            if (!File.Exists(_ifcFilePath))
            {
                EditorUtility.DisplayDialog("Error", "IFC file not found.", "OK");
                return;
            }

            _logMessages.Clear();
            _isProcessing = true;
            _progress = 0f;
            _progressMessage = "Starting import...";

            Log($"Starting IFC import: {_ifcFilePath}");
            Log($"Python executable: {_pythonExecutable}");

            // Create import options
            var options = new IfcImportOptions
            {
                PythonExecutable = _pythonExecutable,
                CreateHierarchy = _createHierarchy,
                AssignMetadata = _assignMetadata,
                ApplyColors = _applyColors,
                CreateColliders = _createColliders,
                GroupByStorey = _groupByStorey,
                GroupByClass = _groupByClass,
                ScaleFactor = _scaleFactor,
                OptimizeMeshes = _optimizeMeshes,
                GenerateUVs = _generateUVs,
                EnableTransparency = _enableTransparency,
                DefaultShaderName = _defaultShaderName
            };

            // Run import
            var processor = new IfcImportProcessor(options);

            try
            {
                // Step 1: Python conversion
                Log("Running Python IFC conversion...");
                bool pythonSuccess = processor.RunPythonConversion(_ifcFilePath, UpdateProgress);

                if (!pythonSuccess)
                {
                    Log("ERROR: Python conversion failed!");
                    _isProcessing = false;
                    return;
                }

                Log($"Python conversion completed in {processor.Statistics.PythonConversionTimeSeconds:F2}s");

                // Step 2: Load exported files
                Log("Loading exported files...");
                bool loadSuccess = processor.LoadExportedFiles(_ifcFilePath, UpdateProgress);

                if (!loadSuccess)
                {
                    Log("ERROR: Failed to load exported files!");
                    _isProcessing = false;
                    return;
                }

                Log($"Loaded {processor.Statistics.CsvElementCount} elements, {processor.Statistics.ObjMeshCount} meshes");

                // Step 3: Build scene
                Log("Building Unity scene...");
                string rootName = Path.GetFileNameWithoutExtension(_ifcFilePath);
                var rootObject = processor.BuildSceneHierarchy(rootName, UpdateProgress);

                if (rootObject != null)
                {
                    // Select the created object
                    Selection.activeGameObject = rootObject;
                    EditorGUIUtility.PingObject(rootObject);

                    // Mark scene dirty
                    EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                }

                // Store statistics
                _lastImportStats = processor.Statistics;

                // Save log file
                SaveImportLog(processor.Statistics);

                Log("═══════════════════════════════════════");
                Log("IMPORT COMPLETE!");
                Log($"Total Time: {processor.Statistics.TotalTimeSeconds:F2}s");
                Log($"Elements: {processor.Statistics.SuccessfullyImported:N0}");
                Log($"Meshes: {processor.Statistics.ElementsWithGeometry:N0}");
                Log("═══════════════════════════════════════");
            }
            catch (Exception e)
            {
                Log($"ERROR: {e.Message}");
                Debug.LogException(e);
            }
            finally
            {
                _isProcessing = false;
                _progress = 1f;
                _progressMessage = "Complete";
                Repaint();
            }
        }

        private void UpdateProgress(float progress, string message)
        {
            _progress = progress;
            _progressMessage = message;
            Repaint();
        }

        private void Log(string message)
        {
            string timestamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logMessages.Add(timestamped);
            Debug.Log($"[IFCImporter] {message}");
            Repaint();
        }

        private void TestPythonInstallation()
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = _pythonExecutable,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    string version = !string.IsNullOrEmpty(output) ? output.Trim() : error.Trim();

                    if (process.ExitCode == 0)
                    {
                        // Test ifcopenshell
                        processInfo.Arguments = "-c \"import ifcopenshell; print(f'ifcopenshell {ifcopenshell.version}')\"";
                        using (var ifcProcess = Process.Start(processInfo))
                        {
                            string ifcOutput = ifcProcess.StandardOutput.ReadToEnd();
                            string ifcError = ifcProcess.StandardError.ReadToEnd();
                            ifcProcess.WaitForExit();

                            if (ifcProcess.ExitCode == 0)
                            {
                                EditorUtility.DisplayDialog("Python Test",
                                    $"✓ Python: {version}\n✓ {ifcOutput.Trim()}", "OK");
                                Log($"Python test passed: {version}, {ifcOutput.Trim()}");
                            }
                            else
                            {
                                EditorUtility.DisplayDialog("Python Test",
                                    $"✓ Python: {version}\n✗ IfcOpenShell not installed\n\nInstall with: pip install ifcopenshell",
                                    "OK");
                                Log($"Python found but IfcOpenShell not installed");
                            }
                        }
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Python Test",
                            $"✗ Python not found at: {_pythonExecutable}", "OK");
                        Log($"Python not found at: {_pythonExecutable}");
                    }
                }
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Python Test",
                    $"✗ Error: {e.Message}", "OK");
                Log($"Python test error: {e.Message}");
            }
        }

        private void SaveImportLog(IfcImportStatistics stats)
        {
            try
            {
                // Get logs directory
                string logsDir = Path.Combine(Application.dataPath, "BIMUniXchange", "Logs");
                if (!Directory.Exists(logsDir))
                {
                    Directory.CreateDirectory(logsDir);
                }

                // Generate filename
                string fileName = $"IFC_Import_{Path.GetFileNameWithoutExtension(stats.IfcFileName)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                string logPath = Path.Combine(logsDir, fileName);

                // Build log content
                var sb = new StringBuilder();

                sb.AppendLine($"Process: IFC_Import");
                sb.AppendLine($"Date: {stats.ImportStartTime:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"IFC File: {stats.IfcFilePath}");
                sb.AppendLine($"Python Executable: {_pythonExecutable}");
                sb.AppendLine($"Python Script: {GetPythonScriptPath()}");
                sb.AppendLine();

                sb.AppendLine("=== TIMING SUMMARY ===");
                sb.AppendLine($"Python Conversion Time: {stats.PythonConversionTimeSeconds:F2}s");
                sb.AppendLine($"Unity Scene Build Time: {stats.SceneBuildTimeSeconds:F2}s");
                sb.AppendLine($"Total Time: {stats.TotalTimeSeconds:F2}s");
                sb.AppendLine();

                sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
                sb.AppendLine("║              IFC IMPORT SUMMARY                              ║");
                sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
                sb.AppendLine();

                sb.AppendLine("📊 PROCESSING STATISTICS:");
                sb.AppendLine($"  ├─ CSV Elements: {stats.CsvElementCount:N0}");
                sb.AppendLine($"  ├─ OBJ Meshes: {stats.ObjMeshCount:N0}");
                sb.AppendLine($"  ├─ Successfully Imported: {stats.SuccessfullyImported:N0} ({(stats.CsvElementCount > 0 ? (double)stats.SuccessfullyImported / stats.CsvElementCount * 100 : 0):F2}%)");
                sb.AppendLine($"  ├─ Elements with Geometry: {stats.ElementsWithGeometry:N0}");
                sb.AppendLine($"  ├─ Logical Nodes (no geometry): {stats.LogicalNodes:N0}");
                sb.AppendLine($"  ├─ Meshes Not Found: {stats.MeshesNotFound:N0}");
                sb.AppendLine($"  ├─ Elements with Color Data: {stats.ElementsWithColor:N0}");
                sb.AppendLine($"  ├─ Failed to Import: {stats.FailedToImport:N0}");
                sb.AppendLine($"  └─ Mesh Match Rate: {stats.MeshMatchRate:F2}%");
                sb.AppendLine();

                sb.AppendLine("⚠️ DATA LOSS ANALYSIS:");
                bool hasDataLoss = stats.DataLossPercentage > 0;
                sb.AppendLine($"  ├─ Status: {(hasDataLoss ? "⚠ DATA LOSS DETECTED" : "✓ NO DATA LOSS")}");
                sb.AppendLine($"  ├─ Elements Lost: {stats.CsvElementCount - stats.SuccessfullyImported:N0}");
                sb.AppendLine($"  ├─ Data Loss Percentage: {stats.DataLossPercentage:F2}%");
                sb.AppendLine($"  └─ Successfully Processed: {100 - stats.DataLossPercentage:F2}%");
                sb.AppendLine();

                sb.AppendLine("🏗️ HIERARCHY STRUCTURE:");
                sb.AppendLine($"  ├─ Sites Created: {stats.SitesCreated:N0}");
                sb.AppendLine($"  ├─ Buildings Created: {stats.BuildingsCreated:N0}");
                sb.AppendLine($"  ├─ Storeys Created: {stats.StoreysCreated:N0}");
                sb.AppendLine($"  ├─ Total Hierarchy Nodes: {stats.TotalHierarchyNodes:N0}");
                sb.AppendLine($"  └─ Materials Created: {stats.MaterialsCreated:N0}");
                sb.AppendLine();

                sb.AppendLine("⏱️ PERFORMANCE METRICS:");
                sb.AppendLine($"  ├─ Total Time: {stats.TotalTimeSeconds:F2} seconds ({stats.TotalTimeSeconds * 1000:F0} ms)");
                sb.AppendLine($"  ├─ CSV Parse Time: {stats.CsvParseTimeSeconds:F2}s ({(stats.TotalTimeSeconds > 0 ? stats.CsvParseTimeSeconds / stats.TotalTimeSeconds * 100 : 0):F1}%)");
                sb.AppendLine($"  ├─ Mesh Load Time: {stats.MeshLoadTimeSeconds:F2}s ({(stats.TotalTimeSeconds > 0 ? stats.MeshLoadTimeSeconds / stats.TotalTimeSeconds * 100 : 0):F1}%)");
                sb.AppendLine($"  ├─ Scene Build Time: {stats.SceneBuildTimeSeconds:F2}s ({(stats.TotalTimeSeconds > 0 ? stats.SceneBuildTimeSeconds / stats.TotalTimeSeconds * 100 : 0):F1}%)");
                sb.AppendLine($"  └─ Processing Speed: {stats.ProcessingSpeed:F1} elements/second");
                sb.AppendLine();

                sb.AppendLine("💾 MEMORY USAGE:");
                sb.AppendLine($"  ├─ Memory at Start: {FormatBytes(stats.MemoryAtStartBytes)}");
                sb.AppendLine($"  ├─ Memory at End: {FormatBytes(stats.MemoryAtEndBytes)}");
                sb.AppendLine($"  ├─ Peak Memory Used: {FormatBytes(stats.PeakMemoryBytes)}");
                sb.AppendLine($"  ├─ Net Memory Change: {FormatBytes(stats.NetMemoryChange)}");
                sb.AppendLine($"  └─ Memory per Element: {stats.MemoryPerElementKB:F2} KB");
                sb.AppendLine();

                // IFC class breakdown
                if (stats.ElementsByClass.Count > 0)
                {
                    sb.AppendLine("📁 TOP IFC CLASSES:");
                    int rank = 1;
                    foreach (var kvp in stats.ElementsByClass.OrderByDescending(x => x.Value).Take(10))
                    {
                        double percentage = (double)kvp.Value / stats.SuccessfullyImported * 100;
                        sb.AppendLine($"   {rank}. {kvp.Key}: {kvp.Value:N0} elements ({percentage:F1}%)");
                        rank++;
                    }
                    if (stats.ElementsByClass.Count > 10)
                    {
                        sb.AppendLine($"  ... and {stats.ElementsByClass.Count - 10} more classes");
                    }
                    sb.AppendLine();
                }

                // Elements by storey
                if (stats.ElementsByStorey.Count > 0)
                {
                    sb.AppendLine("🏢 ELEMENTS BY STOREY:");
                    foreach (var kvp in stats.ElementsByStorey.OrderByDescending(x => x.Value))
                    {
                        double percentage = (double)kvp.Value / stats.SuccessfullyImported * 100;
                        sb.AppendLine($"  ├─ {kvp.Key}: {kvp.Value:N0} elements ({percentage:F1}%)");
                    }
                    sb.AppendLine();
                }

                if (stats.FailedToImport == 0)
                {
                    sb.AppendLine("✓ ALL ELEMENTS SUCCESSFULLY IMPORTED!");
                }
                else
                {
                    sb.AppendLine($"⚠ {stats.FailedToImport:N0} ELEMENTS FAILED TO IMPORT");
                }

                sb.AppendLine();
                sb.AppendLine("════════════════════════════════════════════════════════════════");

                // Write file
                File.WriteAllText(logPath, sb.ToString());
                Log($"Log saved: {logPath}");

                // Refresh asset database
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save import log: {e.Message}");
            }
        }

        private string GetPythonScriptPath()
        {
            return Path.Combine(Application.dataPath, "BIMUniXchange", "IFCImporter", "Python", "ifc_to_unity_export.py");
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:F2} {sizes[order]}";
        }
    }
}
#endif
