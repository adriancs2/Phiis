using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Web.Administration;

namespace Phiis.Core
{
    public sealed class SnapinProvider
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// Registers the Phiis snap-in with IIS Manager through administration.config:
    /// a &lt;moduleProviders&gt; entry (which assembly to load) and a &lt;modules&gt; entry
    /// in location path="." (enable it for the server). Idempotent.
    /// </summary>
    public static class SnapinRegistration
    {
        public const string ProviderName = "Phiis";

        public static ChangeResult Register(AssemblyName snapinAssembly, bool dryRun)
        {
            var type = "Phiis.IisManager.PhiisModuleProvider, " + snapinAssembly.FullName;
            var result = new ChangeResult { DryRun = dryRun };

            using (var manager = new ServerManager())
            {
                var admin = manager.GetAdministrationConfiguration();

                var providers = admin.GetSection("moduleProviders").GetCollection();
                var provider = Find(providers, ProviderName);
                if (provider == null)
                {
                    provider = providers.CreateElement("add");
                    provider["name"] = ProviderName;
                    provider["type"] = type;
                    providers.Add(provider);
                    result.Changes.Add("Add IIS Manager module provider " + type);
                }
                else if (!string.Equals((string)provider["type"], type, StringComparison.Ordinal))
                {
                    provider["type"] = type;
                    result.Changes.Add("Update IIS Manager module provider to " + type);
                }

                var modules = admin.GetSection("modules").GetCollection();
                if (Find(modules, ProviderName) == null)
                {
                    var module = modules.CreateElement("add");
                    module["name"] = ProviderName;
                    modules.Add(module);
                    result.Changes.Add("Enable IIS Manager module " + ProviderName);
                }

                Commit(manager, result);
            }
            return result;
        }

        public static ChangeResult Unregister(bool dryRun)
        {
            var result = new ChangeResult { DryRun = dryRun };
            using (var manager = new ServerManager())
            {
                var admin = manager.GetAdministrationConfiguration();

                var modules = admin.GetSection("modules").GetCollection();
                var module = Find(modules, ProviderName);
                if (module != null)
                {
                    modules.Remove(module);
                    result.Changes.Add("Disable IIS Manager module " + ProviderName);
                }

                var providers = admin.GetSection("moduleProviders").GetCollection();
                var provider = Find(providers, ProviderName);
                if (provider != null)
                {
                    providers.Remove(provider);
                    result.Changes.Add("Remove IIS Manager module provider " + ProviderName);
                }

                Commit(manager, result);
            }
            return result;
        }

        /// <summary>All registered IIS Manager providers that look PHP-related (Phiis, PHP Manager 2, ...).</summary>
        public static List<SnapinProvider> GetPhpProviders()
        {
            using (var manager = new ServerManager())
            {
                var admin = manager.GetAdministrationConfiguration();
                var enabled = new HashSet<string>(
                    admin.GetSection("modules").GetCollection().Select(m => (string)m["name"]),
                    StringComparer.OrdinalIgnoreCase);

                return admin.GetSection("moduleProviders").GetCollection()
                    .Select(p => new SnapinProvider
                    {
                        Name = (string)p["name"],
                        Type = (string)p["type"],
                        Enabled = enabled.Contains((string)p["name"])
                    })
                    .Where(p => p.Name.Equals(ProviderName, StringComparison.OrdinalIgnoreCase)
                                || (p.Type ?? string.Empty).IndexOf("PHP", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }
        }

        private static ConfigurationElement Find(ConfigurationElementCollection collection, string name)
        {
            return collection.FirstOrDefault(e => string.Equals((string)e["name"], name, StringComparison.OrdinalIgnoreCase));
        }

        private static void Commit(ServerManager manager, ChangeResult result)
        {
            if (result.Changed && !result.DryRun)
            {
                var path = System.IO.Path.Combine(Environment.SystemDirectory, @"inetsrv\config\administration.config");
                result.Backups.Add(Backup.CopyToBackup(path, Backup.DefaultDirectory));
                manager.CommitChanges();
            }
        }
    }
}
