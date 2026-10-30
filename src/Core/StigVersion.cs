using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace StigGpoManager.Core
{
    /// <summary>
    /// A DISA STIG version such as "v1r4" (Version 1, Release 4). Comparable and
    /// normalized to lower-case "v{V}r{R}" for naming.
    /// </summary>
    public sealed class StigVersion : IComparable<StigVersion>, IEquatable<StigVersion>
    {
        public static readonly Regex Pattern = new Regex(@"(?<![A-Za-z0-9])v(?<v>\d+)\s*r(?<r>\d+)(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Regex fragment that matches any version token, used to find other versions of a templated GPO name.</summary>
        public const string AnyVersionRegex = @"[vV]\d+\s*[rR]\d+";

        public int Version { get; }
        public int Release { get; }

        public StigVersion(int version, int release)
        {
            if (version < 0 || release < 0) throw new ArgumentOutOfRangeException(nameof(version));
            Version = version;
            Release = release;
        }

        /// <summary>Finds the last version token in the text (e.g. "DoD Windows 11 v2r9" -> v2r9). Returns null if none.</summary>
        public static StigVersion TryParse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var matches = Pattern.Matches(text);
            if (matches.Count == 0) return null;
            var m = matches[matches.Count - 1];
            return new StigVersion(int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture));
        }

        /// <summary>Parses a version entered by the user ("v1r4", "V1R4", "1r4" are accepted). Throws on invalid input.</summary>
        public static StigVersion ParseUserInput(string text)
        {
            var t = (text ?? "").Trim();
            if (t.Length > 0 && char.IsDigit(t[0])) t = "v" + t;
            var m = Regex.Match(t, @"^v(?<v>\d+)\s*r(?<r>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!m.Success) throw new FormatException($"'{text}' is not a STIG version. Use the form v1r4.");
            return new StigVersion(int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture));
        }

        /// <summary>Tries the display name first, then the product folder name.</summary>
        public static StigVersion Detect(string displayName, string productFolder)
        {
            return TryParse(displayName) ?? TryParse(productFolder);
        }

        public int CompareTo(StigVersion other)
        {
            if (other is null) return 1;
            int c = Version.CompareTo(other.Version);
            return c != 0 ? c : Release.CompareTo(other.Release);
        }

        public bool Equals(StigVersion other) => other is object && Version == other.Version && Release == other.Release;
        public override bool Equals(object obj) => Equals(obj as StigVersion);
        public override int GetHashCode() => Version * 1000 + Release;
        public override string ToString() => $"v{Version}r{Release}";

        public static bool operator <(StigVersion a, StigVersion b) => Compare(a, b) < 0;
        public static bool operator >(StigVersion a, StigVersion b) => Compare(a, b) > 0;
        public static bool operator <=(StigVersion a, StigVersion b) => Compare(a, b) <= 0;
        public static bool operator >=(StigVersion a, StigVersion b) => Compare(a, b) >= 0;

        private static int Compare(StigVersion a, StigVersion b)
        {
            if (a is null) return b is null ? 0 : -1;
            return a.CompareTo(b);
        }
    }
}
