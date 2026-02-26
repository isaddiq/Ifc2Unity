using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace IFCImporter
{
    /// <summary>
    /// Utility class for assigning Unity layers and tags
    /// to imported IFC GameObjects for robotics use-cases.
    ///
    /// Layers:
    ///   Walkable  – IfcSlab (FLOOR), floor coverings
    ///   Obstacle  – walls, columns, stairs, furnishings
    ///   Door      – IfcDoor
    ///   Opening   – IfcOpeningElement
    ///   Space     – IfcSpace
    ///
    /// Call EnsureLayersExist() once before import (editor only).
    /// Call AssignLayer(go, ifcClass, predefinedType) per element.
    /// </summary>
    public static class RoboticsLayerSetup
    {
        // Layer names  ─ keep short to fit Unity's 31-layer limit
        public const string LayerWalkable = "Walkable";
        public const string LayerObstacle = "Obstacle";
        public const string LayerDoor = "Door";
        public const string LayerOpening = "Opening";
        public const string LayerSpace = "Space";

        // Fallback: if custom layers are not created, use Default (0)
        private static int _walkable = -1;
        private static int _obstacle = -1;
        private static int _door = -1;
        private static int _opening = -1;
        private static int _space = -1;

        /// <summary>
        /// Resolve layer indices. Call once after EnsureLayersExist.
        /// Safe to call at runtime – uses whatever layers exist.
        /// </summary>
        public static void RefreshLayerIndices()
        {
            _walkable = LayerMask.NameToLayer(LayerWalkable);
            _obstacle = LayerMask.NameToLayer(LayerObstacle);
            _door = LayerMask.NameToLayer(LayerDoor);
            _opening = LayerMask.NameToLayer(LayerOpening);
            _space = LayerMask.NameToLayer(LayerSpace);
        }

        /// <summary>
        /// Assign a Unity layer to a GameObject based on IFC type and predefined type.
        /// </summary>
        public static void AssignLayer(GameObject go, string ifcClass, string predefinedType)
        {
            if (go == null) return;
            // Lazy init
            if (_walkable < 0) RefreshLayerIndices();

            string upper = (ifcClass ?? "").ToUpperInvariant();
            string ptype = (predefinedType ?? "").ToUpperInvariant();

            int layer = 0; // Default

            // Doors
            if (upper.Contains("IFCDOOR"))
            {
                layer = _door >= 0 ? _door : 0;
            }
            // Openings
            else if (upper.Contains("IFCOPENINGELEMENT") || upper.Contains("IFCFEATUREELEMENTSUBTRACTION"))
            {
                layer = _opening >= 0 ? _opening : 0;
            }
            // Spaces
            else if (upper.Contains("IFCSPACE"))
            {
                layer = _space >= 0 ? _space : 0;
            }
            // Walkable surfaces
            else if (upper.Contains("IFCSLAB") && (ptype == "FLOOR" || ptype == "" || ptype == "NOTDEFINED"))
            {
                layer = _walkable >= 0 ? _walkable : 0;
            }
            else if (upper.Contains("IFCCOVERING") && ptype == "FLOORING")
            {
                layer = _walkable >= 0 ? _walkable : 0;
            }
            // Obstacles
            else if (upper.Contains("IFCWALL") || upper.Contains("IFCCOLUMN") ||
                     upper.Contains("IFCSTAIR") || upper.Contains("IFCRAMP") ||
                     upper.Contains("IFCFURNISHING") || upper.Contains("IFCFURNITURE") ||
                     upper.Contains("IFCRAILING") || upper.Contains("IFCBEAM") ||
                     upper.Contains("IFCMEMBER") || upper.Contains("IFCWINDOW") ||
                     (upper.Contains("IFCSLAB") && ptype == "ROOF"))
            {
                layer = _obstacle >= 0 ? _obstacle : 0;
            }

            go.layer = layer;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Ensure the required layers exist in the project.  Editor-only.
        /// Uses the TagManager SerializedObject approach.
        /// </summary>
        public static void EnsureLayersExist()
        {
            CreateLayerIfMissing(LayerWalkable);
            CreateLayerIfMissing(LayerObstacle);
            CreateLayerIfMissing(LayerDoor);
            CreateLayerIfMissing(LayerOpening);
            CreateLayerIfMissing(LayerSpace);
            RefreshLayerIndices();
        }

        private static void CreateLayerIfMissing(string layerName)
        {
            if (LayerMask.NameToLayer(layerName) != -1)
                return; // already exists

            var tagManager = new SerializedObject(
                AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/TagManager.asset"));
            var layersProp = tagManager.FindProperty("layers");

            // Layers 0-7 are built-in; user layers are 8-31
            for (int i = 8; i < layersProp.arraySize; i++)
            {
                var sp = layersProp.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(sp.stringValue))
                {
                    sp.stringValue = layerName;
                    tagManager.ApplyModifiedProperties();
                    Debug.Log($"[RoboticsLayerSetup] Created layer '{layerName}' at index {i}");
                    return;
                }
            }

            Debug.LogWarning($"[RoboticsLayerSetup] No empty layer slot for '{layerName}'.");
        }
#endif
    }
}
