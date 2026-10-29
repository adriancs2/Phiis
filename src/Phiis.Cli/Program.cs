using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Phiis.Core;

namespace Phiis.Cli
{
    /// <summary>
    /// phiis.exe - command line for humans, scripts and AI agents.
    /// Every command supports --json; every change supports --dry-run.
    /// Exit codes: 0 ok, 1 usage/runtime error, 2 doctor found errors, 3 administrator rights required.
    /// </summary>
    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitError = 1;
        private const int ExitDoctorErrors = 2;
        private const int ExitNeedsAdmin = 3;

        private static Options _options;

        private static int Main(string[] args)
        {
            try
            {
                _options = Options.Parse(args);
                return Run(_options);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Fail("Administrator rights are required to read or change the IIS configuration. Run from an elevated prompt. (" + ex.Message + ")", ExitNeedsAdmin);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FileNotFoundException || ex is InvalidOperationException
                                       || ex is IOException || ex is InvalidDataException)
            {
                return Fail(ex.Message, ExitError);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                // Microsoft.Web.Administration reports a missing IIS or access problems as COM errors.
                var message = (uint)ex.HResult == 0x80070005
                    ? "Administrator rights are required to read or change the IIS configuration."
                    : "IIS configuration error: " + ex.Message;
                return Fail(message, (uint)ex.HResult == 0x80070005 ? ExitNeedsAdmin : ExitError);
            }
        }

        private static int Run(Options o)
        {
            var command = o.Positional.Count > 0 ? o.Positional[0].ToLowerInvariant() : "help";
            switch (command)
            {
                case "info": return Info(o);
                case "status": return Status(o);
                case "doctor": return RunDoctor(o);
                case "register": return Register(o);
                case "unregister": return Unregister(o);
                case "use-global": return UseGlobal(o);
                case "ini": return Ini(o);
                case "ext": return Ext(o);
                case "snapin": return Snapin(o);
                case "version":
                case "--version":
                    Console.WriteLine("phiis " + typeof(Program).Assembly.GetName().Version.ToString(3));
                    return ExitOk;
                case "help":
                case "--help":
                case "-h":
                case "/?":
                    PrintHelp();
                    return ExitOk;
                default:
                    return Fail("Unknown command '" + command + "'. Run 'phiis help'.", ExitError);
            }
        }

        // ---------- commands ----------

        private static int Info(Options o)
        {
            var build = PhpBuild.Inspect(o.Require(1, "path to php-cgi.exe"));
            var lifecycle = PhpLifecycle.Load();
            DateTime eol;
            bool known = lifecycle.TryGetEndOfLife(build.Branch, out eol);
            var data = new Dictionary<string, object>
            {
                { "executable", build.ExecutablePath },
                { "version", build.VersionText },
                { "branch", build.Branch },
                { "architecture", build.Architecture },
                { "threadSafe", build.ThreadSafe },
                { "toolset", build.Toolset },
                { "endOfLife", known ? eol.ToString("yyyy-MM-dd") : null },
                { "lifecycle", lifecycle.GetStatus(build.Branch, DateTime.Today).ToString() },
                { "defaultIni", build.DefaultIniPath }
            };
            return Output(data, () =>
            {
                foreach (var pair in data)
                {
                    Console.WriteLine("{0,-14} {1}", pair.Key + ":", pair.Value ?? "(unknown)");
                }
            });
        }

        private static int Status(Options o)
        {
            var status = new IisPhpConfiguration().GetStatus(o.Site);
            var data = StatusToData(status);
            return Output(data, () =>
            {
                Console.WriteLine("Scope:             " + (status.Site ?? "server"));
                if (status.Site != null)
                {
                    Console.WriteLine("PHP for this site: " + DescribeMode(status));
                }
                Console.WriteLine("FastCgiModule:     " + (status.FastCgiModuleInstalled ? "installed" : "NOT installed"));
                Console.WriteLine("index.php default: " + (status.IndexPhpIsDefaultDocument ? "yes" : "no"));
                Console.WriteLine("php.ini:           " + (status.IniPath ?? "-"));
                Console.WriteLine();
                Console.WriteLine("PHP handlers (first one is active):");
                if (status.Handlers.Count == 0)
                {
                    Console.WriteLine("  (none)");
                }
                foreach (var h in status.Handlers)
                {
                    Console.WriteLine("  {0} {1}  ->  {2}", h.IsActive ? "*" : " ", h.Name, h.ScriptProcessor);
                }
            });
        }

