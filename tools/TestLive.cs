using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LiquidUnzip
{
    /// <summary>验证实时输出回调能否真正抓到 7z 的进度行。</summary>
    public static class TestLive
    {
        public static int Main(string[] args)
        {
            string archive = args.Length > 0 ? args[0] : @"E:\我的项目\LiquidUnzip\testdata\sample.zip";
            string dest = @"E:\我的项目\LiquidUnzip\testdata\live_out";
            if (Directory.Exists(dest)) Directory.Delete(dest, true);

            var sz = Extractor.LocateSevenZip();
            if (sz == null) { Console.WriteLine("no 7z"); return 2; }
            var ext = new Extractor(sz);
            var ct = new CancellationTokenSource().Token;

            var entries = ext.ListArchive(archive, ct);
            string work = Path.Combine(Path.GetTempPath(), "lu_live_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var jobs = ext.PlanJobs(entries, work, 2, 1, ct);

            int liveCount = 0;
            Console.WriteLine("== 实时回调输出 ==");
            ext.RunJobs(jobs, archive, dest, 2,
                p => { },
                s =>
                {
                    liveCount++;
                    Console.WriteLine("  [LIVE] " + s);
                },
                ct);

            Console.WriteLine("\n实时行数: " + liveCount);
            try { Directory.Delete(work, true); } catch { }
            Console.WriteLine(liveCount > 0 ? "RESULT PASS (有实时输出)" : "RESULT FAIL (无实时输出)");
            return liveCount > 0 ? 0 : 1;
        }
    }
}
