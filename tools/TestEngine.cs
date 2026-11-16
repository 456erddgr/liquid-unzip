using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LiquidUnzip
{
    /// <summary>端到端功能测试：清单解析 → 任务规划 → 并行解压 → 校验。</summary>
    public static class TestEngine
    {
        public static int Main(string[] args)
        {
            string archive = args.Length > 0 ? args[0] : @"E:\我的项目\LiquidUnzip\testdata\sample.zip";
            string dest = args.Length > 1 ? args[1] : @"E:\我的项目\LiquidUnzip\testdata\out";

            var sz = Extractor.LocateSevenZip();
            Console.WriteLine("7z: " + sz);
            if (sz == null) { Console.WriteLine("FAIL 找不到 7z"); return 2; }

            if (Directory.Exists(dest)) Directory.Delete(dest, true);

            var ext = new Extractor(sz);
            var ct = new CancellationTokenSource().Token;

            var entries = ext.ListArchive(archive, ct);
            long total = 0; int nonZero = 0;
            foreach (var e in entries) { total += e.Size; if (e.Size > 0) nonZero++; }
            Console.WriteLine("清单: " + entries.Count + " 项, 非空 " + nonZero + ", 共 " + total + " 字节");

            string work = Path.Combine(Path.GetTempPath(), "lu_test_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            // 用极小的阈值强制走"大文件独立"分支，确保两条路径都被测到
            var jobs = ext.PlanJobs(entries, work, 3, 1, ct);
            Console.WriteLine("任务: " + jobs.Count + " 个");
            foreach (var j in jobs)
                Console.WriteLine("  " + Path.GetFileName(j.ListFile) + "  " + j.Files + " 文件  " + j.Bytes + " B  " + j.Name);

            ext.RunJobs(jobs, archive, dest, 4, p =>
            {
                if (p.TasksDone == p.TasksTotal && p.TasksTotal > 0)
                    Console.WriteLine(string.Format("进度: {0}/{1} 任务  {2}%  {3:0.0} MB/s",
                        p.TasksDone, p.TasksTotal, p.Percent * 100, p.MBps));
            }, ct);

            // 校验
            int ok = 0, bad = 0, skipped = 0;
            foreach (var e in entries)
            {
                // 跳过压缩包自身的路径条目（zip 归档会记录自身名字，不是真实文件）
                if (e.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    e.Path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ||
                    e.Path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                string fp = Path.Combine(dest, e.Path.Replace('\\', Path.DirectorySeparatorChar));
                if (!File.Exists(fp))
                {
                    if (Directory.Exists(fp)) { ok++; continue; }
                    Console.WriteLine("  缺失: " + e.Path); bad++; continue;
                }
                if (new FileInfo(fp).Length == e.Size) ok++; else { Console.WriteLine("  大小不符: " + e.Path); bad++; }
            }
            Console.WriteLine("校验: OK " + ok + " / BAD " + bad + " / 跳过 " + skipped);

            try { Directory.Delete(work, true); } catch { }

            Console.WriteLine(bad == 0 ? "RESULT PASS" : "RESULT FAIL");
            return bad == 0 ? 0 : 1;
        }
    }
}
