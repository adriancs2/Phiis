using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Web.Administration;

namespace Phiis.Core
{
    /// <summary>
    /// Reads and changes the IIS side of PHP: FastCGI applications, *.php handler mappings and
    /// default documents. Phiis keeps no state of its own; everything is derived from the real
    /// IIS configuration, so changes made by hand, by other tools or by AI agents are picked up.
    ///
    /// Site-level changes are written to applicationHost.config (location path), never into the
    /// site's web.config, so application content folders are not touched.
    /// </summary>
    public sealed class IisPhpConfiguration
    {
        public const int DefaultInstanceMaxRequests = 10000;

        private const string HandlersSection = "system.webServer/handlers";
        private const string FastCgiSection = "system.webServer/fastCgi";
        private const string DefaultDocumentSection = "system.webServer/defaultDocument";
        private const string GlobalModulesSection = "system.webServer/globalModules";

        public static string ApplicationHostConfigPath
        {
            get { return Path.Combine(Environment.SystemDirectory, @"inetsrv\config\applicationHost.config"); }
        }

        // ---------- read ----------

        public PhpStatus GetStatus(string site)
        {
            using (var manager = new ServerManager())
            {
                EnsureSite(manager, site);
                var host = manager.GetApplicationHostConfiguration();

                var status = new PhpStatus
                {
                    Site = site,
                    FastCgiModuleInstalled = host.GetSection(GlobalModulesSection).GetCollection()
                        .Any(m => string.Equals((string)m["name"], "FastCgiModule", StringComparison.OrdinalIgnoreCase)),
                    FastCgiApps = ReadFastCgiApps(host),
                    Handlers = ReadPhpHandlers(manager, site)
                };

                status.ActiveHandler = status.Handlers.FirstOrDefault(h => h.IsActive);
                if (status.ActiveHandler != null)
                {
                    status.ActiveFastCgiApp = FindFastCgiApp(status.FastCgiApps, status.ActiveHandler.ScriptProcessor);
                    status.IniPath = ResolveIniPath(status.ActiveHandler.ScriptProcessor, status.ActiveFastCgiApp);
                }

                var defaultDocs = GetEffectiveSection(manager, site, DefaultDocumentSection);
                status.IndexPhpIsDefaultDocument = defaultDocs.GetCollection("files")
                    .Any(f => string.Equals((string)f["value"], "index.php", StringComparison.OrdinalIgnoreCase));
                return status;
            }
        }

        /// <summary>
        /// The PHP builds IIS already knows about: every FastCGI application pointing at an existing
        /// php-cgi.exe. Any of them can be made active for a scope with <see cref="Register"/>.
        /// </summary>
        public static List<PhpBuild> GetRegisteredBuilds(PhpStatus status)
        {
            var builds = new List<PhpBuild>();
            foreach (var path in status.FastCgiApps.Select(a => Environment.ExpandEnvironmentVariables(a.FullPath ?? string.Empty).Trim('"'))
                         .Where(p => p.EndsWith("php-cgi.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    builds.Add(PhpBuild.Inspect(path));
                }
                catch (Exception)
                {
                    // An unreadable executable is reported by the health check; it just cannot be offered here.
                }
            }
            return builds.OrderByDescending(b => ParsedVersion(b.VersionText)).ThenBy(b => b.ExecutablePath).ToList();
        }

        private static Version ParsedVersion(string text)
        {
            Version version;
            string suffix;
            return PhpVersionParser.TryParse(text, out version, out suffix) ? version : new Version(0, 0);
        }

        /// <summary>The php.ini that PHP will load: PHPRC from the FastCGI app, else next to the executable.</summary>
        public static string ResolveIniPath(string scriptProcessor, FastCgiApp app)
        {
            string phprc;
            if (app != null && app.EnvironmentVariables.TryGetValue("PHPRC", out phprc) && !string.IsNullOrWhiteSpace(phprc))
            {
                var expanded = Environment.ExpandEnvironmentVariables(phprc.Trim('"'));
                return expanded.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                    ? expanded
                    : Path.Combine(expanded, "php.ini");
            }
            var exe = SplitScriptProcessor(scriptProcessor)[0];
            return Path.Combine(Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(exe)) ?? string.Empty, "php.ini");
        }

        // ---------- change ----------

