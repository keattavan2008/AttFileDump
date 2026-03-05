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

        // Parameters
        private bool _useCe = true;
        private bool _useElements = false;
        private bool _useDrawList = false;
        private bool _exportUnsets = true;
        private bool _exportTube = true;
        private HashSet<string> _elementNames = new HashSet<string>();

        [PMLNetCallable()]
        public AttDump() { }

        [PMLNetCallable()]
        public void Assign(AttDump that) { }

        [PMLNetCallable()]
        public void GenerateAttFile(string filename)
        {
            try
            {
                if (string.IsNullOrEmpty(filename)) throw new ArgumentException("You must supply a filename");
                if (filename.StartsWith("/")) filename = filename.Substring(1);

                // Use HashSet for instant duplicate checking
                HashSet<DbElement> rootElements = new HashSet<DbElement>();

                // 1. Gather Root Elements Only (Don't gather children yet)
                if (_useCe)
                {
                    DbElement ce = CurrentElement.Element;
                    if (!ce.IsValid) throw new InvalidOperationException("Current Element is not valid");

                    // Optimization: If WORLD, just add Sites. Don't scan everything yet.
                    if (ce.GetElementType() == DbElementTypeInstance.WORLD)
                    {
                        foreach (DbElement site in new DBElementCollection(ce, new TypeFilter(DbElementTypeInstance.SITE)))
                        {
                            rootElements.Add(site);
                        }
                    }
                    else
                    {
                        // Add CE
                        rootElements.Add(ce);

                        // Add implied owners (Zone/Site) if CE is deep in hierarchy
                        // Note: In a dump, usually you want the hierarchy downwards.
                        // If you want parents included, add them as roots.
                        AddParentsToRoots(ce, rootElements);
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
                // (Drawlist logic omitted for brevity, logic remains similar: add roots)

                if (rootElements.Count == 0) throw new InvalidOperationException("No elements found to export");

                // Sort roots to ensure Site -> Zone order if multiple levels exist
                // We use a custom comparer to ensure we process parents before children if both are in the list
                var sortedRoots = rootElements.OrderBy(e => GetDepthFromWorld(e)).ToList();

                using (StreamWriter writer = new StreamWriter(filename, false, Encoding.UTF8))
                {
                    // Buffer size optimization can be added to StreamWriter for speed
                    WriteHeader(writer);

                    // We use a HashSet to ensure we don't process the same element twice
                    // (e.g. if User selected a Zone AND a Pipe inside that Zone)
                    HashSet<DbElement> processed = new HashSet<DbElement>();

                    foreach (DbElement root in sortedRoots)
                    {
                        // Recursive hierarchy walk
                        ProcessHierarchy(root, 0, writer, processed);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error exporting attributes: " + ex.Message, ex);
            }
        }

        private void AddParentsToRoots(DbElement ele, HashSet<DbElement> roots)
        {
            // Logic to grab the Site/Zone owning this element if required
            // This replicates your GetZone/GetSite logic but cleaner
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

        // RECURSIVE FUNCTION - Naturally handles hierarchy and indentation
        private void ProcessHierarchy(DbElement element, int currentDepth, StreamWriter writer, HashSet<DbElement> processed)
        {
            if (processed.Contains(element)) return; // Prevent duplicates
            processed.Add(element);

            // Skip Tube check
            if (element.GetElementType() == DbElementTypeInstance.TUBE && !_exportTube) return;

            // 1. Write the Element
            WriteElementData(element, currentDepth, writer);

            // 2. Get Children (Standard collection is usually strictly hierarchy order)
            // Using DBElementCollection is faster than GetMembers() for large sets
            DBElementCollection children = new DBElementCollection(element);

            foreach (DbElement child in children)
            {
                ProcessHierarchy(child, currentDepth + 1, writer, processed);
            }

            // 3. Write End Tag for this level
            WriteIndentedLine(writer, currentDepth * 2, "END");
        }

        private void WriteElementData(DbElement element, int depth, StreamWriter writer)
        {
            int tab = depth * 2;
            int iTab = tab + 2;

            try
            {
                string elementName = element.GetAsString(DbAttributeInstance.FLNM);
                WriteIndentedLine(writer, tab, "NEW " + elementName);

                // Get Attributes - We pass the 'element' directly, NO CurrentElement switching!
                List<DbAttribute> attributes = GetAttributes(element);
                int attrSize = attributes.Count + 3;

                foreach (DbAttribute attr in attributes)
                {
                    // Optimize: Don't call GetAsString if we know we don't need it?
                    // Hard to do with generic "AttDump", but we can try-catch safely.
                    string attrName = attr.Name.ToUpper();
                    string attrValue = GetAttributeValue(element, attr);

                    attrValue = ProcessAttributeValue(attrValue);

                    if (ShouldExportAttribute(attrValue, _exportUnsets))
                    {
                        // Handle PML specific escaping
                        bool hasDollarUnderscore = attrValue.Contains("$$_");
                        if (hasDollarUnderscore) attrValue = attrValue.Replace("$$_", "DOLLARU");

                        string formattedLine = FormatAttributeLine(iTab, attrName, attrValue, attrSize);

                        if (hasDollarUnderscore) formattedLine = formattedLine.Replace("DOLLARU", "$$_");
                        if (formattedLine.Contains("DOLLARL")) formattedLine = formattedLine.Replace("DOLLARL", "$$L");

                        writer.WriteLine(formattedLine);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing {element}: {ex.Message}");
            }
        }

        // Helper to get simple depth for sorting roots only
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

        private List<DbAttribute> GetAttributes(DbElement element)
        {
            // Similar to your logic, but cleaner
            List<DbAttribute> attributes = new List<DbAttribute>();
            attributes.AddRange(element.GetAttributes());

            // Add pseudo attributes manually if needed
            // (Your logic for APOS, LPOS etc is fine here)
            // ... (Copy your specific attribute logic here)

            return attributes;
        }

        private string GetAttributeValue(DbElement element, DbAttribute attribute)
        {
            // Use the passed element instance
            try { return element.GetAsString(attribute); }
            catch { return ""; }
        }

        private void WriteHeader(StreamWriter writer)
        {
            // ... (Your header logic)
        }

        private void WriteIndentedLine(StreamWriter writer, int indent, string text)
        {
            // Using char array is slightly faster than creating new string instances repeatedly
            // but for file IO, string buffer is usually fine.
            writer.Write(new string(' ', indent));
            writer.WriteLine(text);
        }

        private string ProcessAttributeValue(string value)
        {
            // ... (Your existing logic)
            if (string.IsNullOrEmpty(value)) return value;
            return value.Trim().Replace("$$V", "V").Replace("$$v", "v").Replace("|", "||").Replace("$$L", "DOLLARL");
        }

        private bool ShouldExportAttribute(string attrValue, bool exportUnsets)
        {
            // ... (Your existing logic)
            if (exportUnsets) return true;
            if (string.IsNullOrEmpty(attrValue)) return false;
            // Optimization: Use IndexOf or explicit checks instead of formatting a new string for contains
            if (IgnorePattern.IndexOf("," + attrValue + ",") >= 0) return false;
            return true;
        }

        private string FormatAttributeLine(int indent, string attrName, string attrValue, int attrSize)
        {
            // ... (Your existing logic)
            return $"{new string(' ', indent)}{attrName}{Delimiter}".PadRight(indent + attrSize + Delimiter.Length + 2) + attrValue;
        }
    }
}
