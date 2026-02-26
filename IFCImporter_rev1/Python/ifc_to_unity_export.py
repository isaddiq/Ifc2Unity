#!/usr/bin/env python3
"""
IFC to Unity Export Script (BIMUniXchange)
Converts IFC files to OBJ meshes with metadata using IfcOpenShell

This script processes IFC files and exports:
- Individual OBJ files for each IFC element
- MTL material file with colors
- CSV metadata file with all IFC properties and hierarchy information

Usage:
    python ifc_to_unity_export.py <ifc_file_path> [output_directory]
"""

import os
import sys
import csv
import time
import json
import shutil
import tempfile
import traceback
from datetime import datetime
from pathlib import Path

# Fix encoding issues on Windows console
if sys.platform == 'win32':
    # Set UTF-8 encoding for stdout/stderr to handle Unicode characters
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')

try:
    import ifcopenshell
    import ifcopenshell.geom
    import ifcopenshell.util.element
    import ifcopenshell.util.placement
    import ifcopenshell.util.shape
except ImportError:
    print("ERROR: IfcOpenShell is required. Install with: pip install ifcopenshell")
    sys.exit(1)

# ============================================================
# Configuration
# ============================================================

# No IFC type filtering — every entity found in the file is exported.
# Elements without 3-D geometry are still included as semantic-only entries
# (metadata row in CSV, no OBJ geometry).  This covers annotations, grids,
# 2-D drawing content, distribution ports, virtual elements, etc.

# Geometry settings - optimized for accurate geometry
GEOMETRY_SETTINGS = {
    "USE_WORLD_COORDS": True,           # CRITICAL: Keep elements at their world position
    "DISABLE_OPENING_SUBTRACTIONS": False,  # Enable proper opening cuts (doors/windows)
    "INCLUDE_CURVES": False,            # Only polygonal geometry
    "WELD_VERTICES": False,             # Disable welding for better triangulation accuracy
    "APPLY_DEFAULT_MATERIALS": True,    # Apply material colors
}

# ============================================================
# Helper Classes
# ============================================================

class ProgressTracker:
    """Track and report progress."""
    def __init__(self, total, description="Processing"):
        self.total = total
        self.current = 0
        self.description = description
        self.start_time = time.time()
        
    def update(self, step=1):
        self.current += step
        
    def report(self, interval=100):
        if self.current % interval == 0 or self.current == self.total:
            elapsed = time.time() - self.start_time
            percent = (self.current / self.total) * 100 if self.total > 0 else 0
            print(f"  {self.description}: {self.current}/{self.total} ({percent:.1f}%)")


class MemoryTracker:
    """Track memory usage."""
    def __init__(self):
        try:
            import psutil
            self.process = psutil.Process()
            self.has_psutil = True
        except ImportError:
            self.has_psutil = False
            
    def get_memory_mb(self):
        if self.has_psutil:
            return self.process.memory_info().rss / (1024 * 1024)
        return 0
        
    def report(self, label=""):
        if self.has_psutil:
            mem = self.get_memory_mb()
            print(f"  [Memory: {mem:.1f} MB] {label}")


