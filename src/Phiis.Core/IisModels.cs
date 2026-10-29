using System.Collections.Generic;

namespace Phiis.Core
{
    public sealed class FastCgiApp
    {
        public string FullPath { get; set; }
        public string Arguments { get; set; }
        public long MaxInstances { get; set; }
        public long InstanceMaxRequests { get; set; }
        public long ActivityTimeout { get; set; }
        public long RequestTimeout { get; set; }
        public Dictionary<string, string> EnvironmentVariables { get; set; }
    }

    public sealed class PhpHandler
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string ScriptProcessor { get; set; }
        /// <summary>The first *.php handler in the list is the one IIS uses.</summary>
        public bool IsActive { get; set; }
        public bool IsLocal { get; set; }
    }

    /// <summary>How a scope gets its PHP.</summary>
    public enum PhpScopeMode
    {
        /// <summary>The server level itself.</summary>
        Server,
        /// <summary>A site with no PHP handler of its own: it uses the server's (global) default.</summary>
        GlobalDefault,
        /// <summary>A site with its own *.php handler, used before any inherited one.</summary>
        Pinned,
        /// <summary>
        /// A site holding a full copy of the server's handler list (&lt;clear/&gt; + every handler), as written by
        /// older Phiis versions and IIS Manager's "View Ordered List". It no longer sees server-level changes.
        /// </summary>
        FullCopy
    }

    /// <summary>Everything Phiis knows about PHP at one IIS scope (server or a site).</summary>
    public sealed class PhpStatus
    {
        /// <summary>null = server level.</summary>
        public string Site { get; set; }
        public PhpScopeMode Mode { get; set; }
        /// <summary>The PHP versions registered at the server level, in IIS order (the first is the global default).</summary>
        public List<PhpHandler> GlobalHandlers { get; set; }
        /// <summary>The server-level default PHP handler, or null.</summary>
        public PhpHandler GlobalDefault { get; set; }
        public bool FastCgiModuleInstalled { get; set; }
        public List<PhpHandler> Handlers { get; set; }
        public List<FastCgiApp> FastCgiApps { get; set; }
        public PhpHandler ActiveHandler { get; set; }
        public FastCgiApp ActiveFastCgiApp { get; set; }
        public string IniPath { get; set; }
        public bool IndexPhpIsDefaultDocument { get; set; }
    }

    /// <summary>Result of a change operation. With dry-run, Changes lists what would happen.</summary>
    public sealed class ChangeResult
    {
        public ChangeResult()
        {
            Changes = new List<string>();
            Backups = new List<string>();
        }

        public bool DryRun { get; set; }
        public List<string> Changes { get; private set; }
        public List<string> Backups { get; private set; }
        public bool Changed
        {
            get { return Changes.Count > 0; }
        }
    }
}
