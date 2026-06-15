using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aveva.Core.PMLNet;
using Aveva.Core.Database;
using Aveva.Core.Database.Filters;
using Serilog;

namespace AttFileDump
{
    [PMLNetCallable]
    public class AttDump
    {
        private const string Delimiter = ":=";
        private const string Separator = "&end&";

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

        private readonly HashSet<string> _elementNames = new HashSet<string>();

        // Caching structures
        private readonly Dictionary<DbElementType, TypeExportProfile> _attributeCache = new Dictionary<DbElementType, TypeExportProfile>();

        private struct ExportAttribute
        {
            public DbAttribute DbAttr;
            public string ExportName;
            public ExportAttribute(DbAttribute attr, string exportName) { DbAttr = attr; ExportName = exportName; }
        }

        private class TypeExportProfile
        {
            public List<ExportAttribute> Attributes = new List<ExportAttribute>();
            public int MaxNameLength = 0;
            public bool SuppressPseudos = false;
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

        #endregion

        [PMLNetCallable()]
        public void GenerateSampleXml()
        {
            string samplePath = @"C:\temp\sampleConfiguration.xml";
            string xmlContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<AttDumpConfig>
  <Element type=""SITE"">
    <Attribute alias=""Site Name"">NAME</Attribute>
    <Attribute>DESC</Attribute>
    <Attribute>PURP</Attribute>
  </Element>
  <Element type=""PIPE"">
    <Attribute>NAME</Attribute>
    <Attribute>BORE</Attribute>
    <Attribute>PSPE</Attribute>
  </Element>
  <Element type=""EQUIPMENT"">
    <Attribute>NAME</Attribute>
    <Attribute>DESC</Attribute>
    <Attribute>FUNC</Attribute>
  </Element>
</AttDumpConfig>";
            File.WriteAllText(samplePath, xmlContent);
        }

        private void InitializeLogger()
        {
            string logPath = Path.Combine(_outputDirectory, "AttDumpLog_.txt");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();
        }

        private void LoadXmlConfig()
        {
            _xmlFilter = null;
            if (string.IsNullOrEmpty(_xmlConfigPath) || !File.Exists(_xmlConfigPath)) return;

            try
            {
                _xmlFilter = new Dictionary<DbElementType, Dictionary<string, string>>();
                XDocument doc = XDocument.Load(_xmlConfigPath);

                foreach (var el in doc.Descendants("Element"))
                {
                    string typeStr = el.Attribute("type")?.Value;
                    if (string.IsNullOrEmpty(typeStr)) continue;

                    DbElementType type = DbElementType.GetElementType(typeStr);
                    var atts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var att in el.Descendants("Attribute"))
                    {
                        string originalName = att.Value;
                        string alias = att.Attribute("alias")?.Value ?? originalName.ToUpper();
                        atts[originalName] = alias;
                    }
                    _xmlFilter[type] = atts;
                }
                Log.Information($"Loaded XML Configuration from {_xmlConfigPath}. Found rules for {_xmlFilter.Count} element types.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load XML Configuration.");
            }
        }

