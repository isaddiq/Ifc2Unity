using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace IFCImporter
{
    /// <summary>
    /// Serializable property structure for storing IFC properties.
    /// </summary>
    [Serializable]
    public struct IfcProperty
    {
        public string Key;
        public string Value;

        /// <summary>
        /// Source-confidence annotation indicating where this value came from.
        /// </summary>
        public IfcSourceAnnotation SourceAnnotation;

        /// <summary>
        /// Whether this property has no meaningful value.
        /// </summary>
        public bool IsEmpty => string.IsNullOrEmpty(Value) ||
                              Value.Equals("<undefined>", StringComparison.OrdinalIgnoreCase);

        public IfcProperty(string key, string value)
        {
            Key = key ?? "";
            Value = value ?? "";
            SourceAnnotation = IfcSourceAnnotation.Fallback();
        }

        public IfcProperty(string key, string value, IfcSourceAnnotation annotation)
        {
            Key = key ?? "";
            Value = value ?? "";
            SourceAnnotation = annotation;
        }
    }

    /// <summary>
    /// Statistics for IFC metadata properties (mirrors MetadataStats).
    /// </summary>
    [Serializable]
    public class IfcMetadataStats
    {
        [SerializeField] private int totalParameters;
        [SerializeField] private int emptyParameters;
        [SerializeField] private int nonEmptyParameters;
        [SerializeField] private int assignedParameters;

        public int TotalParameters { get => totalParameters; set => totalParameters = value; }
        public int EmptyParameters { get => emptyParameters; set => emptyParameters = value; }
        public int NonEmptyParameters { get => nonEmptyParameters; set => nonEmptyParameters = value; }
        public int AssignedParameters { get => assignedParameters; set => assignedParameters = value; }

        public void Reset()
        {
            totalParameters = 0;
            emptyParameters = 0;
            nonEmptyParameters = 0;
            assignedParameters = 0;
        }

        public void Calculate(List<IfcProperty> properties)
        {
            totalParameters = properties.Count;
            emptyParameters = properties.Count(p => p.IsEmpty);
            nonEmptyParameters = totalParameters - emptyParameters;
            assignedParameters = totalParameters;
        }
    }

    /// <summary>
    /// MonoBehaviour component for storing IFC metadata on GameObjects.
    /// This is a standalone component for the IFCImporter module.
    /// </summary>
    [AddComponentMenu("IFC Importer/IFC Metadata")]
    public class IfcMetadata : MonoBehaviour
    {
        #region Category Constants

        /// <summary>
        /// Standard property key prefixes for organizing IFC properties.
        /// </summary>
        public static class Categories
        {
            public const string Identity = "Identity";
            public const string Location = "Location";
            public const string Type = "Type";
            public const string Geometry = "Geometry";
            public const string PropertySets = "Pset";
            public const string QuantitySets = "Qto";
            public const string Materials = "Material";
            public const string Classifications = "Classification";
            public const string Custom = "Custom";
        }

        #endregion

        #region IFC Identification

        [Header("IFC Identification")]
        [Tooltip("The Element ID (e.g., Revit Element ID).")]
        public string ElementId;

        [Tooltip("The unique GlobalId (GUID) of the IFC element.")]
        public string GlobalId;

        [Tooltip("The IFC class type (e.g., IfcWallStandardCase, IfcBeam).")]
        public string IfcClass;

        [Tooltip("The name of the element.")]
        public string ElementName;

        [Tooltip("The description of the element.")]
        public string Description;

        [Tooltip("The object type or predefined type.")]
        public string ObjectType;

        [Tooltip("The IFC PredefinedType (e.g. FLOOR, SINGLE_SWING_LEFT).")]
        public string PredefinedType;

        [Tooltip("Source IFC file reference.")]
        public string SourceFile;

        #endregion

        #region IFC Hierarchy

        [Header("IFC Hierarchy")]
        [Tooltip("The IFC project.")]
        public string Project;

        [Tooltip("The IFC site.")]
        public string Site;

        [Tooltip("The IFC building.")]
        public string Building;

        [Tooltip("The building storey.")]
        public string Storey;

        [Tooltip("Storey elevation in metres.")]
        public float Elevation;

        [Tooltip("Long name (IfcSpace / IfcBuildingStorey).")]
        public string LongName;

        [Tooltip("GlobalId of the spatial parent.")]
        public string ParentGlobalId;

        [Tooltip("The full spatial path.")]
        public string SpatialPath;

        #endregion

        #region Classification

        [Header("Classification")]
        [Tooltip("The discipline (Architectural, Structural, MEP, etc.).")]
        public string Discipline;

        [Tooltip("The category (Walls, Doors, Windows, etc.).")]
        public string Category;

        #endregion

        #region Material Information

        [Header("Material Information")]
        [Tooltip("The material name.")]
        public string MaterialName;

        [Tooltip("The element color.")]
        public Color ElementColor = Color.white;

        #endregion

        #region Door / Opening Dimensions

        [Header("Door / Opening (Robotics)")]
        [Tooltip("Door/window overall width in metres.")]
        public float OverallWidth;

        [Tooltip("Door/window overall height in metres.")]
        public float OverallHeight;

        [Tooltip("Door operation type (e.g. SINGLE_SWING_LEFT).")]
        public string OperationType;

        #endregion

        #region Properties

        [Header("Properties")]
        [SerializeField]
        private List<IfcProperty> _properties = new List<IfcProperty>();

        /// <summary>
        /// All properties as a list (matching Metadata.Properties style).
        /// </summary>
        public List<IfcProperty> Properties => _properties;

        /// <summary>
        /// Number of properties stored.
        /// </summary>
        public int PropertyCount => _properties.Count;

        #endregion

        #region Statistics

        [Header("Statistics")]
        [SerializeField] private IfcMetadataStats _stats = new IfcMetadataStats();
        [SerializeField] private bool _hasMetadata = false;

        /// <summary>
        /// Statistics about property completion.
        /// </summary>
        public IfcMetadataStats Stats => _stats;

        /// <summary>
        /// Whether this component has metadata assigned.
        /// </summary>
        public bool HasMetadata => _hasMetadata && _properties.Count > 0;

        #endregion

        #region Initialization Methods

        /// <summary>
        /// Set the core IFC identification properties.
        /// </summary>
        public void SetIdentification(string globalId, string ifcClass, string elementName,
            string description = null, string objectType = null, string elementId = null,
            string predefinedType = null, string sourceFile = null)
        {
            GlobalId = globalId ?? "";
            IfcClass = ifcClass ?? "";
            ElementName = elementName ?? "";
            Description = description ?? "";
            ObjectType = objectType ?? "";
            ElementId = elementId ?? "";
            PredefinedType = predefinedType ?? "";
            SourceFile = sourceFile ?? "";
            _hasMetadata = !string.IsNullOrEmpty(globalId);
            MarkDirty();
        }

        /// <summary>
        /// Set the IFC spatial hierarchy information.
        /// </summary>
        public void SetHierarchy(string project, string site, string building, string storey,
            string parentGlobalId = null, string spatialPath = null,
            float elevation = 0f, string longName = null)
        {
            Project = project ?? "";
            Site = site ?? "";
            Building = building ?? "";
            Storey = storey ?? "";
            ParentGlobalId = parentGlobalId ?? "";
            SpatialPath = spatialPath ?? "";
            Elevation = elevation;
            LongName = longName ?? "";
            MarkDirty();
        }

        /// <summary>
        /// Set the classification information.
        /// </summary>
        public void SetClassification(string discipline, string category)
        {
            Discipline = discipline ?? "";
            Category = category ?? "";
            MarkDirty();
        }

        /// <summary>
        /// Set the material information.
        /// </summary>
        public void SetMaterial(string materialName, Color? color = null)
        {
            MaterialName = materialName ?? "";
            if (color.HasValue)
                ElementColor = color.Value;
            MarkDirty();
        }

        /// <summary>
        /// Set door/opening dimensions for robotics clearance checking.
        /// </summary>
        public void SetDoorDimensions(float width, float height, string operationType = null)
        {
            OverallWidth = width;
            OverallHeight = height;
            OperationType = operationType ?? "";
            MarkDirty();
        }

        #endregion

        #region Property Management

        /// <summary>
        /// Gets a property value by key.
        /// </summary>
        public string GetProperty(string key)
        {
            foreach (var prop in _properties)
            {
                if (prop.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return prop.Value;
            }
            return null;
        }

        /// <summary>
        /// Check if a property exists.
        /// </summary>
        public bool HasProperty(string key)
        {
            return _properties.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets all properties as a dictionary.
        /// </summary>
        public Dictionary<string, string> GetPropertiesAsDictionary()
        {
            var dict = new Dictionary<string, string>();
            foreach (var prop in _properties)
            {
                if (!dict.ContainsKey(prop.Key))
                    dict.Add(prop.Key, prop.Value);
            }
            return dict;
        }

        /// <summary>
        /// Add or update a property with the given key and value.
        /// </summary>
        public void AddProperty(string key, string value)
        {
            AddProperty(key, value, IfcSourceAnnotation.Fallback());
        }

        /// <summary>
        /// Add or update a property with the given key, value, and source annotation.
        /// </summary>
        public void AddProperty(string key, string value, IfcSourceAnnotation annotation)
        {
            if (string.IsNullOrEmpty(key)) return;

            // Find and update existing property
            for (int i = 0; i < _properties.Count; i++)
            {
                if (_properties[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    _properties[i] = new IfcProperty(key, value, annotation);
                    RefreshMetadata();
                    return;
                }
            }

            // Add new property
            _properties.Add(new IfcProperty(key, value, annotation));
            RefreshMetadata();
        }

        /// <summary>
        /// Add or update a property with a category prefix.
        /// </summary>
        public void AddProperty(string key, string value, string category)
        {
            if (string.IsNullOrEmpty(key)) return;
            string fullKey = key.Contains(".") ? key : $"{category}.{key}";
            AddProperty(fullKey, value);
        }

        /// <summary>
        /// Add or update a property with a category prefix and source annotation.
        /// </summary>
        public void AddProperty(string key, string value, string category, IfcSourceAnnotation annotation)
        {
            if (string.IsNullOrEmpty(key)) return;
            string fullKey = key.Contains(".") ? key : $"{category}.{key}";
            AddProperty(fullKey, value, annotation);
        }

        /// <summary>
        /// Get the source annotation for a given property key.
        /// </summary>
        public IfcSourceAnnotation? GetSourceAnnotation(string key)
        {
            foreach (var prop in _properties)
            {
                if (prop.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return prop.SourceAnnotation;
            }
            return null;
        }

        /// <summary>
        /// Get all properties with a specific source type.
        /// </summary>
        public List<IfcProperty> GetPropertiesBySource(IfcSourceType sourceType)
        {
            return _properties.Where(p => p.SourceAnnotation.Source == sourceType).ToList();
        }

        /// <summary>
        /// Get all properties at or above a specific confidence level.
        /// </summary>
        public List<IfcProperty> GetPropertiesAboveConfidence(IfcConfidenceLevel minConfidence)
        {
            return _properties.Where(p => p.SourceAnnotation.Confidence <= minConfidence).ToList();
        }

        /// <summary>
        /// Get a summary of source annotations for this element.
        /// </summary>
        public string GetAnnotationSummary()
        {
            int standard = 0, pset = 0, qto = 0, typeObj = 0, spatial = 0, material = 0, heuristic = 0, geometry = 0, unknown = 0;
            foreach (var p in _properties)
            {
                switch (p.SourceAnnotation.Source)
                {
                    case IfcSourceType.StandardAttribute: standard++; break;
                    case IfcSourceType.PropertySet: pset++; break;
                    case IfcSourceType.QuantitySet: qto++; break;
                    case IfcSourceType.TypeObject: typeObj++; break;
                    case IfcSourceType.SpatialHierarchy: spatial++; break;
                    case IfcSourceType.MaterialAssociation: material++; break;
                    case IfcSourceType.ImporterHeuristic: heuristic++; break;
                    case IfcSourceType.GeometryDerived: geometry++; break;
                    default: unknown++; break;
                }
            }
            return $"Standard:{standard} Pset:{pset} Qto:{qto} Type:{typeObj} Spatial:{spatial} " +
                   $"Material:{material} Heuristic:{heuristic} Geometry:{geometry} Unknown:{unknown}";
        }

        /// <summary>
        /// Remove a specific property by key.
        /// </summary>
        public bool RemoveProperty(string key)
        {
            for (int i = 0; i < _properties.Count; i++)
            {
                if (_properties[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    _properties.RemoveAt(i);
                    RefreshMetadata();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Get all property keys.
        /// </summary>
        public List<string> GetAllPropertyKeys()
        {
            return _properties.Select(p => p.Key).ToList();
        }

        /// <summary>
        /// Clear all properties.
        /// </summary>
        public void ClearProperties()
        {
            _properties.Clear();
            _stats.Reset();
            _hasMetadata = false;
            MarkDirty();
        }

        /// <summary>
        /// Get all properties from a specific property set.
        /// </summary>
        public Dictionary<string, string> GetPropertySet(string psetName)
        {
            var result = new Dictionary<string, string>();
            string prefix = psetName + ".";

            foreach (var prop in _properties)
            {
                if (prop.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string propertyName = prop.Key.Substring(prefix.Length);
                    result[propertyName] = prop.Value;
                }
            }
            return result;
        }

        /// <summary>
        /// Get all property set names.
        /// </summary>
        public List<string> GetPropertySetNames()
        {
            var psetNames = new HashSet<string>();
            foreach (var prop in _properties)
            {
                int dotIndex = prop.Key.IndexOf('.');
                if (dotIndex > 0)
                {
                    string psetName = prop.Key.Substring(0, dotIndex);
                    psetNames.Add(psetName);
                }
            }
            return psetNames.ToList();
        }

        /// <summary>
        /// Get all properties from a specific category.
        /// </summary>
        public List<IfcProperty> GetPropertiesByCategory(string category)
        {
            string prefix = category + ".";
            return _properties.Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        #endregion

        #region Statistics & Internal

        /// <summary>
        /// Update property statistics.
        /// </summary>
        public void UpdateStats()
        {
            _stats.Calculate(_properties);
        }

        /// <summary>
        /// Refresh metadata state and update stats.
        /// </summary>
        private void RefreshMetadata()
        {
            _hasMetadata = _properties.Count > 0 || !string.IsNullOrEmpty(GlobalId);
            UpdateStats();
            MarkDirty();
        }

        /// <summary>
        /// Mark the component as dirty for serialization.
        /// </summary>
        private void MarkDirty()
        {
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        #endregion

        #region Utility

        /// <summary>
        /// Check if this component has IFC data.
        /// </summary>
        public bool HasIfcData => !string.IsNullOrEmpty(GlobalId);

        /// <summary>
        /// Get a summary string for display.
        /// </summary>
        public string GetSummary()
        {
            return $"{IfcClass}: {ElementName} [{GlobalId}]";
        }

        // Compatibility methods
        public string GetValue(string key) => GetProperty(key);
        public bool HasKey(string key) => HasProperty(key);

        #endregion
    }
}
