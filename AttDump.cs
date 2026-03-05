using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Aveva.Core.PMLNet;
using Aveva.Core.Database;
using Aveva.Core.Database.Filters;

namespace AttFileDump
{
    [PMLNetCallable]
    public class AttDump
    {
        private const string Delimiter = ":=";
        private const string Separator = "&end&";
        private const string IgnorePattern = ",unset,=0/0,";

        // Readonly fields satisfy compiler warnings for single-assignment variables
        private bool _useCe = true;
        private bool _useElements;
        private bool _useDrawList;
        private bool _exportUnsets = true;
        private bool _exportTube = true;
        
        private readonly HashSet<string> _elementNames = new HashSet<string>();
        private readonly Dictionary<DbElementType, List<DbAttribute>> _attributeCache = new Dictionary<DbElementType, List<DbAttribute>>();

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
            
            // Copies the element names if any were added
            _elementNames.Clear();
            foreach (string name in that._elementNames)
            {
                _elementNames.Add(name);
            }
        }

        [PMLNetCallable()]
        public void GenerateAttFile(string filename)
        {
            if (string.IsNullOrEmpty(filename)) throw new ArgumentException("Filename must be supplied.");
            if (filename.StartsWith("/")) filename = filename.Substring(1);

            HashSet<DbElement> rootElements = new HashSet<DbElement>();
            DbElement currentCe = CurrentElement.Element;

            // Element Collection logic
            if (_useCe)
            {
                if (!currentCe.IsValid) throw new InvalidOperationException("Current Element is not valid.");

                if (currentCe.GetElementType() == DbElementTypeInstance.WORLD)
                {
                    TypeFilter filt = new TypeFilter(DbElementTypeInstance.SITE);
                    DBElementCollection collection = new DBElementCollection(currentCe, filt);
                    foreach (DbElement site in collection)
                    {
                        rootElements.Add(site);
                    }
                }
                else
                {
                    rootElements.Add(currentCe);
                    AddParentsToRoots(currentCe, rootElements);
                }
            }
            else if (_useElements && _elementNames != null)
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
            else if (_useDrawList)
            {
                // Compiler Warning Resolution: Execution path defined.
                // Note: Interrogating the DrawList via pure database extraction requires bridging 
                // Aveva.Core.Presentation, which is typically avoided in batch DB dumps.
                throw new NotSupportedException("DrawList extraction is routed through the UI namespace.");
            }

            if (rootElements.Count == 0) throw new InvalidOperationException("No valid elements found to export.");

            // Compiler Warning Resolution: Converted lambda to Method Group
            var sortedRoots = rootElements.OrderBy(GetDepthFromWorld).ToList();

            using (StreamWriter writer = new StreamWriter(filename, false, Encoding.UTF8, 131072))
            {
                // Pass CE to write the specific element name in the header, mimicking PML
                WriteHeader(writer, currentCe);

                HashSet<DbElement> processed = new HashSet<DbElement>();

                foreach (DbElement root in sortedRoots)
                {
                    ProcessHierarchy(root, 0, writer, processed);
                }
            }
        }

        private void AddParentsToRoots(DbElement ele, HashSet<DbElement> roots)
        {
            DbElement ptr = ele.Owner;
            while (ptr.IsValid && ptr.GetElementType() != DbElementTypeInstance.WORLD)
            {
                if (ptr.GetElementType() == DbElementTypeInstance.SITE ||
                    ptr.GetElementType() == DbElementTypeInstance.ZONE)
                {
                    roots.Add(ptr);
                }
                ptr = ptr.Owner;
            }
        }

        private void ProcessHierarchy(DbElement element, int currentDepth, StreamWriter writer, HashSet<DbElement> processed)
        {
            if (!processed.Add(element)) return;

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
            
            // THE FIX: Replicates PML's !attl.width() by finding the longest attribute name
            int maxNameLength = attributes.Count > 0 ? attributes.Max(a => a.Name.Length) : 0;
            int attrSize = maxNameLength + 3;

            StringBuilder sb = new StringBuilder(128);

            foreach (DbAttribute attr in attributes)
            {
                string attrValue; 
                
                try
                {
                    attrValue = element.GetAsString(attr);
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(attrValue)) continue;

                if (ShouldExportAttribute(attrValue, _exportUnsets))
                {
                    attrValue = ProcessAttributeValue(attrValue);

                    bool hasDollarUnderscore = attrValue.Contains("$$_");
                    if (hasDollarUnderscore) attrValue = attrValue.Replace("$$_", "DOLLARU");

                    sb.Clear();
                    sb.Append(' ', iTab);
                    sb.Append(attr.Name.ToUpper());
                    sb.Append(Delimiter);
                    
                    string leftPart = sb.ToString();
                    sb.Clear();
                    // This PadRight will now correctly use the longest name length, not the total count
                    sb.Append(leftPart.PadRight(iTab + attrSize + Delimiter.Length + 2));
                    sb.Append(attrValue);

                    string formattedLine = sb.ToString();

                    if (hasDollarUnderscore) formattedLine = formattedLine.Replace("DOLLARU", "$$_");
                    if (formattedLine.Contains("DOLLARL")) formattedLine = formattedLine.Replace("DOLLARL", "$$L");

                    writer.WriteLine(formattedLine);
                }
            }
        }

