using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiquidUnzip
{
    /// <summary>
    /// 解压引擎：7z 清单分析 + 大文件独立 / 小文件分段的多进程并行解压。
    ///
    /// 设计依据（实测数据，见 docs/BENCHMARK.md）：
    ///   - RAR5 的 m3 高压缩在 7-Zip 中只能单线程解码，一个 7z 进程吃满 1 核；
    ///   - 单进程约 70 MB/s，16 路并行可到约 190 MB/s，CPU 仅 44%；
    ///   - 因此加速的关键是把文件拆成多个任务分发给多个 7z 进程，
    ///     而不是调 7z 的线程参数。
    /// </summary>
    public class Extractor
    {
        public string SevenZipPath { get; private set; }

        public Extractor(string sevenZipPath)
        {
            SevenZipPath = sevenZipPath;
        }

        // ---------------------------------------------------------------
        // 进度回报
        // ---------------------------------------------------------------

        public class Progress
        {
            public int TasksDone;
            public int TasksTotal;
            public long BytesDone;
            public long BytesTotal;
            public string CurrentFile = "";
            public double MBps;
            public int ElapsedSec;

            public double Percent
            {
                get { return BytesTotal > 0 ? Math.Min(1.0, (double)BytesDone / BytesTotal) : 0; }
            }
        }

        // ---------------------------------------------------------------
        // 清单
        // ---------------------------------------------------------------

        public class Entry
        {
            public string Path;
            public long Size;
        }

        /// <summary>读取压缩包清单（-slt 结构化输出）。</summary>
        public List<Entry> ListArchive(string archive, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(SevenZipPath)
            {
                Arguments = "l -slt -scsUTF-8 \"" + archive + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            var list = new List<Entry>();
            Entry cur = null;

            using (var p = Process.Start(psi))
            {
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (line.Length == 0)
                    {
                        if (cur != null && !string.IsNullOrEmpty(cur.Path)) list.Add(cur);
                        cur = null;
                        continue;
                    }
                    int eq = line.IndexOf(" = ", StringComparison.Ordinal);
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq);
                    string v = line.Substring(eq + 3);

                    if (cur == null) cur = new Entry();
                    if (k == "Path") cur.Path = v;
                    else if (k == "Size") { long s; long.TryParse(v, out s); cur.Size = s; }
                    else if (k == "Folder" && v == "+") cur.Path = null; // 目录项跳过
                }
                if (cur != null && !string.IsNullOrEmpty(cur.Path)) list.Add(cur);
                p.WaitForExit();
            }
            return list;
        }

        // ---------------------------------------------------------------
        // 任务规划
        // ---------------------------------------------------------------

        public class Job
        {
            public string ListFile;
            public long Bytes;
            public int Files;
            public string Name;
        }

        /// <summary>
        /// 规划并生成任务清单：大文件各自一个任务，小文件按归档顺序连续切分。
        /// 连续切分很重要——能让每个 7z 进程顺序读取压缩包，避免 USB/机械盘寻道。
        /// </summary>
        public List<Job> PlanJobs(List<Entry> entries, string workDir, int smallSegments,
                                  long bigThreshold, CancellationToken ct)
        {
            Directory.CreateDirectory(workDir);
            var jobs = new List<Job>();
            var big = new List<Entry>();
            var small = new List<Entry>();
            long smallTotal = 0;

            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (e.Path.EndsWith("\\", StringComparison.Ordinal)) continue;
                if (e.Size >= bigThreshold) big.Add(e);
                else { small.Add(e); smallTotal += e.Size; }
            }

            int bi = 0;
            foreach (var e in big)
            {
                string f = System.IO.Path.Combine(workDir, "j_big" + (bi++).ToString("D3") + ".txt");
                File.WriteAllText(f, e.Path + "\n", new UTF8Encoding(false));
                jobs.Add(new Job { ListFile = f, Bytes = e.Size, Files = 1, Name = System.IO.Path.GetFileName(e.Path) });
            }

            if (small.Count > 0)
            {
                int ns = Math.Max(1, Math.Min(smallSegments, small.Count));
                long target = smallTotal / ns;
                var cur = new List<string>();
                long curSz = 0;
                int seg = 0;

                Action flush = () =>
                {
                    if (cur.Count == 0) return;
                    string f = System.IO.Path.Combine(workDir, "j_small" + (seg).ToString("D3") + ".txt");
                    File.WriteAllText(f, string.Join("\n", cur) + "\n", new UTF8Encoding(false));
                    jobs.Add(new Job { ListFile = f, Bytes = curSz, Files = cur.Count, Name = "小文件组 " + seg });
                    seg++;
                    cur.Clear(); curSz = 0;
                };

                foreach (var e in small)
                {
                    // 按归档顺序连续填充：当前段超过目标且还有余量就切段
                    if (seg < ns - 1 && curSz + e.Size > target && cur.Count > 0)
                    {
                        flush();
                    }
                    cur.Add(e.Path);
                    curSz += e.Size;
                }
                flush();
            }

            return jobs;
        }

        // ---------------------------------------------------------------
        // 并行执行
        // ---------------------------------------------------------------

        public void RunJobs(List<Job> jobs, string archive, string destRoot, int concurrency,
                            Action<Progress> onProgress, CancellationToken ct)
        {
            RunJobs(jobs, archive, destRoot, concurrency, onProgress, null, ct);
        }

        public void RunJobs(List<Job> jobs, string archive, string destRoot, int concurrency,
                            Action<Progress> onProgress, Action<string> onLive, CancellationToken ct)
        {
            var pending = new Queue<Job>(jobs);
            var pad = new object();

            long totalBytes = 0;
            foreach (var j in jobs) totalBytes += j.Bytes;

            var prog = new Progress { TasksTotal = jobs.Count, BytesTotal = totalBytes };
            var sw = Stopwatch.StartNew();

            if (jobs.Count == 0)
            {
                onProgress(Snapshot(prog));
                return;
            }

            object qLock = new object();
            Exception firstError = null;

            using (var done = new CountdownEvent(jobs.Count))
            {
                for (int i = 0; i < concurrency; i++)
                {
                    var t = new Thread(() =>
                    {
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            Job job;
                            lock (qLock)
                            {
                                if (pending.Count == 0) break;
                                job = pending.Dequeue();
                            }

                            try
                            {
                                lock (pad) prog.CurrentFile = job.Name;
                                RunOne(job, archive, destRoot, ct, onLive);
                                lock (pad)
                                {
                                    prog.TasksDone++;
                                    prog.BytesDone += job.Bytes;
                                    prog.ElapsedSec = (int)sw.Elapsed.TotalSeconds;
                                    prog.MBps = prog.ElapsedSec > 0
                                        ? prog.BytesDone / 1048576.0 / prog.ElapsedSec : 0;
                                }
                                onProgress(Snapshot(prog));
                            }
                            catch (Exception ex)
                            {
                                if (firstError == null) firstError = ex;
                            }
                            finally
                            {
                                done.Signal();
                            }
                        }
                    });
                    t.IsBackground = true;
                    t.Start();
                }

                // 进度心跳
                while (!done.Wait(400))
                {
                    prog.ElapsedSec = (int)sw.Elapsed.TotalSeconds;
                    prog.MBps = prog.ElapsedSec > 0 ? prog.BytesDone / 1048576.0 / prog.ElapsedSec : 0;
                    onProgress(Snapshot(prog));
                    if (ct.IsCancellationRequested) break;
                }
            }

            prog.ElapsedSec = (int)sw.Elapsed.TotalSeconds;
            prog.BytesDone = totalBytes;
            prog.MBps = prog.ElapsedSec > 0 ? totalBytes / 1048576.0 / prog.ElapsedSec : 0;
            onProgress(Snapshot(prog));

            if (firstError != null) throw firstError;
        }

        static Progress Snapshot(Progress p)
        {
            return new Progress
            {
                TasksDone = p.TasksDone, TasksTotal = p.TasksTotal,
                BytesDone = p.BytesDone, BytesTotal = p.BytesTotal,
                CurrentFile = p.CurrentFile, MBps = p.MBps, ElapsedSec = p.ElapsedSec
            };
        }

        void RunOne(Job job, string archive, string destRoot, CancellationToken ct, Action<string> onLive)
        {
            var psi = new ProcessStartInfo(SevenZipPath)
            {
                // -bsp1 让 7z 把"当前百分比 + 文件名"实时打到 stdout
                Arguments = "x \"" + archive + "\" @" + QuoteIfNeeded(job.ListFile) +
                            " -o\"" + destRoot + "\" -aoa -y -bso1 -bsp1 -bse1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var p = Process.Start(psi))
            {
                var errSb = new StringBuilder();

                // 7z 用 \r 刷新同一行，需要按 \r 和 \n 一起切
                var reader = p.StandardOutput;
                var buf = new char[4096];
                var line = new StringBuilder();
                int n;
                while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        char c = buf[i];
                        if (c == '\r' || c == '\n')
                        {
                            if (line.Length > 0)
                            {
                                string s = line.ToString();
                                line.Length = 0;
                                EmitLive(s, onLive);
                            }
                        }
                        else line.Append(c);
                    }
                    if (ct.IsCancellationRequested)
                    {
                        try { p.Kill(); } catch { }
                        break;
                    }
                }
                if (line.Length > 0) EmitLive(line.ToString(), onLive);

                var errTask = p.StandardError.ReadToEndAsync();
                while (!p.WaitForExit(200))
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { p.Kill(); } catch { }
                        ct.ThrowIfCancellationRequested();
                    }
                }
                ct.ThrowIfCancellationRequested();

                // 7z: 0=OK, 1=警告(非致命), 其他=错误
                if (p.ExitCode > 1)
                {
                    throw new Exception("7-Zip 退出码 " + p.ExitCode + "：" +
                                        (errTask.Result ?? "").Trim());
                }
            }
        }

        /// <summary>只保留有信息量的行：百分比行 / 文件名 / 错误。</summary>
        static void EmitLive(string raw, Action<string> onLive)
        {
            if (onLive == null) return;
            string s = raw.Trim();
            if (s.Length == 0) return;

            // 只要带 % 的进度行，或错误行
            bool hasPct = s.IndexOf('%') >= 0;
            bool isErr = s.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
                      || s.StartsWith("Can not", StringComparison.OrdinalIgnoreCase)
                      || s.StartsWith("Cannot", StringComparison.OrdinalIgnoreCase)
                      || s.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase);

            if (!hasPct && !isErr) return;

            // 过滤 "Extracting archive: ..." 这类噪音（含 % 但不是进度）
            if (s.StartsWith("Extracting", StringComparison.OrdinalIgnoreCase)) return;
            if (s.StartsWith("Scanning", StringComparison.OrdinalIgnoreCase)) return;
            if (s.StartsWith("7-Zip", StringComparison.OrdinalIgnoreCase)) return;

            onLive(s);
        }

        static string QuoteIfNeeded(string s)
        {
            return s.IndexOf(' ') >= 0 ? "\"" + s + "\"" : s;
        }

        // ---------------------------------------------------------------
        // 辅助
        // ---------------------------------------------------------------

        /// <summary>
        /// 只解压指定的文件（用于"预览后单独选择"）。
        /// 会先按大小与顺序规划成多个任务，再并行执行。
        /// </summary>
        public List<Job> PlanSelectedJobs(List<Entry> allEntries, IList<string> selectedPaths,
                                          string workDir, int segments, long bigThreshold,
                                          CancellationToken ct)
        {
            var wanted = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
            // 保持归档顺序：过滤而不是重排
            var subset = new List<Entry>();
            foreach (var e in allEntries)
                if (wanted.Contains(e.Path.TrimEnd('\\'))) subset.Add(e);

            if (subset.Count == 0) return new List<Job>();
            return PlanJobs(subset, workDir, segments, bigThreshold, ct);
        }

        /// <summary>
        /// 规范压缩包内路径：7-Zip 在 -slt 里用反斜杠，且目录项以反斜杠结尾。
        /// </summary>
        public static string NormalizeArcPath(string p)
        {
            if (p == null) return "";
            return p.Replace('/', '\\').TrimEnd('\\');
        }
        public static bool IsArchive(string path)
        {
            string[] ext = { ".rar", ".zip", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz",
                             ".cab", ".iso", ".lzh", ".zst", ".tar.gz", ".tar.bz2" };
            string lower = path.ToLowerInvariant();
            foreach (var e in ext)
                if (lower.EndsWith(e, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>找到随程序附带的或系统安装的 7z.exe。</summary>
        public static string LocateSevenZip()
        {
            var cand = new List<string>();
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            cand.Add(Path.Combine(exeDir, "7z.exe"));
            cand.Add(Path.Combine(exeDir, "tools", "7z.exe"));
            cand.Add(@"C:\Program Files\7-Zip\7z.exe");
            cand.Add(@"C:\Program Files (x86)\7-Zip\7z.exe");

            foreach (var c in cand)
                if (File.Exists(c)) return c;
            return null;
        }
    }
}
