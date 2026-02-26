using System;
using UnityEngine;

namespace IFCImporter
{
    // ════════════════════════════════════════════════════════════════
    //  Source-confidence annotations for IfcMetadata properties.
    //  Indicates whether each value was derived from a standard
    //  IFC attribute, a Pset/Qto, or an authoring-tool heuristic.
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Describes the provenance source of a metadata value.
    /// </summary>
    public enum IfcSourceType
    {
        /// <summary>Value from a standard IFC schema attribute (e.g., GlobalId, Name, IfcClass).</summary>
        StandardAttribute,

        /// <summary>Value from an IfcPropertySet property (Pset_*).</summary>
        PropertySet,

        /// <summary>Value from an IfcElementQuantity set (Qto_*).</summary>
        QuantitySet,

        /// <summary>Value from an IfcTypeObject relationship (TypeName, TypeId).</summary>
        TypeObject,

        /// <summary>Value derived from spatial hierarchy traversal.</summary>
        SpatialHierarchy,

        /// <summary>Value from IfcRelAssociatesMaterial or styled items.</summary>
        MaterialAssociation,

        /// <summary>Value computed by the importer heuristic (discipline classification, mesh bounds fallback, etc.).</summary>
        ImporterHeuristic,

        /// <summary>Value derived from mesh geometry (e.g., bounds-based door dimensions).</summary>
        GeometryDerived,

        /// <summary>Source is unknown or the field is a default/placeholder.</summary>
        Unknown
    }

    /// <summary>
    /// Confidence level for a metadata value.
    /// </summary>
    public enum IfcConfidenceLevel
    {
        /// <summary>Directly from IFC schema — highest reliability.</summary>
        High,

        /// <summary>From a well-defined Pset/Qto or type relationship.</summary>
        Medium,

        /// <summary>Computed by heuristic, mesh bounds, or classification rule.</summary>
        Low,

        /// <summary>Fallback / default value — treat with caution.</summary>
        Fallback
    }

    /// <summary>
    /// Per-field annotation describing the provenance and confidence of a metadata value.
    /// </summary>
    [Serializable]
    public struct IfcSourceAnnotation
    {
        [Tooltip("The provenance source of this value.")]
        public IfcSourceType Source;

        [Tooltip("Confidence level in this value.")]
        public IfcConfidenceLevel Confidence;

        [Tooltip("Optional note (e.g., 'Fallback from mesh bounds', 'Pset_WallCommon').")]
        public string Note;

        public IfcSourceAnnotation(IfcSourceType source, IfcConfidenceLevel confidence, string note = "")
        {
            Source = source;
            Confidence = confidence;
            Note = note ?? "";
        }

        /// <summary>Standard IFC attribute — high confidence.</summary>
        public static IfcSourceAnnotation Standard(string note = "")
            => new IfcSourceAnnotation(IfcSourceType.StandardAttribute, IfcConfidenceLevel.High, note);

        /// <summary>PropertySet value — medium confidence.</summary>
        public static IfcSourceAnnotation Pset(string psetName = "")
            => new IfcSourceAnnotation(IfcSourceType.PropertySet, IfcConfidenceLevel.Medium, psetName);

        /// <summary>QuantitySet value — medium confidence.</summary>
        public static IfcSourceAnnotation Qto(string qtoName = "")
            => new IfcSourceAnnotation(IfcSourceType.QuantitySet, IfcConfidenceLevel.Medium, qtoName);

        /// <summary>Type object — medium confidence.</summary>
        public static IfcSourceAnnotation TypeObj(string note = "")
            => new IfcSourceAnnotation(IfcSourceType.TypeObject, IfcConfidenceLevel.Medium, note);

        /// <summary>Spatial hierarchy traversal — medium confidence.</summary>
        public static IfcSourceAnnotation Spatial(string note = "")
            => new IfcSourceAnnotation(IfcSourceType.SpatialHierarchy, IfcConfidenceLevel.Medium, note);

        /// <summary>Material association — medium confidence.</summary>
        public static IfcSourceAnnotation Material(string note = "")
            => new IfcSourceAnnotation(IfcSourceType.MaterialAssociation, IfcConfidenceLevel.Medium, note);

        /// <summary>Importer heuristic — low confidence.</summary>
        public static IfcSourceAnnotation Heuristic(string note = "")
            => new IfcSourceAnnotation(IfcSourceType.ImporterHeuristic, IfcConfidenceLevel.Low, note);

        /// <summary>Geometry-derived — low confidence.</summary>
        public static IfcSourceAnnotation Geometry(string note = "")
            => new IfcSourceAnnotation(IfcSourceType.GeometryDerived, IfcConfidenceLevel.Low, note);

        /// <summary>Unknown / default fallback.</summary>
        public static IfcSourceAnnotation Fallback(string note = "")
            => new IfcSourceAnnotation(IfcSourceType.Unknown, IfcConfidenceLevel.Fallback, note);

        public override string ToString()
        {
            string s = $"[{Source}/{Confidence}]";
            if (!string.IsNullOrEmpty(Note)) s += $" {Note}";
            return s;
        }
    }
}
