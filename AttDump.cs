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
        private const string IgnorePattern = ",unset,=0/0,";

        // Core Parameters
        private bool _useCe = true;
        private bool _useElements;
        private bool _useDrawList;
        private bool _exportUnsets = true;
        private bool _exportTube = true;

        // Naming & Directory Parameters
        private string _outputDirectory = @"C:\temp";
        private string _filePrefix;
        private string _nameDelimiter = "_";
        private bool _singleFileOutput;

        // XML Configuration Parameters
        private string _xmlConfigPath = "";
        private bool _strictXmlMode;
        private Dictionary<DbElementType, HashSet<string>> _xmlFilter;

        private readonly HashSet<string> _elementNames = new HashSet<string>();
        private readonly Dictionary<DbElementType, List<DbAttribute>> _attributeCache = new Dictionary<DbElementType, List<DbAttribute>>();

        // Dynamic Skip Logic
        private string _skipAttributeName = "";
        private HashSet<string> _skipValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        // Dynamic Naming Methods
        [PMLNetCallable()] public void SetOutputDirectory(string dir) { _outputDirectory = dir; }
        [PMLNetCallable()] public void SetFilePrefix(string prefix) { _filePrefix = prefix; }
        [PMLNetCallable()] public void SetNameDelimiter(string delimiter) { _nameDelimiter = string.IsNullOrEmpty(delimiter) ? "_" : delimiter; }
        [PMLNetCallable()] public void SetSingleFileOutput(bool isSingle) { _singleFileOutput = isSingle; }

        // XML Configuration Methods
        [PMLNetCallable()] public void SetXmlConfig(string xmlPath) { _xmlConfigPath = xmlPath; }
        [PMLNetCallable()] public void SetStrictXmlMode(bool strict) { _strictXmlMode = strict; }
        [PMLNetCallable()] public void SetSkipAttribute(string attributeName) { _skipAttributeName = attributeName; }
        [PMLNetCallable()] public void AddSkipValue(string triggerValue) { if (!string.IsNullOrEmpty(triggerValue)) _skipValues.Add(triggerValue); }

        #endregion

        // Sample XML Generator
        [PMLNetCallable()]
        public void GenerateSampleXml()
        {
            string samplePath = @"C:\temp\sampleConfiguration.xml";
            string xmlContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<AttDumpConfig>
  <Element type=""SITE"">
    <Attribute>NAME</Attribute>
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
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();
        }

        private void LoadXmlConfig()
        {
            _xmlFilter = null;
            if (string.IsNullOrEmpty(_xmlConfigPath) || !File.Exists(_xmlConfigPath)) return;

            try
            {
                _xmlFilter = new Dictionary<DbElementType, HashSet<string>>();
                XDocument doc = XDocument.Load(_xmlConfigPath);

                foreach (var el in doc.Descendants("Element"))
                {
                    string typeStr = el.Attribute("type")?.Value;
                    if (string.IsNullOrEmpty(typeStr)) continue;

                    DbElementType type = DbElementType.GetElementType(typeStr);
                    HashSet<string> atts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var att in el.Descendants("Attribute"))
                    {
                        atts.Add(att.Value);
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
            // _filePrefix = Project.CurrentProject.Code;
            InitializeLogger();
            Log.Information("=== Extraction Started ===");
            var watch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                LoadXmlConfig();
                _attributeCache.Clear(); // Clear cache for new run

                HashSet<DbElement> rootElements = new HashSet<DbElement>();
                DbElement currentCe = CurrentElement.Element;

                // Element Collection
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
                        if (ele.IsValid)
                        {
                            rootElements.Add(ele);
                            AddParentsToRoots(ele, rootElements);
                        }
                    }
                }

                if (rootElements.Count == 0) throw new InvalidOperationException("No valid elements found to export.");

                var sortedRoots = rootElements.OrderBy(GetDepthFromWorld).ToList();

                if (_singleFileOutput)
                {
                    string fileName = Path.Combine(_outputDirectory, $"{_filePrefix}.txt");
                    using (StreamWriter writer = new StreamWriter(fileName, false, Encoding.UTF8, 131072))
                    {
                        WriteHeader(writer, currentCe);
                        HashSet<DbElement> processed = new HashSet<DbElement>();
                        foreach (DbElement root in sortedRoots) ProcessHierarchy(root, 0, writer, processed);
                    }
                    Log.Information($"Successfully exported {sortedRoots.Count} root elements to {fileName}");
                }
                else
                {
                    // Multi-file Output Loop
                    HashSet<DbElement> processed = new HashSet<DbElement>();
                    foreach (DbElement root in sortedRoots)
                    {
                        string rawName = root.GetAsString(DbAttributeInstance.FLNM);
                        // Regex replaces invalid characters AND slashes with your delimiter
                        string safeName = Regex.Replace(rawName, @"[/\\?%*:|""<>]+", _nameDelimiter);
                        if (safeName.StartsWith(_nameDelimiter)) safeName = safeName.Substring(_nameDelimiter.Length);

                        string fileName = Path.Combine(_outputDirectory, $"{_filePrefix}_{safeName}.txt");

                        using (StreamWriter writer = new StreamWriter(fileName, false, Encoding.UTF8, 131072))
                        {
                            WriteHeader(writer, root);
                            ProcessHierarchy(root, 0, writer, processed);
                        }
                        Log.Information($"Exported element {rawName} to {fileName}");
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

        private void ProcessHierarchy(DbElement element, int currentDepth, StreamWriter writer, HashSet<DbElement> processed)
        {
            if (!processed.Add(element)) return;

            // --- SKIP LOGIC (TREE PRUNING) ---
            if (!string.IsNullOrEmpty(_skipAttributeName))
            {
                DbAttribute skipAttr = DbAttribute.GetDbAttribute(_skipAttributeName);
                if (skipAttr != null && element.IsAttributeValid(skipAttr))
                {
                    string attrVal = string.Empty;
                    try { attrVal = element.GetAsString(skipAttr); } catch { }

                    if (!string.IsNullOrEmpty(attrVal) && _skipValues.Contains(attrVal))
                    {
                        // Log the prune and immediately exit. Children are completely ignored!
                        Log.Information($"Tree Pruned: Skipped {element.GetElementType()} {element} due to {_skipAttributeName} = {attrVal}");
                        return;
                    }
                }
            }
            // -------------------------------------

            DbElementType elementType = element.GetElementType();

            if (elementType == DbElementTypeInstance.TUBE && !_exportTube) return;

            WriteElementData(element, elementType, currentDepth, writer);

            DbElement[] children = element.Members();
            foreach (DbElement child in children)
            {
                ProcessHierarchy(child, currentDepth + 1, writer, processed);
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

            List<DbAttribute> attributes = GetCachedAttributes(element, elementType);

            // If strict mode is on and this element isn't in the XML, attributes will be empty. Skip processing attributes.
            if (attributes.Count == 0) return;

            int maxNameLength = attributes.Max(a => a.Name.Length);
            int attrSize = maxNameLength + 3;

            StringBuilder sb = new StringBuilder(128);

            foreach (DbAttribute attr in attributes)
            {
                string attrValue;
                try
                {
                    attrValue = element.GetAsString(attr);
                }
                catch (Exception ex)
                {
                    // Granular logging for specific attribute failures
                    Log.Warning($"Failed to read attribute {attr.Name} on element {elementName}. Error: {ex.Message}");
                    continue;
                }

                if (string.IsNullOrEmpty(attrValue)) continue;

                if (ShouldExportAttribute(attrValue, _exportUnsets))
                {
                    attrValue = ProcessAttributeValue(attrValue);

                    bool hasDollarUnderscore = attrValue.Contains("$$_");
                    if (hasDollarUnderscore) attrValue = attrValue.Replace("$$_", "DOLLARU");

                    sb.Clear();
                    sb.Append(' ', iTab).Append(attr.Name.ToUpper()).Append(Delimiter);

                    string leftPart = sb.ToString();
                    sb.Clear();
                    sb.Append(leftPart.PadRight(iTab + attrSize + Delimiter.Length + 2)).Append(attrValue);

                    string formattedLine = sb.ToString();
                    if (hasDollarUnderscore) formattedLine = formattedLine.Replace("DOLLARU", "$$_");
                    if (formattedLine.Contains("DOLLARL")) formattedLine = formattedLine.Replace("DOLLARL", "$$L");

                    writer.WriteLine(formattedLine);
                }
            }
        }

        private List<DbAttribute> GetCachedAttributes(DbElement element, DbElementType type)
        {
            if (_attributeCache.TryGetValue(type, out List<DbAttribute> cachedAttrs)) return cachedAttrs;

            List<DbAttribute> attributes = new List<DbAttribute>();
            bool hasXml = _xmlFilter != null;
            bool elementInXml = hasXml && _xmlFilter.ContainsKey(type);

            // Strict XML Rule Check
            if (_strictXmlMode && hasXml && !elementInXml)
            {
                _attributeCache[type] = attributes; // Returns empty list
                return attributes;
            }

            DbAttribute[] rawAttrs = element.GetAttributes();

            foreach (DbAttribute attr in rawAttrs)
            {
                if (attr.Type == DbAttributeType.STRINGARRAY) continue;

                if (elementInXml)
                {
                    // If XML rule exists for this type, ONLY add attributes listed in the XML
                    if (_xmlFilter[type].Contains(attr.Name)) attributes.Add(attr);
                }
                else
                {
                    // No XML rule (and not strict mode), add all valid attributes
                    attributes.Add(attr);
                }
            }

            // Standard AVEVA pseudo-attributes (Only append if not in strict mode, or explicitly manage them)
            if (type != DbElementTypeInstance.WORLD)
            {
                DbElement owner = element.Owner;
                if (!elementInXml && owner.IsValid && owner.GetElementType() == DbElementTypeInstance.BRANCH)
                {
                    attributes.Add(DbAttribute.GetDbAttribute("APOS"));
                    attributes.Add(DbAttribute.GetDbAttribute("LPOS"));
                    attributes.Add(DbAttribute.GetDbAttribute("ADIR"));
                    attributes.Add(DbAttribute.GetDbAttribute("LDIR"));
                    attributes.Add(DbAttribute.GetDbAttribute("DTXR"));
                    attributes.Add(DbAttribute.GetDbAttribute("MTXX"));
                }
                if (!elementInXml && type == DbElementTypeInstance.TUBE) attributes.Add(DbAttributeInstance.FLNN);
            }

            _attributeCache[type] = attributes;
            return attributes;
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

        private void WriteIndentedLine(StreamWriter writer, int indent, string text) { writer.Write(new string(' ', indent)); writer.WriteLine(text); }
        private string ProcessAttributeValue(string value) { return value.Trim().Replace("$$V", "V").Replace("$$v", "v").Replace("|", "||").Replace("$$L", "DOLLARL"); }
        private bool ShouldExportAttribute(string attrValue, bool exportUnsets) { if (exportUnsets) return true; if (IgnorePattern.IndexOf("," + attrValue + ",", StringComparison.OrdinalIgnoreCase) >= 0) return false; return true; }
    }
}
