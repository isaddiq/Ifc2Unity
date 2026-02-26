using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace IFCImporter
{
    /// <summary>
    /// Central registry that maps GlobalId → GameObject / IfcMetadata / IfcRelations
    /// and provides quick-access lists grouped by IFC type.
    /// Attach to the IFC_Root GameObject or access via the static Instance.
    /// </summary>
    [AddComponentMenu("IFC Importer/IFC Registry")]
    public class IfcRegistry : MonoBehaviour
    {
        // ─── Singleton accessor (lives on the root import object) ───
        private static IfcRegistry _instance;
        public static IfcRegistry Instance => _instance;

        // ─── Core look-ups ───
        private Dictionary<string, GameObject> _idToGameObject = new Dictionary<string, GameObject>();
        private Dictionary<string, IfcMetadata> _idToMetadata = new Dictionary<string, IfcMetadata>();
        private Dictionary<string, IfcRelations> _idToRelations = new Dictionary<string, IfcRelations>();

        // ─── Quick lists by robotics category ───
        [Header("Categorised element lists (populated at import)")]
        public List<string> DoorIds = new List<string>();
        public List<string> OpeningIds = new List<string>();
        public List<string> SpaceIds = new List<string>();
        public List<string> StoreyIds = new List<string>();
        public List<string> WalkableIds = new List<string>();
        public List<string> ObstacleIds = new List<string>();

        // ─── Per-type index ───
        private Dictionary<string, List<string>> _byIfcType = new Dictionary<string, List<string>>();

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        // ═══════════════════════════════════════════
        //  Registration
        // ═══════════════════════════════════════════

        /// <summary>
        /// Register a GameObject with its GlobalId. Prevents duplicates.
        /// Returns false if the GlobalId was already registered.
        /// </summary>
        public bool Register(string globalId, GameObject go, IfcMetadata meta = null, IfcRelations rels = null)
        {
            if (string.IsNullOrEmpty(globalId) || go == null) return false;

            if (_idToGameObject.ContainsKey(globalId))
            {
                Debug.LogWarning($"[IfcRegistry] Duplicate GlobalId: {globalId} – skipping.");
                return false;
            }

            _idToGameObject[globalId] = go;

            if (meta != null)
                _idToMetadata[globalId] = meta;

            if (rels != null)
                _idToRelations[globalId] = rels;

            // Per-type index
            string ifcType = meta != null ? meta.IfcClass : "";
            if (!string.IsNullOrEmpty(ifcType))
            {
                if (!_byIfcType.TryGetValue(ifcType, out var list))
                {
                    list = new List<string>();
                    _byIfcType[ifcType] = list;
                }
                list.Add(globalId);
            }

            return true;
        }

        /// <summary>
        /// Categorise an element into the quick-access robotics lists.
        /// Call after Register.
        /// </summary>
        public void Categorise(string globalId, string ifcType, string predefinedType)
        {
            if (string.IsNullOrEmpty(globalId)) return;
            string upper = (ifcType ?? "").ToUpperInvariant();
            string ptype = (predefinedType ?? "").ToUpperInvariant();

            // Doors
            if (upper.Contains("IFCDOOR"))
            {
                DoorIds.Add(globalId);
                return;
            }

            // Openings
            if (upper.Contains("IFCOPENINGELEMENT") || upper.Contains("IFCFEATUREELEMENTSUBTRACTION"))
            {
                OpeningIds.Add(globalId);
                return;
            }

            // Spaces
            if (upper.Contains("IFCSPACE"))
            {
                SpaceIds.Add(globalId);
                return;
            }

            // Storeys
            if (upper.Contains("IFCBUILDINGSTOREY"))
            {
                StoreyIds.Add(globalId);
                return;
            }

            // Walkable
            if (upper.Contains("IFCSLAB"))
            {
                if (ptype == "FLOOR" || ptype == "" || ptype == "NOTDEFINED")
                    WalkableIds.Add(globalId);
                else
                    ObstacleIds.Add(globalId); // ROOF, LANDING, etc.
                return;
            }

            if (upper.Contains("IFCCOVERING") && ptype == "FLOORING")
            {
                WalkableIds.Add(globalId);
                return;
            }

            // Obstacles: walls, columns, stairs, furnishings, beams, railings …
            if (upper.Contains("IFCWALL") || upper.Contains("IFCCOLUMN") ||
                upper.Contains("IFCSTAIR") || upper.Contains("IFCRAMP") ||
                upper.Contains("IFCFURNISHING") || upper.Contains("IFCFURNITURE") ||
                upper.Contains("IFCRAILING") || upper.Contains("IFCBEAM") ||
                upper.Contains("IFCMEMBER"))
            {
                ObstacleIds.Add(globalId);
                return;
            }

            // Windows are obstacles
            if (upper.Contains("IFCWINDOW"))
            {
                ObstacleIds.Add(globalId);
                return;
            }
        }

        // ═══════════════════════════════════════════
        //  Queries
        // ═══════════════════════════════════════════

        public GameObject GetGameObject(string globalId)
        {
            _idToGameObject.TryGetValue(globalId, out var go);
            return go;
        }

        public IfcMetadata GetMetadata(string globalId)
        {
            _idToMetadata.TryGetValue(globalId, out var m);
            return m;
        }

        public IfcRelations GetRelations(string globalId)
        {
            _idToRelations.TryGetValue(globalId, out var r);
            return r;
        }

        /// <summary>Get all GlobalIds for a specific IFC type (e.g. "IfcDoor").</summary>
        public List<string> GetByType(string ifcType)
        {
            return _byIfcType.TryGetValue(ifcType, out var list) ? list : new List<string>();
        }

        /// <summary>Get all registered GlobalIds.</summary>
        public IEnumerable<string> AllIds => _idToGameObject.Keys;

        public int Count => _idToGameObject.Count;

        /// <summary>
        /// Resolve all elements contained in a given spatial structure (storey / space).
        /// </summary>
        public List<string> GetElementsInStructure(string structureGlobalId)
        {
            var result = new List<string>();
            foreach (var kvp in _idToRelations)
            {
                if (kvp.Value.ContainedInStructureId == structureGlobalId)
                    result.Add(kvp.Key);
            }
            return result;
        }

        /// <summary>
        /// For a given door, find the opening it fills and the host wall.
        /// Returns (openingGlobalId, hostWallGlobalId) or nulls.
        /// </summary>
        public (string openingId, string hostWallId) GetDoorPassageChain(string doorGlobalId)
        {
            var rels = GetRelations(doorGlobalId);
            if (rels == null) return (null, null);

            string openingId = rels.FillsOpeningId;
            string hostId = null;

            if (!string.IsNullOrEmpty(openingId))
            {
                var openingRels = GetRelations(openingId);
                if (openingRels != null)
                    hostId = openingRels.HostElementId;
            }

            return (openingId, hostId);
        }

        // ═══════════════════════════════════════════
        //  Summary
        // ═══════════════════════════════════════════

        public string GetSummary()
        {
            return $"IfcRegistry: {Count} total | " +
                   $"Doors: {DoorIds.Count} | Openings: {OpeningIds.Count} | " +
                   $"Spaces: {SpaceIds.Count} | Storeys: {StoreyIds.Count} | " +
                   $"Walkable: {WalkableIds.Count} | Obstacles: {ObstacleIds.Count}";
        }
    }
}
