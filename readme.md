# AVEVA E3D High-Performance Attribute Exporter (AttFileDump)

A blazing-fast, enterprise-grade .NET (C#) attribute extraction engine for AVEVA E3D. Designed as a direct, high-performance replacement for the default `mattdump` PML macro.

By shifting the heavy lifting—such as database traversal, string manipulation, XML parsing, and disk I/O—from interpreted PML to compiled .NET, and utilizing a highly optimized v2.1 database interop architecture, this utility handles massive enterprise-scale data with ease. It bypasses native PML bottlenecks to deliver exponential speedups (exact performance gains scale dynamically based on database size and your E3D host `architecture, x86 vs. x64`), often reducing multi-hour native extractions down to a matter of minutes while perfectly matching the native AVEVA E3D output format.

## 🚀 Key Features

- **Extreme Performance & Interop Bypass:** Utilizes intelligent attribute caching. In Strict XML mode, the engine entirely bypasses the expensive `element.GetAttributes()` system call, interrogating the database only for specific keys to virtually eliminate .NET/Unmanaged latency.
- **Global Attribute & Expression Inheritance:** Define general attributes (like `NAME`, `DESC`) or complex PML expressions (like `NAME OF SITE`) once in a global configuration block. They automatically cascade to all extracted element types without repetitive manual mapping.
- **Dynamic Tree Pruning (Skip Logic):** Define rules to instantly drop entire hierarchy branches (e.g., skip elements and all their children if `:void` = `True`), with O(1) hash-set lookups to save massive amounts of processing time.
- **XML-Driven Filtering & Aliasing:** Control exactly what is exported for specific element types using an external XML file. Seamlessly rename attributes or override global configuration rules on the fly using the `alias` tag.
- **Smart Regex File Naming:** Automatically sanitizes complex AVEVA names (like `/SITE-PIPING-AREA01`) into safe file names using customizable delimiters. Intelligently handles empty prefixes to ensure clean file names.
- **Multi-File Output:** Automatically split massive database exports into individual text files per root element, or combine them into a single master file.
- **Enterprise Logging & Monitoring:** Uses the shared **AvevaAddins.Common** library, like every other AVEVA add-in: daily rolling log files, optional central Seq logging, and realtime status (progress per site, heartbeat, success/failure) in the AVEVA Status Monitor.
- **Safe Output:** Every file is written as `.tmp` and renamed only when complete. If a site fails, its previous file is kept, the partial output stays in the `.tmp` file, and the other sites are still exported.

## 📋 Prerequisites

- AVEVA E3D Design 3.1 (32-bit). The default Release build targets x86. For a 64-bit AVEVA product use the `Release|X64` configuration.
- `.NET SDK` targeting `.NET Framework 4.8.1`
- The shared library **AvevaAddins.Common** checked out next to this project (`..\AvevaAddins.Common`)

## 📦 Required Assemblies (Dependencies)

**AVEVA Core Libraries** (copied from the E3D 3.1 installation; the project looks in `C:\Temp\E3D3.1\`, override with `/p:AvevaDllDir=...`):
- `Aveva.ApplicationFramework.dll`, `Aveva.ApplicationFramework.Presentation.dll`
- `Aveva.Core.Database.dll`, `Aveva.Core.Database.Filters.dll`, `Aveva.Core.Presentation.dll`
- `PMLNet.dll`, `PMLNetUtilities.dll`

**Shared library and its dependencies** (restored and copied to `bin\Release` by the build):
- `AvevaAddins.Common.dll`
- `Serilog.dll` (assembly 2.0.0.0, Serilog 2.10/2.11) and `Serilog.Sinks.File.dll` (5.0.0.0). E3D 3.1 does not ship Serilog.
- Only when Seq is used: `Serilog.Sinks.Seq.dll`, `Serilog.Sinks.PeriodicBatching.dll`, `Serilog.Formatting.Compact.dll`
- `Newtonsoft.Json.dll` 13 is shipped by E3D 3.1; do not overwrite E3D's copy.

> Serilog 4.x is deliberately **not** used: it needs newer `System.Diagnostics.DiagnosticSource`,
> `System.Threading.Channels` and `System.Memory` versions than E3D 3.1 ships.

## 🛠️ Installation

1. Build with restore (this also builds AvevaAddins.Common):
   ```powershell
   dotnet msbuild AttFileDump.csproj /restore /t:Build /p:Configuration=Release
   ```
2. Copy from `bin\Release` to the add-in folder: `AttFileDump.dll`, `AvevaAddins.Common.dll`, `Serilog.dll`, `Serilog.Sinks.File.dll`, and the three Seq DLLs if Seq is used. Remove any older `Serilog.dll` 4.x from that folder.
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
  <string>AvevaAddins.Common</string>
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

## ⚙️ Logging, Seq & Status Hub

Logging, Seq and the realtime status hub are configured the same way as in every other add-in
(AvevaAdminToolkit uses the same element names). Put them directly under `<AttDumpConfig>` in the
export XML; each add-in keeps its own XML file:

```xml
<AttDumpConfig>
  <LogDirectory>C:\Temp\AVEVA\Logs</LogDirectory>      <!-- empty = the output directory -->
  <LogLevel>Information</LogLevel>                       <!-- Verbose, Debug, Information, Warning, Error, Fatal -->
  <SeqServerUrl>http://seq-server:5341</SeqServerUrl>    <!-- empty = Seq off -->
  <StatusHub>
    <Url>http://status-hub:5080/api/status</Url>         <!-- empty = hub off -->
    <AccessToken>this server's publisher token</AccessToken>
  </StatusHub>
  ...
</AttDumpConfig>
```

A wrong value never stops the export: an unknown log level falls back to Information, an unusable
log folder falls back to the output folder (then `C:\temp`), and an empty or unreachable Seq or
hub only writes a log line. If `AccessToken` is empty, the environment variable
`ATTFILEDUMP_STATUSHUB_TOKEN` is used.

PML can override each value (the PML value wins over the XML):

| PML method | Overrides |
| :--- | :--- |
| `SetLogLevelName('Debug')` | `LogLevel` |
| `SetLogLevel(INTEGER)` | `LogLevel`, kept for existing macros: 0 = Debug, 1 = Information, 2 = Warning, 3 = Error |
| `SetLogDirectory('C:\Logs')` | `LogDirectory` |
| `SetSeqServerUrl('')` | `SeqServerUrl` (empty switches Seq off) |
| `SetStatusHubUrl(...)`, `SetStatusHubAccessToken(...)` | `StatusHub/Url`, `StatusHub/AccessToken` |

In the AVEVA Status Monitor, each `ExecuteExtraction()` call is one run of source **AttFileDump**:
one progress step per root element (site), the current site and element count, a heartbeat during
long sites, and Completed or Failed at the end.

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

  -- 6. Logging (optional; overrides LogLevel in the XML, default Information)
  !exporter.SetLogLevelName('Warning')

  -- 7. Execute Extraction
  $P Starting high-speed attribute extraction...
  !exporter.ExecuteExtraction()

  -- Optional, to use before extraction on which method to use.
  -- 8. Generate Sample XML File
  !exporter.GenerateSampleXml()                          $* writes C:\temp\sampleConfiguration.xml
  !exporter.GenerateSampleXmlTo('C:\temp\E3D_Exports')   $* folder or .xml file name; created if missing

  handle any
    $P Extraction encountered an error: $!!error.text
  elsehandle none
    $P Extraction completed successfully. Check the log for details.
  endhandle
```

### What is exported, and what happens on errors

- **Root elements:** `SetUseCe(true)` exports the current element and its members (or every site
  when the CE is WORLD). `AddElementToExport(...)` exports each named element and its members. A
  name inside another selected element gets no separate file (it is already in its owner's file).
  A name that does not exist is logged and fails the run, after the other elements are exported.
- **Files:** each file is written as `<name>.txt.tmp` and renamed to `<name>.txt` when complete.
- **A failing site** is logged and the export continues with the next site. That site's previous
  file is kept and its partial output stays in the `.tmp` file. At the end, `ExecuteExtraction()`
  raises an error listing the failed sites, so the PML `handle` block sees it.
- **Configuration problems** stop the run before any file is touched: an `SetXmlConfig` file that
  does not exist or cannot be read, or an invalid skip attribute. Invalid entries inside the XML
  (unknown element type or attribute) are skipped with a warning.
- The output folder is created when it does not exist.

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

Logs are written by the shared AvevaAddins.Common logger to `LogDirectory` (default: the output
directory) as `<PROJECT>_AttFileDump_YYYYMMDD.log`, in the same line format as the other add-ins,
and to Seq when `SeqServerUrl` is set. The logger no longer replaces Serilog's global `Log.Logger`,
so it cannot interfere with other components in the E3D process.

The log records:

- Execution start/end times, total duration, files written, element count and failed sites.

- Each site exported, its destination file and element count.

- Tree pruning events (available in Debug level to reduce disk I/O).

- Warnings for configuration entries that could not be resolved, and errors per failed site.

---
