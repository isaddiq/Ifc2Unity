using System;
using System.Collections.Generic;
using UnityEngine;

namespace IFCImporter
{
    /// <summary>
    /// Represents an IFC element with all its metadata and geometry reference.
    /// </summary>
    [Serializable]
    public class IfcElementData
    {
        public string GlobalId;
        public string Name;
        public string IfcClass;
        public string TypeName;
        public string TypeId;
        public string Description;
        public string ObjectType;
        public string Tag;
        public string PredefinedType;
        public string LongName;

        // Spatial hierarchy
        public string Project;
        public string Site;
        public string Building;
        public string Storey;
        public string Space;

        // Material and color
        public string Material;
        public Color Color;

        // Door/window dimensions (metres, after unit conversion)
        public float OverallWidth;
        public float OverallHeight;
        public string OperationType;

        // Storey elevation (IFC units – scaled to metres later)
        public float Elevation;

        // Additional properties (Pset and Qto)
        public Dictionary<string, string> Properties = new Dictionary<string, string>();

        // Geometry reference
        public bool HasGeometry;
        public string MeshObjectName;

        /// <summary>
        /// Parse a CSV row into element data.
        /// </summary>
        public static IfcElementData FromCsvRow(Dictionary<string, string> row)
        {
            var data = new IfcElementData();

            // Basic identity
            data.GlobalId = GetValue(row, "GlobalId");
            data.Name = GetValue(row, "Name");
            data.IfcClass = GetValue(row, "IfcClass");
            data.TypeName = GetValue(row, "TypeName");
            data.TypeId = GetValue(row, "TypeId");
            data.Description = GetValue(row, "Description");
            data.ObjectType = GetValue(row, "ObjectType");
            data.Tag = GetValue(row, "Tag");
            data.PredefinedType = GetValue(row, "PredefinedType");
            data.LongName = GetValue(row, "LongName");

            // Spatial hierarchy
            data.Project = GetValue(row, "Project");
            data.Site = GetValue(row, "Site");
            data.Building = GetValue(row, "Building");
            data.Storey = GetValue(row, "Storey");
            data.Space = GetValue(row, "Space");

            // Material
            data.Material = GetValue(row, "Material");

            // Color
            float r = ParseFloat(GetValue(row, "Color_R"), 0.8f);
            float g = ParseFloat(GetValue(row, "Color_G"), 0.8f);
            float b = ParseFloat(GetValue(row, "Color_B"), 0.8f);
            float a = ParseFloat(GetValue(row, "Color_A"), 1.0f);
            data.Color = new Color(r, g, b, a);

            // Door/window dimensions
            data.OverallWidth = ParseFloat(GetValue(row, "OverallWidth"), 0f);
            data.OverallHeight = ParseFloat(GetValue(row, "OverallHeight"), 0f);
            data.OperationType = GetValue(row, "OperationType");

            // Storey elevation
            data.Elevation = ParseFloat(GetValue(row, "Elevation"), 0f);

            // Mesh reference (uses GlobalId)
            data.MeshObjectName = data.GlobalId;

            // Copy all properties
            foreach (var kvp in row)
            {
                if (!IsBaseProperty(kvp.Key))
                {
                    data.Properties[kvp.Key] = kvp.Value;
                }
            }

            return data;
        }

        private static string GetValue(Dictionary<string, string> row, string key)
        {
            return row.TryGetValue(key, out string value) ? value : string.Empty;
        }