        private static int RunDoctor(Options o)
        {
            var status = new IisPhpConfiguration().GetStatus(o.Site);
            var findings = new Doctor(PhpLifecycle.Load(), () => DateTime.Today).Check(status);
            findings.AddRange(Doctor.CheckSnapins(SnapinRegistration.GetPhpProviders()));
            int exit = findings.Any(f => f.Severity == Severity.Error) ? ExitDoctorErrors : ExitOk;

            var data = new Dictionary<string, object>
            {
                { "scope", status.Site ?? "server" },
                { "errors", findings.Count(f => f.Severity == Severity.Error) },
                { "warnings", findings.Count(f => f.Severity == Severity.Warning) },
                { "findings", findings.Select(f => new Dictionary<string, object>
                    {
                        { "id", f.Id }, { "severity", f.Severity.ToString().ToLowerInvariant() },
                        { "message", f.Message }, { "hint", f.Hint }
                    }).ToList() }
            };
            Output(data, () =>
            {
                foreach (var f in findings)
                {
                    Console.ForegroundColor = ColorFor(f.Severity);
                    Console.Write("[{0,-7}] ", f.Severity.ToString().ToUpperInvariant());
                    Console.ResetColor();
                    Console.WriteLine(f.Message);
                    if (!string.IsNullOrEmpty(f.Hint))
                    {
                        Console.WriteLine("          -> " + f.Hint);
                    }
                }
            });
            return exit;
        }

