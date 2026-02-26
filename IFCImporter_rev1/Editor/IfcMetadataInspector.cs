using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;

namespace IFCImporter.Editor
{
    /// <summary>
    /// Custom editor for IfcMetadata component - matches the Metadata inspector style
    /// with statistics, search/filter, editable properties, and progress bar.
    /// </summary>
    [CustomEditor(typeof(IfcMetadata))]
    [CanEditMultipleObjects]
    public class IfcMetadataInspector : UnityEditor.Editor
    {
        private Vector2 scrollPosition;
        private bool showAllProperties = false;
        private string searchFilter = "";
        private bool showEmptyProperties = true;
        private bool showIdentification = true;
        private bool showHierarchy = false;
        private bool showClassification = false;
        private bool showMaterial = false;
        private bool showDoorDimensions = false;

        private string newPropertyKey = "";
        private string newPropertyValue = "";

        public override void OnInspectorGUI()
        {
            var metadata = (IfcMetadata)target;

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("IFC Element Metadata", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // Quick info header
            DrawQuickInfo(metadata);
            EditorGUILayout.Space();

            // Statistics section
            DrawStatisticsSection(metadata);
            EditorGUILayout.Space();

            // IFC fields (foldouts)
            DrawIfcFieldSections(metadata);
            EditorGUILayout.Space();

            // Filter and options
            DrawFilterSection();
            EditorGUILayout.Space();

            // Metadata Properties section (key-value list)
            DrawMetadataProperties(metadata);
            EditorGUILayout.Space();

            // Action buttons
            DrawActionButtons(metadata);

            // Apply changes if any
            if (EditorGUI.EndChangeCheck())
            {
                metadata.UpdateStats();
                EditorUtility.SetDirty(metadata);
            }
        }

        private void DrawQuickInfo(IfcMetadata metadata)
        {
            if (!metadata.HasIfcData) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("GlobalId: " + metadata.GlobalId);
            EditorGUILayout.LabelField("Name: " + metadata.ElementName);
            EditorGUILayout.LabelField("IFC Class: " + metadata.IfcClass);
            if (!string.IsNullOrEmpty(metadata.Storey))
                EditorGUILayout.LabelField("Storey: " + metadata.Storey);
            EditorGUILayout.EndVertical();
        }

        private void DrawStatisticsSection(IfcMetadata metadata)
        {
            EditorGUILayout.LabelField("Statistics", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField($"Has Metadata: {metadata.HasMetadata}");
                EditorGUILayout.LabelField($"Total Parameters: {metadata.Stats.TotalParameters}");
                EditorGUILayout.LabelField($"Assigned Parameters: {metadata.Stats.AssignedParameters}");

                // Show non-empty in green
                var oldColor = GUI.contentColor;
                GUI.contentColor = new Color(0.2f, 0.7f, 0.2f);
                EditorGUILayout.LabelField($"Non-empty Parameters: {metadata.Stats.NonEmptyParameters}");
                GUI.contentColor = oldColor;

                // Show empty in orange/red
                GUI.contentColor = new Color(0.9f, 0.4f, 0.1f);
                EditorGUILayout.LabelField($"Empty/Undefined Parameters: {metadata.Stats.EmptyParameters}");
                GUI.contentColor = oldColor;

                if (metadata.Stats.TotalParameters > 0)
                {
                    float completionPercentage = (float)metadata.Stats.NonEmptyParameters / metadata.Stats.TotalParameters * 100f;
                    EditorGUILayout.LabelField($"Completion: {completionPercentage:F1}%");

                    // Progress bar
                    Rect progressRect = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
                    EditorGUI.ProgressBar(progressRect, completionPercentage / 100f, $"{completionPercentage:F1}%");
                }
            }
        }

        private void DrawIfcFieldSections(IfcMetadata metadata)
        {
            // IFC Identification foldout
            showIdentification = EditorGUILayout.Foldout(showIdentification, "IFC Identification", true);
            if (showIdentification)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                metadata.ElementId = EditorGUILayout.TextField("Element ID", metadata.ElementId);
                metadata.GlobalId = EditorGUILayout.TextField("Global ID", metadata.GlobalId);
                metadata.IfcClass = EditorGUILayout.TextField("IFC Class", metadata.IfcClass);
                metadata.ElementName = EditorGUILayout.TextField("Element Name", metadata.ElementName);
                metadata.Description = EditorGUILayout.TextField("Description", metadata.Description);
                metadata.ObjectType = EditorGUILayout.TextField("Object Type", metadata.ObjectType);
                metadata.PredefinedType = EditorGUILayout.TextField("Predefined Type", metadata.PredefinedType);
                metadata.SourceFile = EditorGUILayout.TextField("Source File", metadata.SourceFile);

                EditorGUILayout.EndVertical();
                EditorGUI.indentLevel--;
            }

            // IFC Hierarchy foldout
            showHierarchy = EditorGUILayout.Foldout(showHierarchy, "IFC Hierarchy", true);
            if (showHierarchy)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                metadata.Project = EditorGUILayout.TextField("Project", metadata.Project);
                metadata.Site = EditorGUILayout.TextField("Site", metadata.Site);
                metadata.Building = EditorGUILayout.TextField("Building", metadata.Building);
                metadata.Storey = EditorGUILayout.TextField("Storey", metadata.Storey);
                metadata.Elevation = EditorGUILayout.FloatField("Elevation", metadata.Elevation);
                metadata.LongName = EditorGUILayout.TextField("Long Name", metadata.LongName);
                metadata.ParentGlobalId = EditorGUILayout.TextField("Parent GlobalId", metadata.ParentGlobalId);
                metadata.SpatialPath = EditorGUILayout.TextField("Spatial Path", metadata.SpatialPath);

                EditorGUILayout.EndVertical();
                EditorGUI.indentLevel--;
            }