        private static float ParseFloat(string value, float defaultValue)
        {
            if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float result))
            {
                return result;
            }
            return defaultValue;
        }

        private static bool IsBaseProperty(string key)
        {
            var baseProps = new HashSet<string>
            {
                "GlobalId", "Name", "IfcClass", "TypeName", "TypeId",
                "Description", "ObjectType", "Tag", "PredefinedType", "LongName",
                "Project", "Site", "Building", "Storey", "Space",
                "Material", "Color_R", "Color_G", "Color_B", "Color_A",
                "OverallWidth", "OverallHeight", "OperationType",
                "Elevation", "MaterialCount", "Materials"
            };
            return baseProps.Contains(key);
        }

        /// <summary>
        /// Check if this is a spatial hierarchy element that should be treated as a container.
        /// Spaces are NOT excluded as they may have geometry.
        /// </summary>
        public bool IsSpatialElement()
        {
            return IfcClass == "IfcProject" || IfcClass == "IfcSite" ||
                   IfcClass == "IfcBuilding" || IfcClass == "IfcBuildingStorey";
        }
    }

    /// <summary>
    /// Represents the spatial hierarchy structure from IFC.
    /// </summary>
    [Serializable]
    public class IfcSpatialHierarchy
    {
        public string ProjectName;
        public List<IfcSiteNode> Sites = new List<IfcSiteNode>();
    }

    [Serializable]
    public class IfcSiteNode
    {
        public string GlobalId;
        public string Name;
        public List<IfcBuildingNode> Buildings = new List<IfcBuildingNode>();
    }

    [Serializable]
    public class IfcBuildingNode
    {
        public string GlobalId;
        public string Name;
        public List<IfcStoreyNode> Storeys = new List<IfcStoreyNode>();
    }

    [Serializable]
    public class IfcStoreyNode
    {
        public string GlobalId;
        public string Name;
        public float Elevation;
        public List<string> ElementIds = new List<string>(); // GlobalIds of elements on this storey
    }

    /// <summary>
    /// Import statistics and performance metrics.
    /// </summary>
    [Serializable]
    public class IfcImportStatistics
    {
        public string IfcFilePath;
        public string IfcFileName;
        public DateTime ImportStartTime;
        public DateTime ImportEndTime;

        // Timing
        public double PythonConversionTimeSeconds;
        public double CsvParseTimeSeconds;
        public double MeshLoadTimeSeconds;
        public double SceneBuildTimeSeconds;
        public double TotalTimeSeconds;

        // Element counts
        public int CsvElementCount;
        public int ObjMeshCount;
        public int SuccessfullyImported;
        public int ElementsWithGeometry;
        public int LogicalNodes;
        public int MeshesNotFound;
        public int ElementsWithColor;
        public int FailedToImport;

        // Hierarchy counts
        public int SitesCreated;
        public int BuildingsCreated;
        public int StoreysCreated;
        public int TotalHierarchyNodes;
        public int MaterialsCreated;

        // Memory
        public long MemoryAtStartBytes;
        public long MemoryAtEndBytes;
        public long PeakMemoryBytes;

        // IFC class breakdown
        public Dictionary<string, int> ElementsByClass = new Dictionary<string, int>();
        public Dictionary<string, int> ElementsByStorey = new Dictionary<string, int>();

        // Calculated properties
        public double MeshMatchRate => ObjMeshCount > 0 ?
            (double)ElementsWithGeometry / ObjMeshCount * 100 : 0;

        public double DataLossPercentage => CsvElementCount > 0 ?
            (double)(CsvElementCount - SuccessfullyImported) / CsvElementCount * 100 : 0;

        public double ProcessingSpeed => TotalTimeSeconds > 0 ?
            SuccessfullyImported / TotalTimeSeconds : 0;

        public long NetMemoryChange => MemoryAtEndBytes - MemoryAtStartBytes;

        public double MemoryPerElementKB => SuccessfullyImported > 0 ?
            (double)NetMemoryChange / SuccessfullyImported / 1024 : 0;
    }

    /// <summary>
    /// Configuration options for the IFC import process.
    /// </summary>
    [Serializable]
    public class IfcImportOptions
    {
        public string PythonExecutable = "python";
        public bool CreateHierarchy = true;
        public bool AssignMetadata = true;
        public bool ApplyMaterials = true;
        public bool ApplyColors = true;
        public bool CreateColliders = false;
        public bool OptimizeMeshes = true;
        public bool GenerateUVs = true;
        public float ScaleFactor = 1.0f;
        public bool SaveAssets = true;
        public string AssetSavePath = "Assets/ImportedIFC";

        // Hierarchy options
        public bool GroupByStorey = true;
        public bool GroupByClass = true;
        public bool CreateEmptyForLogicalNodes = true;

        // Material options
        public bool UseStandardShader = true;
        public string DefaultShaderName = "Universal Render Pipeline/Lit";
        public bool EnableTransparency = true;

        // ─── Robotics options ───
        public bool EnableRoboticsMode = true;
        public bool AssignLayers = true;
        public bool SimplifyColliders = true;
        public bool PrepareNavMesh = false;
        public bool CreateSemanticSpaces = true;
        public bool CreateOpenings = true;
        public string SourceFileReference = "";

        // ─── Incremental import options ───
        /// <summary>
        /// When true, compares element fingerprints with a previous manifest
        /// and only re-imports added/modified elements.  Deleted elements are
        /// removed from the scene automatically.
        /// </summary>
        public bool IncrementalImport = false;
    }

    // ════════════════════════════════════════════════════════════
    //  Relationship data models (loaded from _relationships.json)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// A void relationship: host wall/slab → opening element.
    /// </summary>
    [Serializable]
    public class IfcRelVoidData
    {
        public string RelatingBuildingElement;
        public string RelatedOpeningElement;
    }

    /// <summary>
    /// A fill relationship: opening → door/window.
    /// </summary>
    [Serializable]
    public class IfcRelFillData
    {
        public string RelatingOpeningElement;
        public string RelatedBuildingElement;
    }

    /// <summary>
    /// Spatial containment: structure → element.
    /// </summary>
    [Serializable]
    public class IfcRelContainmentData
    {
        public string RelatingStructure;
        public string RelatedElement;
    }

    /// <summary>
    /// Space boundary relationship.
    /// </summary>
    [Serializable]
    public class IfcRelSpaceBoundaryData
    {
        public string RelatingSpace;
        public string RelatedBuildingElement;
        public string PhysicalOrVirtualBoundary;
        public string InternalOrExternalBoundary;
    }

    /// <summary>
    /// Aggregation relationship.
    /// </summary>
    [Serializable]
    public class IfcRelAggregateData
    {
        public string RelatingObject;
        public string RelatedObject;
    }

    /// <summary>
    /// Container for all parsed relationship data from JSON.
    /// </summary>
    [Serializable]
    public class IfcRelationshipBundle
    {
        public List<IfcRelVoidData> Voids = new List<IfcRelVoidData>();
        public List<IfcRelFillData> Fills = new List<IfcRelFillData>();
        public List<IfcRelContainmentData> Containments = new List<IfcRelContainmentData>();
        public List<IfcRelSpaceBoundaryData> SpaceBoundaries = new List<IfcRelSpaceBoundaryData>();
        public List<IfcRelAggregateData> Aggregates = new List<IfcRelAggregateData>();
        public float UnitScaleToMetres = 1f;
        public string Schema = "";
    }
}