        private static int Register(Options o)
        {
            var build = PhpBuild.Inspect(o.Require(1, "path to php-cgi.exe"));
            if (!Path.GetFileName(build.ExecutablePath).Equals("php-cgi.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("IIS FastCGI needs php-cgi.exe, not " + Path.GetFileName(build.ExecutablePath) + ".");
            }

            var iis = new IisPhpConfiguration();
            var result = iis.Register(build.ExecutablePath, o.Site, o.DryRun);

            // php.ini is resolved after registration so PHPRC set on the FastCGI app is honoured.
            var iniPath = o.DryRun ? build.DefaultIniPath : (iis.GetStatus(o.Site).IniPath ?? build.DefaultIniPath);
            PhpIniSetup.Prepare(build, iniPath, result);
            return PrintChange(result);
        }

        private static int Unregister(Options o)
        {
            if (o.Site != null)
            {
                throw new ArgumentException("unregister works at the server level only. To stop a site using its own version: phiis use-global --site \"" + o.Site + "\"");
            }
            return PrintChange(new IisPhpConfiguration().Unregister(o.Require(1, "path to php-cgi.exe"), o.DryRun));
        }

        private static int UseGlobal(Options o)
        {
            if (o.Site == null)
            {
                throw new ArgumentException("use-global needs --site <name>.");
            }
            return PrintChange(new IisPhpConfiguration().UseGlobalDefault(o.Site, o.DryRun));
        }

        private static int Ini(Options o)
        {
            var action = o.Require(1, "get | set | unset | path").ToLowerInvariant();
            var iniPath = ResolveIniPath(o);

            if (action == "path")
            {
                return Output(new Dictionary<string, object> { { "path", iniPath } }, () => Console.WriteLine(iniPath));
            }

            var ini = PhpIniDocument.Load(iniPath);
            var name = o.Require(2, "directive name");
            switch (action)
            {
                case "get":
                    var value = ini.GetValue(name);
                    return Output(new Dictionary<string, object> { { "name", name }, { "value", value } },
                        () => Console.WriteLine(value ?? "(not set)"));
                case "set":
                    var newValue = o.Require(3, "value");
                    return SaveIni(ini, o, ini.SetValue(name, newValue) ? "php.ini: " + name + " = " + newValue : null);
                case "unset":
                    return SaveIni(ini, o, ini.RemoveValue(name) ? "php.ini: comment out " + name : null);
                default:
                    return Fail("Unknown ini action '" + action + "'. Use get, set, unset or path.", ExitError);
            }
        }

        private static int Ext(Options o)
        {
            var action = o.Require(1, "list | enable | disable").ToLowerInvariant();
            var iniPath = ResolveIniPath(o);
            var ini = PhpIniDocument.Load(iniPath);

            switch (action)
            {
                case "list":
                    return ListExtensions(ini, iniPath);
                case "enable":
                    var toEnable = o.Require(2, "extension name");
                    return SaveIni(ini, o, ini.EnableExtension(toEnable, o.Zend) ? "php.ini: enable extension " + PhpIniDocument.NormalizeExtensionName(toEnable) : null);
                case "disable":
                    var toDisable = o.Require(2, "extension name");
                    return SaveIni(ini, o, ini.DisableExtension(toDisable) ? "php.ini: disable extension " + PhpIniDocument.NormalizeExtensionName(toDisable) : null);
                default:
                    return Fail("Unknown ext action '" + action + "'. Use list, enable or disable.", ExitError);
            }
        }

        /// <summary>IIS Manager integration. Used by the MSI installer; also handy for development.</summary>
        private static int Snapin(Options o)
        {
            var action = o.Require(1, "register | unregister | status").ToLowerInvariant();
            switch (action)
            {
                case "register":
                    var dll = o.Positional.Count > 2
                        ? o.Positional[2]
                        : Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? ".", "Phiis.IisManager.dll");
                    if (!File.Exists(dll))
                    {
                        throw new FileNotFoundException("Snap-in assembly not found: " + dll + ". Pass the path: phiis snapin register <Phiis.IisManager.dll>", dll);
                    }
                    return PrintChange(SnapinRegistration.Register(System.Reflection.AssemblyName.GetAssemblyName(dll), o.DryRun));
                case "unregister":
                    return PrintChange(SnapinRegistration.Unregister(o.DryRun));
                case "status":
                    var providers = SnapinRegistration.GetPhpProviders();
                    return Output(new Dictionary<string, object> { { "providers", providers } }, () =>
                    {
                        if (providers.Count == 0)
                        {
                            Console.WriteLine("No PHP-related IIS Manager snap-ins are registered.");
                        }
                        foreach (var p in providers)
                        {
                            bool isPhiis = p.Name.Equals(SnapinRegistration.ProviderName, StringComparison.OrdinalIgnoreCase);
                            Console.WriteLine("{0} [{1}] {2}", p.Name, p.Enabled ? "enabled" : "disabled", p.Type);
                            if (!isPhiis)
                            {
                                Console.WriteLine("  -> Another PHP snap-in (e.g. PHP Manager 2). Both work on the same settings; uninstalling it is recommended.");
                            }
                        }
                    });
                default:
                    return Fail("Unknown snapin action '" + action + "'. Use register, unregister or status.", ExitError);
            }
        }

        private static int ListExtensions(PhpIniDocument ini, string iniPath)
        {
            PhpBuild build = null;
            var phpCgi = Path.Combine(Path.GetDirectoryName(iniPath) ?? ".", "php-cgi.exe");
            if (File.Exists(phpCgi))
            {
                build = PhpBuild.Inspect(phpCgi);
            }
            var extDir = Doctor.ResolveExtensionDir(ini, build, iniPath);

            // Merge what php.ini mentions with what is actually on disk.
            var rows = new SortedDictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in ini.GetExtensions())
            {
                Dictionary<string, object> row;
                if (!rows.TryGetValue(ext.Name, out row))
                {
                    row = new Dictionary<string, object> { { "name", ext.Name }, { "enabled", false }, { "zend", ext.IsZend } };
                    rows[ext.Name] = row;
                }
                if (ext.Enabled)
                {
                    row["enabled"] = true;
                }
                row["installed"] = Doctor.ResolveExtensionFile(ext, extDir) != null;
            }
            if (Directory.Exists(extDir))
            {
                foreach (var file in Directory.GetFiles(extDir, "php_*.dll"))
                {
                    var name = PhpIniDocument.NormalizeExtensionName(file);
                    if (!rows.ContainsKey(name))
                    {
                        rows[name] = new Dictionary<string, object> { { "name", name }, { "enabled", false }, { "zend", false }, { "installed", true } };
                    }
                }
            }

