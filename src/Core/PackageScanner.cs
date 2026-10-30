using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;

namespace StigGpoManager.Core
{
    /// <summary>A GPO backup found in a DISA package (a {GUID} folder containing bkupInfo.xml).</summary>
    public sealed class GpoBackupInfo
    {
        /// <summary>Backup ID ({GUID} folder name) passed to Import-GPO -BackupId.</summary>
        public string BackupId { get; set; }
        /// <summary>GUID of the GPO that DISA backed up (in DISA's domain; not meaningful here).</summary>
        public string SourceGpoGuid { get; set; }
        public string DisplayName { get; set; }
        public string SourceDomain { get; set; }
        public string BackupTime { get; set; }
        /// <summary>Full path of the {GUID} backup folder.</summary>
        public string BackupFolder { get; set; }
        /// <summary>Folder containing the {GUID} folders (Import-GPO -Path).</summary>
        public string BackupRoot { get; set; }
        /// <summary>Name of the DISA product folder, e.g. "DoD WinSvr 2025 MS and DC v1r4".</summary>
        public string ProductFolder { get; set; }
        public bool HasComputerSettings { get; set; }
        public bool HasUserSettings { get; set; }
        public string ReportPath { get; set; }
        public StigVersion DetectedVersion { get; set; }
        /// <summary>Domain accounts / groups (from DISA's build domain) the backup's settings refer to, from Backup.xml.</summary>
        public List<ForeignPrincipal> ForeignPrincipals { get; set; } = new List<ForeignPrincipal>();

        public string Scope =>
            HasComputerSettings && HasUserSettings ? "Computer+User" :
            HasComputerSettings ? "Computer" :
            HasUserSettings ? "User" : "(empty)";
    }

    public enum TemplateKind { Admx, Adml }

    /// <summary>An ADMX or ADML template in the package.</summary>
    public sealed class TemplateFile
    {
        public TemplateKind Kind { get; set; }
        public string FileName { get; set; }
        /// <summary>Path relative to PolicyDefinitions: "x.admx" or "en-US\x.adml".</summary>
        public string RelativeTarget { get; set; }
        public string FullPath { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public string Sha256 { get; set; }
        /// <summary>Number of other copies of the same file found in the package (older ones are ignored).</summary>
        public int DuplicatesIgnored { get; set; }
    }

    public sealed class PackageScanResult
    {
        public string PackageName { get; set; }
        public string ExtractedPath { get; set; }
        public List<GpoBackupInfo> Backups { get; } = new List<GpoBackupInfo>();
        public List<TemplateFile> Templates { get; } = new List<TemplateFile>();
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// Extracts a DISA GPO package and discovers its contents by content (bkupInfo.xml,
    /// *.admx, &lt;Language&gt;\*.adml), never by hardcoded folder names.
    /// </summary>
    public static class PackageScanner
    {
        public static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                return ToHex(sha.ComputeHash(fs));
        }

        public static string ToHex(byte[] bytes)
        {
            var c = new char[bytes.Length * 2];
            const string hex = "0123456789ABCDEF";
            for (int i = 0; i < bytes.Length; i++) { c[i * 2] = hex[bytes[i] >> 4]; c[i * 2 + 1] = hex[bytes[i] & 15]; }
            return new string(c);
        }

