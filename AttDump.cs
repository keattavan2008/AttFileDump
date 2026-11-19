using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aveva.Core.PMLNet;
using Aveva.Core.Database;
using Aveva.Core.Database.Filters;
using AvevaAddins.Configuration;
using AvevaAddins.Logging;
using AvevaAddins.Realtime;

namespace AttFileDump
{
    [PMLNetCallable]
    public class AttDump
    {
        private const string Delimiter = ":=";
        private const string Separator = "&end&";

        // Logging and status (shared AvevaAddins.Common library)
        private const string AddinName = "AttFileDump";
        private const string StatusHubTokenEnvironmentVariable = "ATTFILEDUMP_STATUSHUB_TOKEN";
        private const string DefaultSampleDirectory = @"C:\temp";
        private const string SampleFileName = "sampleConfiguration.xml";
        private const int ElementProgressInterval = 10000;

        /// <summary>Forwards to the logger of the running extraction (dropped between runs).</summary>
        private static readonly AddinLogRelay Log = new AddinLogRelay();

        /// <summary>State file + realtime status hub; one run per ExecuteExtraction call.</summary>
        private static readonly RunStatusReporter Status = new RunStatusReporter(new RunStatusReporterOptions
        {
            Source = AddinName,
            // Add-in version from this assembly; the host application (E3D 3.1, UE, ...) is
            // detected by the shared library from the running executable.
            SourceVersion = HostApplicationInfo.GetAssemblyVersion(typeof(AttDump).Assembly),
            AccessTokenEnvironmentVariable = StatusHubTokenEnvironmentVariable,
            Log = Log,
            ClearOnProcessExit = true
        });

        // Compiled Regex and HashSets for maximum loop performance
        private static readonly Regex SafeNameRegex = new Regex(@"[/\\?%*:|""<>]+", RegexOptions.Compiled);
        private static readonly HashSet<string> IgnoreValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "unset", "=0/0" };

        // Statically cache indent strings to prevent thousands of memory allocations
        private static readonly string[] Indents = Enumerable.Range(0, 50).Select(i => new string(' ', i)).ToArray();

        // Core Parameters
        private bool _useCe = true;
        private bool _useElements;
        private bool _useDrawList; // Reserved for future extension
        private bool _exportUnsets = true;
        private bool _exportTube = true;

        // Naming & Directory Parameters
        private string _outputDirectory = @"C:\temp";
        private string _filePrefix = "";
        private string _nameDelimiter = "_";
        private bool _singleFileOutput;

        // XML Configuration Parameters
        private string _xmlConfigPath = "";
        private bool _strictXmlMode;
        private Dictionary<DbElementType, Dictionary<string, string>> _xmlFilter;
        private readonly HashSet<DbElementType> _includedElements = new HashSet<DbElementType>();
        private readonly HashSet<DbAttribute> _excludedAttributes = new HashSet<DbAttribute>();
        private readonly Dictionary<string, string> _globalAttributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _elementNames = new HashSet<string>();

        // Caching structures
        private readonly Dictionary<DbElementType, TypeExportProfile> _attributeCache = new Dictionary<DbElementType, TypeExportProfile>();

        // Shared settings set from PML. Null = not set: the XML value (or the default) is used.
        private string _logDirectoryOverride;
        private string _logLevelOverride;
        private string _seqServerUrlOverride;
        private string _statusHubUrlOverride;
        private string _statusHubAccessTokenOverride;

        // Current run (for progress reporting from inside the hierarchy walk)
        private string _projectCode = "UNKNOWN";
        private string _currentRootName = "";
        private int _currentRootStartCount;

        private readonly struct ExportItem
        {
            public readonly DbAttribute DbAttr;
            public readonly DbExpression DbExpr;
            public readonly string ExportName;
            public bool IsExpression => DbExpr != null;
            public ExportItem(DbAttribute attr, string exportName) { DbAttr = attr; DbExpr = null; ExportName = exportName; }
            public ExportItem(DbExpression expr, string exportName) { DbAttr = null; DbExpr = expr; ExportName = exportName; }
        }

        private class TypeExportProfile
        {
            public readonly List<ExportItem> Attributes = new List<ExportItem>();
            public int MaxNameLength;
            public bool SuppressPseudos;
        }

        // Dynamic Skip Logic
        private string _skipAttributeName = "";
        private HashSet<string> _skipValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pre-resolved system attributes
        private DbAttribute _dtxrAttr;
        private DbAttribute _mtxxAttr;
        private DbAttribute _flnnAttr;

        [PMLNetCallable()]
        public AttDump() { }

        [PMLNetCallable()]
        public void Assign(AttDump that)
        {
            _useCe = that._useCe;
            _useElements = that._useElements;
            _useDrawList = that._useDrawList;
            _exportUnsets = that._exportUnsets;
            _exportTube = that._exportTube;
            _outputDirectory = that._outputDirectory;
            _filePrefix = that._filePrefix;
            _nameDelimiter = that._nameDelimiter;
            _singleFileOutput = that._singleFileOutput;
            _xmlConfigPath = that._xmlConfigPath;
            _strictXmlMode = that._strictXmlMode;
            _skipAttributeName = that._skipAttributeName;

            _skipValues = new HashSet<string>(that._skipValues, StringComparer.OrdinalIgnoreCase);

            _elementNames.Clear();
            foreach (string name in that._elementNames) _elementNames.Add(name);

            // Log level and the shared logging / status settings
            _logDirectoryOverride = that._logDirectoryOverride;
            _logLevelOverride = that._logLevelOverride;
            _seqServerUrlOverride = that._seqServerUrlOverride;
            _statusHubUrlOverride = that._statusHubUrlOverride;
            _statusHubAccessTokenOverride = that._statusHubAccessTokenOverride;
        }

        #region PML Configuration Methods

        [PMLNetCallable()] public void SetUseCe(bool use) { _useCe = use; if (use) { _useElements = false; _useDrawList = false; } }
        [PMLNetCallable()] public void SetUseElements(bool use) { _useElements = use; if (use) { _useCe = false; _useDrawList = false; } }
        [PMLNetCallable()] public void SetExportUnsets(bool export) { _exportUnsets = export; }
        [PMLNetCallable()] public void SetExportTube(bool export) { _exportTube = export; }
        [PMLNetCallable()] public void AddElementToExport(string elementName) { if (!string.IsNullOrEmpty(elementName)) _elementNames.Add(elementName); }
        [PMLNetCallable()] public void ClearElements() { _elementNames.Clear(); }

