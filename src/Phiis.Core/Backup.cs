using System;
using System.IO;

namespace Phiis.Core
{
    /// <summary>Timestamped copies of every file Phiis is about to change.</summary>
    public static class Backup
    {
        public static string DefaultDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Phiis", "backups");
            }
        }

        public static string CopyToBackup(string file, string backupDirectory)
        {
            Directory.CreateDirectory(backupDirectory);
            var target = Path.Combine(
                backupDirectory,
                Path.GetFileNameWithoutExtension(file) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + Path.GetExtension(file));
            File.Copy(file, target, false);
            return target;
        }
    }
}
