using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace StigGpoManager.Core
{
    public enum CentralStoreStatus { New, Updated, Unchanged, SkippedStoreNewer }

    public sealed class CentralStoreItem
    {
        public TemplateFile Source { get; set; }
        public string TargetPath { get; set; }
        public CentralStoreStatus Status { get; set; }
        public DateTime? StoreLastWriteUtc { get; set; }
        /// <summary>User ticked "overwrite anyway" for a file where the store is newer.</summary>
        public bool OverrideDowngrade { get; set; }

        public bool WillCopy =>
            Status == CentralStoreStatus.New || Status == CentralStoreStatus.Updated ||
            (Status == CentralStoreStatus.SkippedStoreNewer && OverrideDowngrade);

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case CentralStoreStatus.New: return "New";
                    case CentralStoreStatus.Updated: return "Updated";
                    case CentralStoreStatus.Unchanged: return "Unchanged";
                    default: return OverrideDowngrade ? "Store is newer - OVERRIDE (downgrade)" : "Skipped - store is newer";
                }
            }
        }
    }

    /// <summary>Compares package templates against the domain Central Store (PolicyDefinitions) and updates it.</summary>
    public static class CentralStore
    {
        public static string TargetPathFor(string storeRoot, TemplateFile t) =>
            Path.Combine(storeRoot, t.RelativeTarget.Replace('\\', Path.DirectorySeparatorChar));

        /// <summary>
        /// Status per file: New (absent in store), Unchanged (hash match), Updated (package copy is the
        /// same age or newer), SkippedStoreNewer (store copy is newer; never downgraded without an override).
        /// </summary>
        public static List<CentralStoreItem> Compare(IEnumerable<TemplateFile> templates, string storeRoot)
        {
            var items = new List<CentralStoreItem>();
            foreach (var t in templates)
            {
                var target = TargetPathFor(storeRoot, t);
                var item = new CentralStoreItem { Source = t, TargetPath = target };
                if (!File.Exists(target))
                {
                    item.Status = CentralStoreStatus.New;
                }
                else
                {
                    var storeTime = File.GetLastWriteTimeUtc(target);
                    item.StoreLastWriteUtc = storeTime;
                    item.Status = Classify(t.Sha256, t.LastWriteUtc, PackageScanner.ComputeSha256(target), storeTime);
                }
                items.Add(item);
            }
            return items;
        }

        /// <summary>Pure classification used by Compare (and unit tests). Timestamps within 2 s are equal (FAT/zip precision).</summary>
        public static CentralStoreStatus Classify(string packageHash, DateTime packageUtc, string storeHash, DateTime storeUtc)
        {
            if (string.Equals(packageHash, storeHash, StringComparison.OrdinalIgnoreCase)) return CentralStoreStatus.Unchanged;
            if (storeUtc > packageUtc.AddSeconds(2)) return CentralStoreStatus.SkippedStoreNewer;
            return CentralStoreStatus.Updated;
        }

        /// <summary>Zips the entire PolicyDefinitions folder to backupsFolder\PolicyDefinitions-&lt;timestamp&gt;.zip.</summary>
        public static string BackupStore(string storeRoot, string backupsFolder, DateTime now)
        {
            Directory.CreateDirectory(backupsFolder);
            var zip = Path.Combine(backupsFolder, $"PolicyDefinitions-{now:yyyyMMdd-HHmmss}.zip");
            ZipFile.CreateFromDirectory(storeRoot, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
            return zip;
        }

        /// <summary>Copies one template into the store, preserving its timestamp, and verifies the hash afterwards.</summary>
        public static void CopyItem(CentralStoreItem item)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath));
            File.Copy(item.Source.FullPath, item.TargetPath, overwrite: true);
            File.SetLastWriteTimeUtc(item.TargetPath, item.Source.LastWriteUtc);
            var hash = PackageScanner.ComputeSha256(item.TargetPath);
            if (!string.Equals(hash, item.Source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Hash mismatch after copying {item.Source.RelativeTarget} to the Central Store.");
        }
    }
}
