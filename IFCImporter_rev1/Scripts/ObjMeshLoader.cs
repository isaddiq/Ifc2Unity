using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Globalization;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace IFCImporter
{
    /// <summary>
    /// Custom OBJ loader that imports individual objects with their names preserved.
    /// NO LIMITS on vertices, faces, or file size.
    /// </summary>
    public static class ObjMeshLoader
    {
        /// <summary>
        /// Load all meshes from an OBJ file, preserving object names and world positions.
        /// </summary>
        public static Dictionary<string, Mesh> LoadObjFile(string filePath, Action<float, string> progressCallback = null)
        {
            var meshes = new Dictionary<string, Mesh>();

            if (!File.Exists(filePath))
            {
                Debug.LogError($"OBJ file not found: {filePath}");
                return meshes;
            }

            try
            {
                var lines = File.ReadAllLines(filePath);
                int totalLines = lines.Length;
                Debug.Log($"[OBJ Loader] Reading {totalLines} lines from: {filePath}");

                // Debug: Count key elements in file
                int objectCount = 0;
                int vertexCount = 0;
                int faceCount = 0;
                foreach (var line in lines)
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("o ") || trimmed.StartsWith("g ")) objectCount++;
                    else if (trimmed.StartsWith("v ")) vertexCount++;
                    else if (trimmed.StartsWith("f ")) faceCount++;
                }
                Debug.Log($"[OBJ Loader] File contains: {objectCount} objects, {vertexCount} vertices, {faceCount} faces");

                // Global vertex list (OBJ uses 1-based indexing and accumulates vertices)
                var globalVertices = new List<Vector3>();
                var globalNormals = new List<Vector3>();
                var globalUVs = new List<Vector2>();

                // Current object data
                string currentObjectName = null;
                var currentVertexIndices = new List<int>();
                var currentNormalIndices = new List<int>();
                var currentUVIndices = new List<int>();
                int currentVertexStart = 0;

                string currentMaterial = "";
                var objectMaterials = new Dictionary<string, string>(); // Store material per object

                for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    string line = lines[lineIndex].Trim();

                    // Report progress every 1000 lines
                    if (lineIndex % 1000 == 0)
                    {
                        float progress = (float)lineIndex / totalLines;
                        progressCallback?.Invoke(progress, $"Parsing OBJ: {lineIndex}/{totalLines} lines");
                    }

                    if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                        continue;

                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    switch (parts[0])
                    {
                        case "v": // Vertex
                            if (parts.Length >= 4)
                            {
                                float x = ParseFloat(parts[1]);
                                float y = ParseFloat(parts[2]);
                                float z = ParseFloat(parts[3]);
                                globalVertices.Add(new Vector3(x, y, z));
                            }
                            break;

                        case "vn": // Vertex normal
                            if (parts.Length >= 4)
                            {
                                float nx = ParseFloat(parts[1]);
                                float ny = ParseFloat(parts[2]);
                                float nz = ParseFloat(parts[3]);
                                globalNormals.Add(new Vector3(nx, ny, nz));
                            }
                            break;

                        case "vt": // Texture coordinate
                            if (parts.Length >= 3)
                            {
                                float u = ParseFloat(parts[1]);
                                float v = ParseFloat(parts[2]);
                                globalUVs.Add(new Vector2(u, v));
                            }
                            break;

                        case "o": // Object name
                        case "g": // Group name (treat as object)
                            // Save previous object
                            if (!string.IsNullOrEmpty(currentObjectName) && currentVertexIndices.Count > 0)
                            {
                                var mesh = CreateMesh(currentObjectName, globalVertices, globalNormals,
                                    globalUVs, currentVertexIndices, currentNormalIndices, currentUVIndices);
                                if (mesh != null && !meshes.ContainsKey(currentObjectName))
                                {
                                    meshes[currentObjectName] = mesh;
                                }
                            }

                            // Start new object
                            currentObjectName = parts.Length > 1 ? parts[1] : $"Object_{meshes.Count}";
                            currentVertexIndices.Clear();
                            currentNormalIndices.Clear();
                            currentUVIndices.Clear();
                            currentVertexStart = globalVertices.Count;
                            break;

                        case "usemtl": // Material
                            currentMaterial = parts.Length > 1 ? parts[1] : "";
                            break;

                        case "f": // Face
                            ParseFace(parts, currentVertexIndices, currentNormalIndices, currentUVIndices);
                            break;
                    }
                }

                // Save last object
                if (!string.IsNullOrEmpty(currentObjectName) && currentVertexIndices.Count > 0)
                {
                    var mesh = CreateMesh(currentObjectName, globalVertices, globalNormals,
                        globalUVs, currentVertexIndices, currentNormalIndices, currentUVIndices);
                    if (mesh != null && !meshes.ContainsKey(currentObjectName))
                    {
                        meshes[currentObjectName] = mesh;
                    }
                }

                progressCallback?.Invoke(1f, $"Loaded {meshes.Count} meshes");
            }
            catch (Exception e)
            {
                Debug.LogError($"Error loading OBJ file: {e.Message}\n{e.StackTrace}");
            }

            return meshes;
        }

        /// <summary>
        /// Parse face data (supports v, v/vt, v/vt/vn, v//vn formats).
        /// </summary>
        private static void ParseFace(string[] parts, List<int> vertexIndices,
            List<int> normalIndices, List<int> uvIndices)
        {
            if (parts.Length < 4) return; // Need at least 3 vertices for a face

            // Parse all vertices in the face
            var faceVertexIndices = new List<int>();
            var faceNormalIndices = new List<int>();
            var faceUVIndices = new List<int>();

            for (int i = 1; i < parts.Length; i++)
            {
                string[] indices = parts[i].Split('/');

                // Vertex index (required, 1-based in OBJ)
                if (int.TryParse(indices[0], out int vIndex))
                {
                    faceVertexIndices.Add(vIndex - 1); // Convert to 0-based
                }

                // UV index (optional)
                if (indices.Length > 1 && !string.IsNullOrEmpty(indices[1]))
                {
                    if (int.TryParse(indices[1], out int vtIndex))
                    {
                        faceUVIndices.Add(vtIndex - 1);
                    }
                }

                // Normal index (optional)
                if (indices.Length > 2 && !string.IsNullOrEmpty(indices[2]))
                {
                    if (int.TryParse(indices[2], out int vnIndex))
                    {
                        faceNormalIndices.Add(vnIndex - 1);
                    }
                }
            }

            // Triangulate face (fan triangulation)
            for (int i = 1; i < faceVertexIndices.Count - 1; i++)
            {
                vertexIndices.Add(faceVertexIndices[0]);
                vertexIndices.Add(faceVertexIndices[i]);
                vertexIndices.Add(faceVertexIndices[i + 1]);

                if (faceNormalIndices.Count == faceVertexIndices.Count)
                {
                    normalIndices.Add(faceNormalIndices[0]);
                    normalIndices.Add(faceNormalIndices[i]);
                    normalIndices.Add(faceNormalIndices[i + 1]);
                }

                if (faceUVIndices.Count == faceVertexIndices.Count)
                {
                    uvIndices.Add(faceUVIndices[0]);
                    uvIndices.Add(faceUVIndices[i]);
                    uvIndices.Add(faceUVIndices[i + 1]);
                }
            }
        }

        /// <summary>
        /// Create a Unity mesh from OBJ data. NO VERTEX LIMITS - uses 32-bit indices when needed.
        /// </summary>
        private static Mesh CreateMesh(string name, List<Vector3> globalVertices,
            List<Vector3> globalNormals, List<Vector2> globalUVs,
            List<int> vertexIndices, List<int> normalIndices, List<int> uvIndices)
        {
            if (vertexIndices.Count == 0 || vertexIndices.Count % 3 != 0)
                return null;

            var mesh = new Mesh();
            mesh.name = name;

            // ALWAYS use 32-bit indices to remove any limits
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            // Build mesh data - need to expand indexed data to per-vertex data
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            bool hasNormals = normalIndices.Count == vertexIndices.Count;
            bool hasUVs = uvIndices.Count == vertexIndices.Count;

            // Build unique vertex list with proper indexing
            var vertexMap = new Dictionary<string, int>();

            for (int i = 0; i < vertexIndices.Count; i++)
            {
                int vIdx = vertexIndices[i];
                int nIdx = hasNormals ? normalIndices[i] : -1;
                int tIdx = hasUVs ? uvIndices[i] : -1;

                // Create unique key for this vertex combination
                string key = $"{vIdx}_{nIdx}_{tIdx}";

                if (!vertexMap.TryGetValue(key, out int meshIndex))
                {
                    meshIndex = vertices.Count;
                    vertexMap[key] = meshIndex;

                    // Add vertex - preserve world position exactly
                    if (vIdx >= 0 && vIdx < globalVertices.Count)
                    {
                        vertices.Add(globalVertices[vIdx]);
                    }
                    else
                    {
                        vertices.Add(Vector3.zero);
                    }

                    // Add normal
                    if (hasNormals && nIdx >= 0 && nIdx < globalNormals.Count)
                    {
                        normals.Add(globalNormals[nIdx]);
                    }

                    // Add UV
                    if (hasUVs && tIdx >= 0 && tIdx < globalUVs.Count)
                    {
                        uvs.Add(globalUVs[tIdx]);
                    }
                }

                triangles.Add(meshIndex);
            }

            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);

            if (normals.Count == vertices.Count)
            {
                mesh.SetNormals(normals);
            }
            else
            {
                mesh.RecalculateNormals();
            }

            if (uvs.Count == vertices.Count)
            {
                mesh.SetUVs(0, uvs);
            }
            else
            {
                // Generate simple planar UVs
                GeneratePlanarUVs(mesh, vertices);
            }

            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            return mesh;
        }

        /// <summary>
        /// Generate simple planar UVs for a mesh.
        /// </summary>
        private static void GeneratePlanarUVs(Mesh mesh, List<Vector3> vertices)
        {
            if (vertices.Count == 0) return;

            // Find bounds
            var bounds = new Bounds(vertices[0], Vector3.zero);
            foreach (var v in vertices)
            {
                bounds.Encapsulate(v);
            }

            var uvs = new Vector2[vertices.Count];
            var size = bounds.size;
            var min = bounds.min;

            // Use the two largest dimensions for UV mapping
            for (int i = 0; i < vertices.Count; i++)
            {
                var v = vertices[i];
                float u = size.x > 0.001f ? (v.x - min.x) / size.x : 0;
                float vCoord = size.z > 0.001f ? (v.z - min.z) / size.z : 0;
                uvs[i] = new Vector2(u, vCoord);
            }

            mesh.uv = uvs;
        }

        /// <summary>
        /// Parse float with invariant culture.
        /// </summary>
        private static float ParseFloat(string value)
        {
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
            {
                return result;
            }
            return 0f;
        }

        /// <summary>
        /// Load materials from an MTL file.
        /// </summary>
        public static Dictionary<string, Material> LoadMtlFile(string filePath, Shader shader = null)
        {
            var materials = new Dictionary<string, Material>();

            if (!File.Exists(filePath))
            {
                Debug.LogWarning($"MTL file not found: {filePath}");
                return materials;
            }

            if (shader == null)
            {
                // Try URP Lit first, then Standard as fallback
                shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null)
                {
                    shader = Shader.Find("Standard");
                }
            }

            try
            {
                var lines = File.ReadAllLines(filePath);
                string currentMaterialName = null;
                Material currentMaterial = null;
                Color diffuseColor = Color.white;
                float alpha = 1f;

                foreach (var line in lines)
                {
                    string trimmedLine = line.Trim();
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
                        continue;

                    string[] parts = trimmedLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    switch (parts[0].ToLower())
                    {
                        case "newmtl":
                            // Save previous material
                            if (currentMaterial != null && !string.IsNullOrEmpty(currentMaterialName))
                            {
                                materials[currentMaterialName] = currentMaterial;
                            }

                            // Start new material
                            currentMaterialName = parts.Length > 1 ? parts[1] : $"Material_{materials.Count}";
                            currentMaterial = new Material(shader);
                            currentMaterial.name = currentMaterialName;
                            diffuseColor = Color.white;
                            alpha = 1f;
                            break;

                        case "kd": // Diffuse color
                            if (parts.Length >= 4 && currentMaterial != null)
                            {
                                float r = ParseFloat(parts[1]);
                                float g = ParseFloat(parts[2]);
                                float b = ParseFloat(parts[3]);
                                diffuseColor = new Color(r, g, b, alpha);
                                // Set color for both URP and Standard shaders
                                SetMaterialColor(currentMaterial, diffuseColor);
                            }
                            break;

                        case "d": // Dissolve (transparency)
                        case "tr": // Transparency
                            if (parts.Length >= 2 && currentMaterial != null)
                            {
                                alpha = parts[0].ToLower() == "d" ? ParseFloat(parts[1]) : 1f - ParseFloat(parts[1]);
                                diffuseColor.a = alpha;
                                SetMaterialColor(currentMaterial, diffuseColor);

                                // Enable transparency if needed
                                if (alpha < 1f)
                                {
                                    SetMaterialTransparent(currentMaterial);
                                }
                            }
                            break;

                        case "ks": // Specular color
                            if (parts.Length >= 4 && currentMaterial != null)
                            {
                                float sr = ParseFloat(parts[1]);
                                float sg = ParseFloat(parts[2]);
                                float sb = ParseFloat(parts[3]);
                                if (currentMaterial.HasProperty("_SpecColor"))
                                {
                                    currentMaterial.SetColor("_SpecColor", new Color(sr, sg, sb));
                                }
                            }
                            break;

                        case "ns": // Shininess
                            if (parts.Length >= 2 && currentMaterial != null)
                            {
                                float shininess = ParseFloat(parts[1]);
                                float smoothness = Mathf.Clamp01(shininess / 1000f);
                                // URP uses _Smoothness
                                if (currentMaterial.HasProperty("_Smoothness"))
                                {
                                    currentMaterial.SetFloat("_Smoothness", smoothness);
                                }
                                // Standard uses _Glossiness
                                if (currentMaterial.HasProperty("_Glossiness"))
                                {
                                    currentMaterial.SetFloat("_Glossiness", smoothness);
                                }
                            }
                            break;
                    }
                }

                // Save last material
                if (currentMaterial != null && !string.IsNullOrEmpty(currentMaterialName))
                {
                    materials[currentMaterialName] = currentMaterial;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error loading MTL file: {e.Message}");
            }

            return materials;
        }

        /// <summary>
        /// Set material color (supports both URP and Standard shaders).
        /// </summary>
        private static void SetMaterialColor(Material material, Color color)
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
        /// Set material to transparent rendering mode (supports both URP and Standard).
        /// </summary>
        private static void SetMaterialTransparent(Material material)
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

        #region Multi-Material Support

        /// <summary>
        /// Data structure for a mesh with multiple submeshes (multi-material support).
        /// </summary>
        public class MultiMaterialMeshData
        {
            public Mesh Mesh;
            public List<string> MaterialNames = new List<string>();

            public int SubmeshCount => MaterialNames.Count;
            public bool HasMultipleMaterials => MaterialNames.Count > 1;
        }

        /// <summary>
        /// Load OBJ file with multi-material submesh support.
        /// Groups (g) within an object (o) are treated as submeshes with different materials.
        /// </summary>
        public static Dictionary<string, MultiMaterialMeshData> LoadObjFileMultiMaterial(
            string filePath, Action<float, string> progressCallback = null)
        {
            var meshes = new Dictionary<string, MultiMaterialMeshData>();

            if (!File.Exists(filePath))
            {
                Debug.LogError($"OBJ file not found: {filePath}");
                return meshes;
            }

            try
            {
                var lines = File.ReadAllLines(filePath);
                int totalLines = lines.Length;
                Debug.Log($"[OBJ Loader] Reading {totalLines} lines with multi-material support from: {filePath}");

                // Global vertex/normal/uv lists
                var globalVertices = new List<Vector3>();
                var globalNormals = new List<Vector3>();
                var globalUVs = new List<Vector2>();

                // Current object being built
                string currentObjectName = null;
                var currentSubmeshes = new List<SubmeshData>();
                string currentMaterial = "DefaultMaterial";
                SubmeshData currentSubmesh = null;

                // First pass: parse all data
                for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    string line = lines[lineIndex].Trim();

                    if (lineIndex % 5000 == 0)
                    {
                        float progress = (float)lineIndex / totalLines * 0.5f;
                        progressCallback?.Invoke(progress, $"Parsing OBJ: {lineIndex}/{totalLines} lines");
                    }

                    if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                        continue;

                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    switch (parts[0])
                    {
                        case "v": // Vertex
                            if (parts.Length >= 4)
                            {
                                globalVertices.Add(new Vector3(
                                    ParseFloat(parts[1]),
                                    ParseFloat(parts[2]),
                                    ParseFloat(parts[3])));
                            }
                            break;

                        case "vn": // Normal
                            if (parts.Length >= 4)
                            {
                                globalNormals.Add(new Vector3(
                                    ParseFloat(parts[1]),
                                    ParseFloat(parts[2]),
                                    ParseFloat(parts[3])));
                            }
                            break;

                        case "vt": // UV
                            if (parts.Length >= 3)
                            {
                                globalUVs.Add(new Vector2(
                                    ParseFloat(parts[1]),
                                    ParseFloat(parts[2])));
                            }
                            break;

                        case "o": // New object
                            // Save previous object - check if we have any submeshes OR a current submesh with faces
                            bool hasPendingSubmesh = currentSubmesh != null && currentSubmesh.FaceIndices.Count > 0;
                            if (!string.IsNullOrEmpty(currentObjectName) && (currentSubmeshes.Count > 0 || hasPendingSubmesh))
                            {
                                // Ensure current submesh is added
                                if (hasPendingSubmesh)
                                {
                                    currentSubmeshes.Add(currentSubmesh);
                                }

                                var meshData = CreateMultiMaterialMesh(
                                    currentObjectName, globalVertices, globalNormals, globalUVs, currentSubmeshes);
                                if (meshData != null && !meshes.ContainsKey(currentObjectName))
                                {
                                    meshes[currentObjectName] = meshData;
                                }
                            }

                            // Start new object
                            currentObjectName = parts.Length > 1 ? parts[1] : $"Object_{meshes.Count}";
                            currentSubmeshes = new List<SubmeshData>();
                            currentSubmesh = null;
                            currentMaterial = "DefaultMaterial";
                            break;

                        case "g": // Group (submesh within object)
                            // Save current submesh if it has faces
                            if (currentSubmesh != null && currentSubmesh.FaceIndices.Count > 0)
                            {
                                currentSubmeshes.Add(currentSubmesh);
                            }
                            // Start new submesh (will be assigned material on next usemtl)
                            currentSubmesh = new SubmeshData { MaterialName = currentMaterial };
                            break;

                        case "usemtl": // Material assignment
                            currentMaterial = parts.Length > 1 ? parts[1] : "DefaultMaterial";

                            // If we have a current submesh with faces and different material, save it
                            if (currentSubmesh != null && currentSubmesh.FaceIndices.Count > 0
                                && currentSubmesh.MaterialName != currentMaterial)
                            {
                                currentSubmeshes.Add(currentSubmesh);
                                currentSubmesh = new SubmeshData { MaterialName = currentMaterial };
                            }
                            else if (currentSubmesh == null)
                            {
                                currentSubmesh = new SubmeshData { MaterialName = currentMaterial };
                            }
                            else
                            {
                                currentSubmesh.MaterialName = currentMaterial;
                            }
                            break;

                        case "f": // Face
                            if (currentSubmesh == null)
                            {
                                currentSubmesh = new SubmeshData { MaterialName = currentMaterial };
                            }
                            ParseFaceMultiMaterial(parts, currentSubmesh);
                            break;
                    }
                }

                // Save last object
                if (!string.IsNullOrEmpty(currentObjectName))
                {
                    if (currentSubmesh != null && currentSubmesh.FaceIndices.Count > 0)
                    {
                        currentSubmeshes.Add(currentSubmesh);
                    }

                    if (currentSubmeshes.Count > 0)
                    {
                        var meshData = CreateMultiMaterialMesh(
                            currentObjectName, globalVertices, globalNormals, globalUVs, currentSubmeshes);
                        if (meshData != null && !meshes.ContainsKey(currentObjectName))
                        {
                            meshes[currentObjectName] = meshData;
                        }
                    }
                }

                // Count multi-material meshes
                int multiMatCount = 0;
                foreach (var kvp in meshes)
                {
                    if (kvp.Value.HasMultipleMaterials)
                        multiMatCount++;
                }

                Debug.Log($"[OBJ Loader] Loaded {meshes.Count} meshes ({multiMatCount} with multiple materials)");
                progressCallback?.Invoke(1f, $"Loaded {meshes.Count} meshes");
            }
            catch (Exception e)
            {
                Debug.LogError($"Error loading OBJ file: {e.Message}\n{e.StackTrace}");
            }

            return meshes;
        }

        /// <summary>
        /// Data for a single submesh within an object.
        /// </summary>
        private class SubmeshData
        {
            public string MaterialName;
            public List<FaceData> FaceIndices = new List<FaceData>();
        }

        /// <summary>
        /// Face index data.
        /// </summary>
        private struct FaceData
        {
            public int VertexIndex;
            public int NormalIndex;
            public int UVIndex;
        }

        /// <summary>
        /// Parse face into submesh data.
        /// </summary>
        private static void ParseFaceMultiMaterial(string[] parts, SubmeshData submesh)
        {
            if (parts.Length < 4) return;

            var faceVertices = new List<FaceData>();

            for (int i = 1; i < parts.Length; i++)
            {
                string[] indices = parts[i].Split('/');
                var face = new FaceData
                {
                    VertexIndex = -1,
                    NormalIndex = -1,
                    UVIndex = -1
                };

                if (int.TryParse(indices[0], out int vIdx))
                    face.VertexIndex = vIdx - 1;

                if (indices.Length > 1 && !string.IsNullOrEmpty(indices[1]))
                    if (int.TryParse(indices[1], out int vtIdx))
                        face.UVIndex = vtIdx - 1;

                if (indices.Length > 2 && !string.IsNullOrEmpty(indices[2]))
                    if (int.TryParse(indices[2], out int vnIdx))
                        face.NormalIndex = vnIdx - 1;

                faceVertices.Add(face);
            }

            // Triangulate (fan)
            for (int i = 1; i < faceVertices.Count - 1; i++)
            {
                submesh.FaceIndices.Add(faceVertices[0]);
                submesh.FaceIndices.Add(faceVertices[i]);
                submesh.FaceIndices.Add(faceVertices[i + 1]);
            }
        }

        /// <summary>
        /// Create a mesh with multiple submeshes from parsed data.
        /// </summary>
        private static MultiMaterialMeshData CreateMultiMaterialMesh(
            string name,
            List<Vector3> globalVertices,
            List<Vector3> globalNormals,
            List<Vector2> globalUVs,
            List<SubmeshData> submeshes)
        {
            if (submeshes.Count == 0)
                return null;

            var result = new MultiMaterialMeshData();
            var mesh = new Mesh();
            mesh.name = name;
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            // Build unified vertex buffer
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var vertexMap = new Dictionary<string, int>();

            // Submesh triangle lists
            var submeshTriangles = new List<List<int>>();

            bool hasNormals = globalNormals.Count > 0;
            bool hasUVs = globalUVs.Count > 0;

            foreach (var submesh in submeshes)
            {
                var triangles = new List<int>();
                result.MaterialNames.Add(submesh.MaterialName);

                foreach (var face in submesh.FaceIndices)
                {
                    string key = $"{face.VertexIndex}_{face.NormalIndex}_{face.UVIndex}";

                    if (!vertexMap.TryGetValue(key, out int meshIndex))
                    {
                        meshIndex = vertices.Count;
                        vertexMap[key] = meshIndex;

                        // Vertex
                        if (face.VertexIndex >= 0 && face.VertexIndex < globalVertices.Count)
                            vertices.Add(globalVertices[face.VertexIndex]);
                        else
                            vertices.Add(Vector3.zero);

                        // Normal
                        if (hasNormals && face.NormalIndex >= 0 && face.NormalIndex < globalNormals.Count)
                            normals.Add(globalNormals[face.NormalIndex]);

                        // UV
                        if (hasUVs && face.UVIndex >= 0 && face.UVIndex < globalUVs.Count)
                            uvs.Add(globalUVs[face.UVIndex]);
                    }

                    triangles.Add(meshIndex);
                }

                submeshTriangles.Add(triangles);
            }

            // Set mesh data
            mesh.SetVertices(vertices);

            if (normals.Count == vertices.Count)
                mesh.SetNormals(normals);

            if (uvs.Count == vertices.Count)
                mesh.SetUVs(0, uvs);

            // Set submeshes
            mesh.subMeshCount = submeshTriangles.Count;
            for (int i = 0; i < submeshTriangles.Count; i++)
            {
                mesh.SetTriangles(submeshTriangles[i], i);
            }

            // Finalize
            if (normals.Count != vertices.Count)
                mesh.RecalculateNormals();

            if (uvs.Count != vertices.Count)
                GeneratePlanarUVs(mesh, vertices);

            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            result.Mesh = mesh;
            return result;
        }

        #endregion
    }
}
