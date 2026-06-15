# AVEVA E3D High-Performance Attribute Exporter (AttFileDump)

A blazing-fast, enterprise-grade .NET (C#) attribute extraction engine for AVEVA E3D. Designed as a direct, high-performance replacement for the default `mattdump` PML macro.

By shifting the heavy lifting—such as database traversal, string manipulation, XML parsing, and disk I/O—from interpreted PML to compiled .NET, this utility reduces export times by up to **~67x** (e.g., reducing a 2+ minute multi-site extraction down to ~1-2 seconds) while perfectly matching the native AVEVA E3D output format.

## 🚀 Key Features

- **Extreme Performance:** Utilizes intelligent attribute caching and bulk C++ database scanning (`DBElementCollection`) to virtually eliminate .NET/Unmanaged Interop boundary latency.
- **Dynamic Tree Pruning (Skip Logic):** Define rules to instantly drop entire hierarchy branches (e.g., skip elements and all their children if `:void` = `True`), saving massive amounts of processing time.
- **XML-Driven Filtering:** Control exactly which attributes are exported for specific element types using an external XML file. Supports both Strict and Non-Strict modes.
- **Regex File Naming:** Automatically sanitizes complex AVEVA names (like `/SITE-PIPING-AREA01`) into safe file names using customizable delimiters.
- **Multi-File Output:** Automatically split massive database exports into individual text files per root element, or combine them into a single master file.
- **Enterprise Logging:** Fully integrated with **Serilog** to generate daily rolling logs, tracking execution times and gracefully capturing catalog or missing-attribute errors (like missing `MTXX` or `APOS` references) without crashing the extraction.
- **Headless Optimized:** Designed to run flawlessly in TTY mode for automated nightly batch processing.

## 📋 Prerequisites

- AVEVA E3D Design (x86 E3D - 2.1 & 3.1 | x64 - 4.2 / UE compile the DLL according to your E3D version)
- `.NET Framework` (Version matching your E3D installation prefered .4.8.1)
- `Serilog.dll` and `Serilog.Sinks.File.dll` (Available in the default AVEVA E3D installation directory)

## 🛠️ Installation

1. Compile the `AttFileDump` project into a `.dll`.
2. Place the resulting `.dll` (and ensure Serilog DLLs are present) in your AVEVA `%AVEVA_DESIGN_USER%` or standard macro executable directory.
3. Import the DLL into your E3D environment using PML: `import 'AttFileDump'`

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
  !exporter.SetFilePrefix('Project_XYZ')
  !exporter.SetNameDelimiter('_')
  !exporter.SetSingleFileOutput(false) -- Generates one file per Site

  -- 5. XML Configuration
  !exporter.SetXmlConfig('C:\temp\E3D_Exports\ExportConfig.xml')

  -- TRUE = Elements NOT in the XML are entirely skipped
  -- FALSE = Elements NOT in the XML dump ALL their attributes
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

You can filter exactly what data is exported using a simple XML configuration file.

Tip: You can generate a sample XML file dynamically by calling !exporter.GenerateSampleXml() from PML.

Example ExportConfig.xml:

## XML

```
<?xml version="1.0" encoding="utf-8"?>
<AttDumpConfig>
  <Element type="SITE">
    <Attribute>NAME</Attribute>
    <Attribute>DESC</Attribute>
    <Attribute>PURP</Attribute>
  </Element>
  <Element type="PIPE">
    <Attribute>NAME</Attribute>
    <Attribute>BORE</Attribute>
    <Attribute>PSPE</Attribute>
  </Element>
  <Element type="EQUIPMENT">
    <Attribute>NAME</Attribute>
    <Attribute>DESC</Attribute>
    <Attribute>FUNC</Attribute>
  </Element>
</AttDumpConfig>
```

## Strict Mode vs. Non-Strict Mode

The behavior of the XML filter is controlled by SetStrictXmlMode(bool):

## Strict Mode (true):

If an element type (e.g., VALV) is not listed in the XML, it is completely ignored. Only SITE and PIPE (from the example above) would be exported.

## Non-Strict Mode (false):

If an element type is not listed in the XML, the engine defaults to exporting all of its valid attributes. Elements that are listed in the XML will be strictly filtered.

## 📝 Logging

Logging is handled automatically by Serilog. Logs are generated in the same folder as your OutputDirectory with the naming convention AttDumpLog_YYYYMMDD.txt.

The log records:

Execution start/end times and total processing milliseconds.

Elements successfully exported and their destination file paths.

Elements pruned by the skip logic.

Non-fatal warnings for missing catalog data (e.g., unable to resolve MTXX or SPRE).

---
