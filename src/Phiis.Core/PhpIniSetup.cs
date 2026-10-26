using System.IO;

namespace Phiis.Core
{
    /// <summary>
    /// Creates php.ini when missing and applies the few settings PHP needs to run under IIS FastCGI.
    /// Deliberately minimal: Phiis does not impose opinions beyond what IIS requires.
    /// </summary>
    public static class PhpIniSetup
    {
        public static void Prepare(PhpBuild build, string iniPath, ChangeResult result)
        {
            PhpIniDocument ini;
            if (File.Exists(iniPath))
            {
                ini = PhpIniDocument.Load(iniPath);
            }
            else
            {
                var template = Path.Combine(build.Directory, "php.ini-production");
                ini = File.Exists(template)
                    ? PhpIniDocument.FromText(File.ReadAllText(template))
                    : PhpIniDocument.FromText("[PHP]\r\n");
                result.Changes.Add("Create " + iniPath + (File.Exists(template) ? " from php.ini-production" : ""));
            }

            Set(ini, "extension_dir", "\"" + Path.Combine(build.Directory, "ext") + "\"", result);
            Set(ini, "cgi.fix_pathinfo", "1", result);
            Set(ini, "fastcgi.impersonate", "1", result);
            Set(ini, "fastcgi.logging", "0", result);

            if (result.Changed && !result.DryRun)
            {
                ini.Save(iniPath, Backup.DefaultDirectory);
            }
        }

        private static void Set(PhpIniDocument ini, string name, string value, ChangeResult result)
        {
            if (ini.SetValue(name, value))
            {
                result.Changes.Add("php.ini: " + name + " = " + value);
            }
        }
    }
}
