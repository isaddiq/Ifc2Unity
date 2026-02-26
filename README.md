# Ifc2Unity

> A Unity Editor tool that converts **IFC (Industry Foundation Classes)** files directly into Unity scenes using **IfcOpenShell** (Python), preserving geometry, materials, metadata, and the full BIM spatial hierarchy.

<img width="548" height="793" alt="image" src="https://github.com/user-attachments/assets/9014a268-41cd-487b-9154-c49dd48a1100" />

<p align="center"><em>Ifc2Unity — Unity Editor Window</em></p>

---

## Features

| Category               | Details                                           |
| ---------------------- | ------------------------------------------------- |
| **Geometry**           | Individual meshes per IFC element via OBJ export  |
| **Materials & Colors** | Extracted from IFC material definitions           |
| **Metadata**           | All IFC properties, quantities, and attributes    |
| **Spatial Hierarchy**  | Site → Building → Storey → Elements structure     |
| **Performance Logs**   | Detailed timing and statistics per import session |

---

## Table of Contents

- [Prerequisites](#prerequisites)
- [Installation](#installation)
- [Usage](#usage)
- [Import Options](#import-options)
- [Output Files](#output-files)
- [Performance Logs](#performance-logs)
- [Troubleshooting](#troubleshooting)
- [Architecture](#architecture)
- [License](#license)

---

## Prerequisites

- **Unity** 2020.3 LTS or later
- **Python** 3.8 or later — [Download](https://www.python.org/)
- **IfcOpenShell** Python package

---

## Installation

### 1. Install Python

Download and install Python 3.8+ from [python.org](https://www.python.org/), and make sure to check **"Add Python to PATH"** during installation.

### 2. Install Required Python Packages

```bash
pip install ifcopenshell psutil
```

Or install from the included requirements file:

```bash
pip install -r Assets/BIMUniXchange/IFCImporter/Python/requirements.txt
```

### 3. Verify the Setup

```bash
python --version
python -c "import ifcopenshell; print('IfcOpenShell', ifcopenshell.version)"
```

---

## Usage

1. Open your Unity project.
2. In the menu bar, go to **BIMUniXchange → Ifc2Unity**.
3. Click **Browse** to select your `.ifc` file.
4. Configure the import options as needed.
5. Click **Import IFC File** and monitor progress in the Editor window.

---

## Import Options

### Core Options

| Option                    | Description                                                             |
| ------------------------- | ----------------------------------------------------------------------- |
| **Create IFC Hierarchy**  | Builds a Site → Building → Storey → Element GameObject hierarchy        |
| **Assign Metadata**       | Attaches a `Metadata` component with all IFC properties to each element |
| **Apply Colors**          | Reads IFC material colors and creates matching Unity materials          |
| **Create Mesh Colliders** | Adds a `MeshCollider` component to each imported element                |
| **Group by Storey**       | Parents elements under their corresponding storey GameObjects           |
| **Group by IFC Class**    | Additionally groups elements by type (e.g. `IfcWall`, `IfcColumn`)      |

### Advanced Options

| Option                  | Description                                            |
| ----------------------- | ------------------------------------------------------ |
| **Scale Factor**        | Uniform scale applied to all geometry (default: `1.0`) |
| **Optimize Meshes**     | Welds duplicate vertices and optimizes index buffers   |
| **Generate UVs**        | Automatically generates UV coordinates for materials   |
| **Enable Transparency** | Enables alpha support for transparent IFC materials    |
| **Default Shader**      | Unity shader assigned to generated materials           |

---

## Output Files

The Python conversion script writes the following files alongside the source `.ifc`:

| File                      | Content                                               |
| ------------------------- | ----------------------------------------------------- |
| `<filename>.obj`          | Combined mesh geometry for all elements               |
| `<filename>.mtl`          | Material definitions with extracted colors            |
| `<filename>_metadata.csv` | Full metadata table: element GUIDs, types, properties |

---

## Performance Logs

After each import, a detailed log is saved to:

```
Assets/BIMUniXchange/Logs/IFC_Import_<filename>_<timestamp>.txt
```

Each log includes:

- **Timing breakdown** — Python conversion, mesh loading, scene construction
- **Element statistics** — total count, success rate, mesh match rate
- **Hierarchy summary** — number of sites, buildings, and storeys
- **Memory usage** — peak memory during import
- **IFC class breakdown** — element counts per type
- **Elements by storey** — distribution across building levels

---

## Troubleshooting

### Python not found

- Confirm Python is available on your system PATH by running `python --version` in a terminal.
- On Windows, you can specify the full path to the executable (e.g. `C:\Python39\python.exe`) in the importer settings.

### IfcOpenShell not installed

```bash
pip install ifcopenshell
```

If `pip` installs to a different Python environment, use the full path:

```bash
C:\Python39\Scripts\pip.exe install ifcopenshell
```

### Slow imports on large files

- Files with 10,000+ elements can take several minutes to process.
- Monitor real-time progress in the Unity Editor window.
- Review the log file for a per-stage timing breakdown.

### Missing geometry for some elements

- Spaces, openings, and annotation elements are intentionally excluded.
- Check `<filename>_metadata.csv` to see the full element list.
- Only elements with a **Body** geometric representation are imported as meshes.

---

## Architecture

```
IFCImporter/
├── Python/
│   ├── ifc_to_unity_export.py   # IFC → OBJ/MTL/CSV converter (IfcOpenShell)
│   └── requirements.txt          # Python dependencies
├── Scripts/
│   ├── IfcDataModels.cs         # Shared data structures and models
│   ├── CsvMetadataParser.cs     # Parses the generated CSV metadata
│   ├── ObjMeshLoader.cs         # Loads OBJ meshes into Unity
│   └── IfcImportProcessor.cs    # Orchestrates the full import pipeline
└── Editor/
    └── IfcImporterWindow.cs     # Unity Editor custom window UI
```

---

## License

**Ifc2Unity** is part of the **BIMUniXchange** toolkit for Unity BIM and XR development.

