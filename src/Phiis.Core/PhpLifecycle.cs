using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace Phiis.Core
{
    public enum LifecycleStatus
    {
        Unknown,
        Supported,
        EndingSoon,
        EndOfLife
    }

    /// <summary>
    /// PHP branch end-of-life data. Loaded from a JSON file so it can be updated without a rebuild:
    /// %ProgramData%\Phiis\php-lifecycle.json overrides the copy embedded in this assembly.
    /// Unknown (newer) branches are reported as Unknown, never as obsolete.
    /// </summary>
    public sealed class PhpLifecycle
    {
        public const int EndingSoonDays = 180;

        private readonly Dictionary<string, DateTime> _eol;

        public string Updated { get; private set; }
        public string Source { get; private set; }

        private PhpLifecycle(Dictionary<string, DateTime> eol, string updated, string source)
        {
            _eol = eol;
            Updated = updated;
            Source = source;
        }

        public static string OverridePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Phiis", "php-lifecycle.json");
            }
        }

        public static PhpLifecycle Load()
        {
            if (File.Exists(OverridePath))
            {
                try
                {
                    return Parse(File.ReadAllText(OverridePath));
                }
                catch (Exception)
                {
                    // A broken override file must not break Phiis; fall back to the embedded copy.
                }
            }

            using (var stream = typeof(PhpLifecycle).Assembly.GetManifestResourceStream("Phiis.Core.php-lifecycle.json"))
            using (var reader = new StreamReader(stream))
            {
                return Parse(reader.ReadToEnd());
            }
        }

        public static PhpLifecycle Parse(string json)
        {
            var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            var eol = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (var item in (object[])root["branches"])
            {
                var branch = (Dictionary<string, object>)item;
                eol[(string)branch["branch"]] = DateTime.ParseExact(
                    (string)branch["eol"], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            object updated, source;
            root.TryGetValue("updated", out updated);
            root.TryGetValue("source", out source);
            return new PhpLifecycle(eol, updated as string, source as string);
        }

        public bool TryGetEndOfLife(string branch, out DateTime eol)
        {
            eol = DateTime.MinValue;
            return branch != null && _eol.TryGetValue(branch, out eol);
        }

        public LifecycleStatus GetStatus(string branch, DateTime today)
        {
            DateTime eol;
            if (!TryGetEndOfLife(branch, out eol))
            {
                return LifecycleStatus.Unknown;
            }
            if (today.Date > eol)
            {
                return LifecycleStatus.EndOfLife;
            }
            return (eol - today.Date).TotalDays <= EndingSoonDays
                ? LifecycleStatus.EndingSoon
                : LifecycleStatus.Supported;
        }
    }
}