        /// <summary>
        /// Registers a php-cgi.exe with IIS and makes it the active PHP for the scope.
        /// Idempotent: running it twice changes nothing the second time.
        /// </summary>
        public ChangeResult Register(string phpCgiPath, string site, bool dryRun)
        {
            var build = PhpBuild.Inspect(phpCgiPath);
            var result = new ChangeResult { DryRun = dryRun };

            using (var manager = new ServerManager())
            {
                EnsureSite(manager, site);
                var host = manager.GetApplicationHostConfiguration();

                EnsureFastCgiApp(host, build, result);
                EnsureHandlerOnTop(host, site, build, result);
                EnsureIndexPhp(host, site, result);

                if (result.Changed && !dryRun)
                {
                    result.Backups.Add(Backup.CopyToBackup(ApplicationHostConfigPath, Backup.DefaultDirectory));
                    manager.CommitChanges();
                }
            }
            return result;
        }

        private static void EnsureFastCgiApp(Configuration host, PhpBuild build, ChangeResult result)
        {
            var apps = host.GetSection(FastCgiSection).GetCollection();
            var app = apps.FirstOrDefault(a => SamePath((string)a["fullPath"], build.ExecutablePath)
                                               && string.IsNullOrEmpty((string)a["arguments"]));
            if (app == null)
            {
                app = apps.CreateElement("application");
                app["fullPath"] = build.ExecutablePath;
                app["instanceMaxRequests"] = DefaultInstanceMaxRequests;
                apps.Add(app);
                result.Changes.Add("Add FastCGI application " + build.ExecutablePath);
            }

            var env = app.GetCollection("environmentVariables");
            SetEnv(env, "PHP_FCGI_MAX_REQUESTS", Convert.ToInt64(app["instanceMaxRequests"]).ToString(), false, result);
            SetEnv(env, "PHPRC", build.Directory, false, result);
        }

