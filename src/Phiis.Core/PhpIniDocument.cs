using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Phiis.Core
{
    public sealed class PhpIniExtension
    {
        /// <summary>Normalized name, e.g. "curl" for both "extension=curl" and "extension=php_curl.dll".</summary>
        public string Name { get; set; }
        /// <summary>The value exactly as written in php.ini.</summary>
        public string RawValue { get; set; }
        public bool IsZend { get; set; }
        public bool Enabled { get; set; }
        public int LineNumber { get; set; }
    }

    /// <summary>One directive as found in php.ini, for listing in an editor.</summary>
    public sealed class PhpIniDirective
    {
        public string Name { get; set; }
        /// <summary>Effective value (last active line), or null when the directive is not set.</summary>
        public string Value { get; set; }
        /// <summary>Value of the last commented-out occurrence (usually the documented default), or null.</summary>
        public string CommentedValue { get; set; }
        /// <summary>The comment block directly above the directive.</summary>
        public string Description { get; set; }
        /// <summary>Section the directive lives in, e.g. "PHP", "Date", "HOST=www.example.com".</summary>
        public string Section { get; set; }
        /// <summary>
        /// True for [PATH=...] / [HOST=...] sections. Those apply only to one folder or host, so
        /// Phiis lists them but never changes them as if they were global settings.
        /// </summary>
        public bool IsPerScope { get; set; }
        public int LineNumber { get; set; }
    }

    /// <summary>
    /// Line-preserving php.ini editor. Comments, blank lines and ordering are kept intact;
    /// only the lines Phiis changes are touched.
    /// </summary>
    public sealed class PhpIniDocument
    {
        private static readonly Regex DirectiveLine =
            new Regex(@"^\s*(?<comment>;\s*)?(?<name>[A-Za-z_][A-Za-z0-9_.\-]*)\s*=\s*(?<value>.*?)\s*$");

        private static readonly Regex SectionLine = new Regex(@"^\s*\[(?<name>[^\]]+)\]\s*$");

        private readonly List<string> _lines;

        public string Path { get; private set; }

        private PhpIniDocument(string path, List<string> lines)
        {
            Path = path;
            _lines = lines;
        }

        public static PhpIniDocument Load(string path)
        {
            return new PhpIniDocument(path, File.ReadAllLines(path).ToList());
        }

        public static PhpIniDocument FromText(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            return new PhpIniDocument(null, lines);
        }

        public IList<string> Lines
        {
            get { return _lines.AsReadOnly(); }
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            foreach (var line in _lines)
            {
                sb.Append(line).Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>Saves the file. When <paramref name="backupDirectory"/> is given, the old file is copied there first.</summary>
        public void Save(string path, string backupDirectory)
        {
            if (backupDirectory != null && File.Exists(path))
            {
                Backup.CopyToBackup(path, backupDirectory);
            }
            // Write to a temp file and swap, so a crash never leaves a half-written php.ini.
            var temp = path + ".phiis-tmp";
            File.WriteAllText(temp, ToText(), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        // ---------- directives ----------

        /// <summary>Returns the effective (last active) global value of a directive, or null when not set.</summary>
        public string GetValue(string name)
        {
            string result = null;
            var perScope = PerScopeLines();
            for (int i = 0; i < _lines.Count; i++)
            {
                var m = DirectiveLine.Match(_lines[i]);
                if (m.Success && !perScope[i] && !m.Groups["comment"].Success && NameEquals(m, name))
                {
                    result = Unquote(StripInlineComment(m.Groups["value"].Value));
                }
            }
            return result;
        }

        /// <summary>
        /// Lists every directive in the file (active or commented out), one entry per name and section,
        /// excluding extension= / zend_extension= lines (see <see cref="GetExtensions"/>).
        /// </summary>
        public IList<PhpIniDirective> GetDirectives()
        {
            var byKey = new Dictionary<string, PhpIniDirective>(StringComparer.OrdinalIgnoreCase);
            var order = new List<PhpIniDirective>();
            string section = null;
            bool perScope = false;

            for (int i = 0; i < _lines.Count; i++)
            {
                var s = SectionLine.Match(_lines[i]);
                if (s.Success)
                {
                    section = s.Groups["name"].Value.Trim();
                    perScope = IsPerScopeSection(section);
                    continue;
                }

                var m = DirectiveLine.Match(_lines[i]);
                if (!m.Success)
                {
                    continue;
                }
                var name = m.Groups["name"].Value;
                if (name.Equals("extension", StringComparison.OrdinalIgnoreCase) || name.Equals("zend_extension", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Global directives are one entry whatever [section] they sit in (PHP ignores those headers);
                // per-folder/per-host ones are kept apart.
                var key = (perScope ? section + "|" : "|") + name;
                PhpIniDirective d;
                if (!byKey.TryGetValue(key, out d))
                {
                    d = new PhpIniDirective { Name = name, Section = section ?? string.Empty, IsPerScope = perScope, LineNumber = i + 1 };
                    byKey[key] = d;
                    order.Add(d);
                }

                var value = Unquote(StripInlineComment(m.Groups["value"].Value));
                if (m.Groups["comment"].Success)
                {
                    d.CommentedValue = value;
                    if (d.Value == null)
                    {
                        d.LineNumber = i + 1;
                    }
                }
                else
                {
                    d.Value = value;
                    d.Section = section ?? string.Empty;
                    d.LineNumber = i + 1;
                }

                var description = DescriptionAbove(i);
                if (description.Length > 0 && (d.Description == null || !m.Groups["comment"].Success))
                {
                    d.Description = description;
                }
            }
            return order;
        }

        /// <summary>
        /// Sets a directive. Replaces the last active occurrence (keeping its inline comment); otherwise
        /// adds it below the last commented-out occurrence; otherwise appends to the end of the [PHP] section.
        /// Lines in [PATH=...] / [HOST=...] sections are never touched.
        /// Returns false when the value was already set (no change).
        /// </summary>
        public bool SetValue(string name, string value)
        {
            value = FormatValue(value);
            var newLine = name + " = " + value;
            int lastActive = -1, lastCommented = -1;
            var perScope = PerScopeLines();
            for (int i = 0; i < _lines.Count; i++)
            {
                var m = DirectiveLine.Match(_lines[i]);
                if (!m.Success || perScope[i] || !NameEquals(m, name))
                {
                    continue;
                }
                if (m.Groups["comment"].Success)
                {
                    lastCommented = i;
                }
                else
                {
                    lastActive = i;
                }
            }

            if (lastActive >= 0)
            {
                if (string.Equals(GetValue(name), Unquote(value), StringComparison.Ordinal))
                {
                    return false;
                }
                _lines[lastActive] = ReplaceValue(_lines[lastActive], value);
                return true;
            }
            if (lastCommented >= 0)
            {
                _lines.Insert(lastCommented + 1, newLine);
                return true;
            }

            _lines.Insert(FindPhpSectionEnd(), newLine);
            return true;
        }

        /// <summary>Comments out every active occurrence of a directive. Returns false when none was active.</summary>
        public bool RemoveValue(string name)
        {
            bool changed = false;
            var perScope = PerScopeLines();
            for (int i = 0; i < _lines.Count; i++)
            {
                var m = DirectiveLine.Match(_lines[i]);
                if (m.Success && !perScope[i] && !m.Groups["comment"].Success && NameEquals(m, name))
                {
                    _lines[i] = ";" + _lines[i];
                    changed = true;
                }
            }
            return changed;
        }

        // ---------- extensions ----------

        public IList<PhpIniExtension> GetExtensions()
        {
            var result = new List<PhpIniExtension>();
            for (int i = 0; i < _lines.Count; i++)
            {
                var m = DirectiveLine.Match(_lines[i]);
                if (!m.Success)
                {
                    continue;
                }
                var directive = m.Groups["name"].Value;
                bool isZend = directive.Equals("zend_extension", StringComparison.OrdinalIgnoreCase);
                if (!isZend && !directive.Equals("extension", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var raw = Unquote(StripInlineComment(m.Groups["value"].Value));
                var name = NormalizeExtensionName(raw);
                if (name.Length == 0)
                {
                    continue;
                }
                result.Add(new PhpIniExtension
                {
                    Name = name,
                    RawValue = raw,
                    IsZend = isZend,
                    Enabled = !m.Groups["comment"].Success,
                    LineNumber = i + 1
                });
            }
            return result;
        }

        /// <summary>
        /// Enables an extension. Accepts "curl", "php_curl" or "php_curl.dll" interchangeably.
        /// Never creates a duplicate. Returns false when it was already enabled.
        /// </summary>
        public bool EnableExtension(string extension, bool isZend)
        {
            var name = NormalizeExtensionName(extension);
            var matches = GetExtensions().Where(e => e.Name == name).ToList();
            if (matches.Any(e => e.Enabled))
            {
                return false;
            }

            var commented = matches.LastOrDefault();
            if (commented != null)
            {
                _lines[commented.LineNumber - 1] = (commented.IsZend ? "zend_extension=" : "extension=") + commented.RawValue;
                return true;
            }

            var lastExtensionLine = GetExtensions().Select(e => e.LineNumber).DefaultIfEmpty(0).Max();
            var newLine = (isZend ? "zend_extension=" : "extension=") + name;
            _lines.Insert(lastExtensionLine > 0 ? lastExtensionLine : FindPhpSectionEnd(), newLine);
            return true;
        }

        /// <summary>Comments out every active line loading the extension. Returns false when it was not enabled.</summary>
        public bool DisableExtension(string extension)
        {
            var name = NormalizeExtensionName(extension);
            bool changed = false;
            foreach (var ext in GetExtensions().Where(e => e.Name == name && e.Enabled))
            {
                _lines[ext.LineNumber - 1] = ";" + _lines[ext.LineNumber - 1];
                changed = true;
            }
            return changed;
        }

        /// <summary>"php_curl.dll", "php_curl", "curl", "C:\php\ext\php_curl.dll" all become "curl".</summary>
        public static string NormalizeExtensionName(string value)
        {
            var name = Unquote((value ?? string.Empty).Trim());
            int slash = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }
            if (name.StartsWith("php_", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(4);
            }
            return name.ToLowerInvariant();
        }

        // ---------- helpers ----------

        private static bool NameEquals(Match m, string name)
        {
            return string.Equals(m.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase);
        }

        private int FindPhpSectionEnd()
        {
            int phpStart = -1;
            for (int i = 0; i < _lines.Count; i++)
            {
                var m = SectionLine.Match(_lines[i]);
                if (!m.Success)
                {
                    continue;
                }
                var name = m.Groups["name"].Value.Trim();
                if (phpStart >= 0 || IsPerScopeSection(name))
                {
                    // Insert before the blank/comment block that precedes the next section. A [PATH=]/[HOST=]
                    // section also ends the global part when there is no [PHP] header: anything appended
                    // after it would apply to that folder or host only.
                    int pos = i;
                    while (pos - 1 > phpStart && IsBlankOrComment(_lines[pos - 1]))
                    {
                        pos--;
                    }
                    return pos;
                }
                if (name.Equals("PHP", StringComparison.OrdinalIgnoreCase))
                {
                    phpStart = i;
                }
            }
            return _lines.Count;
        }

        private static bool IsPerScopeSection(string section)
        {
            return section.StartsWith("PATH=", StringComparison.OrdinalIgnoreCase)
                   || section.StartsWith("HOST=", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>For each line: true when it is inside a [PATH=...] or [HOST=...] section.</summary>
        private bool[] PerScopeLines()
        {
            var result = new bool[_lines.Count];
            bool perScope = false;
            for (int i = 0; i < _lines.Count; i++)
            {
                var m = SectionLine.Match(_lines[i]);
                if (m.Success)
                {
                    perScope = IsPerScopeSection(m.Groups["name"].Value.Trim());
                }
                result[i] = perScope;
            }
            return result;
        }

        /// <summary>The contiguous comment lines directly above a line, without the leading ';' and without commented-out directives.</summary>
        private string DescriptionAbove(int index)
        {
            var parts = new List<string>();
            for (int i = index - 1; i >= 0; i--)
            {
                var t = _lines[i].Trim();
                if (!t.StartsWith(";", StringComparison.Ordinal))
                {
                    break;
                }
                if (DirectiveLine.IsMatch(_lines[i]))
                {
                    continue; // e.g. the commented default right above the active line
                }
                parts.Insert(0, t.TrimStart(';').Trim());
            }
            return string.Join(Environment.NewLine, parts).Trim();
        }

        /// <summary>Quotes a value containing ';' so PHP does not read the rest as a comment.</summary>
        private static string FormatValue(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.IndexOf(';') >= 0 && !(value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"'))
            {
                return "\"" + value.Replace("\"", "\\\"") + "\"";
            }
            return value;
        }

        /// <summary>Replaces the value of an active directive line, keeping its name, spacing and inline comment.</summary>
        private static string ReplaceValue(string line, string value)
        {
            int eq = line.IndexOf('=');
            var rest = line.Substring(eq + 1);
            int comment = InlineCommentStart(rest);
            var spacing = rest.Length - rest.TrimStart().Length > 0 ? rest.Substring(0, rest.Length - rest.TrimStart().Length) : string.Empty;
            return line.Substring(0, eq + 1) + spacing + value + (comment >= 0 ? " " + rest.Substring(comment) : string.Empty);
        }

        /// <summary>Index of the ';' that starts an inline comment (outside double quotes), or -1.</summary>
        private static int InlineCommentStart(string value)
        {
            bool quoted = false;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '"')
                {
                    quoted = !quoted;
                }
                else if (value[i] == ';' && !quoted)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool IsBlankOrComment(string line)
        {
            var t = line.Trim();
            return t.Length == 0 || t.StartsWith(";", StringComparison.Ordinal);
        }

        private static string StripInlineComment(string value)
        {
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int close = value.IndexOf('"', 1);
                return close > 0 ? value.Substring(0, close + 1) : value;
            }
            int semi = value.IndexOf(';');
            return (semi >= 0 ? value.Substring(0, semi) : value).Trim();
        }

        private static string Unquote(string value)
        {
            if (value != null && value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2);
            }
            return value;
        }
    }
}
