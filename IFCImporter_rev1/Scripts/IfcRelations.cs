using System;
using System.Collections.Generic;
using UnityEngine;

namespace IFCImporter
{
    /// <summary>
    /// MonoBehaviour that stores IFC relationship links on a GameObject.
    /// All IDs are IFC GlobalId strings that can be resolved via IfcRegistry.
    /// </summary>
    [AddComponentMenu("IFC Importer/IFC Relations")]
    public class IfcRelations : MonoBehaviour
    {
        [Header("Spatial Containment")]
        [Tooltip("GlobalId of the spatial container (storey / space / building).")]
        public string ContainedInStructureId;

        [Header("Voids (IfcRelVoidsElement)")]
        [Tooltip("GlobalId of host element that this opening element voids.")]
        public string HostElementId;

        [Tooltip("GlobalIds of opening elements that void this element.")]
        public List<string> OpeningElementIds = new List<string>();

        [Header("Fills (IfcRelFillsElement)")]
        [Tooltip("GlobalId of the opening element that this door/window fills.")]
        public string FillsOpeningId;

        [Tooltip("GlobalId of the door/window that fills this opening.")]
        public string FilledByElementId;

        [Header("Space Boundaries")]
        [Tooltip("GlobalIds of spaces that have boundary relationships with this element.")]
        public List<string> SpaceBoundarySpaceIds = new List<string>();

        [Tooltip("GlobalIds of elements that form the boundary of this space.")]
        public List<string> SpaceBoundaryElementIds = new List<string>();

        [Header("Aggregation")]
        [Tooltip("GlobalId of the parent aggregate object.")]
        public string AggregateParentId;

        [Tooltip("GlobalIds of child objects aggregated under this element.")]
        public List<string> AggregateChildIds = new List<string>();

        // ─── Convenience methods ───

        /// <summary>True if this element is an opening that voids a host.</summary>
        public bool IsOpening => !string.IsNullOrEmpty(HostElementId);

        /// <summary>True if a door/window fills this opening.</summary>
        public bool HasFilling => !string.IsNullOrEmpty(FilledByElementId);

        /// <summary>True if this door/window fills an opening.</summary>
        public bool FillsAnOpening => !string.IsNullOrEmpty(FillsOpeningId);

        /// <summary>True if this element has openings cut into it.</summary>
        public bool HasOpenings => OpeningElementIds != null && OpeningElementIds.Count > 0;
    }

    /// <summary>
    /// Lightweight "Passage" descriptor for doors / openings.
    /// Robotics path-planners use this to decide if a robot can traverse.
    /// </summary>
    [AddComponentMenu("IFC Importer/IFC Passage")]
    public class IfcPassage : MonoBehaviour
    {
        [Tooltip("Passage width in metres.")]
        public float Width;

        [Tooltip("Passage height in metres.")]
        public float Height;

        [Tooltip("GlobalId of the associated host wall / slab.")]
        public string HostWallId;

        [Tooltip("GlobalId of the associated door (if any).")]
        public string DoorId;

        [Tooltip("GlobalId of the opening element (if any).")]
        public string OpeningId;

        [Tooltip("Door operation type (SINGLE_SWING_LEFT, SLIDING, etc.).")]
        public string OperationType;
    }
}
