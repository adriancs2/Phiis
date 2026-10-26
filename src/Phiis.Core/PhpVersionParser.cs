using System;
using System.Text.RegularExpressions;

namespace Phiis.Core
{
    /// <summary>
    /// Parses PHP version strings such as "8.4.1", "8.5.0RC2" or "7.4" without throwing.
    /// </summary>
    public static class PhpVersionParser
    {
        private static readonly Regex Pattern =
            new Regex(@"^\s*(\d+)\.(\d+)(?:\.(\d+))?(?:\.\d+)?\s*(.*)$", RegexOptions.Compiled);

        public static bool TryParse(string text, out Version version, out string suffix)
        {
            version = null;
            suffix = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var match = Pattern.Match(text);
            if (!match.Success)
            {
                return false;
            }

            int major = int.Parse(match.Groups[1].Value);
            int minor = int.Parse(match.Groups[2].Value);
            int patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
            version = new Version(major, minor, patch);
            suffix = match.Groups[4].Value.Trim();
            return true;
        }

        /// <summary>Returns the "major.minor" branch, e.g. "8.4", or null when unparseable.</summary>
        public static string GetBranch(string text)
        {
            Version version;
            string suffix;
            return TryParse(text, out version, out suffix) ? version.ToString(2) : null;
        }
    }
}
