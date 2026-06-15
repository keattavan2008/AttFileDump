# AVEVA E3D High-Performance Attribute Exporter (AttFileDump)

A blazing-fast, enterprise-grade .NET (C#) attribute extraction engine for AVEVA E3D. Designed as a direct, high-performance replacement for the default `mattdump` PML macro.

By shifting the heavy lifting—such as database traversal, string manipulation, XML parsing, and disk I/O—from interpreted PML to compiled .NET, and utilizing a highly optimized v2.0 database interop architecture, this utility handles massive enterprise-scale data with ease. It bypasses native PML bottlenecks to deliver exponential speedups (exact performance gains scale dynamically based on database size and your E3D host architecture, x86 vs. x64), often reducing multi-hour native extractions down to a matter of minutes while perfectly matching the native AVEVA E3D output format.

## 🚀 Key Features

- **Extreme Performance & Interop Bypass:** Utilizes intelligent attribute caching. In Strict XML mode, the engine entirely bypasses the expensive `element.GetAttributes()` system call, interrogating the database only for specific keys to virtually eliminate .NET/Unmanaged latency.
- **Dynamic Tree Pruning (Skip Logic):** Define rules to instantly drop entire hierarchy branches (e.g., skip elements and all their children if `:void` = `True`), with O(1) hash-set lookups to save massive amounts of processing time.
- **XML-Driven Filtering & Aliasing:** Control exactly which attributes are exported for specific element types using an external XML file. Seamlessly rename attributes on the fly using the `alias` tag. 
- **Smart Regex File Naming:** Automatically sanitizes complex AVEVA names (like `/SITE-PIPING-AREA01`) into safe file names using customizable delimiters. Intelligently handles empty prefixes to ensure clean file names.
- **Multi-File Output:** Automatically split massive database exports into individual text files per root element, or combine them into a single master file.
- **Enterprise Logging:** Fully integrated with **Serilog**. Generates daily rolling logs, tracks execution times, gracefully handles missing catalog data, and shifts heavy tree-pruning logs to `Debug` level to prevent I/O blocking.

## 📋 Prerequisites

- AVEVA E3D Design (x86 E3D - 2.1 & 3.1 | x64 - 4.2 / UE (Hybird v3.x, 4.x ,..) compile the DLL according to your E3D version)
- `.NET SDK` targeting `.NET Framework 4.8` (or matching your E3D installation)
- `Serilog.dll` and `Serilog.Sinks.File.dll` (Available in the default AVEVA UE installation directory, for older version of E3D download manually and place it with compiled AttFileDump.dll and invoke in DesignAddin.xml)

## 📦 Required Assemblies (Dependencies)

To successfully compile and run this project, you must reference the following assemblies. These cannot be downloaded via NuGet (except Serilog) and must be copied from your local AVEVA E3D installation directory:

**AVEVA Core Libraries:**
- `Aveva.Core.Database.dll`
- `Aveva.Core.Database.Filters.dll`
- `PMLNet.dll`
- `PMLNetUtilities.dll`

**Third-Party Libraries:**
- `Serilog.dll` (v4.2.0 or compatible)
- `Serilog.Sinks.File.dll`

*Note: For the cleanest cross-platform compilation, place these files in a local `lib/` directory at the root of your project.*

## 🛠️ Installation

1. Compile the `AttFileDump` project into a `.dll` using `dotnet build -c Release`.
2. Place the resulting `.dll` (and ensure Serilog DLLs are present) in your AVEVA `%AVEVA_DESIGN_USER%` or standard macro executable directory.
3. Import the DLL into your E3D environment using PML: `import 'AttFileDump'`

## 🧩 Application Framework Registration (designAddins.xml)

While the `import 'AttFileDump'` PML command works dynamically, best practice for enterprise deployments is to register the DLL via the AVEVA Application Framework (AAF). This ensures the assembly is loaded into the AppDomain safely at startup.

Create a file named `designAddins.xml` (or `AttFileDump.addin`) and place it in your `%AVEVA_DESIGN_USER%\Addins\` or `%AVEVA_DESIGN_WORK%\Addins\` folder. 

Depending on your target environment, use the appropriate configuration below:

### For AVEVA E3D 2.1 & 3.1 and UE (3.x, 4.x)
The legacy E3D framework uses a straightforward Add-in registration. Since this project is a `[PMLNetCallable]` library and does not implement the `IAddin` UI interface.

#### For E3D 2.1 & 3.1
```xml
<?xml version="1.0" encoding="utf-8"?>
<ArrayOfString xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <string>AttFileDump</string>
  <string>Serilog</string>
  <string>Serilog.Sinks.File</string>
</ArrayOfString>
```

#### For UE (3.x, 4.x ..)

```xml
<?xml version="1.0" encoding="utf-8"?>
<ArrayOfString xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <string>AttFileDump</string>
</ArrayOfString>
```

## 💻 Usage (PML Interface)

The engine is controlled entirely via PML. The C# logic remains safely encapsulated. Below is a standard PML execution macro:

```pml
import 'AttFileDump'
handle any
  $P CRITICAL ERROR: Failed to load AttFileDump library.
  return
endhandle

  using namespace 'AttFileDump'
  -- 1. Instantiate the Exporter
  !exporter = object AttDump()

  -- optional settings which can be set
  !exporter.SetExportUnsets(false)  $* default set to true if this is not initialised explicitly
  !exporter.SetExportTube(false) $* default set to true if this is not initialised explicitly

  -- 2. Define Target Elements
  !exporter.SetUseElements(true)
  !exporter.AddElementToExport('/SITE-PIPING-AREA01')
  !exporter.AddElementToExport('/SITE-EQUIP-AREA01')

  -- Alternative to 2.
  -- using the CE
  -- !exporter.SetUseCe(true) $* this is used to extract current element only
  -- export attributes from the drawlist at the moment not available.

  -- 3. Dynamic Tree Pruning (Skip Logic)
  -- Skips any element (and its children) where :void is True or 1
  !exporter.SetSkipAttribute(':void')
  !exporter.AddSkipValue('True')
  !exporter.AddSkipValue('1')

  -- 4. Output Naming and Location
  !exporter.SetOutputDirectory('C:\temp\E3D_Exports')
  !exporter.SetFilePrefix('Project_XYZ') -- Leave blank ('') to omit prefix
  !exporter.SetNameDelimiter('_')
  !exporter.SetSingleFileOutput(false) -- Generates one file per Site

  -- 5. XML Configuration
  !exporter.SetXmlConfig('C:\temp\E3D_Exports\ExportConfig.xml')

  -- TRUE = High-Speed Bypass. Elements NOT in the XML are skipped.
  -- FALSE = Elements NOT in the XML dump ALL their default attributes.
  !exporter.SetStrictXmlMode(false)

  -- 6. Execute Extraction
  $P Starting high-speed attribute extraction...
  !exporter.ExecuteExtraction()

  -- Optional, to use before extraction on which method to use.
  -- 7. Generate Sample XML File
  !exporter.GenerateSampleXml()

  handle any
    $P Extraction encountered an error: $!!error.text
  elsehandle none
    $P Extraction completed successfully. Check the Serilog output for details.
  endhandle
```

## ⚙️ XML Configuration Guide

You can filter exactly what data is exported, and even rename attributes on the fly, using a simple XML configuration file.

Tip: You can generate a sample XML file dynamically by calling `!exporter.GenerateSampleXml()` from PML.

Example `ExportConfig.xml`:

## XML

```
<?xml version="1.0" encoding="utf-8"?>
<AttDumpConfig>
  <Element type="SITE">
    <Attribute alias="Site Name">NAME</Attribute>
    <Attribute>DESC</Attribute>
    <Attribute>PURP</Attribute>
  </Element>
  <Element type="PIPE">
    <Attribute>NAME</Attribute>
    <Attribute>BORE</Attribute>
    <Attribute alias="Specification">PSPE</Attribute>
  </Element>
  <Element type="EQUIPMENT">
    <Attribute>NAME</Attribute>
    <Attribute>DESC</Attribute>
    <Attribute alias="Site Name">name of Site</Attribute>
  </Element>
</AttDumpConfig>
```

## Strict Mode vs. Non-Strict Mode

The behavior of the XML filter is heavily optimized and controlled by `SetStrictXmlMode(bool)`:

* **Strict Mode (true):** Maximum Performance. If an element type `(e.g., VALV)` is not listed in the XML, its attributes are completely ignored. The engine bypasses the AVEVA attribute dictionary entirely and only fetches the exact keys defined in the XML.

* **Non-Strict Mode (false):** Data Discovery. If an element type is not listed in the XML, the engine defaults to exporting all of its valid system attributes. If an element is listed in the XML, the engine exports all system attributes, applies your custom aliases, and explicitly injects any custom UDAs you defined in the XML.

## 📝 Logging

Logging is handled automatically by Serilog. Logs are generated in the same folder as your OutputDirectory with the naming convention `AttDumpLog_YYYYMMDD.txt`.

The log records:

- Execution start/end times and total processing milliseconds.

- Elements successfully exported and their destination file paths.

- Tree pruning events (available in Debug level to reduce disk I/O).

- Non-fatal warnings for missing catalog data `(e.g., unable to resolve MTXX or SPRE)`.

---