            // Classification foldout
            showClassification = EditorGUILayout.Foldout(showClassification, "Classification", true);
            if (showClassification)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                metadata.Discipline = EditorGUILayout.TextField("Discipline", metadata.Discipline);
                metadata.Category = EditorGUILayout.TextField("Category", metadata.Category);

                EditorGUILayout.EndVertical();
                EditorGUI.indentLevel--;
            }

            // Material foldout
            showMaterial = EditorGUILayout.Foldout(showMaterial, "Material Information", true);
            if (showMaterial)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                metadata.MaterialName = EditorGUILayout.TextField("Material Name", metadata.MaterialName);
                metadata.ElementColor = EditorGUILayout.ColorField("Element Color", metadata.ElementColor);

                EditorGUILayout.EndVertical();
                EditorGUI.indentLevel--;
            }

            // Door / Opening foldout (only show if relevant)
            bool hasDoorData = metadata.OverallWidth > 0 || metadata.OverallHeight > 0 ||
                               !string.IsNullOrEmpty(metadata.OperationType);
            if (hasDoorData || showDoorDimensions)
            {
                showDoorDimensions = EditorGUILayout.Foldout(showDoorDimensions || hasDoorData, "Door / Opening (Robotics)", true);
                if (showDoorDimensions)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                    metadata.OverallWidth = EditorGUILayout.FloatField("Overall Width (m)", metadata.OverallWidth);
                    metadata.OverallHeight = EditorGUILayout.FloatField("Overall Height (m)", metadata.OverallHeight);
                    metadata.OperationType = EditorGUILayout.TextField("Operation Type", metadata.OperationType);

                    EditorGUILayout.EndVertical();
                    EditorGUI.indentLevel--;
                }
            }
        }

        private void DrawFilterSection()
        {
            EditorGUILayout.LabelField("Display Options", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
                searchFilter = EditorGUILayout.TextField(searchFilter);
                if (GUILayout.Button("Clear", GUILayout.Width(50)))
                {
                    searchFilter = "";
                    GUI.FocusControl(null);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                bool newShowEmpty = EditorGUILayout.Toggle("Show Empty/Undefined", showEmptyProperties);
                if (newShowEmpty != showEmptyProperties)
                {
                    showEmptyProperties = newShowEmpty;
                    Repaint();
                }
                bool newShowAll = EditorGUILayout.Toggle("Show All Properties", showAllProperties);
                if (newShowAll != showAllProperties)
                {
                    showAllProperties = newShowAll;
                    Repaint();
                }
                EditorGUILayout.EndHorizontal();

                if (!showEmptyProperties)
                {
                    EditorGUILayout.HelpBox("Empty properties (including '<undefined>') are hidden. Enable 'Show Empty/Undefined' to see them.", MessageType.Info);
                }
            }
        }

        private void DrawMetadataProperties(IfcMetadata metadata)
        {
            EditorGUILayout.LabelField("Metadata Properties", EditorStyles.boldLabel);

            if (metadata.Properties.Count == 0)
            {
                EditorGUILayout.HelpBox("No metadata properties assigned to this element.", MessageType.Info);
                DrawAddPropertySection(metadata);
                return;
            }

            // Filter properties
            var filteredProperties = FilterProperties(metadata.Properties);

            if (filteredProperties.Count == 0)
            {
                EditorGUILayout.HelpBox("No properties match the current filter.", MessageType.Info);
                return;
            }

            // Scrollable area for properties
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.MaxHeight(400));

            DrawEditableProperties(filteredProperties, metadata);

            EditorGUILayout.EndScrollView();

            // Add new property section
            EditorGUILayout.Space();
            DrawAddPropertySection(metadata);
        }

        private List<IfcProperty> FilterProperties(List<IfcProperty> properties)
        {
            var filtered = properties.AsEnumerable();

            // Apply search filter
            if (!string.IsNullOrEmpty(searchFilter))
            {
                filtered = filtered.Where(p =>
                    p.Key.ToLower().Contains(searchFilter.ToLower()) ||
                    (p.Value != null && p.Value.ToLower().Contains(searchFilter.ToLower())));
            }

            // Apply empty property filter
            if (!showEmptyProperties)
            {
                filtered = filtered.Where(p => !p.IsEmpty);
            }

            var result = filtered.ToList();

            // Limit display if not showing all
            if (!showAllProperties && result.Count > 20)
            {
                result = result.Take(20).ToList();
            }

            return result;
        }

        private void DrawEditableProperties(List<IfcProperty> properties, IfcMetadata metadata)
        {
            for (int i = 0; i < properties.Count; i++)
            {
                var property = properties[i];

                // Highlight empty properties with a different background color
                if (property.IsEmpty)
                {
                    GUI.backgroundColor = new Color(1f, 0.9f, 0.9f); // Light red tint for empty
                }

                EditorGUILayout.BeginHorizontal();

                // Key field
                EditorGUILayout.LabelField("Key:", GUILayout.Width(30));
                string newKey = EditorGUILayout.TextField(property.Key, GUILayout.MinWidth(120));

                // Value field with visual indicator for empty values
                EditorGUILayout.LabelField("Value:", GUILayout.Width(40));

                string displayValue = property.Value ?? "";
                string newValue = EditorGUILayout.TextField(displayValue, GUILayout.MinWidth(120));

                // Show empty indicator
                if (property.IsEmpty)
                {
                    EditorGUILayout.LabelField("(empty)", EditorStyles.miniLabel, GUILayout.Width(50));
                }

                // Delete button
                if (GUILayout.Button("\u00d7", GUILayout.Width(25)))
                {
                    metadata.RemoveProperty(property.Key);
                    GUI.backgroundColor = Color.white;
                    break;
                }

                EditorGUILayout.EndHorizontal();

                // Reset background color
                GUI.backgroundColor = Color.white;

                // Update property if changed
                if (newKey != property.Key || newValue != property.Value)
                {
                    // If key changed, remove old and add new
                    if (newKey != property.Key)
                    {
                        metadata.RemoveProperty(property.Key);
                        metadata.AddProperty(newKey, newValue);
                    }
                    else
                    {
                        metadata.AddProperty(newKey, newValue);
                    }
                    EditorUtility.SetDirty(metadata);
                }

                // Visual separator
                if (i < properties.Count - 1)
                {
                    EditorGUILayout.Space(2);
                }
            }

            // Show truncation notice
            if (!showAllProperties && metadata.Properties.Count > 20)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox($"Showing 20 of {metadata.Properties.Count} properties. Enable 'Show All Properties' to see more.", MessageType.Info);
            }
        }

        private void DrawAddPropertySection(IfcMetadata metadata)
        {
            EditorGUILayout.LabelField("Add New Property", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Key:", GUILayout.Width(30));
                newPropertyKey = EditorGUILayout.TextField(newPropertyKey, GUILayout.MinWidth(120));
                EditorGUILayout.LabelField("Value:", GUILayout.Width(40));
                newPropertyValue = EditorGUILayout.TextField(newPropertyValue, GUILayout.MinWidth(120));

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(newPropertyKey)))
                {
                    if (GUILayout.Button("Add", GUILayout.Width(50)))
                    {
                        metadata.AddProperty(newPropertyKey, newPropertyValue);
                        newPropertyKey = "";
                        newPropertyValue = "";
                        EditorUtility.SetDirty(metadata);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawActionButtons(IfcMetadata metadata)
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Clear All Properties"))
            {
                if (EditorUtility.DisplayDialog("Clear Metadata",
                    "Are you sure you want to clear all metadata properties for this element?",
                    "Yes", "Cancel"))
                {
                    metadata.ClearProperties();
                    EditorUtility.SetDirty(metadata);
                }
            }

            if (metadata.Properties.Count > 0 && GUILayout.Button("Refresh Stats"))
            {
                metadata.UpdateStats();
                EditorUtility.SetDirty(metadata);
            }

            if (GUILayout.Button("Export to CSV"))
            {
                ExportMetadataToCSV(metadata);
            }

            EditorGUILayout.EndHorizontal();

            // Prefab warning
            if (PrefabUtility.IsPartOfPrefabAsset(metadata.gameObject))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("This is part of a prefab. Changes may affect all instances.", MessageType.Info);
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Debug Info"))
            {
                Debug.Log($"IfcMetadata Debug Info for {metadata.gameObject.name}:");
                Debug.Log($"- HasMetadata: {metadata.HasMetadata}");
                Debug.Log($"- HasIfcData: {metadata.HasIfcData}");
                Debug.Log($"- GlobalId: {metadata.GlobalId}");
                Debug.Log($"- IfcClass: {metadata.IfcClass}");
                Debug.Log($"- Properties Count: {metadata.Properties.Count}");
                Debug.Log($"- Stats: Total={metadata.Stats.TotalParameters}, NonEmpty={metadata.Stats.NonEmptyParameters}, Empty={metadata.Stats.EmptyParameters}");
                foreach (var prop in metadata.Properties)
                {
                    Debug.Log($"  {prop.Key}: '{prop.Value}' (IsEmpty: {prop.IsEmpty})");
                }
            }
        }

        private void ExportMetadataToCSV(IfcMetadata metadata)
        {
            string path = EditorUtility.SaveFilePanel("Export Metadata to CSV", "",
                $"{metadata.gameObject.name}_ifc_metadata.csv", "csv");

            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    var csv = new System.Text.StringBuilder();
                    csv.AppendLine("Key,Value");

                    // Export IFC identification fields
                    AppendCsvLine(csv, "GlobalId", metadata.GlobalId);
                    AppendCsvLine(csv, "IfcClass", metadata.IfcClass);
                    AppendCsvLine(csv, "ElementName", metadata.ElementName);
                    AppendCsvLine(csv, "ElementId", metadata.ElementId);
                    AppendCsvLine(csv, "Description", metadata.Description);
                    AppendCsvLine(csv, "ObjectType", metadata.ObjectType);
                    AppendCsvLine(csv, "PredefinedType", metadata.PredefinedType);
                    AppendCsvLine(csv, "SourceFile", metadata.SourceFile);
                    AppendCsvLine(csv, "Project", metadata.Project);
                    AppendCsvLine(csv, "Site", metadata.Site);
                    AppendCsvLine(csv, "Building", metadata.Building);
                    AppendCsvLine(csv, "Storey", metadata.Storey);
                    AppendCsvLine(csv, "Elevation", metadata.Elevation.ToString("F3"));
                    AppendCsvLine(csv, "SpatialPath", metadata.SpatialPath);
                    AppendCsvLine(csv, "Discipline", metadata.Discipline);
                    AppendCsvLine(csv, "Category", metadata.Category);
                    AppendCsvLine(csv, "MaterialName", metadata.MaterialName);

                    // Export all properties
                    foreach (var prop in metadata.Properties)
                    {
                        AppendCsvLine(csv, prop.Key, prop.Value);
                    }

                    System.IO.File.WriteAllText(path, csv.ToString());
                    EditorUtility.DisplayDialog("Export Complete",
                        $"Metadata exported successfully to:\n{path}", "OK");
                }
                catch (System.Exception e)
                {
                    EditorUtility.DisplayDialog("Export Failed",
                        $"Failed to export metadata:\n{e.Message}", "OK");
                }
            }
        }

        private void AppendCsvLine(System.Text.StringBuilder csv, string key, string value)
        {
            key = key?.Replace("\"", "\"\"") ?? "";
            value = value?.Replace("\"", "\"\"") ?? "";

            if (key.Contains(",") || key.Contains("\"") || key.Contains("\n"))
                key = $"\"{key}\"";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                value = $"\"{value}\"";

            csv.AppendLine($"{key},{value}");
        }
    }
}