        [PMLNetCallable()]
        public void ExecuteExtraction()
        {
            InitializeLogger();
            Log.Information("=== Extraction Started ===");
            var watch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                LoadXmlConfig();
                _attributeCache.Clear();

                _dtxrAttr = DbAttribute.GetDbAttribute("DTXR");
                _mtxxAttr = DbAttribute.GetDbAttribute("MTXX");
                _flnnAttr = DbAttribute.GetDbAttribute("FLNN");

                DbAttribute skipAttr = null;
                if (!string.IsNullOrEmpty(_skipAttributeName))
                {
                    skipAttr = DbAttribute.GetDbAttribute(_skipAttributeName);
                }

                HashSet<DbElement> rootElements = new HashSet<DbElement>();
                DbElement currentCe = CurrentElement.Element;

                if (_useCe)
                {
                    if (!currentCe.IsValid) throw new InvalidOperationException("Current Element is not valid.");
                    if (currentCe.GetElementType() == DbElementTypeInstance.WORLD)
                    {
                        TypeFilter filt = new TypeFilter(DbElementTypeInstance.SITE);
                        DBElementCollection collection = new DBElementCollection(currentCe, filt);
                        foreach (DbElement site in collection) rootElements.Add(site);
                    }
                    else
                    {
                        rootElements.Add(currentCe);
                        AddParentsToRoots(currentCe, rootElements);
                    }
                }
                else if (_useElements)
                {
                    foreach (string name in _elementNames)
                    {
                        DbElement ele = DbElement.GetElement(name);
                        if (ele.IsValid) { rootElements.Add(ele); AddParentsToRoots(ele, rootElements); }
                    }
                }

                if (rootElements.Count == 0) throw new InvalidOperationException("No valid elements found to export.");

                var sortedRoots = rootElements.OrderBy(GetDepthFromWorld).ToList();

                if (_singleFileOutput)
                {
                    string baseFileName = string.IsNullOrEmpty(_filePrefix) ? "Export" : _filePrefix;
                    string fileName = Path.Combine(_outputDirectory, $"{baseFileName}.txt");

                    using (StreamWriter writer = new StreamWriter(fileName, false, Encoding.UTF8, 131072))
                    {
                        WriteHeader(writer, currentCe);
                        HashSet<DbElement> processed = new HashSet<DbElement>();
                        foreach (DbElement root in sortedRoots) ProcessHierarchy(root, 0, writer, processed, skipAttr);
                    }
                    Log.Information($"Successfully exported {sortedRoots.Count} root elements to {fileName}");
                }
                else
                {
                    HashSet<DbElement> processed = new HashSet<DbElement>();
                    foreach (DbElement root in sortedRoots)
                    {
                        string rawName = root.GetAsString(DbAttributeInstance.FLNM);
                        string safeName = SafeNameRegex.Replace(rawName, _nameDelimiter);

                        if (safeName.StartsWith(_nameDelimiter)) safeName = safeName.Substring(_nameDelimiter.Length);

                        string baseFileName = string.IsNullOrEmpty(_filePrefix)
                            ? safeName
                            : $"{_filePrefix}{_nameDelimiter}{safeName}";

                        string fileName = Path.Combine(_outputDirectory, $"{baseFileName}.txt");

                        using (StreamWriter writer = new StreamWriter(fileName, false, Encoding.UTF8, 131072))
                        {
                            WriteHeader(writer, root);
                            ProcessHierarchy(root, 0, writer, processed, skipAttr);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Critical failure during extraction process.");
                throw;
            }
            finally
            {
                watch.Stop();
                Log.Information($"=== Extraction Completed in {watch.ElapsedMilliseconds} ms ===");
                Log.CloseAndFlush();
            }
        }

        private void ProcessHierarchy(DbElement element, int currentDepth, StreamWriter writer, HashSet<DbElement> processed, DbAttribute skipAttr)
        {
            if (!processed.Add(element)) return;

            if (skipAttr != null && element.IsAttributeValid(skipAttr))
            {
                string attrVal = string.Empty;
                try { attrVal = element.GetAsString(skipAttr); } catch { }

                if (!string.IsNullOrEmpty(attrVal) && _skipValues.Contains(attrVal))
                {
                    Log.Debug($"Pruned: Skipped {element.GetElementType()} {element} ({_skipAttributeName} = {attrVal})");
                    return;
                }
            }

            DbElementType elementType = element.GetElementType();
            if (elementType == DbElementTypeInstance.TUBE && !_exportTube) return;

            WriteElementData(element, elementType, currentDepth, writer);

            DbElement[] children = element.Members();
            foreach (DbElement child in children)
            {
                ProcessHierarchy(child, currentDepth + 1, writer, processed, skipAttr);
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

            foreach (ExportAttribute exportAttr in profile.Attributes)
            {
                WriteSingleAttribute(element, exportAttr.DbAttr, exportAttr.ExportName, iTab, attrSize, writer, sb);
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

            if (string.IsNullOrEmpty(attrValue)) return;

            if (ShouldExportAttribute(attrValue, _exportUnsets))
            {
                attrValue = ProcessAttributeValue(attrValue);
                bool hasDollarUnderscore = attrValue.Contains("$$_");
                if (hasDollarUnderscore) attrValue = attrValue.Replace("$$_", "DOLLARU");

                sb.Clear();
                sb.Append(' ', iTab).Append(exportName).Append(Delimiter);
                string leftPart = sb.ToString();
                sb.Clear();
                sb.Append(leftPart.PadRight(iTab + attrSize + Delimiter.Length + 2)).Append(attrValue);

                string formattedLine = sb.ToString();
                if (hasDollarUnderscore) formattedLine = formattedLine.Replace("DOLLARU", "$$_");
                if (formattedLine.Contains("DOLLARL")) formattedLine = formattedLine.Replace("DOLLARL", "$$L");

                writer.WriteLine(formattedLine);
            }
        }

        private TypeExportProfile GetCachedProfile(DbElement element, DbElementType type)
        {
            if (_attributeCache.TryGetValue(type, out TypeExportProfile profile)) return profile;

            profile = new TypeExportProfile();
            bool hasXml = _xmlFilter != null;
            bool isInXml = hasXml && _xmlFilter.ContainsKey(type);

            if (_strictXmlMode)
            {
                // SCENARIO 3: Strict Mode (ONLY XML attributes)
                if (isInXml)
                {
                    profile.SuppressPseudos = true;
                    foreach (var kvp in _xmlFilter[type])
                    {
                        DbAttribute explicitAttr = DbAttribute.GetDbAttribute(kvp.Key);
                        if (explicitAttr != null) profile.Attributes.Add(new ExportAttribute(explicitAttr, kvp.Value));
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

                    processedAttrs.Add(attr.Name);

                    if (isInXml && _xmlFilter[type].TryGetValue(attr.Name, out string alias))
                    {
                        profile.Attributes.Add(new ExportAttribute(attr, alias));
                    }
                    else
                    {
                        profile.Attributes.Add(new ExportAttribute(attr, attr.Name.ToUpper()));
                    }
                }

                // If admin added custom UDA keys in XML that weren't caught by the raw GetAttributes() array
                if (isInXml)
                {
                    foreach (var kvp in _xmlFilter[type])
                    {
                        if (!processedAttrs.Contains(kvp.Key))
                        {
                            DbAttribute explicitAttr = DbAttribute.GetDbAttribute(kvp.Key);
                            if (explicitAttr != null) profile.Attributes.Add(new ExportAttribute(explicitAttr, kvp.Value));
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

        private int GetDepthFromWorld(DbElement element)
        {
            int d = 0; DbElement p = element;
            while (p.IsValid && p.GetElementType() != DbElementTypeInstance.WORLD) { d++; p = p.Owner; }
            return d;
        }

        private void AddParentsToRoots(DbElement ele, HashSet<DbElement> roots)
        {
            DbElement ptr = ele.Owner;
            while (ptr.IsValid && ptr.GetElementType() != DbElementTypeInstance.WORLD)
            {
                if (ptr.GetElementType() == DbElementTypeInstance.SITE || ptr.GetElementType() == DbElementTypeInstance.ZONE) roots.Add(ptr);
                ptr = ptr.Owner;
            }
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