        [PMLNetCallable()] public void SetOutputDirectory(string dir) { _outputDirectory = dir; }
        [PMLNetCallable()] public void SetFilePrefix(string prefix) { _filePrefix = prefix; }
        [PMLNetCallable()] public void SetNameDelimiter(string delimiter) { _nameDelimiter = string.IsNullOrEmpty(delimiter) ? "_" : delimiter; }
        [PMLNetCallable()] public void SetSingleFileOutput(bool isSingle) { _singleFileOutput = isSingle; }

        [PMLNetCallable()] public void SetXmlConfig(string xmlPath) { _xmlConfigPath = xmlPath; }
        [PMLNetCallable()] public void SetStrictXmlMode(bool strict) { _strictXmlMode = strict; }
        [PMLNetCallable()] public void SetSkipAttribute(string attributeName) { _skipAttributeName = attributeName; }
        [PMLNetCallable()] public void AddSkipValue(string triggerValue) { if (!string.IsNullOrEmpty(triggerValue)) _skipValues.Add(triggerValue); }

        /// <summary>
        /// Kept for existing macros: 0 = Debug, 1 = Information, 2 = Warning, 3 = Error.
        /// Overrides LogLevel from the XML. New macros can use SetLogLevelName.
        /// </summary>
        [PMLNetCallable()]
        public void SetLogLevel(int level)
        {
            switch (level)
            {
                case 0: _logLevelOverride = "Debug"; break;
                case 1: _logLevelOverride = "Information"; break;
                case 2: _logLevelOverride = "Warning"; break;
                case 3: _logLevelOverride = "Error"; break;
                default: _logLevelOverride = "Information"; break;
            }
        }

        /// <summary>Verbose, Debug, Information, Warning, Error or Fatal (same names as the XML LogLevel).</summary>
        [PMLNetCallable()] public void SetLogLevelName(string level) { _logLevelOverride = level; }

        /// <summary>Overrides LogDirectory from the XML. Default: the output directory.</summary>
        [PMLNetCallable()] public void SetLogDirectory(string dir) { _logDirectoryOverride = dir; }

        /// <summary>Overrides SeqServerUrl from the XML; an empty value switches Seq off.</summary>
        [PMLNetCallable()] public void SetSeqServerUrl(string url) { _seqServerUrlOverride = url; }

        /// <summary>Overrides StatusHub/Url from the XML; an empty value switches the hub off.</summary>
        [PMLNetCallable()] public void SetStatusHubUrl(string url) { _statusHubUrlOverride = url; }

        /// <summary>Overrides StatusHub/AccessToken from the XML.</summary>
        [PMLNetCallable()] public void SetStatusHubAccessToken(string token) { _statusHubAccessTokenOverride = token; }

        #endregion

        /// <summary>Writes the sample configuration to C:\temp\sampleConfiguration.xml.</summary>
        [PMLNetCallable()]
        public void GenerateSampleXml()
        {
            GenerateSampleXmlTo("");
        }

