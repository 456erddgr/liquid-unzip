using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace LiquidUnzip
{
    /// <summary>验证预览树构建 + 选中子集解压。</summary>
    public static class TestSelect
    {
        static int CountNodes(IEnumerable<ArcNode> nodes)
        {
            if (nodes == null) return 0;
            int c = 0;
            foreach (var n in nodes)
            {
                c++;
                if (n.Children != null) c += CountNodes(n.Children);
            }
            return c;
        }

        static void Print(IEnumerable<ArcNode> ns, string indent)
        {
            if (ns == null) return;
            foreach (var n in ns)
            {
                Console.WriteLine(string.Format("{0}{1}  [{2}] {3}",
                    indent, n.Name, n.IsFolder ? "D" : "F", n.SizeText));
                if (n.IsFolder && n.Children != null) Print(n.Children, indent + "  ");
            }
        }

        static void CollectLeaves(IEnumerable<ArcNode> ns, List<ArcNode> leaves)
        {
            if (ns == null) return;
            foreach (var n in ns)
            {
                if (n.IsFolder) CollectLeaves(n.Children, leaves);
                else leaves.Add(n);
            }
        }

        public static int Main(string[] args)
        {
            string archive = args.Length > 0 ? args[0] : @"E:\我的项目\LiquidUnzip\testdata\sample.zip";
            string dest = @"E:\我的项目\LiquidUnzip\testdata\sel_out";
            if (Directory.Exists(dest)) Directory.Delete(dest, true);

            var sz = Extractor.LocateSevenZip();
            if (sz == null) { Console.WriteLine("no 7z"); return 2; }
            var ext = new Extractor(sz);
            var ct = new CancellationTokenSource().Token;

            var entries = ext.ListArchive(archive, ct);
            Console.WriteLine("清单: " + entries.Count + " 项");

            var tree = ArcNode.BuildTree(entries);
            Console.WriteLine("树节点数: " + CountNodes(tree));
            Print(tree, "");
            Console.WriteLine();

            var leaves = new List<ArcNode>();
            CollectLeaves(tree, leaves);
            if (leaves.Count < 2) { Console.WriteLine("叶子不足"); return 2; }

            var pick = leaves.Take(2).Select(x => x.FullPath).ToList();
            Console.WriteLine("选中: " + string.Join(" | ", pick));

            foreach (var n in leaves) n.IsChecked = pick.Contains(n.FullPath);
            var confirmed = new List<string>();
            ArcNode.CollectChecked(tree, confirmed);
            Console.WriteLine("CollectChecked: " + confirmed.Count + " 项");
            foreach (var c in confirmed) Console.WriteLine("   " + c);

            string work = Path.Combine(Path.GetTempPath(), "lu_sel_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var jobs = ext.PlanSelectedJobs(entries, pick, work, 2, 1, ct);
            Console.WriteLine("\n任务数: " + jobs.Count);
            foreach (var j in jobs)
                Console.WriteLine("   " + j.Files + " 文件 " + j.Bytes + " B  " + j.Name);

            ext.RunJobs(jobs, archive, dest, 2, p => { }, s => { }, ct);

            var produced = new List<string>();
            foreach (var f in Directory.GetFiles(dest, "*", SearchOption.AllDirectories))
                produced.Add(f.Substring(dest.Length).TrimStart('\\'));

            Console.WriteLine("\n产出文件: " + produced.Count);
            foreach (var p in produced) Console.WriteLine("   " + p);

            bool exact = produced.Count == pick.Count;
            Console.WriteLine(exact ? "RESULT PASS (只解出选中的)" : "RESULT FAIL (数量不符)");
            try { Directory.Delete(work, true); } catch { }
            return exact ? 0 : 1;
        }
    }
}