class MaterialManager:
    """Manage materials and colors from IFC."""
    def __init__(self):
        self.materials = {}  # name -> (r, g, b, a)
        self.default_color = (0.8, 0.8, 0.8, 1.0)
        self._style_cache = {}  # Cache for element styles
        
    def get_material_from_element(self, element, ifc_file):
        """Extract material/color from IFC element."""
        try:
            # Method 1: Try to get color from element's styled item (most reliable for Revit exports)
            color = self._get_color_from_styled_item(element, ifc_file)
            if color:
                mat_name = self._get_material_name(element, ifc_file)
                return (mat_name, color)
            
            # Method 2: Try to get material from element associations
            if hasattr(element, 'HasAssociations'):
                for association in element.HasAssociations:
                    if association.is_a('IfcRelAssociatesMaterial'):
                        material = association.RelatingMaterial
                        result = self._extract_material_data(material, ifc_file)
                        if result:
                            return result
            
            # Method 3: Try to get from element type
            element_type = ifcopenshell.util.element.get_type(element)
            if element_type:
                # Check type's styled items
                color = self._get_color_from_styled_item(element_type, ifc_file)
                if color:
                    mat_name = self._get_material_name(element_type, ifc_file) or self._get_material_name(element, ifc_file)
                    return (mat_name, color)
                    
                # Check type's material associations
                if hasattr(element_type, 'HasAssociations'):
                    for association in element_type.HasAssociations:
                        if association.is_a('IfcRelAssociatesMaterial'):
                            material = association.RelatingMaterial
                            result = self._extract_material_data(material, ifc_file)
                            if result:
                                return result
                        
        except Exception as e:
            pass
            
        return None
    
    def _get_material_name(self, element, ifc_file):
        """Get material name from element."""
        try:
            if hasattr(element, 'HasAssociations'):
                for association in element.HasAssociations:
                    if association.is_a('IfcRelAssociatesMaterial'):
                        material = association.RelatingMaterial
                        if hasattr(material, 'Name') and material.Name:
                            return material.Name
                        if material.is_a('IfcMaterialLayerSetUsage'):
                            if material.ForLayerSet and material.ForLayerSet.MaterialLayers:
                                mat = material.ForLayerSet.MaterialLayers[0].Material
                                if mat and mat.Name:
                                    return mat.Name
                        elif material.is_a('IfcMaterialLayerSet'):
                            if material.MaterialLayers:
                                mat = material.MaterialLayers[0].Material
                                if mat and mat.Name:
                                    return mat.Name
                        elif material.is_a('IfcMaterialConstituentSet'):
                            if material.MaterialConstituents:
                                mat = material.MaterialConstituents[0].Material
                                if mat and mat.Name:
                                    return mat.Name
                        elif material.is_a('IfcMaterialList'):
                            if material.Materials:
                                mat = material.Materials[0]
                                if mat and mat.Name:
                                    return mat.Name
        except Exception:
            pass
        return element.is_a()
    
    def _get_color_from_styled_item(self, element, ifc_file):
        """Get color from element's representation styled items."""
        try:
            if not element.Representation:
                return None
                
            for rep in element.Representation.Representations:
                # Check items for styled representations
                if hasattr(rep, 'Items'):
                    for item in rep.Items:
                        color = self._extract_color_from_item(item, ifc_file)
                        if color:
                            return color
                        
                        # Check mapped items
                        if item.is_a('IfcMappedItem'):
                            if hasattr(item, 'MappingSource') and item.MappingSource:
                                mapped_rep = item.MappingSource.MappedRepresentation
                                if hasattr(mapped_rep, 'Items'):
                                    for mapped_item in mapped_rep.Items:
                                        color = self._extract_color_from_item(mapped_item, ifc_file)
                                        if color:
                                            return color
        except Exception:
            pass
        return None
    
    def _extract_color_from_item(self, item, ifc_file):
        """Extract color from a representation item."""
        try:
            # Check if item has styles directly
            if hasattr(item, 'StyledByItem') and item.StyledByItem:
                for styled in item.StyledByItem:
                    if hasattr(styled, 'Styles'):
                        for style in styled.Styles:
                            color = self._extract_color_from_presentation_style(style)
                            if color:
                                return color
            
            # For Breps and other solid geometry, check styled item associations
            styled_items = ifc_file.by_type('IfcStyledItem')
            for styled_item in styled_items:
                if styled_item.Item == item:
                    if hasattr(styled_item, 'Styles'):
                        for style in styled_item.Styles:
                            color = self._extract_color_from_presentation_style(style)
                            if color:
                                return color
        except Exception:
            pass
        return None
    
    def _extract_color_from_presentation_style(self, style):
        """Extract color from presentation style."""
        try:
            # Handle IfcPresentationStyleAssignment (IFC2X3)
            if style.is_a('IfcPresentationStyleAssignment'):
                if hasattr(style, 'Styles'):
                    for s in style.Styles:
                        color = self._extract_color_from_surface_style(s)
                        if color:
                            return color
            # Handle IfcSurfaceStyle directly (IFC4)
            elif style.is_a('IfcSurfaceStyle'):
                return self._extract_color_from_surface_style(style)
        except Exception:
            pass
        return None
    
    def _extract_color_from_surface_style(self, surface_style):
        """Extract color from surface style."""
        try:
            if not surface_style.is_a('IfcSurfaceStyle'):
                return None
                
            if hasattr(surface_style, 'Styles'):
                for s in surface_style.Styles:
                    # IfcSurfaceStyleRendering has the color
                    if s.is_a('IfcSurfaceStyleRendering'):
                        color = s.SurfaceColour
                        if color:
                            r = color.Red
                            g = color.Green
                            b = color.Blue
                            a = 1.0 - (s.Transparency if s.Transparency else 0.0)
                            return (r, g, b, a)
                    # IfcSurfaceStyleShading also has color
                    elif s.is_a('IfcSurfaceStyleShading'):
                        color = s.SurfaceColour
                        if color:
                            r = color.Red
                            g = color.Green
                            b = color.Blue
                            a = 1.0 - (s.Transparency if hasattr(s, 'Transparency') and s.Transparency else 0.0)
                            return (r, g, b, a)
        except Exception:
            pass
        return None
        
    def _extract_material_data(self, material, ifc_file):
        """Extract color data from IFC material."""
        try:
            if material.is_a('IfcMaterial'):
                return self._get_material_color(material, ifc_file)
            elif material.is_a('IfcMaterialLayerSetUsage'):
                layer_set = material.ForLayerSet
                if layer_set and layer_set.MaterialLayers:
                    return self._get_material_color(layer_set.MaterialLayers[0].Material, ifc_file)
            elif material.is_a('IfcMaterialLayerSet'):
                if material.MaterialLayers:
                    return self._get_material_color(material.MaterialLayers[0].Material, ifc_file)
            elif material.is_a('IfcMaterialConstituentSet'):
                if material.MaterialConstituents:
                    return self._get_material_color(material.MaterialConstituents[0].Material, ifc_file)
            elif material.is_a('IfcMaterialList'):
                if material.Materials:
                    return self._get_material_color(material.Materials[0], ifc_file)
        except Exception:
            pass
        return None
        
    def _get_material_color(self, material, ifc_file):
        """Get color from material definition."""
        if material is None:
            return None
            
        name = material.Name if hasattr(material, 'Name') and material.Name else "Unknown"
        
        # Method 1: Try to get from material's HasRepresentation (IFC4)
        if hasattr(material, 'HasRepresentation') and material.HasRepresentation:
            for rep in material.HasRepresentation:
                if hasattr(rep, 'Representations'):
                    for r in rep.Representations:
                        if hasattr(r, 'Items'):
                            for item in r.Items:
                                color = self._extract_color_from_style(item)
                                if color:
                                    return (name, color)
        
        # Method 2: Look for IfcMaterialDefinitionRepresentation
        try:
            for mat_def_rep in ifc_file.by_type('IfcMaterialDefinitionRepresentation'):
                if mat_def_rep.RepresentedMaterial == material:
                    for rep in mat_def_rep.Representations:
                        if hasattr(rep, 'Items'):
                            for item in rep.Items:
                                color = self._extract_color_from_style(item)
                                if color:
                                    return (name, color)
        except Exception:
            pass
        
        # Method 3: Search for styled items that reference this material
        try:
            for styled_item in ifc_file.by_type('IfcStyledItem'):
                if hasattr(styled_item, 'Styles'):
                    for style in styled_item.Styles:
                        color = self._extract_color_from_presentation_style(style)
                        if color:
                            # Check if this style is associated with our material somehow
                            # This is a fallback - we'll use it if name matches
                            return (name, color)
        except Exception:
            pass
                                    
        return (name, self.default_color)
        
    def _extract_color_from_style(self, style_item):
        """Extract RGB color from style item."""
        try:
            if hasattr(style_item, 'Styles'):
                for style in style_item.Styles:
                    color = self._extract_color_from_presentation_style(style)
                    if color:
                        return color
        except Exception:
            pass
        return None
        
    def add_material(self, name, color):
        """Add a material to the manager."""
        if name not in self.materials:
            self.materials[name] = color
    
    def update_material_color(self, name, color):
        """Update an existing material's color (useful when getting color from geometry)."""
        if name in self.materials:
            # Only update if current color is default
            if self.materials[name] == self.default_color:
                self.materials[name] = color
        else:
            self.materials[name] = color
            
    def get_or_create_material(self, element, ifc_file):
        """Get material name for element, creating if necessary."""
        mat_data = self.get_material_from_element(element, ifc_file)
        
        if mat_data:
            name, color = mat_data
            safe_name = self._sanitize_name(name)
            self.add_material(safe_name, color)
            return safe_name
        else:
            # Use IFC class as fallback material
            ifc_class = element.is_a()
            default_name = f"Default_{ifc_class}"
            self.add_material(default_name, self.default_color)
            return default_name
            
    def _sanitize_name(self, name):
        """Sanitize material name for OBJ/MTL format."""
        if not name:
            return "UnnamedMaterial"
        # Replace problematic characters
        safe = name.replace(' ', '_').replace('/', '_').replace('\\', '_')
        safe = ''.join(c for c in safe if c.isalnum() or c in '_-')
        return safe or "UnnamedMaterial"
        
    def write_mtl(self, filepath):
        """Write MTL file with all materials."""
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write("# Material Library\n")
            f.write(f"# Generated by ifc_to_unity_export.py (BIMUniXchange)\n")
            f.write(f"# Date: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
            f.write(f"# Materials: {len(self.materials)}\n\n")
            
            for name, color in self.materials.items():
                r, g, b, a = color
                f.write(f"newmtl {name}\n")
                f.write(f"Ka {r:.6f} {g:.6f} {b:.6f}\n")  # Ambient
                f.write(f"Kd {r:.6f} {g:.6f} {b:.6f}\n")  # Diffuse
                f.write(f"Ks 0.200000 0.200000 0.200000\n")  # Specular
                f.write(f"Ns 50.000000\n")  # Shininess
                f.write(f"d {a:.6f}\n")  # Transparency
                f.write(f"illum 2\n\n")
                
        return len(self.materials)


# ============================================================
# IFC Processing Functions
# ============================================================

def get_spatial_hierarchy(element, ifc_file):
    """Get the spatial hierarchy for an element (Project, Site, Building, Storey, Space)."""
    hierarchy = {
        'Project': '',
        'Site': '',
        'Building': '',
        'Storey': '',
        'Space': ''
    }
    
    try:
        # Method 1: Get direct containment via ContainedInStructure
        container = None
        if hasattr(element, 'ContainedInStructure') and element.ContainedInStructure:
            for rel in element.ContainedInStructure:
                structure = rel.RelatingStructure
                container = structure
                if structure.is_a('IfcBuildingStorey'):
                    name = structure.Name or ''
                    hierarchy['Storey'] = f"IfcBuildingStorey: {name}" if name else 'IfcBuildingStorey'
                elif structure.is_a('IfcSpace'):
                    name = structure.Name or ''
                    hierarchy['Space'] = f"IfcSpace: {name}" if name else 'IfcSpace'
                elif structure.is_a('IfcBuilding'):
                    name = structure.Name or ''
                    hierarchy['Building'] = f"IfcBuilding: {name}" if name else 'IfcBuilding'
                elif structure.is_a('IfcSite'):
                    name = structure.Name or ''
                    hierarchy['Site'] = f"IfcSite: {name}" if name else 'IfcSite'
                break  # Take first containment
        
        # Method 2: Check ReferencedInStructures (for elements referenced but not contained)
        if not container and hasattr(element, 'ReferencedInStructures') and element.ReferencedInStructures:
            for rel in element.ReferencedInStructures:
                structure = rel.RelatingStructure
                if not container:
                    container = structure
                if structure.is_a('IfcBuildingStorey') and not hierarchy['Storey']:
                    name = structure.Name or ''
                    hierarchy['Storey'] = f"IfcBuildingStorey: {name}" if name else 'IfcBuildingStorey'
                elif structure.is_a('IfcSpace') and not hierarchy['Space']:
                    name = structure.Name or ''
                    hierarchy['Space'] = f"IfcSpace: {name}" if name else 'IfcSpace'
                elif structure.is_a('IfcBuilding') and not hierarchy['Building']:
                    name = structure.Name or ''
                    hierarchy['Building'] = f"IfcBuilding: {name}" if name else 'IfcBuilding'
                elif structure.is_a('IfcSite') and not hierarchy['Site']:
                    name = structure.Name or ''
                    hierarchy['Site'] = f"IfcSite: {name}" if name else 'IfcSite'
        
        # Method 3: Check Decomposes relationship (for aggregated elements like roof slabs)
        if not container and hasattr(element, 'Decomposes') and element.Decomposes:
            for rel in element.Decomposes:
                parent = rel.RelatingObject
                # If parent is a spatial element, use it as container
                if parent.is_a('IfcSpatialStructureElement'):
                    container = parent
                    break
                # If parent is another product, check its containment
                elif parent.is_a('IfcProduct'):
                    if hasattr(parent, 'ContainedInStructure') and parent.ContainedInStructure:
                        for parent_rel in parent.ContainedInStructure:
                            container = parent_rel.RelatingStructure
                            break
                    if container:
                        break
        
        # Walk up from the container to get full hierarchy
        current = container
        visited = set()
        for _ in range(15):  # Max depth to prevent infinite loops
            if current is None or id(current) in visited:
                break
            visited.add(id(current))
            
            # Fill in hierarchy from current - use IFC class prefix for clear identification
            if current.is_a('IfcProject') and not hierarchy['Project']:
                name = current.Name or ''
                hierarchy['Project'] = f"IfcProject: {name}" if name else 'IfcProject'
            elif current.is_a('IfcSite') and not hierarchy['Site']:
                name = current.Name or ''
                hierarchy['Site'] = f"IfcSite: {name}" if name else 'IfcSite'
            elif current.is_a('IfcBuilding') and not hierarchy['Building']:
                name = current.Name or ''
                hierarchy['Building'] = f"IfcBuilding: {name}" if name else 'IfcBuilding'
            elif current.is_a('IfcBuildingStorey') and not hierarchy['Storey']:
                name = current.Name or ''
                hierarchy['Storey'] = f"IfcBuildingStorey: {name}" if name else 'IfcBuildingStorey'
            elif current.is_a('IfcSpace') and not hierarchy['Space']:
                name = current.Name or ''
                hierarchy['Space'] = f"IfcSpace: {name}" if name else 'IfcSpace'
            
            # Move up via Decomposes
            decomposes = getattr(current, 'Decomposes', None)
            if not decomposes:
                break
            
            found_parent = False
            for rel in decomposes:
                parent = rel.RelatingObject
                current = parent
                found_parent = True
                break
            
            if not found_parent:
                break
        
        # If we still don't have project/site/building, try to get from file directly
        if not hierarchy['Project']:
            projects = ifc_file.by_type('IfcProject')
            if projects:
                name = projects[0].Name or ''
                hierarchy['Project'] = f"IfcProject: {name}" if name else 'IfcProject'
        
        if not hierarchy['Site']:
            sites = ifc_file.by_type('IfcSite')
            if sites:
                name = sites[0].Name or ''
                hierarchy['Site'] = f"IfcSite: {name}" if name else 'IfcSite'
        
        if not hierarchy['Building']:
            buildings = ifc_file.by_type('IfcBuilding')
            if buildings:
                name = buildings[0].Name or ''
                hierarchy['Building'] = f"IfcBuilding: {name}" if name else 'IfcBuilding'
                
    except Exception as e:
        pass
        
    return hierarchy


def get_element_properties(element, ifc_file):
    """Extract all properties from an IFC element."""
    properties = {}
    
    try:
        # Get property sets
        if hasattr(element, 'IsDefinedBy'):
            for rel in element.IsDefinedBy:
                if rel.is_a('IfcRelDefinesByProperties'):
                    prop_def = rel.RelatingPropertyDefinition
                    if prop_def.is_a('IfcPropertySet'):
                        pset_name = prop_def.Name or 'UnnamedPset'
                        for prop in prop_def.HasProperties:
                            if prop.is_a('IfcPropertySingleValue'):
                                key = f"Pset_{pset_name}_{prop.Name}"
                                value = prop.NominalValue.wrappedValue if prop.NominalValue else ''
                                properties[key] = str(value) if value is not None else ''
                    elif prop_def.is_a('IfcElementQuantity'):
                        qto_name = prop_def.Name or 'UnnamedQto'
                        for qty in prop_def.Quantities:
                            key = f"Qto_{qto_name}_{qty.Name}"
                            # Handle different quantity types
                            if hasattr(qty, 'LengthValue'):
                                value = qty.LengthValue
                            elif hasattr(qty, 'AreaValue'):
                                value = qty.AreaValue
                            elif hasattr(qty, 'VolumeValue'):
                                value = qty.VolumeValue
                            elif hasattr(qty, 'CountValue'):
                                value = qty.CountValue
                            elif hasattr(qty, 'WeightValue'):
                                value = qty.WeightValue
                            else:
                                value = ''
                            properties[key] = str(value) if value is not None else ''
                            
    except Exception as e:
        pass
        
    return properties


def get_predefined_type(element):
    """Extract PredefinedType from an IFC element, checking element and its type."""
    ptype = ''
    try:
        # Direct PredefinedType attribute
        if hasattr(element, 'PredefinedType') and element.PredefinedType:
            ptype = str(element.PredefinedType)
            if ptype and ptype != 'NOTDEFINED' and ptype != 'USERDEFINED':
                return ptype
        # Check ObjectType for USERDEFINED
        if ptype == 'USERDEFINED' and hasattr(element, 'ObjectType') and element.ObjectType:
            return f"USERDEFINED:{element.ObjectType}"
        # Fallback: check the element type
        element_type = ifcopenshell.util.element.get_type(element)
        if element_type and hasattr(element_type, 'PredefinedType') and element_type.PredefinedType:
            tptype = str(element_type.PredefinedType)
            if tptype and tptype != 'NOTDEFINED':
                return tptype
    except Exception:
        pass
    return ptype


def get_door_dimensions(element, ifc_file):
    """Extract OverallWidth/OverallHeight for IfcDoor (and IfcWindow)."""
    dims = {'OverallWidth': '', 'OverallHeight': '', 'OperationType': ''}
    try:
        if hasattr(element, 'OverallWidth') and element.OverallWidth is not None:
            dims['OverallWidth'] = str(float(element.OverallWidth))
        if hasattr(element, 'OverallHeight') and element.OverallHeight is not None:
            dims['OverallHeight'] = str(float(element.OverallHeight))
        # Operation type from IfcDoorStyle (IFC2X3) or IfcDoorType (IFC4)
        element_type = ifcopenshell.util.element.get_type(element)
        if element_type:
            if hasattr(element_type, 'OperationType') and element_type.OperationType:
                dims['OperationType'] = str(element_type.OperationType)
            elif hasattr(element_type, 'OperationType') and element_type.OperationType:
                dims['OperationType'] = str(element_type.OperationType)
    except Exception:
        pass
    return dims


def get_unit_scale_to_metres(ifc_file):
    """Work out the IFC file's length unit scale factor to metres.
    Returns the multiplier so that:  ifc_value * scale = metres.
    """
    try:
        import ifcopenshell.util.unit
        return ifcopenshell.util.unit.calculate_unit_scale(ifc_file)
    except Exception:
        pass
    # Fallback: try manual parsing
    try:
        for unit_assignment in ifc_file.by_type('IfcUnitAssignment'):
            for unit in unit_assignment.Units:
                if unit.is_a('IfcSIUnit') and unit.UnitType == 'LENGTHUNIT':
                    prefix = getattr(unit, 'Prefix', None)
                    if prefix == 'MILLI':
                        return 0.001
                    elif prefix == 'CENTI':
                        return 0.01
                    elif prefix == 'KILO':
                        return 1000.0
                    return 1.0
                elif unit.is_a('IfcConversionBasedUnit') and unit.UnitType == 'LENGTHUNIT':
                    factor = unit.ConversionFactor.ValueComponent.wrappedValue
                    return float(factor)
    except Exception:
        pass
    return 1.0


def extract_relationships(ifc_file):
    """Extract IFC relationships relevant for robotics navigation.
    Returns a dict: { relationship_type: [ {globalid fields...} ] }
    """
    rels = {
        'IfcRelVoidsElement': [],          # host -> opening
        'IfcRelFillsElement': [],          # opening -> door/window
        'IfcRelContainedInSpatialStructure': [],
        'IfcRelSpaceBoundary': [],
        'IfcRelAggregates': [],
    }

    # IfcRelVoidsElement: host element voided by an opening
    for rel in ifc_file.by_type('IfcRelVoidsElement'):
        try:
            rels['IfcRelVoidsElement'].append({
                'RelatingBuildingElement': rel.RelatingBuildingElement.GlobalId if rel.RelatingBuildingElement else '',
                'RelatedOpeningElement': rel.RelatedOpeningElement.GlobalId if rel.RelatedOpeningElement else '',
            })
        except Exception:
            pass

    # IfcRelFillsElement: opening filled by a door/window
    for rel in ifc_file.by_type('IfcRelFillsElement'):
        try:
            rels['IfcRelFillsElement'].append({
                'RelatingOpeningElement': rel.RelatingOpeningElement.GlobalId if rel.RelatingOpeningElement else '',
                'RelatedBuildingElement': rel.RelatedBuildingElement.GlobalId if rel.RelatedBuildingElement else '',
            })
        except Exception:
            pass

    # IfcRelContainedInSpatialStructure
    for rel in ifc_file.by_type('IfcRelContainedInSpatialStructure'):
        try:
            container_id = rel.RelatingStructure.GlobalId if rel.RelatingStructure else ''
            for el in (rel.RelatedElements or []):
                rels['IfcRelContainedInSpatialStructure'].append({
                    'RelatingStructure': container_id,
                    'RelatedElement': el.GlobalId,
                })
        except Exception:
            pass

    # IfcRelSpaceBoundary (if available)
    try:
        for rel in ifc_file.by_type('IfcRelSpaceBoundary'):
            try:
                rels['IfcRelSpaceBoundary'].append({
                    'RelatingSpace': rel.RelatingSpace.GlobalId if rel.RelatingSpace else '',
                    'RelatedBuildingElement': rel.RelatedBuildingElement.GlobalId if rel.RelatedBuildingElement else '',
                    'PhysicalOrVirtualBoundary': str(getattr(rel, 'PhysicalOrVirtualBoundary', '')) or '',
                    'InternalOrExternalBoundary': str(getattr(rel, 'InternalOrExternalBoundary', '')) or '',
                })
            except Exception:
                pass
    except Exception:
        pass  # IfcRelSpaceBoundary may not exist in some schemas

    # IfcRelAggregates (storey->building, building->site, etc.)
    for rel in ifc_file.by_type('IfcRelAggregates'):
        try:
            parent_id = rel.RelatingObject.GlobalId if rel.RelatingObject else ''
            for child in (rel.RelatedObjects or []):
                rels['IfcRelAggregates'].append({
                    'RelatingObject': parent_id,
                    'RelatedObject': child.GlobalId,
                })
        except Exception:
            pass

    return rels


def export_spaces_and_storeys(ifc_file, material_manager, settings):
    """Export IfcSpace and IfcBuildingStorey entities.
    Spaces are always exported even without geometry.
    Storeys get Elevation metadata.
    Returns (metadata_list, geometry_list).
    """
    extra_metadata = []
    extra_geometry = []

    # --- IfcSpace ---
    for space in ifc_file.by_type('IfcSpace'):
        metadata = get_element_metadata(space, ifc_file, material_manager)
        metadata['PredefinedType'] = get_predefined_type(space)
        # Add LongName
        metadata['LongName'] = getattr(space, 'LongName', '') or ''
        extra_metadata.append(metadata)

        # Try geometry
        try:
            geo_result = process_geometry_multi_material(space, settings, material_manager)
            if geo_result and not geo_result['error'] and geo_result['vertices'] and geo_result['submeshes']:
                extra_geometry.append({
                    'id': space.GlobalId,
                    'vertices': geo_result['vertices'],
                    'normals': geo_result['normals'],
                    'submeshes': geo_result['submeshes'],
                })
        except Exception:
            pass  # Geometry is optional for spaces

    # --- IfcBuildingStorey ---
    for storey in ifc_file.by_type('IfcBuildingStorey'):
        elev = ''
        try:
            if hasattr(storey, 'Elevation') and storey.Elevation is not None:
                elev = str(float(storey.Elevation))
        except Exception:
            pass
        hierarchy = get_spatial_hierarchy(storey, ifc_file)
        meta = {
            'GlobalId': storey.GlobalId,
            'Name': format_hierarchy_name('IfcBuildingStorey', storey.Name),
            'IfcClass': 'IfcBuildingStorey',
            'Description': storey.Description or '',
            'ObjectType': getattr(storey, 'ObjectType', '') or '',
            'Tag': getattr(storey, 'Tag', '') or '',
            'PredefinedType': '',
            'Elevation': elev,
            'LongName': getattr(storey, 'LongName', '') or '',
            'Project': hierarchy.get('Project', ''),
            'Site': hierarchy.get('Site', ''),
            'Building': hierarchy.get('Building', ''),
            'Storey': format_hierarchy_name('IfcBuildingStorey', storey.Name),
            'Space': '',
            'Material': '',
            'Color_R': '', 'Color_G': '', 'Color_B': '', 'Color_A': '',
        }
        # Storey properties
        props = get_element_properties(storey, ifc_file)
        meta.update(props)
        extra_metadata.append(meta)

    return extra_metadata, extra_geometry


def get_element_metadata(element, ifc_file, material_manager):
    """Get comprehensive metadata for an IFC element."""
    metadata = {}
    
    # Basic identity
    metadata['GlobalId'] = element.GlobalId or ''
    metadata['Name'] = element.Name or ''
    metadata['IfcClass'] = element.is_a()
    metadata['Description'] = element.Description or ''
    metadata['ObjectType'] = getattr(element, 'ObjectType', '') or ''
    metadata['Tag'] = getattr(element, 'Tag', '') or ''
    
    # PredefinedType
    metadata['PredefinedType'] = get_predefined_type(element)
    
    # LongName (for spaces, storeys)
    metadata['LongName'] = getattr(element, 'LongName', '') or ''
    
    # Door/Window dimensions
    ifc_class = element.is_a()
    if ifc_class in ('IfcDoor', 'IfcDoorStandardCase', 'IfcWindow', 'IfcWindowStandardCase'):
        dims = get_door_dimensions(element, ifc_file)
        metadata['OverallWidth'] = dims['OverallWidth']
        metadata['OverallHeight'] = dims['OverallHeight']
        metadata['OperationType'] = dims['OperationType']
    
    # Get type information
    try:
        element_type = ifcopenshell.util.element.get_type(element)
        if element_type:
            metadata['TypeName'] = element_type.Name or ''
            metadata['TypeId'] = element_type.GlobalId or ''
        else:
            metadata['TypeName'] = ''
            metadata['TypeId'] = ''
    except Exception:
        metadata['TypeName'] = ''
        metadata['TypeId'] = ''
        
    # Spatial hierarchy
    hierarchy = get_spatial_hierarchy(element, ifc_file)
    metadata['Project'] = hierarchy['Project']
    metadata['Site'] = hierarchy['Site']
    metadata['Building'] = hierarchy['Building']
    metadata['Storey'] = hierarchy['Storey']
    metadata['Space'] = hierarchy['Space']
    
    # Material information
    material_name = material_manager.get_or_create_material(element, ifc_file)
    metadata['Material'] = material_name
    
    # Color (from material)
    if material_name in material_manager.materials:
        r, g, b, a = material_manager.materials[material_name]
        metadata['Color_R'] = f"{r:.4f}"
        metadata['Color_G'] = f"{g:.4f}"
        metadata['Color_B'] = f"{b:.4f}"
        metadata['Color_A'] = f"{a:.4f}"
    else:
        metadata['Color_R'] = '0.8'
        metadata['Color_G'] = '0.8'
        metadata['Color_B'] = '0.8'
        metadata['Color_A'] = '1.0'
    
    # Properties
    properties = get_element_properties(element, ifc_file)
    metadata.update(properties)
    
    return metadata


def filter_products(ifc_file):
    """Filter IFC products to get exportable elements - NO LIMITS on quantity."""
    all_products = ifc_file.by_type('IfcProduct')
    filtered = []
    filtered_ids = set()  # Track already added elements by id
    stats = {
        'total': len(all_products),
        'exported': 0,
        'filtered_out': {}
    }
    
    def add_element(product):
        """Add an element to the filtered list if not already added."""
        if id(product) not in filtered_ids:
            filtered_ids.add(id(product))
            filtered.append(product)
            stats['exported'] += 1
            return True
        return False
    
    def get_decomposed_children(element):
        """Get all decomposed children of an element (e.g., balusters from railing).
        Includes children with or without geometry."""
        children = []
        if hasattr(element, 'IsDecomposedBy') and element.IsDecomposedBy:
            for rel in element.IsDecomposedBy:
                if hasattr(rel, 'RelatedObjects'):
                    for child in rel.RelatedObjects:
                        children.append(child)
                        # Recursively get children of children
                        children.extend(get_decomposed_children(child))
        return children
    
    for product in all_products:
        ifc_class = product.is_a()

        # Include EVERY product unconditionally.
        # Elements without a Representation are still added as semantic-only
        # entries (CSV metadata row, no OBJ geometry).
        add_element(product)

        # Also add decomposed children (e.g., balusters from railings)
        decomposed_children = get_decomposed_children(product)
        for child in decomposed_children:
            add_element(child)
                
    return filtered, stats


def extract_material_color(mat):
    """Extract color from a geometry material object.
    Returns: (r, g, b, a) tuple or None
    """
    try:
        r, g, b, a = 0.8, 0.8, 0.8, 1.0
        
        # Try to get diffuse color
        if hasattr(mat, 'diffuse') and mat.diffuse:
            diffuse = mat.diffuse
            # IfcOpenShell 0.8.x - color object with callable r(), g(), b()
            if callable(getattr(diffuse, 'r', None)):
                r, g, b = float(diffuse.r()), float(diffuse.g()), float(diffuse.b())
            # Color object with r, g, b attributes (non-callable)
            elif hasattr(diffuse, 'r') and hasattr(diffuse, 'g') and hasattr(diffuse, 'b'):
                r, g, b = float(diffuse.r), float(diffuse.g), float(diffuse.b)
            # Subscriptable (older versions)
            elif hasattr(diffuse, '__getitem__'):
                r, g, b = float(diffuse[0]), float(diffuse[1]), float(diffuse[2])
        elif hasattr(mat, 'surface_colour') and mat.surface_colour:
            sc = mat.surface_colour
            if callable(getattr(sc, 'r', None)):
                r, g, b = float(sc.r()), float(sc.g()), float(sc.b())
            elif hasattr(sc, 'r') and hasattr(sc, 'g') and hasattr(sc, 'b'):
                r, g, b = float(sc.r), float(sc.g), float(sc.b)
            elif hasattr(sc, '__getitem__'):
                r, g, b = float(sc[0]), float(sc[1]), float(sc[2])
        
        # Get transparency
        if hasattr(mat, 'transparency') and mat.transparency:
            t = mat.transparency
            if callable(t):
                t = t()
            a = 1.0 - float(t) if t else 1.0
        
        return (r, g, b, a)
    except Exception:
        return None


def process_geometry_multi_material(element, settings, material_manager):
    """Process geometry for a single element, extracting multi-material submeshes.
    
    Returns: dict with:
        - 'vertices': list of (x, y, z) tuples
        - 'normals': list of (nx, ny, nz) tuples
        - 'submeshes': list of {'material': name, 'triangles': [(i0, i1, i2), ...], 'color': (r,g,b,a)}
        - 'error': error message or None
    """
    result = {
        'vertices': [],
        'normals': [],
        'submeshes': [],
        'error': None
    }
    
    try:
        shape = ifcopenshell.geom.create_shape(settings, element)
        if not shape:
            result['error'] = "No shape created"
            return result
            
        geometry = shape.geometry
        verts = geometry.verts
        faces = geometry.faces
        
        if not verts or not faces or len(verts) == 0 or len(faces) == 0:
            result['error'] = "Empty geometry"
            return result
        
        # Get normals
        normals = geometry.normals if hasattr(geometry, 'normals') else None
        
        # Get material info
        geo_materials = []
        if hasattr(geometry, 'materials') and geometry.materials:
            for mat in geometry.materials:
                mat_name = mat.name if hasattr(mat, 'name') and mat.name else f"Material_{len(geo_materials)}"
                mat_color = extract_material_color(mat)
                if mat_color is None:
                    mat_color = (0.8, 0.8, 0.8, 1.0)
                geo_materials.append({
                    'name': material_manager._sanitize_name(mat_name),
                    'color': mat_color
                })
        
        # Get per-face material IDs
        material_ids = list(geometry.material_ids) if hasattr(geometry, 'material_ids') else []
        
        # Convert vertices
        for i in range(0, len(verts), 3):
            # IFC to Unity coordinate conversion (with 180-degree Y rotation correction)
            x = -verts[i]           # Negate X for 180-degree Y rotation
            y = verts[i + 2]        # IFC Z -> Unity Y
            z = -verts[i + 1]       # IFC Y -> Unity Z (negated for 180-degree Y rotation)
            result['vertices'].append((x, y, z))
        
        # Convert normals
        if normals and len(normals) > 0:
            for i in range(0, len(normals), 3):
                nx = -normals[i]    # Negate X for 180-degree Y rotation
                ny = normals[i + 2]
                nz = -normals[i + 1]  # Negated for 180-degree Y rotation
                result['normals'].append((nx, ny, nz))
        
        # Group faces by material
        face_count = len(faces) // 3
        
        if material_ids and geo_materials:
            # Multi-material: group triangles by material ID
            submesh_triangles = {}  # material_id -> list of triangles
            
            for face_idx in range(face_count):
                mat_id = material_ids[face_idx] if face_idx < len(material_ids) else 0
                
                # Get face indices
                base = face_idx * 3
                # Reverse winding order for Unity
                tri = (faces[base], faces[base + 2], faces[base + 1])
                
                if mat_id not in submesh_triangles:
                    submesh_triangles[mat_id] = []
                submesh_triangles[mat_id].append(tri)
            
            # Create submeshes
            for mat_id in sorted(submesh_triangles.keys()):
                if mat_id < len(geo_materials):
                    mat_info = geo_materials[mat_id]
                else:
                    mat_info = {'name': f"Material_{mat_id}", 'color': (0.8, 0.8, 0.8, 1.0)}
                
                # Add material to manager
                material_manager.add_material(mat_info['name'], mat_info['color'])
                
                result['submeshes'].append({
                    'material': mat_info['name'],
                    'triangles': submesh_triangles[mat_id],
                    'color': mat_info['color']
                })
        else:
            # Single material: all faces in one submesh
            triangles = []
            for face_idx in range(face_count):
                base = face_idx * 3
                tri = (faces[base], faces[base + 2], faces[base + 1])
                triangles.append(tri)
            
            # Get a default material
            if geo_materials:
                mat_info = geo_materials[0]
                material_manager.add_material(mat_info['name'], mat_info['color'])
                result['submeshes'].append({
                    'material': mat_info['name'],
                    'triangles': triangles,
                    'color': mat_info['color']
                })
            else:
                default_name = f"Default_{element.is_a()}"
                material_manager.add_material(default_name, (0.8, 0.8, 0.8, 1.0))
                result['submeshes'].append({
                    'material': default_name,
                    'triangles': triangles,
                    'color': (0.8, 0.8, 0.8, 1.0)
                })
        
        return result
        
    except Exception as e:
        result['error'] = str(e)
        return result


def process_geometry(element, settings):
    """Process geometry for a single element using IfcOpenShell.
    Returns: (vertices, triangles, normals, color, error_msg)
    DEPRECATED: Use process_geometry_multi_material instead for multi-material support.
    """
    try:
        shape = ifcopenshell.geom.create_shape(settings, element)
        if shape:
            geometry = shape.geometry
            verts = geometry.verts
            faces = geometry.faces
            
            if not verts or not faces or len(verts) == 0 or len(faces) == 0:
                return None, None, None, None, "Empty geometry"
            
            # Get normals if available for better triangulation
            normals = geometry.normals if hasattr(geometry, 'normals') else None
            
            # Get material colors from geometry if available
            materials_colors = None
            if hasattr(geometry, 'materials') and geometry.materials:
                materials_colors = []
                for mat in geometry.materials:
                    color = extract_material_color(mat)
                    if color:
                        materials_colors.append(color)
            
            # Convert flat vertex list to tuples
            # Vertices are already in world coordinates with USE_WORLD_COORDS=True
            vertices = []
            for i in range(0, len(verts), 3):
                # IFC coordinate system to Unity coordinate system conversion:
                # IFC: X=right, Y=forward, Z=up
                # Unity: X=right, Y=up, Z=forward
                # With 180-degree Y rotation correction
                x = -verts[i]           # Negate X for 180-degree Y rotation
                y = verts[i + 2]        # IFC Z becomes Unity Y (up)
                z = -verts[i + 1]       # IFC Y becomes Unity Z (negated for 180-degree Y rotation)
                vertices.append((x, y, z))
            
            # Process normals if available
            normal_list = []
            if normals and len(normals) > 0:
                for i in range(0, len(normals), 3):
                    nx = -normals[i]    # Negate X for 180-degree Y rotation
                    ny = normals[i + 2] # Same transformation as vertices
                    nz = -normals[i + 1]  # Negated for 180-degree Y rotation
                    normal_list.append((nx, ny, nz))
                
            # Convert flat face list to triangle indices
            triangles = []
            for i in range(0, len(faces), 3):
                # Reverse winding order for Unity (counter-clockwise to clockwise)
                triangles.append((faces[i], faces[i + 2], faces[i + 1]))
            
            # Return first material color if available
            geo_color = materials_colors[0] if materials_colors else None
                
            return vertices, triangles, normal_list, geo_color, None
        else:
            return None, None, None, None, "No shape created"
    except Exception as e:
        return None, None, None, None, str(e)


def write_element_obj(filepath, element_id, vertices, triangles, material_name):
    """Write OBJ data for a single element."""
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(f"# IFC Element: {element_id}\n")
        f.write(f"# Vertices: {len(vertices)}, Faces: {len(triangles)}\n")
        f.write(f"# Generated by ifc_to_unity_export.py (BIMUniXchange)\n\n")
        
        # Reference material file
        mtl_name = os.path.basename(filepath).replace('.obj', '.mtl')
        f.write(f"mtllib {mtl_name}\n\n")
        
        # Object name
        f.write(f"o {element_id}\n")
        f.write(f"usemtl {material_name}\n\n")
        
        # Vertices
        for v in vertices:
            f.write(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}\n")
            
        f.write("\n")
        
        # Faces (OBJ is 1-indexed)
        for tri in triangles:
            f.write(f"f {tri[0]+1} {tri[1]+1} {tri[2]+1}\n")
            
    return len(vertices), len(triangles)


def write_combined_obj_multi_material(filepath, all_elements_data, material_manager):
    """Write combined OBJ file with all elements, supporting multi-material submeshes.
    
    Each element can have multiple submeshes with different materials.
    OBJ format uses groups (g) for submeshes within an object (o).
    """
    total_vertices = 0
    total_normals = 0
    total_faces = 0
    vertex_offset = 0
    normal_offset = 0
    multi_mat_elements = 0
    
    mtl_name = os.path.basename(filepath).replace('.obj', '.mtl')
    
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write("# IFC Export - Combined OBJ with Multi-Material Support\n")
        f.write(f"# Generated by ifc_to_unity_export.py (BIMUniXchange)\n")
        f.write(f"# Date: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
        f.write(f"# Elements: {len(all_elements_data)}\n")
        f.write(f"# Coordinate System: World coordinates preserved\n")
        f.write(f"# Multi-material support: Each element can have multiple submeshes\n\n")
        f.write(f"mtllib {mtl_name}\n\n")
        
        for element_data in all_elements_data:
            element_id = element_data['id']
            vertices = element_data['vertices']
            normals = element_data.get('normals', [])
            submeshes = element_data.get('submeshes', [])
            
            if not vertices or not submeshes:
                continue
            
            # Count total triangles
            element_face_count = sum(len(sm['triangles']) for sm in submeshes)
            
            # Write object header
            f.write(f"# Element: {element_id}\n")
            f.write(f"# Vertices: {len(vertices)}, Faces: {element_face_count}, Submeshes: {len(submeshes)}\n")
            f.write(f"o {element_id}\n")
            
            if len(submeshes) > 1:
                multi_mat_elements += 1
            
            # Write vertices (world coordinates preserved)
            for v in vertices:
                f.write(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}\n")
            
            # Write normals if available
            has_normals = len(normals) == len(vertices)
            if has_normals:
                for n in normals:
                    f.write(f"vn {n[0]:.6f} {n[1]:.6f} {n[2]:.6f}\n")
            
            # Write submeshes (groups with different materials)
            for submesh_idx, submesh in enumerate(submeshes):
                material_name = submesh['material']
                triangles = submesh['triangles']
                
                # Use group to mark submesh
                f.write(f"g {element_id}_submesh{submesh_idx}\n")
                f.write(f"usemtl {material_name}\n")
                
                # Write faces with offset
                for tri in triangles:
                    if has_normals:
                        # Face with normals: f v1//vn1 v2//vn2 v3//vn3
                        f.write(f"f {tri[0]+1+vertex_offset}//{tri[0]+1+normal_offset} "
                               f"{tri[1]+1+vertex_offset}//{tri[1]+1+normal_offset} "
                               f"{tri[2]+1+vertex_offset}//{tri[2]+1+normal_offset}\n")
                    else:
                        # Face without normals
                        f.write(f"f {tri[0]+1+vertex_offset} {tri[1]+1+vertex_offset} {tri[2]+1+vertex_offset}\n")
                    
                    total_faces += 1
            
            f.write("\n")
            
            total_vertices += len(vertices)
            vertex_offset += len(vertices)
            if has_normals:
                total_normals += len(normals)
                normal_offset += len(normals)
    
    print(f"  Multi-material elements: {multi_mat_elements}")
    return total_vertices, total_faces


def write_combined_obj(filepath, all_elements_data, material_manager):
    """Write combined OBJ file with all elements, preserving world positions and materials."""
    total_vertices = 0
    total_normals = 0
    total_faces = 0
    vertex_offset = 0
    normal_offset = 0
    
    mtl_name = os.path.basename(filepath).replace('.obj', '.mtl')
    
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write("# IFC Export - Combined OBJ\n")
        f.write(f"# Generated by ifc_to_unity_export.py (BIMUniXchange)\n")
        f.write(f"# Date: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
        f.write(f"# Elements: {len(all_elements_data)}\n")
        f.write(f"# Coordinate System: World coordinates preserved\n\n")
        f.write(f"mtllib {mtl_name}\n\n")
        
        for element_data in all_elements_data:
            element_id = element_data['id']
            vertices = element_data['vertices']
            triangles = element_data['triangles']
            normals = element_data.get('normals', [])
            material = element_data['material']
            
            if not vertices or not triangles:
                continue
            
            # Write object header with material
            f.write(f"# Element: {element_id}\n")
            f.write(f"# Vertices: {len(vertices)}, Faces: {len(triangles)}\n")
            f.write(f"o {element_id}\n")
            
            # Write vertices (world coordinates preserved)
            for v in vertices:
                f.write(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}\n")
            
            # Write normals if available
            has_normals = len(normals) == len(vertices)
            if has_normals:
                for n in normals:
                    f.write(f"vn {n[0]:.6f} {n[1]:.6f} {n[2]:.6f}\n")
            
            # Set material for this element
            f.write(f"usemtl {material}\n")
            
            # Write faces with offset
            for tri in triangles:
                if has_normals:
                    # Face with normals: f v1//vn1 v2//vn2 v3//vn3
                    f.write(f"f {tri[0]+1+vertex_offset}//{tri[0]+1+normal_offset} "
                           f"{tri[1]+1+vertex_offset}//{tri[1]+1+normal_offset} "
                           f"{tri[2]+1+vertex_offset}//{tri[2]+1+normal_offset}\n")
                else:
                    # Face without normals
                    f.write(f"f {tri[0]+1+vertex_offset} {tri[1]+1+vertex_offset} {tri[2]+1+vertex_offset}\n")
                
            f.write("\n")
            
            total_vertices += len(vertices)
            total_faces += len(triangles)
            vertex_offset += len(vertices)
            if has_normals:
                total_normals += len(normals)
                normal_offset += len(normals)
            
    return total_vertices, total_faces


def write_metadata_csv(filepath, all_metadata):
    """Write metadata CSV file."""
    if not all_metadata:
        return 0
        
    # Collect all unique keys
    all_keys = set()
    for meta in all_metadata:
        all_keys.update(meta.keys())
        
    # Sort keys with priority columns first
    priority_cols = ['GlobalId', 'Name', 'IfcClass', 'TypeName', 'PredefinedType',
                     'Project', 'Site', 'Building', 'Storey', 'Space',
                     'Material', 'Color_R', 'Color_G', 'Color_B', 'Color_A',
                     'Description', 'ObjectType', 'Tag', 'TypeId',
                     'Elevation', 'LongName',
                     'OverallWidth', 'OverallHeight', 'OperationType']
    
    sorted_keys = [k for k in priority_cols if k in all_keys]
    remaining_keys = sorted([k for k in all_keys if k not in priority_cols])
    sorted_keys.extend(remaining_keys)
    
    with open(filepath, 'w', newline='', encoding='utf-8') as f:
        writer = csv.DictWriter(f, fieldnames=sorted_keys, extrasaction='ignore')
        writer.writeheader()
        for meta in all_metadata:
            writer.writerow(meta)
            
    return len(all_metadata)


def format_hierarchy_name(ifc_class, name):
    """Format hierarchy name with IFC class prefix."""
    if name:
        return f"{ifc_class}: {name}"
    return ifc_class


def export_spatial_hierarchy(ifc_file):
    """Export spatial hierarchy elements (Project, Site, Building).
    NOTE: IfcBuildingStorey and IfcSpace are now handled by export_spaces_and_storeys().
    """
    hierarchy_metadata = []
    
    # Project
    for project in ifc_file.by_type('IfcProject'):
        project_name = format_hierarchy_name('IfcProject', project.Name)
        hierarchy_metadata.append({
            'GlobalId': project.GlobalId,
            'Name': project_name,
            'IfcClass': 'IfcProject',
            'Description': project.Description or '',
            'PredefinedType': '',
            'LongName': getattr(project, 'LongName', '') or '',
            'Elevation': '',
            'Project': project_name,
            'Site': '',
            'Building': '',
            'Storey': '',
            'Space': '',
            'Material': '',
            'Color_R': '', 'Color_G': '', 'Color_B': '', 'Color_A': ''
        })
        
    # Site
    for site in ifc_file.by_type('IfcSite'):
        site_name = format_hierarchy_name('IfcSite', site.Name)
        hierarchy_metadata.append({
            'GlobalId': site.GlobalId,
            'Name': site_name,
            'IfcClass': 'IfcSite',
            'Description': site.Description or '',
            'PredefinedType': '',
            'LongName': getattr(site, 'LongName', '') or '',
            'Elevation': '',
            'Project': '',
            'Site': site_name,
            'Building': '',
            'Storey': '',
            'Space': '',
            'Material': '',
            'Color_R': '', 'Color_G': '', 'Color_B': '', 'Color_A': ''
        })
        
    # Building
    for building in ifc_file.by_type('IfcBuilding'):
        building_name = format_hierarchy_name('IfcBuilding', building.Name)
        hierarchy_metadata.append({
            'GlobalId': building.GlobalId,
            'Name': building_name,
            'IfcClass': 'IfcBuilding',
            'Description': building.Description or '',
            'PredefinedType': '',
            'LongName': getattr(building, 'LongName', '') or '',
            'Elevation': '',
            'Project': '',
            'Site': '',
            'Building': building_name,
            'Storey': '',
            'Space': '',
            'Material': '',
            'Color_R': '', 'Color_G': '', 'Color_B': '', 'Color_A': ''
        })
        
    return hierarchy_metadata


# ============================================================
# Main Export Function
# ============================================================

def export_ifc_to_unity(ifc_path, output_dir=None):
    """Main export function."""
    print("=" * 60)
    print("IFC to Unity Export (BIMUniXchange)")
    print("=" * 60)
    
    start_time = time.time()
    memory_tracker = MemoryTracker()
    
    # Validate input
    if not os.path.exists(ifc_path):
        print(f"ERROR: IFC file not found: {ifc_path}")
        return False
        
    ifc_path = os.path.abspath(ifc_path)
    ifc_name = os.path.splitext(os.path.basename(ifc_path))[0]
    
    # Set output directory
    if output_dir is None:
        output_dir = os.path.dirname(ifc_path)
    else:
        os.makedirs(output_dir, exist_ok=True)
        
    # Output file paths
    obj_path = os.path.join(output_dir, f"{ifc_name}.obj")
    mtl_path = os.path.join(output_dir, f"{ifc_name}.mtl")
    csv_path = os.path.join(output_dir, f"{ifc_name}_metadata.csv")
    rel_path = os.path.join(output_dir, f"{ifc_name}_relationships.json")
    
    print(f"Input: {ifc_path}")
    print(f"Output OBJ: {obj_path}")
    print(f"Output MTL: {mtl_path}")
    print(f"Output CSV: {csv_path}")
    print(f"Output REL: {rel_path}")
    
    # Load IFC file
    print("\nLoading IFC file...")
    memory_tracker.report("Starting")
    
    try:
        ifc_file = ifcopenshell.open(ifc_path)
    except Exception as e:
        print(f"ERROR: Failed to open IFC file: {e}")
        return False
        
    schema = ifc_file.schema
    print(f"IFC Schema: {schema}")
    
    # Compute unit scale
    unit_scale = get_unit_scale_to_metres(ifc_file)
    print(f"Length unit scale to metres: {unit_scale}")
    
    memory_tracker.report("IFC file loaded")
    
    # Get project info
    projects = ifc_file.by_type('IfcProject')
    if projects:
        print(f"Project: {projects[0].Name or 'Unnamed'}")
        
    sites = ifc_file.by_type('IfcSite')
    if sites:
        print(f"Site: {sites[0].Name or 'Unnamed'}")
        
    buildings = ifc_file.by_type('IfcBuilding')
    if buildings:
        print(f"Building: {buildings[0].Name or 'Unnamed'}")
    
    # Filter products
    print("\nCollecting elements...")
    filter_start = time.time()
    products, filter_stats = filter_products(ifc_file)
    filter_time = time.time() - filter_start
    
    print(f"Total products: {filter_stats['total']}")
    print(f"To export: {filter_stats['exported']}")
    
    if not products:
        print("ERROR: No products to export")
        return False
        
    # Initialize managers
    material_manager = MaterialManager()
    
    # Configure geometry settings (IfcOpenShell 0.8.4+ API)
    settings = ifcopenshell.geom.settings()
    settings.set("use-world-coords", GEOMETRY_SETTINGS['USE_WORLD_COORDS'])
    settings.set("disable-opening-subtractions", GEOMETRY_SETTINGS['DISABLE_OPENING_SUBTRACTIONS'])
    settings.set("weld-vertices", GEOMETRY_SETTINGS['WELD_VERTICES'])
    settings.set("apply-default-materials", GEOMETRY_SETTINGS['APPLY_DEFAULT_MATERIALS'])
    
    # Additional settings for better geometry quality
    try:
        settings.set("generate-uvs", False)  # UVs handled by Unity
        settings.set("mesher-linear-deflection", 0.001)  # 1mm tolerance for better triangulation
        settings.set("mesher-angular-deflection", 0.5)  # 0.5 degree tolerance
    except Exception:
        pass  # Some settings may not be available
    
    # Process elements - NO LIMITS on quantity
    print("\nProcessing geometry with multi-material support...")
    all_elements_data = []
    all_metadata = []
    export_start = time.time()
    
    progress = ProgressTracker(len(products), "Processing")
    exported_count = 0
    failed_count = 0
    multi_material_count = 0
    geo_errors = []  # Track geometry errors
    
    for i, element in enumerate(products):
        progress.update()
        progress.report(100)
        
        element_id = element.GlobalId
        
        # Get metadata first (always collect metadata even if geometry fails)
        metadata = get_element_metadata(element, ifc_file, material_manager)
        all_metadata.append(metadata)
        
        # Process geometry with multi-material support
        geo_result = process_geometry_multi_material(element, settings, material_manager)
        
        if geo_result['error']:
            failed_count += 1
            if len(geo_errors) < 10:
                geo_errors.append(f"{element.is_a()}/{element_id}: {geo_result['error']}")
        elif geo_result['vertices'] and geo_result['submeshes']:
            # Count multi-material elements
            if len(geo_result['submeshes']) > 1:
                multi_material_count += 1
            
            # Get primary material info for metadata (first submesh)
            primary_submesh = geo_result['submeshes'][0]
            metadata['Material'] = primary_submesh['material']
            if primary_submesh['color']:
                r, g, b, a = primary_submesh['color']
                metadata['Color_R'] = f"{r:.4f}"
                metadata['Color_G'] = f"{g:.4f}"
                metadata['Color_B'] = f"{b:.4f}"
                metadata['Color_A'] = f"{a:.4f}"
            
            # Store all materials for this element
            metadata['MaterialCount'] = str(len(geo_result['submeshes']))
            metadata['Materials'] = ';'.join([sm['material'] for sm in geo_result['submeshes']])
            
            all_elements_data.append({
                'id': element_id,
                'vertices': geo_result['vertices'],
                'normals': geo_result['normals'],
                'submeshes': geo_result['submeshes']
            })
            exported_count += 1
        else:
            failed_count += 1
            
    export_time = time.time() - export_start
    memory_tracker.report("After geometry processing")
    
    print(f"Exported: {exported_count}, Failed: {failed_count}")
    print(f"Multi-material elements: {multi_material_count}")
    
    # Print geometry errors if any
    if geo_errors:
        print(f"\nFirst {len(geo_errors)} geometry errors:")
        for err in geo_errors:
            print(f"  - {err}")
    
    # Debug: Print sample hierarchy info
    if all_metadata:
        print(f"\nSample element hierarchy data:")
        sample_count = 0
        for meta in all_metadata[:10]:
            if meta.get('IfcClass') not in ['IfcProject', 'IfcSite', 'IfcBuilding', 'IfcBuildingStorey']:
                print(f"  {meta.get('IfcClass', '?')}: Site='{meta.get('Site', '')}' Building='{meta.get('Building', '')}' Storey='{meta.get('Storey', '')}'")
                sample_count += 1
                if sample_count >= 5:
                    break
    
    # Add spatial hierarchy to metadata
    hierarchy_metadata = export_spatial_hierarchy(ifc_file)
    all_metadata.extend(hierarchy_metadata)
    print(f"Added {len(hierarchy_metadata)} spatial hierarchy records (Project/Site/Building)")
    
    # Export spaces and storeys (always, even without geometry)
    space_storey_meta, space_storey_geo = export_spaces_and_storeys(ifc_file, material_manager, settings)
    # Deduplicate: only add spaces/storeys not already present
    existing_gids = {m['GlobalId'] for m in all_metadata if m.get('GlobalId')}
    added_ss = 0
    for m in space_storey_meta:
        if m.get('GlobalId') and m['GlobalId'] not in existing_gids:
            all_metadata.append(m)
            existing_gids.add(m['GlobalId'])
            added_ss += 1
    print(f"Added {added_ss} space/storey metadata records")
    # Add space geometry if any
    for sg in space_storey_geo:
        if sg['id'] not in {ed['id'] for ed in all_elements_data}:
            all_elements_data.append(sg)
    
    # Extract relationships
    print("\nExtracting IFC relationships...")
    relationships = extract_relationships(ifc_file)
    rel_counts = {k: len(v) for k, v in relationships.items()}
    print(f"Relationships extracted: {rel_counts}")
    # Add unit scale to the relationship JSON
    relationships['_meta'] = {'unit_scale_to_metres': unit_scale, 'schema': schema}
    
    # Write output files
    print("\nWriting output files...")
    
    # Write combined OBJ with multi-material support
    total_verts, total_faces = write_combined_obj_multi_material(obj_path, all_elements_data, material_manager)
    print(f"OBJ written: {obj_path}")
    
    # Write MTL
    material_manager.write_mtl(mtl_path)
    print(f"MTL written: {mtl_path}")
    
    # Write CSV
    write_metadata_csv(csv_path, all_metadata)
    print(f"CSV written: {csv_path}")
    
    # Write relationships JSON
    with open(rel_path, 'w', encoding='utf-8') as f:
        json.dump(relationships, f, indent=2, ensure_ascii=False)
    print(f"Relationships JSON written: {rel_path}")
    
    # Final summary
    total_time = time.time() - start_time
    
    print("\n" + "=" * 60)
    print("Export Complete!")
    print("=" * 60)
    print(f"Elements: {exported_count}")
    print(f"Vertices: {total_verts:,}")
    print(f"Faces: {total_faces:,}")
    print(f"Materials: {len(material_manager.materials)}")
    print(f"Total Time: {total_time:.2f}s")
    print("=" * 60)
    
    return True


# ============================================================
# Entry Point
# ============================================================

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python ifc_to_unity_export.py <ifc_file_path> [output_directory]")
        sys.exit(1)
        
    ifc_path = sys.argv[1]
    output_dir = sys.argv[2] if len(sys.argv) > 2 else None
    
    success = export_ifc_to_unity(ifc_path, output_dir)
    sys.exit(0 if success else 1)
