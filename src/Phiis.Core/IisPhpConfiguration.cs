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

                status.GlobalHandlers = site == null ? status.Handlers : ReadPhpHandlers(manager, null);
                status.GlobalDefault = status.GlobalHandlers.FirstOrDefault();
                status.Mode = site == null ? PhpScopeMode.Server : ReadSiteMode(site);

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
        /// The PHP versions registered at the server level (its *.php handlers), newest first. For a pinned site the
        /// version it uses is included even when it is no longer registered globally, so it can stay selected.
        /// </summary>
        public static List<PhpBuild> GetRegisteredBuilds(PhpStatus status)
        {
            var paths = status.GlobalHandlers.Select(h => SplitScriptProcessor(h.ScriptProcessor)[0]).ToList();
            if (status.Mode == PhpScopeMode.Pinned && status.ActiveHandler != null)
            {
                paths.Add(SplitScriptProcessor(status.ActiveHandler.ScriptProcessor)[0]);
            }

            var builds = new List<PhpBuild>();
            foreach (var path in paths.Select(p => Environment.ExpandEnvironmentVariables(p).Trim('"'))
                         .Where(File.Exists)
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

        /// <summary>True when the executable is one of the server-level (global) PHP versions.</summary>
        public static bool IsGloballyRegistered(PhpStatus status, string exe)
        {
            return status.GlobalHandlers.Any(h => SamePath(SplitScriptProcessor(h.ScriptProcessor)[0], exe));
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
        /// Server level (site == null): registers a php-cgi.exe with IIS and makes it the global default.
        /// Site level: pins the site to a PHP version that is registered at the server level (see <see cref="PinSite"/>).
        /// Idempotent: running it twice changes nothing the second time.
        /// </summary>
        public ChangeResult Register(string phpCgiPath, string site, bool dryRun)
        {
            if (site != null)
            {
                return PinSite(phpCgiPath, site, dryRun);
            }

            var build = PhpBuild.Inspect(phpCgiPath);
            var result = new ChangeResult { DryRun = dryRun };

            using (var manager = new ServerManager())
            {
                var host = manager.GetApplicationHostConfiguration();

                EnsureFastCgiApp(host, build, result);
                EnsureHandlerOnTop(host, null, build, result);
                EnsureIndexPhp(host, null, result);

                if (result.Changed && !dryRun)
                {
                    result.Backups.Add(Backup.CopyToBackup(ApplicationHostConfigPath, Backup.DefaultDirectory));
                    manager.CommitChanges();
                }
            }
            return result;
        }

        /// <summary>
        /// Gives a site its own PHP version. The site keeps it even if the version is later unregistered at the
        /// server level, until the site is switched to another version or back to the global default.
        /// Only versions registered at the server level (or the one the site already uses) can be chosen.
        /// </summary>
        public ChangeResult PinSite(string phpCgiPath, string site, bool dryRun)
        {
            var build = PhpBuild.Inspect(phpCgiPath);
            var result = new ChangeResult { DryRun = dryRun };
            var status = GetStatus(site);

            bool registered = status.GlobalHandlers.Any(h => SamePath(SplitScriptProcessor(h.ScriptProcessor)[0], build.ExecutablePath));
            bool current = status.Mode == PhpScopeMode.Pinned && status.ActiveHandler != null
                           && SamePath(SplitScriptProcessor(status.ActiveHandler.ScriptProcessor)[0], build.ExecutablePath);
            if (current)
            {
                return result; // already pinned to this version
            }
            if (!registered)
            {
                throw new InvalidOperationException("PHP " + build.VersionText + " (" + build.ExecutablePath + ") is not registered at the server level. "
                                                    + "Register it there first: phiis register \"" + build.ExecutablePath + "\"");
            }

            var name = "Phiis_PHP_" + (build.Branch ?? "unknown") + "_" + build.Architecture;
            PlanLeaveFullCopy(site, status, result);
            result.Changes.Add("Use PHP " + build.VersionText + " (" + build.ExecutablePath + ") for site '" + site + "' only (handler " + name + ")");

            if (!dryRun)
            {
                result.Backups.Add(Backup.CopyToBackup(ApplicationHostConfigPath, Backup.DefaultDirectory));
                LeaveFullCopy(site, status);
                AppHostAdmin.PinPhpHandler(site, name, build.ExecutablePath, ServerPhpNames(status));
            }
            return result;
        }

        private static HashSet<string> ServerPhpNames(PhpStatus status)
        {
            return new HashSet<string>(status.GlobalHandlers.Select(h => h.Name), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Makes a site use the server's (global) default PHP version again.</summary>
        public ChangeResult UseGlobalDefault(string site, bool dryRun)
        {
            if (site == null)
            {
                throw new ArgumentException("UseGlobalDefault applies to a site; the server level is the global default.");
            }
            var result = new ChangeResult { DryRun = dryRun };
            var status = GetStatus(site);
            if (status.Mode == PhpScopeMode.GlobalDefault && !AppHostAdmin.ReadLocal(site).Removes.Any(ServerPhpNames(status).Contains))
            {
                return result; // already inheriting, and no leftover <remove> hides the server's PHP
            }

            PlanLeaveFullCopy(site, status, result);
            result.Changes.Add("Site '" + site + "' uses the global default PHP"
                               + (status.GlobalDefault == null ? " (none is registered at the server level)" : " (" + status.GlobalDefault.ScriptProcessor + ")"));

            if (!dryRun)
            {
                result.Backups.Add(Backup.CopyToBackup(ApplicationHostConfigPath, Backup.DefaultDirectory));
                LeaveFullCopy(site, status);
                AppHostAdmin.ResetPhp(site, ServerPhpNames(status));
            }
            return result;
        }

        /// <summary>
        /// Removes a PHP version from the server-level list. The global default cannot be removed (switch first).
        /// Sites pinned to the version keep working: its FastCGI application is kept while any site uses it.
        /// PHP files on disk are never touched.
        /// </summary>
        public ChangeResult Unregister(string phpCgiPath, bool dryRun)
        {
            var exe = Path.GetFullPath(Environment.ExpandEnvironmentVariables(phpCgiPath.Trim('"')));
            var result = new ChangeResult { DryRun = dryRun };

            using (var manager = new ServerManager())
            {
                var host = manager.GetApplicationHostConfiguration();
                var handlers = host.GetSection(HandlersSection).GetCollection();
                var php = handlers.Where(IsPhpHandler).ToList();
                var matches = php.Where(h => SamePath(SplitScriptProcessor((string)h["scriptProcessor"])[0], exe)).ToList();
                if (matches.Count == 0)
                {
                    return result;
                }
                if (php.IndexOf(matches[0]) == 0)
                {
                    throw new InvalidOperationException(exe + " is the global default PHP. Make another version the default first, then unregister this one.");
                }

                foreach (var h in matches)
                {
                    result.Changes.Add("Remove server-level handler " + h["name"] + " (" + exe + ")");
                    handlers.Remove(h);
                }

                var users = SitesUsing(manager, exe);
                var apps = host.GetSection(FastCgiSection).GetCollection();
                foreach (var app in apps.Where(a => SamePath((string)a["fullPath"], exe)).ToList())
                {
                    if (users.Count > 0)
                    {
                        result.Changes.Add("Keep FastCGI application " + exe + ": still used by site(s) " + string.Join(", ", users));
                    }
                    else
                    {
                        result.Changes.Add("Remove FastCGI application " + exe);
                        apps.Remove(app);
                    }
                }

                if (!dryRun)
                {
                    result.Backups.Add(Backup.CopyToBackup(ApplicationHostConfigPath, Backup.DefaultDirectory));
                    manager.CommitChanges();
                }
            }
            return result;
        }

        /// <summary>Sites whose own handlers point at the executable: pinned sites and full copies.</summary>
        public static List<string> SitesUsing(ServerManager manager, string exe)
        {
            return manager.Sites.Select(s => s.Name)
                .Where(site => AppHostAdmin.ReadLocal(site).PhpAdds.Any(a => SamePath(SplitScriptProcessor(a.ScriptProcessor)[0], exe)))
                .ToList();
        }

        private static PhpScopeMode ReadSiteMode(string site)
        {
            var local = AppHostAdmin.ReadLocal(site);
            if (local.HasClear)
            {
                return PhpScopeMode.FullCopy;
            }
            return local.PhpAdds.Any() ? PhpScopeMode.Pinned : PhpScopeMode.GlobalDefault;
        }

        /// <summary>
        /// A full copy (&lt;clear/&gt; + every handler) is replaced by inheritance before changing PHP, but only when
        /// its non-PHP handlers equal the server's: otherwise it holds real site customisations, which Phiis will not drop.
        /// </summary>
        private void PlanLeaveFullCopy(string site, PhpStatus status, ChangeResult result)
        {
            if (status.Mode != PhpScopeMode.FullCopy)
            {
                return;
            }
            var differences = FullCopyDifferences(site);
            if (differences.Count > 0)
            {
                throw new InvalidOperationException("Site '" + site + "' has its own copy of the whole handler list with custom changes ("
                                                    + string.Join(", ", differences.Take(5)) + (differences.Count > 5 ? ", ..." : "")
                                                    + "). Phiis will not drop them. Review 'Handler Mappings' for the site in IIS Manager "
                                                    + "('Revert To Parent' removes the copy), then try again.");
            }
            result.Changes.Add("Replace site '" + site + "''s frozen copy of the server handler list with inheritance (it has no custom handlers; it also picks up handlers added to the server since)");
        }

        private static void LeaveFullCopy(string site, PhpStatus status)
        {
            if (status.Mode == PhpScopeMode.FullCopy)
            {
                AppHostAdmin.DeleteLocalSection(site);
            }
        }

        /// <summary>
        /// Non-PHP handlers that exist only in a site's full copy, or differ from the server's: real site customisations.
        /// Handlers the server has but the copy lacks are just staleness (added to the server after the copy was made),
        /// and inheritance brings them back, so they do not count. Neither does order.
        /// </summary>
        private static List<string> FullCopyDifferences(string site)
        {
            using (var manager = new ServerManager())
            {
                var host = manager.GetApplicationHostConfiguration();
                Func<ConfigurationElementCollection, List<string>> describe = c => c
                    .Where(h => !IsPhpHandler(h))
                    .Select(h => string.Join("|", Snapshot(h).OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)))
                    .ToList();
                var server = describe(host.GetSection(HandlersSection).GetCollection());
                var copy = describe(host.GetSection(HandlersSection, site).GetCollection());

                return copy.Except(server).Select(NameOf).Distinct().ToList();
            }
        }

        private static string NameOf(string description)
        {
            var name = description.Split('|').FirstOrDefault(p => p.StartsWith("name=", StringComparison.Ordinal));
            return name == null ? description : name.Substring(5);
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

        /// <summary>Server level only: sites get their PHP through <see cref="PinSite"/>, which writes a delta instead of a copy.</summary>
        private static void EnsureHandlerOnTop(Configuration host, string site, PhpBuild build, ChangeResult result)
        {
            var section = host.GetSection(HandlersSection);
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
