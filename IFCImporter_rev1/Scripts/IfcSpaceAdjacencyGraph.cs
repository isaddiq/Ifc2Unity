using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace IFCImporter
{
    // ════════════════════════════════════════════════════════════════
    //  IfcSpaceAdjacencyGraph — first-class topological NavGraph
    //  Built from IfcRelSpaceBoundary data at import time.
    //  Provides multi-floor path planning for robotics planners.
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Represents an edge in the space adjacency graph.
    /// Two spaces are adjacent if they share at least one boundary element.
    /// </summary>
    [Serializable]
    public class SpaceAdjacencyEdge
    {
        [Tooltip("GlobalId of the first space.")]
        public string SpaceA;

        [Tooltip("GlobalId of the second space.")]
        public string SpaceB;

        [Tooltip("GlobalIds of shared boundary elements connecting these spaces.")]
        public List<string> SharedBoundaryElements = new List<string>();

        [Tooltip("Whether any shared boundary is a door (traversable).")]
        public bool HasDoor;

        [Tooltip("Whether any shared boundary is a virtual boundary (no physical barrier).")]
        public bool HasVirtualBoundary;

        [Tooltip("Whether this edge crosses floors (vertical adjacency via stairs/ramps/elevators).")]
        public bool IsVerticalTransition;

        [Tooltip("Estimated traversal cost (1.0 = normal, higher = harder). Doors=1.0, walls=Infinity, virtual=0.5.")]
        public float TraversalCost = float.PositiveInfinity;

        /// <summary>
        /// Returns true if a robot can physically traverse this edge.
        /// </summary>
        public bool IsTraversable => !float.IsPositiveInfinity(TraversalCost);
    }

    /// <summary>
    /// Represents a node (space) in the adjacency graph.
    /// </summary>
    [Serializable]
    public class SpaceGraphNode
    {
        [Tooltip("GlobalId of the IfcSpace.")]
        public string SpaceId;

        [Tooltip("Human-readable name of the space.")]
        public string SpaceName;

        [Tooltip("GlobalId of the containing storey.")]
        public string StoreyId;

        [Tooltip("Storey name for display.")]
        public string StoreyName;

        [Tooltip("Centroid position in Unity world coordinates.")]
        public Vector3 Position;

        [Tooltip("Indices into the edge list for this node's adjacencies.")]
        public List<int> EdgeIndices = new List<int>();

        [Tooltip("All boundary element GlobalIds for this space.")]
        public List<string> BoundaryElementIds = new List<string>();
    }

    /// <summary>
    /// Runtime NavGraph asset built from IfcRelSpaceBoundary data.
    /// Attach to the IFC_Root GameObject alongside IfcRegistry.
    /// Provides O(1) space-to-neighbours lookup and multi-floor path planning.
    /// </summary>
    [AddComponentMenu("IFC Importer/IFC Space Adjacency Graph")]
    public class IfcSpaceAdjacencyGraph : MonoBehaviour
    {
        [Header("Graph Data")]
        [SerializeField] private List<SpaceGraphNode> _nodes = new List<SpaceGraphNode>();
        [SerializeField] private List<SpaceAdjacencyEdge> _edges = new List<SpaceAdjacencyEdge>();

        // Runtime lookup maps (built on Awake or after import)
        private Dictionary<string, int> _spaceIdToNodeIndex;
        private Dictionary<string, List<int>> _storeyToNodeIndices;
        private bool _indexBuilt;

        /// <summary>All nodes in the graph.</summary>
        public IReadOnlyList<SpaceGraphNode> Nodes => _nodes;

        /// <summary>All edges in the graph.</summary>
        public IReadOnlyList<SpaceAdjacencyEdge> Edges => _edges;

        /// <summary>Number of spaces in the graph.</summary>
        public int NodeCount => _nodes.Count;

        /// <summary>Number of adjacency edges.</summary>
        public int EdgeCount => _edges.Count;

        /// <summary>Number of traversable edges (doors, virtual boundaries).</summary>
        public int TraversableEdgeCount => _edges.Count(e => e.IsTraversable);

        private void Awake()
        {
            RebuildIndices();
        }

        // ═══════════════════════════════════════════
        //  Graph Construction (called at import time)
        // ═══════════════════════════════════════════

        /// <summary>
        /// Build the adjacency graph from space boundary data and registry.
        /// </summary>
        public void BuildFromRelationships(
            IfcRelationshipBundle relationships,
            IfcRegistry registry)
        {
            _nodes.Clear();
            _edges.Clear();

            if (relationships == null || registry == null) return;

            // Step 1: Create nodes for all registered spaces
            var spaceIdSet = new HashSet<string>(registry.SpaceIds);
            var spaceToNode = new Dictionary<string, int>();

            foreach (var spaceId in registry.SpaceIds)
            {
                var meta = registry.GetMetadata(spaceId);
                var go = registry.GetGameObject(spaceId);

                var node = new SpaceGraphNode
                {
                    SpaceId = spaceId,
                    SpaceName = meta != null ? meta.ElementName : spaceId,
                    StoreyId = meta != null ? meta.ParentGlobalId : "",
                    StoreyName = meta != null ? meta.Storey : "",
                    Position = go != null ? go.transform.position : Vector3.zero
                };

                // If position is zero, try mesh bounds centre
                if (go != null && node.Position == Vector3.zero)
                {
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                        node.Position = mf.sharedMesh.bounds.center;
                }

                spaceToNode[spaceId] = _nodes.Count;
                _nodes.Add(node);
            }

            // Step 2: Build boundary element → space(s) mapping
            var elementToSpaces = new Dictionary<string, List<string>>();
            foreach (var sb in relationships.SpaceBoundaries)
            {
                if (string.IsNullOrEmpty(sb.RelatingSpace) || string.IsNullOrEmpty(sb.RelatedBuildingElement))
                    continue;

                // Only consider spaces that are registered
                if (!spaceIdSet.Contains(sb.RelatingSpace))
                    continue;

                if (!elementToSpaces.TryGetValue(sb.RelatedBuildingElement, out var spaceList))
                {
                    spaceList = new List<string>();
                    elementToSpaces[sb.RelatedBuildingElement] = spaceList;
                }

                if (!spaceList.Contains(sb.RelatingSpace))
                    spaceList.Add(sb.RelatingSpace);

                // Track boundary elements on the node
                if (spaceToNode.TryGetValue(sb.RelatingSpace, out int nodeIdx))
                {
                    if (!_nodes[nodeIdx].BoundaryElementIds.Contains(sb.RelatedBuildingElement))
                        _nodes[nodeIdx].BoundaryElementIds.Add(sb.RelatedBuildingElement);
                }
            }

            // Step 3: Build boundary type lookup (physical vs virtual)
            var boundaryTypes = new Dictionary<string, (bool isVirtual, bool isExternal)>();
            foreach (var sb in relationships.SpaceBoundaries)
            {
                if (string.IsNullOrEmpty(sb.RelatedBuildingElement)) continue;
                string key = $"{sb.RelatingSpace}_{sb.RelatedBuildingElement}";
                bool isVirtual = (sb.PhysicalOrVirtualBoundary ?? "").ToUpperInvariant().Contains("VIRTUAL");
                bool isExternal = (sb.InternalOrExternalBoundary ?? "").ToUpperInvariant().Contains("EXTERNAL");
                boundaryTypes[key] = (isVirtual, isExternal);
            }

            // Step 4: Create edges where two spaces share a boundary element
            var edgeSet = new HashSet<string>(); // "spaceA_spaceB" dedup key

            foreach (var kvp in elementToSpaces)
            {
                string elementId = kvp.Key;
                var spaces = kvp.Value;

                for (int i = 0; i < spaces.Count; i++)
                {
                    for (int j = i + 1; j < spaces.Count; j++)
                    {
                        string a = spaces[i];
                        string b = spaces[j];

                        // Canonical edge key
                        string edgeKey = string.CompareOrdinal(a, b) < 0 ? $"{a}_{b}" : $"{b}_{a}";

                        if (!edgeSet.Contains(edgeKey))
                        {
                            edgeSet.Add(edgeKey);

                            var edge = new SpaceAdjacencyEdge
                            {
                                SpaceA = a,
                                SpaceB = b
                            };
                            int edgeIdx = _edges.Count;
                            _edges.Add(edge);

                            // Wire edge indices to nodes
                            if (spaceToNode.TryGetValue(a, out int idxA))
                                _nodes[idxA].EdgeIndices.Add(edgeIdx);
                            if (spaceToNode.TryGetValue(b, out int idxB))
                                _nodes[idxB].EdgeIndices.Add(edgeIdx);
                        }

                        // Find existing edge and add shared element
                        string ek2 = string.CompareOrdinal(a, b) < 0 ? $"{a}_{b}" : $"{b}_{a}";
                        // Find edge in list
                        for (int e = _edges.Count - 1; e >= 0; e--)
                        {
                            var eg = _edges[e];
                            string k = string.CompareOrdinal(eg.SpaceA, eg.SpaceB) < 0
                                ? $"{eg.SpaceA}_{eg.SpaceB}" : $"{eg.SpaceB}_{eg.SpaceA}";
                            if (k == ek2)
                            {
                                if (!eg.SharedBoundaryElements.Contains(elementId))
                                    eg.SharedBoundaryElements.Add(elementId);
                                break;
                            }
                        }
                    }
                }
            }

            // Step 5: Classify edges (door detection, virtual boundaries, vertical transitions)
            var doorIds = new HashSet<string>(registry.DoorIds);
            var openingIds = new HashSet<string>(registry.OpeningIds);

            foreach (var edge in _edges)
            {
                bool hasDoor = false;
                bool hasVirtual = false;
                bool isVertical = false;

                foreach (var elemId in edge.SharedBoundaryElements)
                {
                    // Check if the boundary element is a door
                    if (doorIds.Contains(elemId))
                        hasDoor = true;

                    // Check if a door fills an opening in this element
                    var rels = registry.GetRelations(elemId);
                    if (rels != null)
                    {
                        if (!string.IsNullOrEmpty(rels.FilledByElementId) && doorIds.Contains(rels.FilledByElementId))
                            hasDoor = true;
                        if (rels.OpeningElementIds != null)
                        {
                            foreach (var oid in rels.OpeningElementIds)
                            {
                                var oRels = registry.GetRelations(oid);
                                if (oRels != null && !string.IsNullOrEmpty(oRels.FilledByElementId) &&
                                    doorIds.Contains(oRels.FilledByElementId))
                                    hasDoor = true;
                            }
                        }
                    }

                    // Check virtual boundary via boundary type lookup
                    string keyA = $"{edge.SpaceA}_{elemId}";
                    string keyB = $"{edge.SpaceB}_{elemId}";
                    if (boundaryTypes.TryGetValue(keyA, out var btA) && btA.isVirtual)
                        hasVirtual = true;
                    if (boundaryTypes.TryGetValue(keyB, out var btB) && btB.isVirtual)
                        hasVirtual = true;

                    // Check vertical transition elements (stairs, ramps, elevators)
                    var elemMeta = registry.GetMetadata(elemId);
                    if (elemMeta != null)
                    {
                        string cls = (elemMeta.IfcClass ?? "").ToUpperInvariant();
                        if (cls.Contains("IFCSTAIR") || cls.Contains("IFCRAMP") ||
                            cls.Contains("IFCTRANSPORTELEMENT") || cls.Contains("IFCELEVATOR"))
                            isVertical = true;
                    }
                }

                // Also detect cross-floor by storey mismatch
                if (spaceToNode.TryGetValue(edge.SpaceA, out int nA) &&
                    spaceToNode.TryGetValue(edge.SpaceB, out int nB))
                {
                    if (!string.IsNullOrEmpty(_nodes[nA].StoreyName) &&
                        !string.IsNullOrEmpty(_nodes[nB].StoreyName) &&
                        _nodes[nA].StoreyName != _nodes[nB].StoreyName)
                        isVertical = true;
                }

                edge.HasDoor = hasDoor;
                edge.HasVirtualBoundary = hasVirtual;
                edge.IsVerticalTransition = isVertical;

                // Compute traversal cost
                if (hasDoor)
                    edge.TraversalCost = 1.0f;
                else if (hasVirtual)
                    edge.TraversalCost = 0.5f;
                else if (openingIds.Intersect(edge.SharedBoundaryElements).Any())
                    edge.TraversalCost = 0.8f; // unfilled opening
                else
                    edge.TraversalCost = float.PositiveInfinity; // solid wall — not traversable
            }

            RebuildIndices();

            Debug.Log($"[IfcSpaceAdjacencyGraph] Built: {_nodes.Count} spaces, {_edges.Count} edges " +
                      $"({TraversableEdgeCount} traversable, {_edges.Count(e => e.IsVerticalTransition)} vertical).");
        }

        // ═══════════════════════════════════════════
        //  Runtime Queries
        // ═══════════════════════════════════════════

        /// <summary>
        /// Get the graph node for a space by GlobalId, or null.
        /// </summary>
        public SpaceGraphNode GetNode(string spaceId)
        {
            EnsureIndices();
            if (_spaceIdToNodeIndex != null && _spaceIdToNodeIndex.TryGetValue(spaceId, out int idx))
                return _nodes[idx];
            return null;
        }

        /// <summary>
        /// Get all directly adjacent spaces (traversable only by default).
        /// </summary>
        public List<string> GetAdjacentSpaces(string spaceId, bool traversableOnly = true)
        {
            EnsureIndices();
            var result = new List<string>();
            var node = GetNode(spaceId);
            if (node == null) return result;

            foreach (int edgeIdx in node.EdgeIndices)
            {
                if (edgeIdx < 0 || edgeIdx >= _edges.Count) continue;
                var edge = _edges[edgeIdx];
                if (traversableOnly && !edge.IsTraversable) continue;

                string neighbour = edge.SpaceA == spaceId ? edge.SpaceB : edge.SpaceA;
                if (!result.Contains(neighbour))
                    result.Add(neighbour);
            }
            return result;
        }

        /// <summary>
        /// Get all spaces on a given storey.
        /// </summary>
        public List<string> GetSpacesOnStorey(string storeyName)
        {
            EnsureIndices();
            if (_storeyToNodeIndices != null && _storeyToNodeIndices.TryGetValue(storeyName, out var indices))
                return indices.Select(i => _nodes[i].SpaceId).ToList();
            return new List<string>();
        }

        /// <summary>
        /// Find a shortest path between two spaces using BFS on traversable edges.
        /// Returns an ordered list of space GlobalIds from start to goal (inclusive),
        /// or an empty list if no path exists.
        /// </summary>
        public List<string> FindPath(string startSpaceId, string goalSpaceId)
        {
            EnsureIndices();
            if (_spaceIdToNodeIndex == null) return new List<string>();
            if (!_spaceIdToNodeIndex.ContainsKey(startSpaceId) || !_spaceIdToNodeIndex.ContainsKey(goalSpaceId))
                return new List<string>();

            if (startSpaceId == goalSpaceId) return new List<string> { startSpaceId };

            // BFS
            var visited = new HashSet<string>();
            var queue = new Queue<string>();
            var parent = new Dictionary<string, string>();

            visited.Add(startSpaceId);
            queue.Enqueue(startSpaceId);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                foreach (string neighbour in GetAdjacentSpaces(current, traversableOnly: true))
                {
                    if (visited.Contains(neighbour)) continue;
                    visited.Add(neighbour);
                    parent[neighbour] = current;

                    if (neighbour == goalSpaceId)
                    {
                        // Reconstruct path
                        var path = new List<string>();
                        string node = goalSpaceId;
                        while (node != null)
                        {
                            path.Add(node);
                            parent.TryGetValue(node, out node);
                            if (node == startSpaceId) { path.Add(node); break; }
                        }
                        path.Reverse();
                        return path;
                    }

                    queue.Enqueue(neighbour);
                }
            }

            return new List<string>(); // No path found
        }

        /// <summary>
        /// Find path with Dijkstra using traversal costs.
        /// Returns (path, totalCost). Empty path if unreachable.
        /// </summary>
        public (List<string> path, float cost) FindWeightedPath(string startSpaceId, string goalSpaceId)
        {
            EnsureIndices();
            if (_spaceIdToNodeIndex == null ||
                !_spaceIdToNodeIndex.ContainsKey(startSpaceId) ||
                !_spaceIdToNodeIndex.ContainsKey(goalSpaceId))
                return (new List<string>(), float.PositiveInfinity);

            if (startSpaceId == goalSpaceId) return (new List<string> { startSpaceId }, 0f);

            var dist = new Dictionary<string, float>();
            var prev = new Dictionary<string, string>();
            var open = new SortedList<float, string>(new DuplicateKeyComparer());

            dist[startSpaceId] = 0;
            open.Add(0, startSpaceId);

            while (open.Count > 0)
            {
                string current = open.Values[0];
                float currentDist = open.Keys[0];
                open.RemoveAt(0);

                if (current == goalSpaceId)
                {
                    var path = new List<string>();
                    string n = goalSpaceId;
                    while (n != null)
                    {
                        path.Add(n);
                        prev.TryGetValue(n, out n);
                        if (n == startSpaceId) { path.Add(n); break; }
                    }
                    path.Reverse();
                    return (path, currentDist);
                }

                if (currentDist > (dist.TryGetValue(current, out float d) ? d : float.PositiveInfinity))
                    continue;

                var node = GetNode(current);
                if (node == null) continue;

                foreach (int edgeIdx in node.EdgeIndices)
                {
                    if (edgeIdx < 0 || edgeIdx >= _edges.Count) continue;
                    var edge = _edges[edgeIdx];
                    if (!edge.IsTraversable) continue;

                    string neighbour = edge.SpaceA == current ? edge.SpaceB : edge.SpaceA;
                    float newDist = currentDist + edge.TraversalCost;

                    if (newDist < (dist.TryGetValue(neighbour, out float nd) ? nd : float.PositiveInfinity))
                    {
                        dist[neighbour] = newDist;
                        prev[neighbour] = current;
                        open.Add(newDist, neighbour);
                    }
                }
            }

            return (new List<string>(), float.PositiveInfinity);
        }

        /// <summary>
        /// Get a summary string for debug logging.
        /// </summary>
        public string GetSummary()
        {
            int traversable = _edges.Count(e => e.IsTraversable);
            int doorEdges = _edges.Count(e => e.HasDoor);
            int virtualEdges = _edges.Count(e => e.HasVirtualBoundary);
            int verticalEdges = _edges.Count(e => e.IsVerticalTransition);

            return $"NavGraph: {_nodes.Count} spaces, {_edges.Count} edges " +
                   $"(traversable: {traversable}, doors: {doorEdges}, virtual: {virtualEdges}, vertical: {verticalEdges})";
        }

        // ─── Internal ───

        private void RebuildIndices()
        {
            _spaceIdToNodeIndex = new Dictionary<string, int>();
            _storeyToNodeIndices = new Dictionary<string, List<int>>();

            for (int i = 0; i < _nodes.Count; i++)
            {
                _spaceIdToNodeIndex[_nodes[i].SpaceId] = i;

                string storey = _nodes[i].StoreyName ?? "";
                if (!_storeyToNodeIndices.TryGetValue(storey, out var list))
                {
                    list = new List<int>();
                    _storeyToNodeIndices[storey] = list;
                }
                list.Add(i);
            }
            _indexBuilt = true;
        }

        private void EnsureIndices()
        {
            if (!_indexBuilt || _spaceIdToNodeIndex == null)
                RebuildIndices();
        }

        /// <summary>
        /// Comparer that allows duplicate keys in SortedList (for Dijkstra priority queue).
        /// </summary>
        private class DuplicateKeyComparer : IComparer<float>
        {
            public int Compare(float x, float y)
            {
                int result = x.CompareTo(y);
                return result == 0 ? 1 : result; // Never return 0 to allow duplicates
            }
        }
    }
}
