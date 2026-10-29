using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace Phiis.Core
{
    /// <summary>What a site's &lt;location&gt; in applicationHost.config itself says about handlers (not the merged view).</summary>
    public sealed class LocalHandlers
    {
        public LocalHandlers()
        {
            Adds = new List<LocalHandler>();
            Removes = new List<string>();
        }

        /// <summary>&lt;clear/&gt; present: the site holds a full copy and inherits nothing.</summary>
        public bool HasClear { get; set; }
        public List<LocalHandler> Adds { get; private set; }
        public List<string> Removes { get; private set; }

        public IEnumerable<LocalHandler> PhpAdds
        {
            get { return Adds.Where(a => a.IsPhp); }
        }

        /// <summary>True when the location holds nothing but PHP entries, so deleting it loses nothing else.</summary>
        public bool OnlyPhp(ICollection<string> serverPhpNames)
        {
            return !HasClear && Adds.All(a => a.IsPhp)
                   && Removes.All(r => serverPhpNames.Contains(r) || Adds.Any(a => a.IsPhp && a.Name.Equals(r, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public sealed class LocalHandler
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Modules { get; set; }
        public string ScriptProcessor { get; set; }

        public bool IsPhp
        {
            get
            {
                return string.Equals(Path, "*.php", StringComparison.OrdinalIgnoreCase)
                       && (Modules ?? string.Empty).IndexOf("FastCgiModule", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }

    /// <summary>
    /// Site-level *.php handlers, done so that sites keep inheriting everything else from the server.
    ///
    /// Reading: the site's &lt;location&gt; is read from applicationHost.config directly (read-only), because neither
    /// Microsoft.Web.Administration (it reports inherited handlers as locally stored at site level) nor AHADMIN tells
    /// reliably which handlers belong to the site.
    ///
    /// Writing: through IIS's native configuration API (AHADMIN, the API appcmd uses). Microsoft.Web.Administration
    /// writes any site change to the ordered handlers collection as &lt;clear/&gt; plus a full copy of the server's
    /// list; such a site is frozen (it no longer sees server changes) and an appended entry lands after the StaticFile
    /// catch-all. AHADMIN writes a real delta: one &lt;add&gt; that IIS merges in front of the inherited handlers.
    /// AHADMIN is an in-process COM server, so it works inside IIS Manager, which blocks starting appcmd.exe.
    /// Interface layouts are taken from the type library in nativerd.dll.
    /// </summary>
    internal static class AppHostAdmin
    {
        private const string HandlersSection = "system.webServer/handlers";
        private const string AppHostPath = "MACHINE/WEBROOT/APPHOST";

        public static LocalHandlers ReadLocal(string site)
        {
            var result = new LocalHandlers();
            XDocument doc;
            using (var stream = new FileStream(IisPhpConfiguration.ApplicationHostConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                doc = XDocument.Load(stream);
            }

            var locations = doc.Root == null ? Enumerable.Empty<XElement>() : doc.Root.Elements("location")
                .Where(l => string.Equals(((string)l.Attribute("path") ?? string.Empty).Trim('/'), site, StringComparison.OrdinalIgnoreCase));
            foreach (var handlers in locations.Select(l => l.Element("system.webServer")).Where(w => w != null).Select(w => w.Element("handlers")).Where(h => h != null))
            {
                foreach (var e in handlers.Elements())
                {
                    switch (e.Name.LocalName)
                    {
                        case "clear":
                            result.HasClear = true;
                            result.Adds.Clear();
                            result.Removes.Clear();
                            break;
                        case "remove":
                            result.Removes.Add((string)e.Attribute("name"));
                            break;
                        case "add":
                            result.Adds.Add(new LocalHandler
                            {
                                Name = (string)e.Attribute("name"),
                                Path = (string)e.Attribute("path"),
                                Modules = (string)e.Attribute("modules"),
                                ScriptProcessor = (string)e.Attribute("scriptProcessor")
                            });
                            break;
                    }
                }
            }
            return result;
        }

        /// <summary>Removes the site's own PHP entries, so the site inherits the server's PHP again.</summary>
        public static void ResetPhp(string site, ICollection<string> serverPhpNames)
        {
            var local = ReadLocal(site);
            if (local.OnlyPhp(serverPhpNames))
            {
                DeleteLocalSection(site); // nothing else lives there: back to pure inheritance, <remove> entries included
                return;
            }

            // The site has other handler customisations: delete only its PHP <add> entries.
            var names = new HashSet<string>(local.PhpAdds.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            var manager = Open();
            var collection = manager.GetAdminSection(HandlersSection, SitePath(site)).Collection;
            for (int i = (int)collection.Count - 1; i >= 0; i--)
            {
                if (names.Contains(Get(collection[i], "name")))
                {
                    collection.DeleteElement(i);
                }
            }
            manager.CommitChanges();
        }

        /// <summary>Gives the site one *.php handler that IIS uses before any inherited one.</summary>
        public static void PinPhpHandler(string site, string name, string scriptProcessor, ICollection<string> serverPhpNames)
        {
            ResetPhp(site, serverPhpNames);

            var manager = Open();
            var collection = manager.GetAdminSection(HandlersSection, SitePath(site)).Collection;
            var e = collection.CreateNewElement("add");
            Set(e, "name", name);
            Set(e, "path", "*.php");
            Set(e, "verb", "GET,HEAD,POST");
            Set(e, "modules", "FastCgiModule");
            Set(e, "scriptProcessor", scriptProcessor);
            Set(e, "resourceType", "Either");
            Set(e, "requireAccess", "Script");
            collection.AddElement(e, 0);
            manager.CommitChanges();
        }

        /// <summary>Deletes the site's whole handlers section in applicationHost.config (pure inheritance).</summary>
        public static void DeleteLocalSection(string site)
        {
            using (var manager = new Microsoft.Web.Administration.ServerManager())
            {
                manager.GetApplicationHostConfiguration().GetSection(HandlersSection, site).Delete();
                manager.CommitChanges();
            }
        }

        // ---------- helpers ----------

        private static IAppHostWritableAdminManager Open()
        {
            var manager = (IAppHostWritableAdminManager)new AppHostWritableAdminManager();
            manager.CommitPath = AppHostPath;
            return manager;
        }

        private static string SitePath(string site)
        {
            return AppHostPath + "/" + site;
        }

        private static string Get(IAppHostElement e, string property)
        {
            return Convert.ToString(e.GetPropertyByName(property).Value) ?? string.Empty;
        }

        private static void Set(IAppHostElement e, string property, string value)
        {
            e.GetPropertyByName(property).Value = value;
        }

        // ---------- COM interop (layouts from the nativerd.dll type library) ----------

        [ComImport, Guid("2b72133b-3f5b-4602-8952-803546ce3344")]
        private class AppHostWritableAdminManager
        {
        }

        [ComImport, Guid("fa7660f6-7b3f-4237-a8bf-ed0ad0dcbbd9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAppHostWritableAdminManager
        {
            IAppHostElement GetAdminSection([MarshalAs(UnmanagedType.BStr)] string sectionName, [MarshalAs(UnmanagedType.BStr)] string path);
            [return: MarshalAs(UnmanagedType.Struct)] object GetMetadata([MarshalAs(UnmanagedType.BStr)] string type);
            void SetMetadata([MarshalAs(UnmanagedType.BStr)] string type, [MarshalAs(UnmanagedType.Struct)] object value);
            object ConfigManager { [return: MarshalAs(UnmanagedType.IUnknown)] get; }
            void CommitChanges();
            string CommitPath { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        }

        [ComImport, Guid("64ff8ccc-b287-4dae-b08a-a72cbf45f453"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAppHostElement
        {
            string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
            IAppHostElementCollection Collection { get; }
            object Properties { [return: MarshalAs(UnmanagedType.IUnknown)] get; }
            object ChildElements { [return: MarshalAs(UnmanagedType.IUnknown)] get; }
            [return: MarshalAs(UnmanagedType.Struct)] object GetMetadata([MarshalAs(UnmanagedType.BStr)] string type);
            void SetMetadata([MarshalAs(UnmanagedType.BStr)] string type, [MarshalAs(UnmanagedType.Struct)] object value);
            object Schema { [return: MarshalAs(UnmanagedType.IUnknown)] get; }
            IAppHostElement GetElementByName([MarshalAs(UnmanagedType.BStr)] string name);
            IAppHostProperty GetPropertyByName([MarshalAs(UnmanagedType.BStr)] string name);
            void Clear();
            object Methods { [return: MarshalAs(UnmanagedType.IUnknown)] get; }
        }

        [ComImport, Guid("c8550bff-5281-4b1e-ac34-99b6fa38464d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAppHostElementCollection
        {
            uint Count { get; }
            IAppHostElement this[[MarshalAs(UnmanagedType.Struct)] object index] { get; }
            void AddElement(IAppHostElement element, int position);
            void DeleteElement([MarshalAs(UnmanagedType.Struct)] object index);
            void Clear();
            IAppHostElement CreateNewElement([MarshalAs(UnmanagedType.BStr)] string name);
            object Schema { [return: MarshalAs(UnmanagedType.IUnknown)] get; }
        }

        [ComImport, Guid("ed35f7a1-5024-4e7b-a44d-07ddaf4b524d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAppHostProperty
        {
            string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
            object Value { [return: MarshalAs(UnmanagedType.Struct)] get; [param: MarshalAs(UnmanagedType.Struct)] set; }
        }
    }
}
