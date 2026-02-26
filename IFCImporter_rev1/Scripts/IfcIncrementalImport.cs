using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace IFCImporter
{
    // ════════════════════════════════════════════════════════════════
    //  Incremental Import — delta-update strategy
    //  Avoids full re-import by comparing element fingerprints
    //  (GlobalId + content hash) between imports.
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tracks the state of an individual element from the previous import.
    /// </summary>
    [Serializable]
    public class IfcElementFingerprint
    {
        public string GlobalId;
        public string ContentHash;     // SHA-256 of identity + geometry + property values
        public string IfcClass;
        public string Name;
        public long LastImportTimestamp; // Unix epoch ms
    }

    /// <summary>
    /// Manifest file that records the state of all elements from the last import.
    /// Persisted as JSON alongside the import artefacts.
    /// </summary>
    [Serializable]
    public class IfcImportManifest
    {
        public string IfcFilePath;
        public string IfcFileHash;       // SHA-256 of the entire IFC file
        public long ImportTimestamp;
        public string Schema;
        public int ElementCount;
        public List<IfcElementFingerprint> Elements = new List<IfcElementFingerprint>();

        // Runtime lookup (not serialized)
        [NonSerialized] private Dictionary<string, IfcElementFingerprint> _lookup;

        /// <summary>
        /// Build the runtime lookup dictionary.
        /// </summary>
        public void BuildLookup()
        {
            _lookup = new Dictionary<string, IfcElementFingerprint>();
            foreach (var elem in Elements)
            {
                if (!string.IsNullOrEmpty(elem.GlobalId))
                    _lookup[elem.GlobalId] = elem;
            }
        }

        /// <summary>
        /// Get fingerprint for an element, or null if not in manifest.
        /// </summary>
        public IfcElementFingerprint GetFingerprint(string globalId)
        {
            if (_lookup == null) BuildLookup();
            _lookup.TryGetValue(globalId, out var fp);
            return fp;
        }

        /// <summary>
        /// Get all GlobalIds in the manifest.
        /// </summary>
        public HashSet<string> GetAllIds()
        {
            if (_lookup == null) BuildLookup();
            return new HashSet<string>(_lookup.Keys);
        }
    }

    /// <summary>
    /// Delta result from comparing current import data against previous manifest.
    /// </summary>
    [Serializable]
    public class IfcImportDelta
    {
        /// <summary>Elements that are new (not in previous manifest).</summary>
        public List<string> AddedIds = new List<string>();

        /// <summary>Elements whose content hash changed.</summary>
        public List<string> ModifiedIds = new List<string>();

        /// <summary>Elements in the previous manifest but not in current data.</summary>
        public List<string> RemovedIds = new List<string>();

        /// <summary>Elements unchanged between imports.</summary>
        public List<string> UnchangedIds = new List<string>();

        /// <summary>Whether there are any changes.</summary>
        public bool HasChanges => AddedIds.Count > 0 || ModifiedIds.Count > 0 || RemovedIds.Count > 0;

        /// <summary>Total elements in the delta.</summary>
        public int TotalCount => AddedIds.Count + ModifiedIds.Count + RemovedIds.Count + UnchangedIds.Count;

        /// <summary>Number of elements that need processing.</summary>
        public int ChangedCount => AddedIds.Count + ModifiedIds.Count + RemovedIds.Count;

        public string GetSummary()
        {
            return $"Delta: +{AddedIds.Count} added, ~{ModifiedIds.Count} modified, " +
                   $"-{RemovedIds.Count} removed, ={UnchangedIds.Count} unchanged";
        }
    }

    /// <summary>
    /// Incremental import manager.
    /// Computes delta between current import data and previous manifest.
    /// Applies selective updates to the scene instead of full rebuild.
    /// </summary>
    public static class IfcIncrementalImport
    {
        private const string ManifestFileName = "_import_manifest.json";

        /// <summary>
        /// Get the manifest file path for a given IFC file.
        /// </summary>
        public static string GetManifestPath(string ifcFilePath)
        {
            string basePath = Path.ChangeExtension(ifcFilePath, null);
            return basePath + ManifestFileName;
        }

        /// <summary>
        /// Load the previous import manifest, or null if none exists.
        /// </summary>
        public static IfcImportManifest LoadManifest(string ifcFilePath)
        {
            string manifestPath = GetManifestPath(ifcFilePath);
            if (!File.Exists(manifestPath))
                return null;

            try
            {
                string json = File.ReadAllText(manifestPath);
                var manifest = JsonUtility.FromJson<IfcImportManifest>(json);
                manifest?.BuildLookup();
                return manifest;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[IncrementalImport] Failed to load manifest: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Save the current import manifest.
        /// </summary>
        public static void SaveManifest(string ifcFilePath, IfcImportManifest manifest)
        {
            string manifestPath = GetManifestPath(ifcFilePath);
            try
            {
                string json = JsonUtility.ToJson(manifest, true);
                File.WriteAllText(manifestPath, json, Encoding.UTF8);
                Debug.Log($"[IncrementalImport] Manifest saved: {manifestPath} ({manifest.ElementCount} elements)");
            }
            catch (Exception e)
            {
                Debug.LogError($"[IncrementalImport] Failed to save manifest: {e.Message}");
            }
        }

        /// <summary>
        /// Build a manifest from the current import data.
        /// </summary>
        public static IfcImportManifest BuildManifest(
            string ifcFilePath,
            List<IfcElementData> elements,
            string schema,
            string ifcFileHash = null)
        {
            var manifest = new IfcImportManifest
            {
                IfcFilePath = ifcFilePath,
                IfcFileHash = ifcFileHash ?? ComputeFileHash(ifcFilePath),
                ImportTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Schema = schema,
                ElementCount = elements.Count
            };

            foreach (var elem in elements)
            {
                manifest.Elements.Add(new IfcElementFingerprint
                {
                    GlobalId = elem.GlobalId,
                    ContentHash = ComputeElementHash(elem),
                    IfcClass = elem.IfcClass,
                    Name = elem.Name,
                    LastImportTimestamp = manifest.ImportTimestamp
                });
            }

            manifest.BuildLookup();
            return manifest;
        }

        /// <summary>
        /// Compute delta between current elements and a previous manifest.
        /// </summary>
        public static IfcImportDelta ComputeDelta(
            List<IfcElementData> currentElements,
            IfcImportManifest previousManifest)
        {
            var delta = new IfcImportDelta();

            if (previousManifest == null)
            {
                // First import — everything is new
                delta.AddedIds = currentElements.Select(e => e.GlobalId).ToList();
                return delta;
            }

            var previousIds = previousManifest.GetAllIds();
            var currentIds = new HashSet<string>();

            foreach (var elem in currentElements)
            {
                string gid = elem.GlobalId;
                currentIds.Add(gid);

                var prevFp = previousManifest.GetFingerprint(gid);
                if (prevFp == null)
                {
                    delta.AddedIds.Add(gid);
                }
                else
                {
                    string currentHash = ComputeElementHash(elem);
                    if (currentHash != prevFp.ContentHash)
                        delta.ModifiedIds.Add(gid);
                    else
                        delta.UnchangedIds.Add(gid);
                }
            }

            // Removed: in previous but not in current
            foreach (var prevId in previousIds)
            {
                if (!currentIds.Contains(prevId))
                    delta.RemovedIds.Add(prevId);
            }

            return delta;
        }

        /// <summary>
        /// Apply incremental updates to an existing scene.
        /// Removes deleted elements, updates modified ones, adds new ones.
        /// Returns the number of elements actually processed (skipping unchanged).
        /// </summary>
        public static int ApplyDelta(
            IfcImportDelta delta,
            IfcRegistry registry,
            Action<string> onRemoved = null,
            Action<string> onModifiedPre = null,
            Action<string> onAdded = null)
        {
            int processed = 0;

            // Step 1: Remove deleted elements
            foreach (var removedId in delta.RemovedIds)
            {
                var go = registry.GetGameObject(removedId);
                if (go != null)
                {
                    Debug.Log($"[IncrementalImport] Removing deleted element: {removedId}");
                    UnityEngine.Object.DestroyImmediate(go);
                    onRemoved?.Invoke(removedId);
                    processed++;
                }
            }

            // Step 2: Mark modified elements for re-processing
            foreach (var modId in delta.ModifiedIds)
            {
                var go = registry.GetGameObject(modId);
                if (go != null)
                {
                    // Remove existing mesh/metadata so they can be re-created
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null) UnityEngine.Object.DestroyImmediate(mf);
                    var mr = go.GetComponent<MeshRenderer>();
                    if (mr != null) UnityEngine.Object.DestroyImmediate(mr);
                    var oldMeta = go.GetComponent<IfcMetadata>();
                    if (oldMeta != null) oldMeta.ClearProperties();
                    onModifiedPre?.Invoke(modId);
                    processed++;
                }
            }

            // Step 3: Notify about added elements (caller creates them)
            foreach (var addedId in delta.AddedIds)
            {
                onAdded?.Invoke(addedId);
                processed++;
            }

            return processed;
        }

        /// <summary>
        /// Quick check: has the IFC file itself changed since the last manifest?
        /// Uses file hash comparison without performing a full element diff.
        /// </summary>
        public static bool HasFileChanged(string ifcFilePath, IfcImportManifest previousManifest)
        {
            if (previousManifest == null) return true;
            string currentHash = ComputeFileHash(ifcFilePath);
            return currentHash != previousManifest.IfcFileHash;
        }

        // ─── Hashing helpers ───

        /// <summary>
        /// Compute a content hash for an IfcElementData.
        /// Captures identity, geometry flag, material, hierarchy, and properties.
        /// </summary>
        public static string ComputeElementHash(IfcElementData elem)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder(512);
                sb.Append(elem.GlobalId ?? "");
                sb.Append('|');
                sb.Append(elem.Name ?? "");
                sb.Append('|');
                sb.Append(elem.IfcClass ?? "");
                sb.Append('|');
                sb.Append(elem.TypeName ?? "");
                sb.Append('|');
                sb.Append(elem.PredefinedType ?? "");
                sb.Append('|');
                sb.Append(elem.Site ?? "");
                sb.Append('|');
                sb.Append(elem.Building ?? "");
                sb.Append('|');
                sb.Append(elem.Storey ?? "");
                sb.Append('|');
                sb.Append(elem.Space ?? "");
                sb.Append('|');
                sb.Append(elem.Material ?? "");
                sb.Append('|');
                sb.Append(elem.Color.r.ToString("F4"));
                sb.Append(elem.Color.g.ToString("F4"));
                sb.Append(elem.Color.b.ToString("F4"));
                sb.Append(elem.Color.a.ToString("F4"));
                sb.Append('|');
                sb.Append(elem.OverallWidth.ToString("F4"));
                sb.Append(elem.OverallHeight.ToString("F4"));
                sb.Append('|');
                // Include sorted property values in hash
                if (elem.Properties != null && elem.Properties.Count > 0)
                {
                    var sortedKeys = elem.Properties.Keys.OrderBy(k => k).ToList();
                    foreach (var key in sortedKeys)
                    {
                        sb.Append(key);
                        sb.Append('=');
                        sb.Append(elem.Properties[key] ?? "");
                        sb.Append(';');
                    }
                }

                byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16); // 16 hex chars
            }
        }

        /// <summary>
        /// Compute SHA-256 hash of a file.
        /// </summary>
        public static string ComputeFileHash(string filePath)
        {
            if (!File.Exists(filePath)) return "";
            try
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] hashBytes = sha.ComputeHash(stream);
                    return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