        private static void SetEnv(ConfigurationElementCollection env, string name, string value, bool overwrite, ChangeResult result)
        {
            var existing = env.FirstOrDefault(e => string.Equals((string)e["name"], name, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                var e = env.CreateElement("environmentVariable");
                e["name"] = name;
                e["value"] = value;
                env.Add(e);
                result.Changes.Add("Set FastCGI environment " + name + "=" + value);
            }
            else if (overwrite && !string.Equals((string)existing["value"], value, StringComparison.Ordinal))
            {
                existing["value"] = value;
                result.Changes.Add("Update FastCGI environment " + name + "=" + value);
            }
        }

        private static void EnsureHandlerOnTop(Configuration host, string site, PhpBuild build, ChangeResult result)
        {
            var section = site == null ? host.GetSection(HandlersSection) : host.GetSection(HandlersSection, site);
            var handlers = section.GetCollection();

            var all = handlers.ToList();
            int existingIndex = all.FindIndex(h => IsPhpHandler(h) && SamePath(SplitScriptProcessor((string)h["scriptProcessor"])[0], build.ExecutablePath));
            int firstPhpIndex = all.FindIndex(IsPhpHandler);

            if (existingIndex >= 0 && existingIndex == firstPhpIndex)
            {
                return; // already registered and active
            }

            if (existingIndex < 0)
            {
                var handler = handlers.CreateElement("add");
                handler["name"] = UniqueHandlerName(handlers, "PHP_via_FastCGI_" + (build.Branch ?? "unknown") + "_" + build.Architecture);
                handler["path"] = "*.php";
                handler["verb"] = "GET,HEAD,POST";
                handler["modules"] = "FastCgiModule";
                handler["scriptProcessor"] = build.ExecutablePath;
                handler["resourceType"] = "Either";
                handler["requireAccess"] = "Script";
                handlers.AddAt(0, handler);
                result.Changes.Add("Add handler mapping " + handler["name"] + " (*.php -> " + build.ExecutablePath + ")" + ScopeText(site));
                return;
            }

            var existingName = (string)all[existingIndex]["name"];
            MoveToTop(handlers, existingIndex);
            result.Changes.Add("Make handler " + existingName + " the active PHP handler" + ScopeText(site));
        }

        private static void EnsureIndexPhp(Configuration host, string site, ChangeResult result)
        {
            var section = site == null ? host.GetSection(DefaultDocumentSection) : host.GetSection(DefaultDocumentSection, site);
            var files = section.GetCollection("files");
            if (files.Any(f => string.Equals((string)f["value"], "index.php", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            var file = files.CreateElement("add");
            file["value"] = "index.php";
            files.AddAt(0, file);
            result.Changes.Add("Add index.php as default document" + ScopeText(site));
        }

        /// <summary>
        /// Moves an element to the top. Inherited elements cannot be moved at a child scope, so the
        /// collection is cleared and rebuilt locally in the new order (the same thing IIS Manager's
        /// "View Ordered List" does).
        /// </summary>
        private static void MoveToTop(ConfigurationElementCollection collection, int index)
        {
            var snapshot = collection.Select(Snapshot).ToList();
            var target = snapshot[index];
            snapshot.RemoveAt(index);
            snapshot.Insert(0, target);

            collection.Clear();
            foreach (var attributes in snapshot)
            {
                var copy = collection.CreateElement("add");
                foreach (var pair in attributes)
                {
                    copy[pair.Key] = pair.Value;
                }
                collection.Add(copy);
            }
        }

        private static List<KeyValuePair<string, object>> Snapshot(ConfigurationElement element)
        {
            return element.Attributes
                .Where(a => !a.IsInheritedFromDefaultValue && a.Value != null)
                .Select(a => new KeyValuePair<string, object>(a.Name, a.Value))
                .ToList();
        }

        // ---------- helpers ----------

        private static List<FastCgiApp> ReadFastCgiApps(Configuration host)
        {
            var list = new List<FastCgiApp>();
            foreach (var a in host.GetSection(FastCgiSection).GetCollection())
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in a.GetCollection("environmentVariables"))
                {
                    env[(string)e["name"]] = (string)e["value"];
                }
                list.Add(new FastCgiApp
                {
                    FullPath = (string)a["fullPath"],
                    Arguments = (string)a["arguments"],
                    MaxInstances = Convert.ToInt64(a["maxInstances"]),
                    InstanceMaxRequests = Convert.ToInt64(a["instanceMaxRequests"]),
                    ActivityTimeout = Convert.ToInt64(a["activityTimeout"]),
                    RequestTimeout = Convert.ToInt64(a["requestTimeout"]),
                    EnvironmentVariables = env
                });
            }
            return list;
        }

        private static List<PhpHandler> ReadPhpHandlers(ServerManager manager, string site)
        {
            var list = new List<PhpHandler>();
            var collection = GetEffectiveSection(manager, site, HandlersSection).GetCollection();
            bool first = true;
            foreach (var h in collection)
            {
                if (!IsPhpHandler(h))
                {
                    continue;
                }
                list.Add(new PhpHandler
                {
                    Name = (string)h["name"],
                    Path = (string)h["path"],
                    ScriptProcessor = (string)h["scriptProcessor"],
                    IsActive = first,
                    IsLocal = h.IsLocallyStored
                });
                first = false;
            }
            return list;
        }

        /// <summary>Effective (merged, including web.config) settings for reading.</summary>
        private static ConfigurationSection GetEffectiveSection(ServerManager manager, string site, string section)
        {
            return site == null
                ? manager.GetApplicationHostConfiguration().GetSection(section)
                : manager.GetWebConfiguration(site).GetSection(section);
        }

        private static bool IsPhpHandler(ConfigurationElement h)
        {
            var path = (string)h["path"];
            var modules = (string)h["modules"];
            return path != null && path.Equals("*.php", StringComparison.OrdinalIgnoreCase)
                   && modules != null && modules.IndexOf("FastCgiModule", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static FastCgiApp FindFastCgiApp(IEnumerable<FastCgiApp> apps, string scriptProcessor)
        {
            var parts = SplitScriptProcessor(scriptProcessor);
            return apps.FirstOrDefault(a => SamePath(a.FullPath, parts[0])
                                            && string.Equals(a.Arguments ?? string.Empty, parts[1], StringComparison.Ordinal));
        }

        /// <summary>A handler's scriptProcessor is "fullPath|arguments".</summary>
        private static string[] SplitScriptProcessor(string scriptProcessor)
        {
            var value = scriptProcessor ?? string.Empty;
            int bar = value.IndexOf('|');
            return bar < 0
                ? new[] { value, string.Empty }
                : new[] { value.Substring(0, bar), value.Substring(bar + 1) };
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }
            try
            {
                return string.Equals(
                    Path.GetFullPath(Environment.ExpandEnvironmentVariables(a.Trim('"'))),
                    Path.GetFullPath(Environment.ExpandEnvironmentVariables(b.Trim('"'))),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string UniqueHandlerName(ConfigurationElementCollection handlers, string baseName)
        {
            var names = new HashSet<string>(handlers.Select(h => (string)h["name"]), StringComparer.OrdinalIgnoreCase);
            var name = baseName;
            for (int i = 2; names.Contains(name); i++)
            {
                name = baseName + "_" + i;
            }
            return name;
        }

        private static void EnsureSite(ServerManager manager, string site)
        {
            if (site != null && manager.Sites[site] == null)
            {
                throw new ArgumentException("IIS site not found: " + site);
            }
        }

        private static string ScopeText(string site)
        {
            return site == null ? " (server level)" : " (site '" + site + "')";
        }
    }
}