        /// <summary>Normalizes a pasted hash (spaces, colons, case) for comparison.</summary>
        public static string NormalizeHash(string hash) =>
            new string((hash ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

        /// <summary>
        /// Extracts the zip to packagesFolder\&lt;short-name&gt;-&lt;hash8&gt;. The folder name carries the package hash,
        /// so a folder of that name holding a different (or half-extracted) package can only be a leftover from an
        /// earlier attempt of ours: it is replaced, never a reason to stop. Extraction goes to a ".partial" folder
        /// first and is moved into place only when complete, so an interrupted extraction never looks finished.
        /// Entries that would escape the destination are rejected. Nested zips are expanded one level.
        /// </summary>
        public static string Extract(string zipPath, string packagesFolder, string sha256, Action<string> log = null)
        {
            var dest = Path.Combine(packagesFolder, FolderNameFor(zipPath, sha256));
            var marker = Path.Combine(dest, ".stiggpomanager-source.sha256");
            if (Directory.Exists(dest))
            {
                if (File.Exists(marker) && string.Equals(File.ReadAllText(marker).Trim(), sha256, StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"Package already extracted at {dest} (hash matches); reusing.");
                    return dest;
                }
                log?.Invoke($"Removing an incomplete earlier extraction at {dest} ...");
                DeleteTree(dest);
            }
            var partial = dest + ".partial";
            if (Directory.Exists(partial)) DeleteTree(partial);
            Directory.CreateDirectory(packagesFolder);
            log?.Invoke($"Extracting {Path.GetFileName(zipPath)} to {dest} ...");
            try
            {
                Directory.CreateDirectory(partial);
                ExtractSafe(zipPath, partial);

                // DISA occasionally nests zips; expand one level so discovery can see them.
                foreach (var inner in Directory.GetFiles(partial, "*.zip", SearchOption.AllDirectories))
                {
                    var innerDest = Path.Combine(Path.GetDirectoryName(inner), Path.GetFileNameWithoutExtension(inner));
                    if (Directory.Exists(innerDest)) continue;
                    log?.Invoke($"Expanding nested archive {inner.Substring(partial.Length).TrimStart('\\', '/')}");
                    Directory.CreateDirectory(innerDest);
                    ExtractSafe(inner, innerDest);
                }
                File.WriteAllText(Path.Combine(partial, ".stiggpomanager-source.sha256"), sha256);
                Directory.Move(partial, dest);
            }
            catch (Exception ex)
            {
                try { DeleteTree(partial); } catch { /* best effort; replaced on the next attempt */ }
                if (ex is InvalidDataException) throw;
                throw new IOException($"Could not extract {Path.GetFileName(zipPath)} to {packagesFolder}: {ex.Message}", ex);
            }
            return dest;
        }

        /// <summary>"U_STIG_GPO_Package_October_2026.zip" -&gt; "U_STIG_GPO_Package_Octob-C8A093C0": short, so DISA's deep
        /// folder structure stays well under the 260-character path limit GPMC and older tools still have.</summary>
        public static string FolderNameFor(string zipPath, string sha256)
        {
            var name = new string(Path.GetFileNameWithoutExtension(zipPath).Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray()).Trim('_');
            if (name.Length > 24) name = name.Substring(0, 24).TrimEnd('_', '-');
            if (name.Length == 0) name = "package";
            return name + "-" + NormalizeHash(sha256).PadRight(8, '0').Substring(0, 8);
        }

        public static void DeleteTree(string path)
        {
            if (!Directory.Exists(LongPath(path))) return;
            foreach (var f in Directory.GetFiles(LongPath(path), "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal); // read-only files in DISA packages would block the delete
            Directory.Delete(LongPath(path), true);
        }

        /// <summary>On Windows, the \\?\ form lets .NET Framework write paths longer than 260 characters.</summary>
        private static string LongPath(string fullPath)
        {
            if (Path.DirectorySeparatorChar != '\\' || fullPath.Length < 240 || fullPath.StartsWith(@"\\?\")) return fullPath;
            return fullPath.StartsWith(@"\\") ? @"\\?\UNC\" + fullPath.Substring(2) : @"\\?\" + fullPath;
        }

        private static void ExtractSafe(string zipPath, string dest)
        {
            var root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    var relative = entry.FullName.Replace('\\', '/');
                    var target = Path.GetFullPath(Path.Combine(dest, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Zip entry '{entry.FullName}' would extract outside the package folder; the package is rejected.");
                    if (relative.EndsWith("/")) { Directory.CreateDirectory(LongPath(target)); continue; }
                    Directory.CreateDirectory(LongPath(Path.GetDirectoryName(target)));
                    var path = LongPath(target);
                    if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
                    entry.ExtractToFile(path, true);
                    try { File.SetLastWriteTime(path, entry.LastWriteTime.DateTime); } catch (IOException) { } catch (ArgumentOutOfRangeException) { }
                }
            }
        }

        /// <summary>Discovers GPO backups and ADMX/ADML templates under an extracted package.</summary>
        public static PackageScanResult Scan(string extractedPath, string language, Action<string> log = null)
        {
            var result = new PackageScanResult
            {
                ExtractedPath = extractedPath,
                PackageName = Path.GetFileName(extractedPath.TrimEnd('\\', '/'))
            };

            foreach (var info in Directory.GetFiles(extractedPath, "bkupInfo.xml", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var b = ReadBackup(info);
                    result.Backups.Add(b);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Could not read {info}: {ex.Message}");
                }
            }
            log?.Invoke($"Found {result.Backups.Count} GPO backup(s).");

            var dupGroups = result.Backups.GroupBy(b => b.DisplayName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
            foreach (var g in dupGroups)
                result.Warnings.Add($"Backup display name '{g.Key}' appears {g.Count()} times in the package.");

            result.Templates.AddRange(FindTemplates(extractedPath, language, result.Warnings));
            log?.Invoke($"Found {result.Templates.Count(t => t.Kind == TemplateKind.Admx)} ADMX and {result.Templates.Count(t => t.Kind == TemplateKind.Adml)} {language} ADML file(s).");
            return result;
        }

        public static GpoBackupInfo ReadBackup(string bkupInfoPath)
        {
            var doc = LoadXml(bkupInfoPath);
            string Get(string local) => doc.DocumentElement?.SelectSingleNode($"*[local-name()='{local}']")?.InnerText?.Trim();

            var folder = Path.GetDirectoryName(bkupInfoPath);
            var rootFolder = Path.GetDirectoryName(folder);
            var backupId = Get("ID");
            if (string.IsNullOrEmpty(backupId)) backupId = Path.GetFileName(folder);
            var b = new GpoBackupInfo
            {
                BackupId = backupId,
                SourceGpoGuid = Get("GPOGuid"),
                DisplayName = Get("GPODisplayName"),
                SourceDomain = Get("GPODomain"),
                BackupTime = Get("BackupTime"),
                BackupFolder = folder,
                BackupRoot = rootFolder,
                ProductFolder = ProductFolderOf(rootFolder),
            };
            if (string.IsNullOrEmpty(b.DisplayName)) throw new InvalidDataException("bkupInfo.xml has no GPODisplayName.");

            var report = Path.Combine(folder, "gpreport.xml");
            if (File.Exists(report))
            {
                b.ReportPath = report;
                ReadReportScopes(LoadXml(report), out var hasComputer, out var hasUser);
                b.HasComputerSettings = hasComputer;
                b.HasUserSettings = hasUser;
            }
            b.DetectedVersion = StigVersion.Detect(b.DisplayName, b.ProductFolder);
            b.ForeignPrincipals = MigrationTables.ReadBackupXml(folder);
            return b;
        }

        /// <summary>The product folder is the parent of a "GPOs" folder, else the folder holding the backups.</summary>
        private static string ProductFolderOf(string backupRoot)
        {
            var name = Path.GetFileName(backupRoot);
            if (string.Equals(name, "GPOs", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "GPO", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(Path.GetDirectoryName(backupRoot));
            return name;
        }

        /// <summary>True for each side (Computer/User) that has at least one extension with settings.</summary>
        public static void ReadReportScopes(XmlDocument report, out bool hasComputer, out bool hasUser)
        {
            hasComputer = report.SelectSingleNode("/*[local-name()='GPO']/*[local-name()='Computer']/*[local-name()='ExtensionData']") != null;
            hasUser = report.SelectSingleNode("/*[local-name()='GPO']/*[local-name()='User']/*[local-name()='ExtensionData']") != null;
        }

        public static List<TemplateFile> FindTemplates(string root, string language, List<string> warnings)
        {
            var candidates = new List<TemplateFile>();
            foreach (var f in Directory.GetFiles(root, "*.admx", SearchOption.AllDirectories))
                candidates.Add(MakeTemplate(f, TemplateKind.Admx, Path.GetFileName(f)));
            foreach (var f in Directory.GetFiles(root, "*.adml", SearchOption.AllDirectories))
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(f));
                if (!string.Equals(parent, language, StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add(MakeTemplate(f, TemplateKind.Adml, language + "\\" + Path.GetFileName(f)));
            }
            return DeduplicateNewest(candidates, warnings);
        }

        /// <summary>If the same target file appears more than once, the newest LastWriteTime wins.</summary>
        public static List<TemplateFile> DeduplicateNewest(IEnumerable<TemplateFile> candidates, List<string> warnings)
        {
            var result = new List<TemplateFile>();
            foreach (var g in candidates.GroupBy(t => t.RelativeTarget, StringComparer.OrdinalIgnoreCase))
            {
                var ordered = g.OrderByDescending(t => t.LastWriteUtc).ThenBy(t => t.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
                var winner = ordered[0];
                winner.DuplicatesIgnored = ordered.Count - 1;
                if (ordered.Count > 1 && warnings != null)
                {
                    var distinct = ordered.Select(t => t.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    if (distinct > 1)
                        warnings.Add($"{g.Key}: {ordered.Count} different copies in package; using newest ({winner.LastWriteUtc:yyyy-MM-dd}) from {winner.FullPath}.");
                }
                result.Add(winner);
            }
            return result.OrderBy(t => t.RelativeTarget, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static TemplateFile MakeTemplate(string path, TemplateKind kind, string relative) => new TemplateFile
        {
            Kind = kind,
            FileName = Path.GetFileName(path),
            RelativeTarget = relative,
            FullPath = path,
            LastWriteUtc = File.GetLastWriteTimeUtc(path),
            Sha256 = ComputeSha256(path),
        };

        /// <summary>Loads XML with DTD processing disabled (package content is untrusted).</summary>
        public static XmlDocument LoadXml(string path)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, settings)) doc.Load(reader);
            return doc;
        }

        public static XmlDocument LoadXmlString(string xml)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            var doc = new XmlDocument { XmlResolver = null };
            using (var sr = new StringReader(xml))
            using (var reader = XmlReader.Create(sr, settings)) doc.Load(reader);
            return doc;
        }
    }
}
