using System;
using System.Diagnostics;
using System.IO;

namespace Phiis.Core
{
    /// <summary>
    /// Facts about one PHP installation, read from the binaries themselves (no hardcoded tables).
    /// </summary>
    public sealed class PhpBuild
    {
        public string ExecutablePath { get; set; }
        public string Directory { get; set; }
        public string VersionText { get; set; }
        public string Branch { get; set; }
        public string Architecture { get; set; }
        public bool ThreadSafe { get; set; }
        public string Toolset { get; set; }
        public int LinkerMajor { get; set; }
        public int LinkerMinor { get; set; }

        public static PhpBuild Inspect(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException("PHP executable path is required.", "executablePath");
            }

            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(executablePath.Trim('"')));
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("PHP executable not found.", fullPath);
            }

            var dir = Path.GetDirectoryName(fullPath);
            var fileVersion = FileVersionInfo.GetVersionInfo(fullPath);
            var versionText = (fileVersion.ProductVersion ?? fileVersion.FileVersion ?? string.Empty).Trim();
            var pe = PeImageInfo.Read(fullPath);

            return new PhpBuild
            {
                ExecutablePath = fullPath,
                Directory = dir,
                VersionText = versionText,
                Branch = PhpVersionParser.GetBranch(versionText),
                Architecture = pe.Architecture,
                ThreadSafe = System.IO.Directory.GetFiles(dir, "php*ts.dll").Length > 0,
                Toolset = pe.Toolset,
                LinkerMajor = pe.LinkerMajor,
                LinkerMinor = pe.LinkerMinor
            };
        }

        /// <summary>Default php.ini location for CGI when PHPRC is not set: next to the executable.</summary>
        public string DefaultIniPath
        {
            get { return Path.Combine(Directory, "php.ini"); }
        }
    }
}
