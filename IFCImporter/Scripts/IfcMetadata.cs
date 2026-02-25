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

        public IfcProperty(string key, string value)
        {
            Key = key ?? "";
            Value = value ?? "";
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

        #region Properties

        [Header("Properties")]
        [SerializeField]
        private List<IfcProperty> _properties = new List<IfcProperty>();

        /// <summary>
        /// All properties as a read-only list.
        /// </summary>
        public IReadOnlyList<IfcProperty> Properties => _properties;

        /// <summary>
        /// Number of properties stored.
        /// </summary>
        public int PropertyCount => _properties.Count;

        #endregion

        #region Initialization Methods

        /// <summary>
        /// Set the core IFC identification properties.
        /// </summary>
        public void SetIdentification(string globalId, string ifcClass, string elementName,
            string description = null, string objectType = null, string elementId = null)
        {
            GlobalId = globalId ?? "";
            IfcClass = ifcClass ?? "";
            ElementName = elementName ?? "";
            Description = description ?? "";
            ObjectType = objectType ?? "";
            ElementId = elementId ?? "";
        }

        /// <summary>
        /// Set the IFC spatial hierarchy information.
        /// </summary>
        public void SetHierarchy(string project, string site, string building, string storey,
            string parentGlobalId = null, string spatialPath = null)
        {
            Project = project ?? "";
            Site = site ?? "";
            Building = building ?? "";
            Storey = storey ?? "";
            ParentGlobalId = parentGlobalId ?? "";
            SpatialPath = spatialPath ?? "";
        }

        /// <summary>
        /// Set the classification information.
        /// </summary>
        public void SetClassification(string discipline, string category)
        {
            Discipline = discipline ?? "";
            Category = category ?? "";
        }

        /// <summary>
        /// Set the material information.
        /// </summary>
        public void SetMaterial(string materialName, Color? color = null)
        {
            MaterialName = materialName ?? "";
            if (color.HasValue)
                ElementColor = color.Value;
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
            if (string.IsNullOrEmpty(key)) return;

            // Find and update existing property
            for (int i = 0; i < _properties.Count; i++)
            {
                if (_properties[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    _properties[i] = new IfcProperty(key, value);
                    return;
                }
            }

            // Add new property
            _properties.Add(new IfcProperty(key, value));
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
        /// Remove a specific property by key.
        /// </summary>
        public bool RemoveProperty(string key)
        {
            for (int i = 0; i < _properties.Count; i++)
            {
                if (_properties[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    _properties.RemoveAt(i);
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

        #endregion
    }
}
