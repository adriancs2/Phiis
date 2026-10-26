using System;
using Microsoft.Web.Management.Server;

namespace Phiis.IisManager
{
    /// <summary>
    /// Entry point registered in administration.config. Tells IIS Manager which client module to load
    /// and at which scopes (server and site) the Phiis page is available.
    /// </summary>
    public sealed class PhiisModuleProvider : ModuleProvider
    {
        public override Type ServiceType
        {
            get { return null; } // v0.1 works on local connections only; no remote service yet.
        }

        public override ModuleDefinition GetModuleDefinition(IManagementContext context)
        {
            return new ModuleDefinition(Name, typeof(PhiisModule).AssemblyQualifiedName);
        }

        public override bool SupportsScope(ManagementScope scope)
        {
            return scope == ManagementScope.Server || scope == ManagementScope.Site;
        }
    }
}