        private List<DbAttribute> GetCachedAttributes(DbElement element, DbElementType type)
        {
            if (_attributeCache.TryGetValue(type, out List<DbAttribute> cachedAttrs))
            {
                return cachedAttrs;
            }

            List<DbAttribute> attributes = new List<DbAttribute>();
            DbAttribute[] rawAttrs = element.GetAttributes();

            foreach (DbAttribute attr in rawAttrs)
            {
                // Updated per AVEVA E3D API constraint observation
                if (attr.Type == DbAttributeType.STRINGARRAY)
                {
                    continue; 
                }
                attributes.Add(attr);
            }

            if (type != DbElementTypeInstance.WORLD)
            {
                DbElement owner = element.Owner;
                if (owner.IsValid && owner.GetElementType() == DbElementTypeInstance.BRANCH)
                {
                    attributes.Add(DbAttribute.GetDbAttribute("APOS"));
                    attributes.Add(DbAttribute.GetDbAttribute("LPOS"));
                    attributes.Add(DbAttribute.GetDbAttribute("DTXR"));
                    attributes.Add(DbAttribute.GetDbAttribute("MTXX"));
                }
                
                if (type == DbElementTypeInstance.TUBE)
                {
                    attributes.Add(DbAttributeInstance.FLNN);
                }
            }

            _attributeCache[type] = attributes;
            return attributes;
        }

        private int GetDepthFromWorld(DbElement element)
        {
            int d = 0;
            DbElement p = element;
            while (p.IsValid && p.GetElementType() != DbElementTypeInstance.WORLD)
            {
                d++;
                p = p.Owner;
            }
            return d;
        }

        private void WriteHeader(StreamWriter writer, DbElement ce)
        {
            // Fully replicates the native PML header output formatting
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

        private void WriteIndentedLine(StreamWriter writer, int indent, string text)
        {
            writer.Write(new string(' ', indent));
            writer.WriteLine(text);
        }

        private string ProcessAttributeValue(string value)
        {
            return value.Trim().Replace("$$V", "V").Replace("$$v", "v").Replace("|", "||").Replace("$$L", "DOLLARL");
        }

        private bool ShouldExportAttribute(string attrValue, bool exportUnsets)
        {
            if (exportUnsets) return true;
            
            // Compiler Warning Resolution: Culture-specific IndexOf replaced with OrdinalIgnoreCase
            if (IgnorePattern.IndexOf("," + attrValue + ",", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            
            return true;
        }
        
        [PMLNetCallable()]
        public void SetUseCe(bool use)
        {
            _useCe = use;
            // Smart Toggle: If CE is true, the others should logically be false
            if (use) 
            {
                _useElements = false;
                _useDrawList = false;
            }
        }

        [PMLNetCallable()]
        public void SetUseElements(bool use)
        {
            _useElements = use;
            if (use) 
            {
                _useCe = false;
                _useDrawList = false;
            }
        }

        [PMLNetCallable()]
        public void SetUseDrawList(bool use)
        {
            _useDrawList = use;
            if (use) 
            {
                _useCe = false;
                _useElements = false;
            }
        }

        [PMLNetCallable()]
        public void SetExportUnsets(bool export)
        {
            _exportUnsets = export;
        }

        [PMLNetCallable()]
        public void SetExportTube(bool export)
        {
            _exportTube = export;
        }
        
        // Helper method to add specific elements when _useElements is true
        [PMLNetCallable()]
        public void AddElementToExport(string elementName)
        {
            if (!string.IsNullOrEmpty(elementName))
            {
                _elementNames.Add(elementName);
            }
        }
        
        [PMLNetCallable()]
        public void ClearElements()
        {
            _elementNames.Clear();
        }
    }
}