        /// <summary>
        /// Writes the sample configuration. The path can be a folder (sampleConfiguration.xml is
        /// written in it) or an .xml file name; empty uses C:\temp. Missing folders are created.
        /// </summary>
        [PMLNetCallable()]
        public void GenerateSampleXmlTo(string path)
        {
            string samplePath;
            if (string.IsNullOrWhiteSpace(path))
                samplePath = Path.Combine(DefaultSampleDirectory, SampleFileName);
            else if (path.Trim().EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                samplePath = path.Trim();
            else
                samplePath = Path.Combine(path.Trim(), SampleFileName);

            string directory = Path.GetDirectoryName(samplePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string xmlContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<AttDumpConfig>
  <!-- Shared add-in settings: the same element names are used in every add-in's XML.
       Empty or missing values use the default; a wrong value never stops the export. -->
  <!-- Log folder; empty = the output directory. -->
  <LogDirectory></LogDirectory>
  <!-- Run state files (crash detection); empty = C:\Temp\AVEVA\State, shared by all add-ins. -->
  <StateDirectory>C:\Temp\AVEVA\State</StateDirectory>
  <!-- Verbose, Debug, Information, Warning, Error or Fatal. -->
  <LogLevel>Information</LogLevel>
  <!-- Optional Seq server, e.g. http://seq-server:5341; empty = Seq off. -->
  <SeqServerUrl></SeqServerUrl>
  <!-- Optional realtime status hub, e.g. http://status-hub:5080/api/status; empty Url = off. -->
  <StatusHub>
    <Url></Url>
    <AccessToken></AccessToken>
  </StatusHub>

  <IncludedElements>
    <Type>SITE</Type>
    <Type>ZONE</Type>
    <Type>PIPE</Type>
    <Type>EQUI</Type>
    <Type>STRU</Type>
    <Type>BRAN</Type>
    <Type>ATTA</Type>
    <Type>BEND</Type>
    <Type>BRCO</Type>
    <Type>CAP</Type>
    <Type>CLOS</Type>
    <Type>COUP</Type>
    <Type>CROS</Type>
    <Type>DUCT</Type>
    <Type>ELBO</Type>
    <Type>FBLI</Type>
    <Type>FILT</Type>
    <Type>FLAN</Type>
    <Type>FTUB</Type>
    <Type>GASK</Type>
    <Type>INST</Type>
    <Type>LJSE</Type>
    <Type>OLET</Type>
    <Type>PCOM</Type>
    <Type>REDU</Type>
    <Type>SHU</Type>
    <Type>STIF</Type>
    <Type>TEE</Type>
    <Type>TRAP</Type>
    <Type>UNIO</Type>
    <Type>VALV</Type>
    <Type>VENT</Type>
    <Type>VFWA</Type>
    <Type>VTWA</Type>
    <Type>WELD</Type>
    <Type>ELCONN</Type>
    <Type>NOZZ</Type>
    <Type>PTRA</Type>
    <Type>SUBE</Type>
    <Type>FRMW</Type>
    <Type>SBFR</Type>
    <Type>FLOO</Type>
    <Type>FITT</Type>
    <Type>GWALL</Type>
    <Type>SCTN</Type>
    <Type>GENSEC</Type>
    <Type>PANE</Type>
    <Type>STWALL</Type>
    <Type>WALL</Type>
    <Type>TMPL</Type>
    <Type>EXTR</Type>
    <Type>BOX</Type>
    <Type>CONE</Type>
    <Type>RTOR</Type>
    <Type>REVO</Type>
    <Type>CYLI</Type>
    <Type>SUBS</Type>
  </IncludedElements>

  <ExcludedAttributes>
    <Attribute>UWMTXT</Attribute>
    <Attribute>DSCO</Attribute>
    <Attribute>PTSP</Attribute>
    <Attribute>INSC</Attribute>
    <Attribute>SKEY</Attribute>
    <Attribute>STLR</Attribute>
    <Attribute>ORRF</Attribute>
    <Attribute>STMF</Attribute>
    <Attribute>INVF</Attribute>
    <Attribute>UUIDIA</Attribute>
    <Attribute>PROPST</Attribute>
    <Attribute>SPRE</Attribute>
    <Attribute>FSTAT</Attribute>
    <Attribute>MDSYSF</Attribute>
    <Attribute>INPRTR</Attribute>
    <Attribute>OUPRTR</Attribute>
    <Attribute>FAREA</Attribute>
    <Attribute>FPLINE</Attribute>
    <Attribute>FDRA</Attribute>
    <Attribute>FREV</Attribute>
    <Attribute>FRDR</Attribute>
    <Attribute>FSTAT</Attribute>
    <Attribute>PLANU</Attribute>
    <Attribute>MODU</Attribute>
    <Attribute>RLSTOR</Attribute>
    <Attribute>:NOOFSAVE</Attribute>
    <Attribute>:REVDATA</Attribute>
    <Attribute>:SCOPE</Attribute>
    <Attribute>:SDNFREVNO</Attribute>
    <Attribute>:SDNFTRANO</Attribute>
    <Attribute>:SDNFTRRVNO</Attribute>
    <Attribute>:FABELIST</Attribute>
    <Attribute>:FABMODNR</Attribute>
    <Attribute>:FABTARGET</Attribute>
    <Attribute>:FABSOURCE</Attribute>
    <Attribute>:FABDECODE</Attribute>
    <Attribute>:FABPRID</Attribute>
    <Attribute>:FABSTID</Attribute>
    <Attribute>:FABCLID</Attribute>
    <Attribute>:FABEFID</Attribute>
    <Attribute>:FABTRRVNO</Attribute>
    <Attribute>:FABTRANO</Attribute>
    <Attribute>:FABREVNO</Attribute>
    <Attribute>:FABITIME</Attribute>
    <Attribute>:FABIDATE</Attribute>
    <Attribute>:FABETIME</Attribute>
    <Attribute>:FABEDATE</Attribute>
    <Attribute>:SDNFMODNR</Attribute>
    <Attribute>:SDNFEDATE</Attribute>
    <Attribute>:SDNFETIME</Attribute>
    <Attribute>:SDNFIDATE</Attribute>
    <Attribute>:SDNFITIME</Attribute>
    <Attribute>:SDNFEFID</Attribute>
    <Attribute>:SDNFCLID</Attribute>
    <Attribute>:SDNFSTID</Attribute>
    <Attribute>:SDNFPRID</Attribute>
    <Attribute>:SDNFDECODE</Attribute>
    <Attribute>:SDNFSOURCE</Attribute>
    <Attribute>:SDNFTARGET</Attribute>
    <Attribute>:SDNFELIST</Attribute>
    <Attribute>:MDSBARS</Attribute>
    <Attribute>:MDSCP1</Attribute>
    <Attribute>:MDSCP2</Attribute>
    <Attribute>:MDSCP3</Attribute>
    <Attribute>:MDSEDIT</Attribute>
    <Attribute>:MDSHANGGROUP</Attribute>
    <Attribute>:MDSHANGROT</Attribute>
    <Attribute>:MDSHV</Attribute>
    <Attribute>:MDSMAXBORE</Attribute>
    <Attribute>:MDSMAXPIPES</Attribute>
    <Attribute>:MDSMINBORE</Attribute>
    <Attribute>:MDSRANGE</Attribute>
    <Attribute>:MDSAUTOFILE</Attribute>
  </ExcludedAttributes>

  <IncludedAttributes>
    <Attribute alias=""Tube Length"">TULE</Attribute>
    <Attribute alias=""Cut Length"">CLLE</Attribute>
    <Expression alias=""Site Name"">NAME OF SITE</Expression>
  </IncludedAttributes>

  <Element type=""EQUI"">
    <Expression alias=""Site Purpose"">Purpose OF SITE</Expression>
  </Element>
</AttDumpConfig>";
            File.WriteAllText(samplePath, xmlContent);
        }

        // ------------------------------------------------------------------
        // Shared settings, logging and status
        // ------------------------------------------------------------------

        /// <summary>
        /// Shared settings: defaults, then the XML values, then the PML overrides. Never throws;
        /// corrections (e.g. an unknown log level) are returned for logging.
        /// </summary>
        private AddinCommonSettings BuildSettings(XDocument configDocument, List<string> notes)
        {
            var settings = new AddinCommonSettings { LogDirectory = _outputDirectory ?? string.Empty };
            if (configDocument != null)
                settings.ReadFromLinq(configDocument.Root);

            if (!string.IsNullOrWhiteSpace(_logDirectoryOverride)) settings.LogDirectory = _logDirectoryOverride;
            if (_logLevelOverride != null) settings.LogLevel = _logLevelOverride;
            if (_seqServerUrlOverride != null) settings.SeqServerUrl = _seqServerUrlOverride;
            if (_statusHubUrlOverride != null) settings.StatusHubUrl = _statusHubUrlOverride;
            if (_statusHubAccessTokenOverride != null) settings.StatusHubAccessToken = _statusHubAccessTokenOverride;

            notes.AddRange(settings.Normalize());
            return settings;
        }

        /// <summary>
        /// Creates the run's logger. If the configured log folder cannot be used, the output folder
        /// and then C:\temp are tried, so a wrong LogDirectory never stops the export.
        /// </summary>
        private AddinLogger StartLogging(string projectCode, AddinCommonSettings settings, List<string> notes)
        {
            var candidates = new List<string>();
            foreach (string directory in new[] { settings.LogDirectory, _outputDirectory, DefaultSampleDirectory })
                if (!string.IsNullOrWhiteSpace(directory) &&
                    !candidates.Contains(directory.Trim(), StringComparer.OrdinalIgnoreCase))
                    candidates.Add(directory.Trim());

            foreach (string directory in candidates)
            {
                try
                {
                    AddinLogger logger = AddinLogger.Create(new AddinLoggerOptions
                    {
                        AddinName = AddinName,
                        ProjectName = projectCode,
                        LogDirectory = directory,
                        MinimumLevel = settings.MinimumLevel,
                        SeqServerUrl = settings.SeqServerUrl
                    });
                    if (!string.Equals(directory, settings.LogDirectory, StringComparison.OrdinalIgnoreCase))
                        notes.Add("Log directory '" + settings.LogDirectory + "' could not be used; logging to '" + directory + "'.");
                    settings.LogDirectory = directory;
                    return logger;
                }
                catch (Exception ex)
                {
                    notes.Add("Log directory '" + directory + "' could not be used: " + ex.Message);
                }
            }

            return null; // export continues without a log file
        }

        private void StartStatus(AddinCommonSettings settings)
        {
            // XML StateDirectory, else the shared C:\Temp\AVEVA\State (never inside the log folder).
            string stateDirectory = settings.EffectiveStateDirectory;
            try
            {
                Status.ConfigureStateDirectory(stateDirectory);
            }
            catch (Exception ex)
            {
                Log.Warn("State directory {StateDirectory} could not be used ({Reason}); no state file is written.",
                    stateDirectory, ex.Message);
            }

            Status.ConfigureHub(settings.StatusHubUrl, settings.StatusHubAccessToken); // never throws
        }

        private static string GetProjectCode()
        {
            try
            {
                Project project = Project.CurrentProject;
                return project != null && !string.IsNullOrWhiteSpace(project.Code) ? project.Code : "UNKNOWN";
            }
            catch (Exception)
            {
                return "UNKNOWN";
            }
        }

        private static string GetElementName(DbElement element)
        {
            try { return element.GetAsString(DbAttributeInstance.FLNM); }
            catch { return element.ToString(); }
        }

        // ------------------------------------------------------------------
        // Export configuration
        // ------------------------------------------------------------------

        /// <summary>
        /// Loads the export rules. Invalid entries are skipped with a warning; a document that
        /// cannot be processed fails the run before any output file is touched.
        /// </summary>
        private void LoadXmlConfig(XDocument doc)
        {
            _xmlFilter = null;
            _includedElements.Clear();
            _excludedAttributes.Clear();
            _globalAttributes.Clear();

            if (doc == null) return;

            try
            {
                _xmlFilter = new Dictionary<DbElementType, Dictionary<string, string>>();

                var includedElementsNode = doc.Descendants("IncludedElements").FirstOrDefault();
                if (includedElementsNode != null)
                {
                    foreach (var child in includedElementsNode.Elements())
                    {
                        string typeStr = child.Value;
                        if (!string.IsNullOrEmpty(typeStr))
                        {
                            try { _includedElements.Add(DbElementType.GetElementType(typeStr)); }
                            catch (Exception) { Log.Warn("Could not load included element type: {ElementType}", typeStr); }
                        }
                    }
                }

                var excludedAttributesNode = doc.Descendants("ExcludedAttributes").FirstOrDefault();
                if (excludedAttributesNode != null)
                {
                    foreach (var child in excludedAttributesNode.Elements())
                    {
                        string attrStr = child.Value;
                        if (!string.IsNullOrEmpty(attrStr))
                        {
                            try { _excludedAttributes.Add(DbAttribute.GetDbAttribute(attrStr)); }
                            catch (Exception) { Log.Warn("Could not load excluded attribute: {Attribute}", attrStr); }
                        }
                    }
                }

                var includedAttributesNode = doc.Descendants("IncludedAttributes").FirstOrDefault();
                if (includedAttributesNode != null)
                {
                    foreach (var att in includedAttributesNode.Elements())
                    {
                        if (att.Name == "Attribute")
                        {
                            string originalName = att.Value;
                            string alias = att.Attribute("alias")?.Value ?? originalName.ToUpper();
                            _globalAttributes[originalName] = alias;
                        }
                        else if (att.Name == "Expression")
                        {
                            string exprStr = att.Value;
                            string alias = att.Attribute("alias")?.Value ?? exprStr;
                            _globalAttributes["EXP:" + exprStr] = alias;
                        }
                    }
                }

                foreach (var el in doc.Descendants("Element"))
                {
                    string typeStr = el.Attribute("type")?.Value;
                    if (string.IsNullOrEmpty(typeStr)) continue;

                    DbElementType type;
                    try { type = DbElementType.GetElementType(typeStr); }
                    catch (Exception)
                    {
                        // One wrong <Element type> must not drop the rules that follow it.
                        Log.Warn("Could not load element rule: unknown element type {ElementType}; this rule is skipped.", typeStr);
                        continue;
                    }

                    var atts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var att in el.Elements())
                    {
                        if (att.Name == "Attribute")
                        {
                            string originalName = att.Value;
                            string alias = att.Attribute("alias")?.Value ?? originalName.ToUpper();
                            atts[originalName] = alias;
                        }
                        else if (att.Name == "Expression")
                        {
                            string exprStr = att.Value;
                            string alias = att.Attribute("alias")?.Value ?? exprStr;
                            atts["EXP:" + exprStr] = alias;
                        }
                    }
                    _xmlFilter[type] = atts;
                }
                Log.Info("Loaded XML Configuration from {ConfigPath}. Rules={Rules} Whitelist={Whitelist} Blacklist={Blacklist}",
                    _xmlConfigPath, _xmlFilter.Count, _includedElements.Count, _excludedAttributes.Count);
            }
            catch (Exception ex)
            {
                // Exporting with half the rules would silently produce different files.
                throw new InvalidDataException("Failed to load XML Configuration " + _xmlConfigPath + ": " + ex.Message, ex);
            }
        }

        // ------------------------------------------------------------------
        // Extraction
        // ------------------------------------------------------------------

        /// <summary>
        /// Exports every root element (site, zone or named element) to its own file, or to one file.
        /// Each file is written as .tmp and renamed when complete, so a failed root keeps the
        /// previous file and leaves its partial output in the .tmp file; the other roots continue.
        /// Throws at the end when the run could not start or any root failed.
        /// </summary>
        [PMLNetCallable()]
        public void ExecuteExtraction()
        {
            var watch = Stopwatch.StartNew();
            _projectCode = GetProjectCode();

            // 1. Read the configuration file once (export rules + shared settings). Problems are
            //    remembered and reported after logging has started.
            XDocument configDocument = null;
            Exception configError = null;
            if (!string.IsNullOrWhiteSpace(_xmlConfigPath))
            {
                if (!File.Exists(_xmlConfigPath))
                    configError = new FileNotFoundException("Export configuration file not found: " + _xmlConfigPath, _xmlConfigPath);
                else
                {
                    try { configDocument = XDocument.Load(_xmlConfigPath); }
                    catch (Exception ex)
                    {
                        configError = new InvalidDataException(
                            "Export configuration file could not be read: " + _xmlConfigPath + " (" + ex.Message + ")", ex);
                    }
                }
            }

            // 2. Logging and status (never stop the export).
            var notes = new List<string>();
            AddinCommonSettings settings = BuildSettings(configDocument, notes);
            AddinLogger logger = StartLogging(_projectCode, settings, notes);
            Log.Target = logger;
            StartStatus(settings);

            Log.Info("=== Extraction Started === Project={Project} MDB={Mdb}", _projectCode, GetMdbName());
            foreach (string note in notes)
                Log.Warn("{SettingsWarning}", note);

            var failedRoots = new List<string>();
            int rootCount = 0;
            int filesWritten = 0;
            int processedCount = 0;
            bool runStarted = false;
            Exception runError = null;

            try
            {
                if (configError != null) throw configError;
                if (string.IsNullOrWhiteSpace(_outputDirectory))
                    throw new InvalidOperationException("Output directory is not set.");

                LoadXmlConfig(configDocument);
                _attributeCache.Clear();

                _dtxrAttr = DbAttribute.GetDbAttribute("DTXR");
                _mtxxAttr = DbAttribute.GetDbAttribute("MTXX");
                _flnnAttr = DbAttribute.GetDbAttribute("FLNN");

                DbAttribute skipAttr = null;
                if (!string.IsNullOrEmpty(_skipAttributeName))
                {
                    try { skipAttr = DbAttribute.GetDbAttribute(_skipAttributeName); }
                    catch (Exception ex)
                    {
                        // Without it, elements that should be skipped would be exported.
                        throw new InvalidOperationException("Skip attribute '" + _skipAttributeName + "' is not a valid attribute.", ex);
                    }
                }

                Directory.CreateDirectory(_outputDirectory);

                DbElement currentCe = CurrentElement.Element;
                List<DbElement> sortedRoots = ResolveRoots(currentCe, failedRoots);
                if (sortedRoots.Count == 0) throw new InvalidOperationException("No valid elements found to export.");
                rootCount = sortedRoots.Count;

                // One progress step per root element.
                Status.Progress.BeginRun(rootCount + failedRoots.Count);
                Status.Progress.BeginTask(rootCount + failedRoots.Count);
                foreach (string missing in failedRoots) Status.Progress.CompleteStep(false);
                PublishRunStarted(rootCount);
                runStarted = true;

                // Cleared per root: bounded by the largest root instead of growing over the whole run.
                HashSet<string> processed = new HashSet<string>(250000);

                if (_singleFileOutput)
                {
                    string baseFileName = string.IsNullOrEmpty(_filePrefix) ? "Export" : _filePrefix;
                    string fileName = Path.Combine(_outputDirectory, $"{baseFileName}.txt");
                    string tempFileName = fileName + ".tmp";
                    int failedBefore = failedRoots.Count;

                    using (StreamWriter writer = new StreamWriter(tempFileName, false, Encoding.UTF8, 81920))
                    {
                        WriteHeader(writer, currentCe);
                        for (int i = 0; i < sortedRoots.Count; i++)
                            ExportRoot(sortedRoots[i], i, sortedRoots.Count, writer, processed, skipAttr, ref processedCount, failedRoots);
                    }

                    if (failedRoots.Count == failedBefore)
                    {
                        ReplaceFile(tempFileName, fileName);
                        filesWritten = 1;
                        Log.Info("Successfully exported {RootCount} root elements to {FileName}", sortedRoots.Count, fileName);
                    }
                    else
                    {
                        Log.Error(null, "Single-file export incomplete: the previous {FileName} is kept; the partial output is in {TempFile}.",
                            fileName, tempFileName);
                    }
                }
                else
                {
                    for (int i = 0; i < sortedRoots.Count; i++)
                    {
                        DbElement root = sortedRoots[i];
                        string rootName = GetElementName(root);
                        string fileName = null;
                        string tempFileName = null;
                        try
                        {
                            string safeName = SafeNameRegex.Replace(rootName, _nameDelimiter);
                            if (safeName.StartsWith(_nameDelimiter)) safeName = safeName.Substring(_nameDelimiter.Length);

                            string baseFileName = string.IsNullOrEmpty(_filePrefix)
                                ? safeName
                                : $"{_filePrefix}{_nameDelimiter}{safeName}";

                            fileName = Path.Combine(_outputDirectory, $"{baseFileName}.txt");
                            tempFileName = fileName + ".tmp";
                            int failedBefore = failedRoots.Count;

                            using (StreamWriter writer = new StreamWriter(tempFileName, false, Encoding.UTF8, 81920))
                            {
                                WriteHeader(writer, root);
                                ExportRoot(root, i, sortedRoots.Count, writer, processed, skipAttr, ref processedCount, failedRoots,
                                    completeStep: false);
                            }

                            if (failedRoots.Count != failedBefore)
                            {
                                Log.Error(null, "Export of {Root} failed: the previous {FileName} is kept; the partial output is in {TempFile}.",
                                    rootName, fileName, tempFileName);
                                Status.Progress.CompleteStep(false);
                                continue;
                            }

                            ReplaceFile(tempFileName, fileName);
                            filesWritten++;
                            Log.Info("Exported {Root} to {FileName}. Elements={Elements}", rootName, fileName,
                                processedCount - _currentRootStartCount);
                            Status.Progress.CompleteStep(true);
                        }
                        catch (Exception ex)
                        {
                            // File could not be created or renamed: keep going with the next root.
                            if (!failedRoots.Contains(rootName)) failedRoots.Add(rootName);
                            Log.Error(ex, "Export of {Root} failed: the previous {FileName} is kept; partial output (if any) is in {TempFile}.",
                                rootName, fileName, tempFileName);
                            Status.Progress.CompleteStep(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                runError = ex;
                Log.Error(ex, "Critical failure during extraction process.");
            }
            finally
            {
                watch.Stop();
                bool success = runError == null && failedRoots.Count == 0;
                if (!runStarted) PublishRunStarted(rootCount);
                PublishRunCompleted(success, rootCount, filesWritten, processedCount, failedRoots, runError, watch.Elapsed);
                Status.Progress.EndTask();
                Status.Progress.EndRun();

                Log.Info(@"=== Extraction Completed in {Duration} === Success={Success} Files={Files} Elements={Elements} FailedRoots={FailedRoots}",
                    watch.Elapsed.ToString(@"hh\:mm\:ss\.fff"), success, filesWritten, processedCount, failedRoots.Count);
                Status.Shutdown(); // ends this run on the hub and removes the state file
                Log.Target = null;
                if (logger != null) logger.Dispose();
            }

            if (runError != null)
                ExceptionDispatchInfo.Capture(runError).Throw();
            if (failedRoots.Count > 0)
                throw new InvalidOperationException("Extraction finished with " + failedRoots.Count +
                    " failed root element(s): " + string.Join(", ", failedRoots) +
                    ". Their previous files were kept; partial output is in .tmp files. See the log for details.");
        }

        /// <summary>
        /// Writes one root's hierarchy. Errors are caught here, so one damaged element only fails
        /// its own root; the root is added to <paramref name="failedRoots"/>.
        /// </summary>
        private void ExportRoot(DbElement root, int index, int total, StreamWriter writer, HashSet<string> processed,
            DbAttribute skipAttr, ref int processedCount, List<string> failedRoots, bool completeStep = true)
        {
            string rootName = GetElementName(root);
            _currentRootName = rootName;
            _currentRootStartCount = processedCount;
            Status.Progress.SetPhase("EXPORT", index + 1, total);
            Status.UpdateState(_projectCode, "Exporting " + rootName);

            bool succeeded = false;
            try
            {
                processed.Clear();
                ProcessHierarchy(root, 0, writer, processed, skipAttr, ref processedCount);
                succeeded = true;
            }
            catch (Exception ex)
            {
                failedRoots.Add(rootName);
                Log.Error(ex, "Export of {Root} stopped after {Elements} elements.", rootName, processedCount - _currentRootStartCount);
            }

            if (completeStep)
                Status.Progress.CompleteStep(succeeded);
        }

        /// <summary>
        /// Roots to export. A name below site level exports only that element and its members.
        /// A root inside another selected root is not exported separately (it is already in the
        /// owner's file), so no empty files are written. Names that are not found are reported.
        /// </summary>
        private List<DbElement> ResolveRoots(DbElement currentCe, List<string> failedRoots)
        {
            var candidates = new List<DbElement>();

            if (_useCe)
            {
                if (!currentCe.IsValid) throw new InvalidOperationException("Current Element is not valid.");
                if (currentCe.GetElementType() == DbElementTypeInstance.WORLD)
                {
                    TypeFilter filt = new TypeFilter(DbElementTypeInstance.SITE);
                    DBElementCollection collection = new DBElementCollection(currentCe, filt);
                    foreach (DbElement site in collection) candidates.Add(site);
                }
                else
                {
                    candidates.Add(currentCe);
                }
            }
            else if (_useElements)
            {
                foreach (string name in _elementNames)
                {
                    DbElement ele = null;
                    try { ele = DbElement.GetElement(name); }
                    catch (Exception) { /* reported below */ }

                    if (ele != null && ele.IsValid) candidates.Add(ele);
                    else
                    {
                        failedRoots.Add(name);
                        Log.Error(null, "Element {Element} was not found in the project; it is not exported.", name);
                    }
                }
            }

            // Unique roots, keyed by reference.
            var selected = new Dictionary<string, DbElement>(StringComparer.OrdinalIgnoreCase);
            foreach (DbElement candidate in candidates)
            {
                string reference = candidate.GetAsString(DbAttributeInstance.REF);
                if (!selected.ContainsKey(reference)) selected.Add(reference, candidate);
            }

            var roots = new List<DbElement>();
            foreach (KeyValuePair<string, DbElement> pair in selected)
            {
                DbElement owner = pair.Value.Owner;
                string coveredBy = null;
                while (owner.IsValid && owner.GetElementType() != DbElementTypeInstance.WORLD)
                {
                    if (selected.ContainsKey(owner.GetAsString(DbAttributeInstance.REF)))
                    {
                        coveredBy = GetElementName(owner);
                        break;
                    }
                    owner = owner.Owner;
                }

                if (coveredBy != null)
                    Log.Info("{Element} is inside {Owner}, which is also exported; it has no separate file.",
                        GetElementName(pair.Value), coveredBy);
                else
                    roots.Add(pair.Value);
            }

            return roots.OrderBy(GetDepthFromWorld).ToList();
        }

        private void PublishRunStarted(int roots)
        {
            Status.PublishEvent(_projectCode, RunStatusReporter.RunStartedEvent, "Attribute extraction", null,
                new Dictionary<string, object>
                {
                    { "Mode", _useCe ? "CE" : _useElements ? "Elements" : "None" },
                    { "Roots", roots },
                    { "SingleFile", _singleFileOutput },
                    { "StrictXml", _strictXmlMode },
                    { "OutputDirectory", _outputDirectory ?? string.Empty },
                    { "ConfigFile", _xmlConfigPath ?? string.Empty },
                    { RunProgress.CompletedKey, 0 },
                    { RunProgress.TotalKey, roots },
                    { RunProgress.FailedKey, 0 },
                    { RunProgress.PhaseKey, string.Empty }
                });
        }

        private void PublishRunCompleted(bool success, int roots, int files, int elements, List<string> failedRoots,
            Exception runError, TimeSpan duration)
        {
            var data = new Dictionary<string, object>
            {
                { "Success", success },
                { "Roots", roots },
                { "FilesWritten", files },
                { "Elements", elements },
                { "FailedRoots", string.Join(", ", failedRoots) },
                { "OutputDirectory", _outputDirectory ?? string.Empty },
                { "DurationSeconds", Math.Round(duration.TotalSeconds, 1) }
            };
            RunProgressSnapshot progress = Status.Progress.Snapshot();
            if (progress != null)
                foreach (KeyValuePair<string, object> pair in progress.ToData())
                    data[pair.Key] = pair.Value;

            string message = runError != null ? runError.Message
                : failedRoots.Count > 0 ? failedRoots.Count + " root element(s) failed"
                : null;
            Status.PublishEvent(_projectCode, RunStatusReporter.RunCompletedEvent, "Attribute extraction finished", message, data);
        }

        private static string GetMdbName()
        {
            try { return MDB.CurrentMDB != null ? MDB.CurrentMDB.Name : "UNKNOWN"; }
            catch (Exception) { return "UNKNOWN"; }
        }

        /// <summary>Swaps a completed .tmp file in for the previous output.</summary>
        private static void ReplaceFile(string tempFileName, string fileName)
        {
            if (File.Exists(fileName))
                File.Replace(tempFileName, fileName, null);
            else
                File.Move(tempFileName, fileName);
        }

        private void ProcessHierarchy(DbElement element, int currentDepth, StreamWriter writer, HashSet<string> processed, DbAttribute skipAttr, ref int processedCount)
        {
            string elRef = element.GetAsString(DbAttributeInstance.REF);
            if (!processed.Add(elRef)) return;

            processedCount++;
            if (processedCount % 250000 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            if (processedCount % ElementProgressInterval == 0)
            {
                Status.UpdateState(_projectCode, string.Format("Exporting {0} ({1:N0} elements)",
                    _currentRootName, processedCount - _currentRootStartCount));
            }

            if (skipAttr != null && element.IsAttributeValid(skipAttr))
            {
                string attrVal = string.Empty;
                try { attrVal = element.GetAsString(skipAttr); } catch (Exception ex)
                { Log.Error(ex, "Could not read skip attribute {SkipAttributeName} of {Element}", _skipAttributeName, element); }

                if (!string.IsNullOrEmpty(attrVal) && _skipValues.Contains(attrVal))
                {
                    Log.Debug("Pruned: Skipped {ElementType} {Element} ({SkipAttributeName} = {AttrVal})", element.GetElementType(), element, _skipAttributeName, attrVal);
                    return;
                }
            }

            DbElementType elementType = element.GetElementType();
            if (elementType == DbElementTypeInstance.TUBE && !_exportTube) return;

            if (_includedElements.Count > 0 && !_includedElements.Contains(elementType)) return;

            WriteElementData(element, elementType, currentDepth, writer);

            DbElement[] children = element.Members();
            foreach (DbElement child in children)
            {
                ProcessHierarchy(child, currentDepth + 1, writer, processed, skipAttr, ref processedCount);
            }

            WriteIndentedLine(writer, currentDepth * 2, "END");
        }

        private void WriteElementData(DbElement element, DbElementType elementType, int depth, StreamWriter writer)
        {
            int tab = depth * 2;
            int iTab = tab + 2;
            string elementName;

            try { elementName = element.GetAsString(DbAttributeInstance.FLNM); }
            catch { elementName = element.ToString(); }

            WriteIndentedLine(writer, tab, "NEW " + elementName);

            TypeExportProfile profile = GetCachedProfile(element, elementType);

            if (_strictXmlMode && profile.Attributes.Count == 0) return;

            int maxNameLength = profile.MaxNameLength;
            bool injectBranchPseudos = false;
            bool injectTubePseudos = false;

            if (!profile.SuppressPseudos && elementType != DbElementTypeInstance.WORLD)
            {
                DbElement owner = element.Owner;
                if (owner.IsValid && owner.GetElementType() == DbElementTypeInstance.BRANCH)
                {
                    injectBranchPseudos = true;
                    if (4 > maxNameLength) maxNameLength = 4;
                }
                if (elementType == DbElementTypeInstance.TUBE)
                {
                    injectTubePseudos = true;
                    if (4 > maxNameLength) maxNameLength = 4;
                }
            }

            int attrSize = maxNameLength + 3;
            StringBuilder sb = new StringBuilder(128);

            foreach (ExportItem exportItem in profile.Attributes)
            {
                string attrValue;

                try
                {
                    // Use the native method that Rider already verified works!
                    attrValue = exportItem.IsExpression ? element.EvaluateString(exportItem.DbExpr) :
                        // Standard attribute extraction
                        element.GetAsString(exportItem.DbAttr);
                }
                catch
                {
                    // If the attribute is unset or the PML expression is invalid for this element,
                    // silently catch the error and move to the next attribute.
                    continue;
                }

                WriteFormattedAttribute(attrValue, exportItem.ExportName, iTab, attrSize, writer, sb);
            }

            if (injectBranchPseudos)
            {
                if (_dtxrAttr != null) WriteSingleAttribute(element, _dtxrAttr, "DTXR", iTab, attrSize, writer, sb);
                if (_mtxxAttr != null) WriteSingleAttribute(element, _mtxxAttr, "MTXX", iTab, attrSize, writer, sb);
            }
            if (injectTubePseudos && _flnnAttr != null)
            {
                WriteSingleAttribute(element, _flnnAttr, "FLNN", iTab, attrSize, writer, sb);
            }
        }

        private void WriteSingleAttribute(DbElement element, DbAttribute attr, string exportName, int iTab, int attrSize, StreamWriter writer, StringBuilder sb)
        {
            string attrValue;
            try { attrValue = element.GetAsString(attr); }
            catch { return; }
            WriteFormattedAttribute(attrValue, exportName, iTab, attrSize, writer, sb);
        }

        private void WriteFormattedAttribute(string attrValue, string exportName, int iTab, int attrSize, StreamWriter writer, StringBuilder sb)
        {
            if (string.IsNullOrEmpty(attrValue)) return;

            if (ShouldExportAttribute(attrValue, _exportUnsets))
            {
                attrValue = ProcessAttributeValue(attrValue);
                bool hasDollarUnderscore = attrValue.Contains("$$_");
                if (hasDollarUnderscore) attrValue = attrValue.Replace("$$_", "DOLLARU");

                sb.Clear();
                sb.Append(' ', iTab).Append(exportName).Append(Delimiter);

                // MANUALLY calculate padding to avoid .PadRight() string allocation
                int currentLength = sb.Length;
                int targetLength = iTab + attrSize + Delimiter.Length + 2;
                if (targetLength > currentLength)
                {
                    sb.Append(' ', targetLength - currentLength);
                }

                sb.Append(attrValue);

                // Perform replacements DIRECTLY in the StringBuilder to avoid intermediate string allocations
                if (hasDollarUnderscore) sb.Replace("DOLLARU", "$$_");
                sb.Replace("DOLLARL", "$$L");

                writer.WriteLine(sb.ToString());
            }
        }

        private TypeExportProfile GetCachedProfile(DbElement element, DbElementType type)
        {
            if (_attributeCache.TryGetValue(type, out TypeExportProfile profile)) return profile;

            profile = new TypeExportProfile();
            bool hasXml = _xmlFilter != null;
            bool isInXml = hasXml && _xmlFilter.ContainsKey(type);

            var combinedRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (hasXml)
            {
                foreach (var kvp in _globalAttributes)
                {
                    combinedRules[kvp.Key] = kvp.Value;
                }

                if (isInXml)
                {
                    foreach (var kvp in _xmlFilter[type])
                    {
                        combinedRules[kvp.Key] = kvp.Value; // Overwrite globals with element specific rules
                    }
                }
            }

            bool hasCombinedRules = combinedRules.Count > 0;

            if (_strictXmlMode)
            {
                // SCENARIO 3: Strict Mode (ONLY XML attributes)
                if (hasCombinedRules)
                {
                    profile.SuppressPseudos = true;
                    foreach (var kvp in combinedRules)
                    {
                        if (kvp.Key.StartsWith("EXP:"))
                        {
                            string exprStr = kvp.Key.Substring(4);
                            try
                            {
                                DbExpression expr = DbExpression.Parse(exprStr);
                                profile.Attributes.Add(new ExportItem(expr, kvp.Value));
                            }
                            catch (Exception ex){ Log.Warn("Invalid PML Expression '{Expression}': {Reason}", exprStr, ex.Message); }
                        }
                        else
                        {
                            DbAttribute explicitAttr = TryGetAttribute(kvp.Key, type);
                            if (explicitAttr != null && !_excludedAttributes.Contains(explicitAttr))
                                profile.Attributes.Add(new ExportItem(explicitAttr, kvp.Value));
                        }
                    }
                }
            }
            else
            {
                // SCENARIOS 1 & 2: Non-Strict Mode (Default attributes + XML overrides/additions)
                DbAttribute[] rawAttrs = element.GetAttributes();
                HashSet<string> processedAttrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (DbAttribute attr in rawAttrs)
                {
                    if (attr.Type == DbAttributeType.STRINGARRAY) continue;
                    if (_excludedAttributes.Contains(attr)) continue;

                    processedAttrs.Add(attr.Name);

                    if (hasCombinedRules && combinedRules.TryGetValue(attr.Name, out string alias))
                    {
                        profile.Attributes.Add(new ExportItem(attr, alias));
                    }
                    else
                    {
                        profile.Attributes.Add(new ExportItem(attr, attr.Name.ToUpper()));
                    }
                }

                // If admin added custom UDA keys in XML that weren't caught by the raw GetAttributes() array
                if (hasCombinedRules)
                {
                    foreach (var kvp in combinedRules)
                    {
                        if (kvp.Key.StartsWith("EXP:"))
                        {
                            string exprStr = kvp.Key.Substring(4);
                            try
                            {
                                DbExpression expr = DbExpression.Parse(exprStr);
                                profile.Attributes.Add(new ExportItem(expr, kvp.Value));
                            }
                            catch (Exception ex){ Log.Warn("Invalid PML Expression '{Expression}': {Reason}", exprStr, ex.Message); }
                        }
                        else
                        {
                            if (!processedAttrs.Contains(kvp.Key))
                            {
                                DbAttribute explicitAttr = TryGetAttribute(kvp.Key, type);
                                if (explicitAttr != null && !_excludedAttributes.Contains(explicitAttr))
                                    profile.Attributes.Add(new ExportItem(explicitAttr, kvp.Value));
                            }
                        }
                    }
                }
            }

            if (profile.Attributes.Count > 0)
            {
                profile.MaxNameLength = profile.Attributes.Max(a => a.ExportName.Length);
            }

            _attributeCache[type] = profile;
            return profile;
        }

        /// <summary>
        /// Resolves an attribute named in the XML. An unknown name (e.g. a mistyped UDA) is skipped
        /// with a warning instead of stopping the export; profiles are cached per type, so the
        /// warning appears once per element type.
        /// </summary>
        private static DbAttribute TryGetAttribute(string name, DbElementType type)
        {
            try { return DbAttribute.GetDbAttribute(name); }
            catch (Exception ex)
            {
                Log.Warn("Attribute {Attribute} from the XML could not be resolved for {ElementType} ({Reason}); it is skipped.",
                    name, type, ex.Message);
                return null;
            }
        }

        private int GetDepthFromWorld(DbElement element)
        {
            int d = 0; DbElement p = element;
            while (p.IsValid && p.GetElementType() != DbElementTypeInstance.WORLD) { d++; p = p.Owner; }
            return d;
        }

        private void WriteHeader(StreamWriter writer, DbElement ce)
        {
            writer.WriteLine($"AVEVA_Attributes_File v1.0 , start: NEW , end: END , name_end: {Delimiter} , sep: {Separator}");
            writer.WriteLine("NEW Header Information");
            string date = DateTime.Now.ToString("dd MMM yyyy");
            string time = DateTime.Now.ToString("HH:mm:ss");
            writer.WriteLine($"  Source{Delimiter} AVEVA E3D Design Data {Separator} Date{Delimiter} {date} {Separator} Time{Delimiter} {time}");
            string mdbName = MDB.CurrentMDB != null ? MDB.CurrentMDB.Name : "UNKNOWN";
            string prjCode = Project.CurrentProject != null ? Project.CurrentProject.Code : "UNKNOWN";
            string ceName = ce.IsValid ? ce.GetAsString(DbAttributeInstance.FLNM) : "UNKNOWN";
            writer.WriteLine($"  Project{Delimiter} {prjCode} {Separator} MDB{Delimiter} {mdbName} {Separator} Element{Delimiter} {ceName}");
            writer.WriteLine("END");
        }

        // Uses pre-allocated strings for memory efficiency
        private void WriteIndentedLine(StreamWriter writer, int indent, string text)
        {
            writer.Write(indent < Indents.Length ? Indents[indent] : new string(' ', indent));
            writer.WriteLine(text);
        }

        private string ProcessAttributeValue(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Contains("$$V") || trimmed.Contains("$$v") || trimmed.Contains("|") || trimmed.Contains("$$L"))
            {
                return trimmed.Replace("$$V", "V").Replace("$$v", "v").Replace("|", "||").Replace("$$L", "DOLLARL");
            }
            return trimmed;
        }

        // Uses HashSet for O(1) high-speed lookups instead of string parsing
        private bool ShouldExportAttribute(string attrValue, bool exportUnsets)
        {
            if (exportUnsets) return true;
            return !IgnoreValues.Contains(attrValue);
        }
    }
}
