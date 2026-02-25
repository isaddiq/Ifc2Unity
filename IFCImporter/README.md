# IFC Importer for Unity (BIMUniXchange)

This importer uses **IfcOpenShell** (Python) to convert IFC files to Unity-compatible formats with full support for:

- **Geometry**: Individual meshes for each IFC element
- **Colors & Materials**: Extracted from IFC material definitions
- **Metadata**: All IFC properties, quantities, and spatial hierarchy
- **IFC Schema Hierarchy**: Site → Building → Storey → Elements structure

## Prerequisites

### Python Installation

1. Install Python 3.8+ from https://www.python.org/
2. Add Python to your system PATH
3. Install required packages:

```bash
pip install ifcopenshell psutil
```

Or use the requirements file:

```bash
pip install -r Assets/BIMUniXchange/IFCImporter/Python/requirements.txt
```

### Verify Installation

Open a terminal and run:

```bash
python --version
python -c "import ifcopenshell; print(ifcopenshell.version)"
```

## Usage

### Via Unity Editor Menu

1. Open Unity Editor
2. Go to **BIMUniXchange → IFC Importer (IfcOpenShell)**
3. Select your IFC file using the Browse button
4. Configure import options as needed
5. Click **Import IFC File**

### Import Options

| Option                    | Description                                     |
| ------------------------- | ----------------------------------------------- |
| **Create IFC Hierarchy**  | Creates Site/Building/Storey structure          |
| **Assign Metadata**       | Adds Metadata component to each element         |
| **Apply Colors**          | Uses IFC material colors                        |
| **Create Mesh Colliders** | Adds MeshCollider to elements                   |
| **Group by Storey**       | Organizes elements under storeys                |
| **Group by IFC Class**    | Additionally groups by IfcWall, IfcColumn, etc. |

### Advanced Options

| Option                  | Description                    |
| ----------------------- | ------------------------------ |
| **Scale Factor**        | Scale geometry (default: 1.0)  |
| **Optimize Meshes**     | Weld vertices and optimize     |
| **Generate UVs**        | Create UVs for materials       |
| **Enable Transparency** | Support transparent materials  |
| **Default Shader**      | Shader for generated materials |

## Output Files

The Python script generates:

- `<filename>.obj` - Combined OBJ with all element geometry
- `<filename>.mtl` - Material definitions with colors
- `<filename>_metadata.csv` - All element metadata and properties

## Performance Logs

Import performance metrics are saved to:

```
Assets/BIMUniXchange/Logs/IFC_Import_<filename>_<timestamp>.txt
```

Log includes:

- Timing breakdown (Python conversion, mesh loading, scene building)
- Element statistics (count, success rate, mesh match rate)
- Hierarchy structure (sites, buildings, storeys)
- Memory usage
- IFC class breakdown
- Elements by storey

## Troubleshooting

### "Python not found"

- Ensure Python is in your system PATH
- Try using the full path to python.exe
- On Windows: `C:\Python39\python.exe`

### "IfcOpenShell not installed"

```bash
pip install ifcopenshell
```

### Large file performance

- IFC files with 10,000+ elements may take several minutes
- Monitor progress in the Unity Editor window
- Check the log file for detailed timing

### Missing geometry

- Some IFC elements (spaces, openings) are filtered out by design
- Check the CSV file to see which elements have geometry
- Elements without Body representation are excluded

## Architecture

```
IFCImporter/
├── Python/
│   ├── ifc_to_unity_export.py  # Python converter
│   └── requirements.txt         # Python dependencies
├── Scripts/
│   ├── IfcDataModels.cs        # Data structures
│   ├── CsvMetadataParser.cs    # CSV parsing
│   ├── ObjMeshLoader.cs        # OBJ mesh loading
│   └── IfcImportProcessor.cs   # Import logic
└── Editor/
    └── IfcImporterWindow.cs    # Unity Editor UI
```

## License

Part of BIMUniXchange toolkit for Unity BIM/XR development.
