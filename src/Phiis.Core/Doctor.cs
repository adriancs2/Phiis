using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Phiis.Core
{
    public enum Severity
    {
        Ok,
        Info,
        Warning,
        Error
    }

    public sealed class Finding
    {
        public string Id { get; set; }
        public Severity Severity { get; set; }
        public string Message { get; set; }
        public string Hint { get; set; }
    }

    /// <summary>
    /// Health checks for PHP on IIS. Each check has a stable Id so scripts and AI agents can rely on it.
    /// </summary>
    public sealed class Doctor
    {
        private readonly PhpLifecycle _lifecycle;
        private readonly Func<DateTime> _today;

        public Doctor(PhpLifecycle lifecycle, Func<DateTime> today)
        {
            _lifecycle = lifecycle;
            _today = today;
        }

        public List<Finding> Check(PhpStatus status)
        {
            var findings = new List<Finding>();

            if (!status.FastCgiModuleInstalled)
            {
                Add(findings, "iis.fastcgi-module", Severity.Error, "IIS FastCgiModule is not installed.",
                    "Install the IIS 'CGI' role service (Windows Features > IIS > Application Development > CGI).");
            }

            if (status.ActiveHandler == null)
            {
                Add(findings, "iis.handler-missing", Severity.Error, "No *.php handler mapping is configured" + Scope(status) + ".",
                    "Run: phiis register <path-to-php-cgi.exe>");
                return findings;
            }

            Add(findings, "iis.handler", Severity.Ok, "Active PHP handler: " + status.ActiveHandler.Name + " -> " + status.ActiveHandler.ScriptProcessor, null);

            var exe = status.ActiveHandler.ScriptProcessor.Split('|')[0];
            if (!File.Exists(Environment.ExpandEnvironmentVariables(exe)))
            {
                Add(findings, "php.executable-missing", Severity.Error, "PHP executable not found: " + exe,
                    "Register an existing PHP installation with: phiis register <path-to-php-cgi.exe>");
                return findings;
            }

            if (status.ActiveFastCgiApp == null)
            {
                Add(findings, "iis.fastcgi-app-missing", Severity.Error,
                    "The active handler has no matching FastCGI application; requests will fail with HTTP 500.",
                    "Run: phiis register \"" + exe + "\"");
            }
            else
            {
                CheckFastCgiApp(status.ActiveFastCgiApp, findings);
            }

            if (status.Handlers.Count > 1)
            {
                Add(findings, "iis.multiple-handlers", Severity.Info,
                    status.Handlers.Count + " *.php handlers exist; only the first one is used.", null);
            }

            if (!status.IndexPhpIsDefaultDocument)
            {
                Add(findings, "iis.default-document", Severity.Warning, "index.php is not a default document.",
                    "Running 'phiis register' adds it.");
            }

            PhpBuild build = null;
            try
            {
                build = PhpBuild.Inspect(exe);
            }
            catch (Exception ex)
            {
                Add(findings, "php.inspect-failed", Severity.Warning, "Could not inspect " + exe + ": " + ex.Message, null);
            }

            if (build != null)
            {
                Add(findings, "php.build", Severity.Ok,
                    "PHP " + build.VersionText + " " + build.Architecture + " " + (build.ThreadSafe ? "TS" : "NTS") + ", " + build.Toolset, null);
                CheckThreadSafety(build, findings);
                CheckLifecycle(build, findings);
                CheckVcRuntime(build, findings);
            }

            CheckIni(status.IniPath, build, findings);
            return findings;
        }

        /// <summary>Warns about other PHP snap-ins (e.g. PHP Manager 2) editing the same settings.</summary>
        public static List<Finding> CheckSnapins(IEnumerable<SnapinProvider> providers)
        {
            var findings = new List<Finding>();
            foreach (var p in providers.Where(p => !p.Name.Equals(SnapinRegistration.ProviderName, StringComparison.OrdinalIgnoreCase)))
            {
                Add(findings, "iis.other-php-snapin", Severity.Warning,
                    "Another PHP tool is registered in IIS Manager: " + p.Name + " (" + p.Type + ").",
                    "PHP Manager 2 and Phiis edit the same settings with different rules. Uninstall PHP Manager 2 from 'Apps & features'; your PHP setup stays intact.");
            }
            return findings;
        }

        private static void CheckFastCgiApp(FastCgiApp app, List<Finding> findings)
        {
            string value;
            long phpMax;
            if (app.EnvironmentVariables.TryGetValue("PHP_FCGI_MAX_REQUESTS", out value) && long.TryParse(value, out phpMax))
            {
                if (phpMax > app.InstanceMaxRequests)
                {
                    Add(findings, "fastcgi.max-requests", Severity.Warning,
                        "PHP_FCGI_MAX_REQUESTS (" + phpMax + ") is greater than instanceMaxRequests (" + app.InstanceMaxRequests + ").",
                        "PHP may exit while IIS still sends requests to it; keep PHP_FCGI_MAX_REQUESTS <= instanceMaxRequests.");
                }
            }
            else
            {
                Add(findings, "fastcgi.max-requests", Severity.Info,
                    "PHP_FCGI_MAX_REQUESTS is not set (PHP defaults to 500, which recycles processes often).",
                    "Set it equal to instanceMaxRequests (" + app.InstanceMaxRequests + ").");
            }
        }

        private static void CheckThreadSafety(PhpBuild build, List<Finding> findings)
        {
            if (build.ThreadSafe)
            {
                Add(findings, "php.thread-safe", Severity.Info,
                    "This is a Thread Safe (TS) build. IIS FastCGI runs single-threaded PHP processes.",
                    "The Non Thread Safe (NTS) build is recommended for IIS and is slightly faster.");
            }
        }

        private void CheckLifecycle(PhpBuild build, List<Finding> findings)
        {
            var status = _lifecycle.GetStatus(build.Branch, _today());
            DateTime eol;
            _lifecycle.TryGetEndOfLife(build.Branch, out eol);
            switch (status)
            {
                case LifecycleStatus.EndOfLife:
                    Add(findings, "php.lifecycle", Severity.Warning,
                        "PHP " + build.Branch + " reached end of life on " + eol.ToString("yyyy-MM-dd") + " and gets no security fixes.",
                        "Upgrade to a supported PHP branch.");
                    break;
                case LifecycleStatus.EndingSoon:
                    Add(findings, "php.lifecycle", Severity.Info,
                        "PHP " + build.Branch + " reaches end of life on " + eol.ToString("yyyy-MM-dd") + ".", "Plan an upgrade.");
                    break;
                case LifecycleStatus.Supported:
                    Add(findings, "php.lifecycle", Severity.Ok,
                        "PHP " + build.Branch + " is supported until " + eol.ToString("yyyy-MM-dd") + ".", null);
                    break;
                default:
                    Add(findings, "php.lifecycle", Severity.Info,
                        "PHP " + (build.Branch ?? build.VersionText) + " is not in Phiis' lifecycle data (data updated " + _lifecycle.Updated + ").",
                        "This is normal for new releases. Update " + PhpLifecycle.OverridePath + " to refresh the data.");
                    break;
            }
        }

        private static void CheckVcRuntime(PhpBuild build, List<Finding> findings)
        {
            findings.AddRange(CheckVcRuntime(build,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.Is64BitOperatingSystem));
        }

        /// <summary>
        /// Checks that the Visual C++ runtime matching the toolset that built PHP is installed.
        /// Before VS2015 every toolset had its own runtime DLL; since VS2015 they all share vcruntime140.dll.
        /// </summary>
        public static List<Finding> CheckVcRuntime(PhpBuild build, string windowsDir, bool is64BitOs)
        {
            var findings = new List<Finding>();
            var systemDir = Path.Combine(windowsDir, is64BitOs && build.Architecture == "x86" ? "SysWOW64" : "System32");

            switch (build.LinkerMajor)
            {
                case 14:
                    CheckVcRuntime14(build, systemDir, findings);
                    break;

                case 11: // VC11 (VS2012): PHP 5.5 / 5.6
                    CheckRuntimeFile(build, Path.Combine(systemDir, "msvcr110.dll"), "2012 Update 4",
                        "https://www.microsoft.com/download/details.aspx?id=30679", findings);
                    break;

                case 9: // VC9 (VS2008): PHP 5.3 / 5.4. The runtime lives in the WinSxS side-by-side store.
                    CheckVc9Runtime(build, windowsDir, findings);
                    break;

                case 6: // VC6: PHP 5.2 and older use msvcrt.dll, which ships with Windows.
                    Add(findings, "php.vc-runtime", Severity.Ok, "VC6 build uses msvcrt.dll, which is part of Windows.", null);
                    break;

                default:
                    Add(findings, "php.vc-runtime", Severity.Info,
                        "Unknown toolset (linker " + build.LinkerMajor + "." + build.LinkerMinor + "); the Visual C++ runtime was not checked.", null);
                    break;
            }

            return findings;
        }

        private static void CheckRuntimeFile(PhpBuild build, string runtime, string redistName, string url, List<Finding> findings)
        {
            if (!File.Exists(runtime))
            {
                Add(findings, "php.vc-runtime", Severity.Error,
                    "Visual C++ " + redistName + " runtime (" + Path.GetFileName(runtime) + ") is missing for " + build.Architecture + ".",
                    "Install the Visual C++ " + redistName + " Redistributable (" + build.Architecture + ") from " + url);
                return;
            }

            Add(findings, "php.vc-runtime", Severity.Ok,
                "Visual C++ runtime " + Path.GetFileName(runtime) + " " + FileVersionInfo.GetVersionInfo(runtime).FileVersion + " found.", null);
        }

        private static void CheckVc9Runtime(PhpBuild build, string windowsDir, List<Finding> findings)
        {
            var prefix = build.Architecture == "x64" ? "amd64" : "x86";
            var sxs = Path.Combine(windowsDir, "WinSxS");
            var found = Directory.Exists(sxs) &&
                        Directory.EnumerateDirectories(sxs, prefix + "_microsoft.vc90.crt_*")
                            .Any(d => File.Exists(Path.Combine(d, "msvcr90.dll")));

            if (!found)
            {
                Add(findings, "php.vc-runtime", Severity.Error,
                    "Visual C++ 2008 runtime (msvcr90.dll) is missing for " + build.Architecture + ".",
                    "Install the Visual C++ 2008 SP1 Redistributable (" + build.Architecture + ") from https://www.microsoft.com/download/details.aspx?id=26368");
                return;
            }

            Add(findings, "php.vc-runtime", Severity.Ok, "Visual C++ 2008 runtime (msvcr90.dll) found in WinSxS.", null);
        }

        private static void CheckVcRuntime14(PhpBuild build, string systemDir, List<Finding> findings)
        {
            var runtime = Path.Combine(systemDir, "vcruntime140.dll");
            var downloadHint = "Install the latest Microsoft Visual C++ Redistributable (" + build.Architecture + ") from https://aka.ms/vs/17/release/vc_redist." + build.Architecture + ".exe";

            if (!File.Exists(runtime))
            {
                Add(findings, "php.vc-runtime", Severity.Error, "Visual C++ runtime (vcruntime140.dll) is missing for " + build.Architecture + ".", downloadHint);
                return;
            }

            var version = FileVersionInfo.GetVersionInfo(runtime);
            if (version.FileMajorPart == 14 && version.FileMinorPart < build.LinkerMinor)
            {
                Add(findings, "php.vc-runtime", Severity.Error,
                    "Visual C++ runtime " + version.FileVersion + " is older than the toolset that built PHP (14." + build.LinkerMinor + ").",
                    downloadHint);
                return;
            }

            Add(findings, "php.vc-runtime", Severity.Ok, "Visual C++ runtime " + version.FileVersion + " found.", null);
        }

        private static void CheckIni(string iniPath, PhpBuild build, List<Finding> findings)
        {
            if (string.IsNullOrEmpty(iniPath) || !File.Exists(iniPath))
            {
                Add(findings, "ini.missing", Severity.Warning, "php.ini not found at " + iniPath + "; PHP runs with built-in defaults.",
                    "Copy php.ini-production to php.ini in the PHP folder.");
                return;
            }

            Add(findings, "ini.path", Severity.Ok, "php.ini: " + iniPath, null);
            var ini = PhpIniDocument.Load(iniPath);

            ExpectValue(ini, "cgi.fix_pathinfo", "1", Severity.Warning, "Required for correct PATH_INFO handling under IIS FastCGI.", findings);
            ExpectValue(ini, "fastcgi.impersonate", "1", Severity.Info, "Lets PHP run with the IIS request identity.", findings);
            ExpectValue(ini, "display_errors", "Off", Severity.Warning, "Showing errors to visitors leaks internals on production servers.", findings);

            if (string.IsNullOrEmpty(ini.GetValue("date.timezone")))
            {
                Add(findings, "ini.date.timezone", Severity.Info, "date.timezone is not set; PHP falls back to UTC.",
                    "Run: phiis ini set date.timezone <Region/City>");
            }

            var extensionDir = ResolveExtensionDir(ini, build, iniPath);
            if (!Directory.Exists(extensionDir))
            {
                Add(findings, "ini.extension_dir", Severity.Error, "extension_dir does not exist: " + extensionDir,
                    "Run: phiis ini set extension_dir \"<php folder>\\ext\"");
                return;
            }

            var enabled = ini.GetExtensions().Where(e => e.Enabled).ToList();
            foreach (var group in enabled.GroupBy(e => e.Name).Where(g => g.Count() > 1))
            {
                Add(findings, "ini.extension-duplicate", Severity.Warning,
                    "Extension '" + group.Key + "' is loaded more than once (lines " + string.Join(", ", group.Select(e => e.LineNumber)) + ").",
                    "Run 'phiis ext disable " + group.Key + "', then 'phiis ext enable " + group.Key + "'.");
            }

            foreach (var ext in enabled)
            {
                if (ResolveExtensionFile(ext, extensionDir) == null)
                {
                    Add(findings, "ini.extension-missing", Severity.Error,
                        "Extension '" + ext.RawValue + "' (line " + ext.LineNumber + ") not found in " + extensionDir + ".",
                        "Run: phiis ext disable " + ext.Name);
                }
            }
        }

        public static string ResolveExtensionDir(PhpIniDocument ini, PhpBuild build, string iniPath)
        {
            var phpDir = build != null ? build.Directory : Path.GetDirectoryName(iniPath);
            var value = ini.GetValue("extension_dir");
            if (string.IsNullOrEmpty(value))
            {
                return Path.Combine(phpDir, "ext");
            }
            value = Environment.ExpandEnvironmentVariables(value);
            return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(phpDir, value));
        }

        public static string ResolveExtensionFile(PhpIniExtension ext, string extensionDir)
        {
            if (Path.IsPathRooted(ext.RawValue))
            {
                return File.Exists(ext.RawValue) ? ext.RawValue : null;
            }
            var candidates = new[]
            {
                Path.Combine(extensionDir, ext.RawValue),
                Path.Combine(extensionDir, "php_" + ext.Name + ".dll"),
                Path.Combine(extensionDir, ext.Name + ".dll")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static void ExpectValue(PhpIniDocument ini, string name, string expected, Severity severity, string why, List<Finding> findings)
        {
            var actual = ini.GetValue(name);
            bool ok = actual != null && (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                                         || (expected == "Off" && (actual == "0" || actual.Equals("false", StringComparison.OrdinalIgnoreCase)))
                                         || (expected == "1" && actual.Equals("On", StringComparison.OrdinalIgnoreCase)));
            if (!ok)
            {
                Add(findings, "ini." + name, severity, name + " is " + (actual ?? "(not set)") + "; recommended: " + expected + ". " + why,
                    "Run: phiis ini set " + name + " " + expected);
            }
        }

        private static string Scope(PhpStatus status)
        {
            return status.Site == null ? " at server level" : " for site '" + status.Site + "'";
        }

        private static void Add(List<Finding> findings, string id, Severity severity, string message, string hint)
        {
            findings.Add(new Finding { Id = id, Severity = severity, Message = message, Hint = hint });
        }
    }
}
