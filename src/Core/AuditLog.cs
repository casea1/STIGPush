using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace StigGpoManager.Core
{
    /// <summary>
    /// Append-only audit log: one JSON object per line. The file is only ever opened with
    /// FileMode.Append; it is never rewritten or truncated.
    /// </summary>
    public sealed class AuditLog
    {
        private static readonly object Gate = new object();
        public string Path { get; }
        public string User { get; }
        public string Machine { get; }

        public AuditLog(string path, string user, string machine = null)
        {
            Path = path;
            User = user;
            Machine = machine ?? Environment.MachineName;
        }

        public void Write(string runId, string action, string target, string result, string error = null, string detail = null)
        {
            var entry = Json.NewObject();
            entry["timestamp"] = DateTime.UtcNow.ToString("o");
            entry["user"] = User;
            entry["machine"] = Machine;
            entry["runId"] = runId;
            entry["action"] = action;
            entry["target"] = target;
            entry["result"] = result;
            entry["error"] = error;
            if (detail != null) entry["detail"] = detail;
            var line = Json.Serialize(entry, indent: false) + "\n";
            var bytes = new UTF8Encoding(false).GetBytes(line);

            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using (var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read))
                        {
                            fs.Write(bytes, 0, bytes.Length);
                            fs.Flush(true);
                        }
                        return;
                    }
                    catch (IOException) when (attempt < 20)
                    {
                        Thread.Sleep(100);
                    }
                }
            }
        }

        /// <summary>Reads the log (for display/report); malformed lines are returned as raw text entries.</summary>
        public List<Dictionary<string, object>> ReadAll(string runId = null)
        {
            var list = new List<Dictionary<string, object>>();
            if (!File.Exists(Path)) return list;
            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    Dictionary<string, object> o;
                    try { o = Json.AsObject(Json.Parse(line)); }
                    catch (FormatException) { o = Json.NewObject(); o["raw"] = line; }
                    if (o == null) continue;
                    if (runId == null || string.Equals(Json.GetString(o, "runId"), runId, StringComparison.OrdinalIgnoreCase)) list.Add(o);
                }
            }
            return list;
        }
    }
}