            var list = rows.Values.ToList();
            return Output(new Dictionary<string, object> { { "ini", iniPath }, { "extensionDir", extDir }, { "extensions", list } }, () =>
            {
                Console.WriteLine("php.ini:       " + iniPath);
                Console.WriteLine("extension_dir: " + extDir);
                Console.WriteLine();
                foreach (var row in list)
                {
                    object installed;
                    row.TryGetValue("installed", out installed);
                    Console.WriteLine("  [{0}] {1}{2}{3}",
                        (bool)row["enabled"] ? "x" : " ",
                        row["name"],
                        (bool)row["zend"] ? " (zend)" : "",
                        installed is bool && !(bool)installed ? "  (DLL missing)" : "");
                }
            });
        }

        // ---------- helpers ----------

        /// <summary>--ini wins, then --php (php.ini next to it), then the active handler of the scope.</summary>
        private static string ResolveIniPath(Options o)
        {
            if (o.Ini != null)
            {
                return Path.GetFullPath(o.Ini);
            }
            if (o.Php != null)
            {
                return PhpBuild.Inspect(o.Php).DefaultIniPath;
            }
            var status = new IisPhpConfiguration().GetStatus(o.Site);
            if (status.IniPath == null)
            {
                throw new InvalidOperationException("No PHP is registered for this scope. Use --php <php-cgi.exe> or --ini <php.ini>.");
            }
            return status.IniPath;
        }

        private static int SaveIni(PhpIniDocument ini, Options o, string change)
        {
            var result = new ChangeResult { DryRun = o.DryRun };
            if (change != null)
            {
                result.Changes.Add(change);
                if (!o.DryRun)
                {
                    ini.Save(ini.Path, Backup.DefaultDirectory);
                }
            }
            return PrintChange(result);
        }

        private static int PrintChange(ChangeResult result)
        {
            var data = new Dictionary<string, object>
            {
                { "dryRun", result.DryRun },
                { "changed", result.Changed },
                { "changes", result.Changes },
                { "backups", result.Backups }
            };
            return Output(data, () =>
            {
                if (!result.Changed)
                {
                    Console.WriteLine("Nothing to change; already configured.");
                    return;
                }
                Console.WriteLine(result.DryRun ? "Would apply (dry run):" : "Applied:");
                foreach (var c in result.Changes)
                {
                    Console.WriteLine("  - " + c);
                }
                foreach (var b in result.Backups)
                {
                    Console.WriteLine("Backup: " + b);
                }
            });
        }

        private static string DescribeMode(PhpStatus s)
        {
            switch (s.Mode)
            {
                case PhpScopeMode.GlobalDefault:
                    return "global default" + (s.GlobalDefault == null ? " (none registered)" : " (" + s.GlobalDefault.ScriptProcessor + ")");
                case PhpScopeMode.Pinned:
                    return "own version" + (s.ActiveHandler == null ? "" : " (" + s.ActiveHandler.ScriptProcessor + ")")
                           + (s.ActiveHandler != null && !IisPhpConfiguration.IsGloballyRegistered(s, s.ActiveHandler.ScriptProcessor.Split('|')[0])
                               ? " - no longer registered globally" : "");
                case PhpScopeMode.FullCopy:
                    return "frozen copy of the server handler list (switching PHP converts it to a normal site)";
                default:
                    return "server";
            }
        }

        private static Dictionary<string, object> StatusToData(PhpStatus s)
        {
            return new Dictionary<string, object>
            {
                { "scope", s.Site ?? "server" },
                { "mode", s.Mode.ToString() },
                { "globalDefault", s.GlobalDefault == null ? null : s.GlobalDefault.ScriptProcessor },
                { "fastCgiModuleInstalled", s.FastCgiModuleInstalled },
                { "indexPhpIsDefaultDocument", s.IndexPhpIsDefaultDocument },
                { "iniPath", s.IniPath },
                { "activeHandler", s.ActiveHandler == null ? null : s.ActiveHandler.Name },
                { "handlers", s.Handlers },
                { "fastCgiApps", s.FastCgiApps }
            };
        }

        private static int Output(object data, Action printText)
        {
            if (_options.Json)
            {
                Console.WriteLine(new JavaScriptSerializer().Serialize(data));
            }
            else
            {
                printText();
            }
            return ExitOk;
        }

        private static int Fail(string message, int exitCode)
        {
            if (_options != null && _options.Json)
            {
                Console.WriteLine(new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "error", message }, { "exitCode", exitCode } }));
            }
            else
            {
                Console.Error.WriteLine("phiis: " + message);
            }
            return exitCode;
        }

        private static ConsoleColor ColorFor(Severity severity)
        {
            switch (severity)
            {
                case Severity.Error: return ConsoleColor.Red;
                case Severity.Warning: return ConsoleColor.Yellow;
                case Severity.Info: return ConsoleColor.Cyan;
                default: return ConsoleColor.Green;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"Phiis - PHP Configurator for IIS

Usage: phiis <command> [options]

Commands:
  info <php-cgi.exe>               Show version, architecture, TS/NTS, toolset, end of life
  status                           Show PHP handlers, FastCGI apps and php.ini for a scope
  doctor                           Check the PHP/IIS setup and explain problems
  register <php-cgi.exe>           Register PHP at the server level and make it the global default
  register <php-cgi.exe> --site X  Give site X its own PHP version (must be registered globally)
  use-global --site X              Make site X use the global default PHP again
  unregister <php-cgi.exe>         Remove a PHP version from the server-level list
                                   (sites pinned to it keep working; files are not deleted)
  ini path | get <name>            Show php.ini location or a directive value
  ini set <name> <value>           Set a php.ini directive
  ini unset <name>                 Comment out a php.ini directive
  ext list                         List extensions (php.ini + DLLs on disk)
  ext enable|disable <name>        Toggle an extension ('curl' = 'php_curl.dll')
  snapin register|unregister       Add/remove the Phiis page in IIS Manager
  snapin status                    List PHP-related IIS Manager snap-ins (Phiis, PHP Manager 2)
  version                          Show Phiis version

Options:
  --site <name>     Act on an IIS site instead of the server level
  --php <path>      Use the php.ini next to this php-cgi.exe (ini/ext commands)
  --ini <path>      Use this php.ini directly (ini/ext commands)
  --zend            With 'ext enable': load as zend_extension (e.g. opcache, xdebug)
  --dry-run         Show what would change without changing anything
  --json            Machine-readable output

Exit codes: 0 ok, 1 error, 2 doctor found errors, 3 administrator rights required.
Backups of changed files: " + Backup.DefaultDirectory);
        }

        private sealed class Options
        {
            public List<string> Positional = new List<string>();
            public string Site;
            public string Php;
            public string Ini;
            public bool Json;
            public bool DryRun;
            public bool Zend;

            public static Options Parse(string[] args)
            {
                var o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    var a = args[i];
                    switch (a.ToLowerInvariant())
                    {
                        case "--json": o.Json = true; break;
                        case "--dry-run": o.DryRun = true; break;
                        case "--zend": o.Zend = true; break;
                        case "--site": o.Site = Next(args, ref i, a); break;
                        case "--php": o.Php = Next(args, ref i, a); break;
                        case "--ini": o.Ini = Next(args, ref i, a); break;
                        default:
                            if (a.StartsWith("--", StringComparison.Ordinal))
                            {
                                throw new ArgumentException("Unknown option " + a);
                            }
                            o.Positional.Add(a);
                            break;
                    }
                }
                return o;
            }

            public string Require(int index, string what)
            {
                if (Positional.Count <= index)
                {
                    throw new ArgumentException("Missing argument: " + what + ". Run 'phiis help'.");
                }
                return Positional[index];
            }

            private static string Next(string[] args, ref int i, string option)
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException(option + " needs a value.");
                }
                return args[++i];
            }
        }
    }
}
