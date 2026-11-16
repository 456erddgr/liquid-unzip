using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace LiquidUnzip
{
    /// <summary>归档里的一项，供预览树使用。</summary>
    public class ArcNode : INotifyPropertyChanged
    {
        public string FullPath { get; set; }     // 归档内完整路径
        public string Name { get; set; }         // 显示名
        public long Size { get; set; }
        public bool IsFolder { get; set; }

        public ObservableCollection<ArcNode> Children { get; set; }
        public bool HasChildren { get { return Children != null && Children.Count > 0; } }

        bool _checked;
        public bool IsChecked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                OnPropertyChanged("IsChecked");
                // 级联到子项
                if (Children != null)
                    foreach (var c in Children) c.IsChecked = value;
            }
        }

        bool _expanded;
        public bool IsExpanded
        {
            get { return _expanded; }
            set { if (_expanded != value) { _expanded = value; OnPropertyChanged("IsExpanded"); } }
        }

        public string SizeText
        {
            get
            {
                if (IsFolder) return CountText;
                return MainWindow.FormatSize(Size);
            }
        }

        /// <summary>文件类型分类，用于选图标/配色。</summary>
        public FileKind Kind
        {
            get
            {
                if (IsFolder) return FileKind.Folder;
                return Classify(Name);
            }
        }

        public enum FileKind
        {
            Folder, Model, Image, Video, Audio, Archive, Code, Config, Text, Other
        }

        static FileKind Classify(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            int dot = n.LastIndexOf('.');
            string ext = dot >= 0 ? n.Substring(dot) : "";

            switch (ext)
            {
                case ".safetensors":
                case ".ckpt":
                case ".pt":
                case ".pth":
                case ".bin":
                case ".onnx":
                case ".gguf":
                case ".npz":
                    return FileKind.Model;

                case ".png": case ".jpg": case ".jpeg": case ".webp":
                case ".bmp": case ".gif": case ".tif": case ".tiff":
                case ".svg": case ".ico":
                    return FileKind.Image;

                case ".mp4": case ".mkv": case ".mov": case ".avi":
                case ".webm": case ".flv": case ".wmv":
                    return FileKind.Video;

                case ".wav": case ".mp3": case ".flac": case ".ogg":
                case ".m4a": case ".aac": case ".opus":
                    return FileKind.Audio;

                case ".zip": case ".rar": case ".7z": case ".tar":
                case ".gz": case ".bz2": case ".xz":
                    return FileKind.Archive;

                case ".py": case ".js": case ".ts": case ".cs": case ".cpp":
                case ".c": case ".h": case ".java": case ".go": case ".rs":
                case ".sh": case ".bat": case ".ps1": case ".lua": case ".rb":
                    return FileKind.Code;

                case ".json": case ".yaml": case ".yml": case ".toml":
                case ".ini": case ".cfg": case ".xml": case ".env":
                    return FileKind.Config;

                case ".txt": case ".md": case ".log": case ".csv":
                case ".rst": case ".pdf":
                    return FileKind.Text;

                default:
                    return FileKind.Other;
            }
        }

        /// <summary>图标字符（用几何符号，避免依赖彩色 emoji 字体）。</summary>
        public string Glyph
        {
            get
            {
                switch (Kind)
                {
                    case FileKind.Folder: return "\u25A0";   // ■ 目录
                    case FileKind.Model: return "\u25C6";    // ◆ 模型
                    case FileKind.Image: return "\u25CF";    // ● 图片
                    case FileKind.Video: return "\u25B6";    // ▶ 视频
                    case FileKind.Audio: return "\u266B";    // ♫ 音频
                    case FileKind.Archive: return "\u25A3";  // ▣ 压缩包
                    case FileKind.Code: return "\u2318";     // ⌘ 代码
                    case FileKind.Config: return "\u2699";   // ⚙ 配置
                    case FileKind.Text: return "\u2261";     // ≡ 文本
                    default: return "\u25CB";                // ○ 其它
                }
            }
        }

        /// <summary>图标配色。</summary>
        public string GlyphColor
        {
            get
            {
                switch (Kind)
                {
                    case FileKind.Folder: return "#8FB4FF";
                    case FileKind.Model: return "#64D2FF";
                    case FileKind.Image: return "#32D74B";
                    case FileKind.Video: return "#FF9F0A";
                    case FileKind.Audio: return "#BF5AF2";
                    case FileKind.Archive: return "#FFD60A";
                    case FileKind.Code: return "#5E5CE6";
                    case FileKind.Config: return "#98989D";
                    case FileKind.Text: return "#AEAEB6";
                    default: return "#6E6E78";
                }
            }
        }

        public string CountText
        {
            get
            {
                if (!IsFolder) return "";
                int n = CountFiles(this);
                return n + " 个文件";
            }
        }

        static int CountFiles(ArcNode n)
        {
            if (n.Children == null) return 0;
            int t = 0;
            foreach (var c in n.Children) t += c.IsFolder ? CountFiles(c) : 1;
            return t;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void OnPropertyChanged(string p)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(p));
        }

        /// <summary>把扁平清单构建成树。</summary>
        public static ObservableCollection<ArcNode> BuildTree(IEnumerable<Extractor.Entry> entries)
        {
            var root = new ObservableCollection<ArcNode>();
            var dirs = new Dictionary<string, ArcNode>(StringComparer.OrdinalIgnoreCase);

            Func<ArcNode> ensureRoot = () => new ArcNode
            {
                FullPath = "",
                Name = "",
                IsFolder = true,
                Children = new ObservableCollection<ArcNode>()
            };

            // 先按路径深度排序，保证父目录先建
            var list = entries.Where(e => e.Size >= 0)
                              .Where(e => !IsSelfReference(e.Path))
                              .OrderBy(e => e.Path.Count(c => c == '\\'))
                              .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                              .ToList();

            var top = new ArcNode { Children = root };

            foreach (var e in list)
            {
                string path = e.Path.TrimEnd('\\');
                if (path.Length == 0) continue;

                bool isFolder = e.Path.EndsWith("\\", StringComparison.Ordinal) || e.Size == 0 && !e.Path.Contains(".");
                // 7z 的目录项在 -slt 里 Folder = +，我们的 Entry 里已被过滤掉标记，
                // 这里用"路径以反斜杠结尾"判断（Extractor.ListArchive 保留了原始 Path）

                var parts = path.Split('\\');
                ArcNode parent = top;

                for (int i = 0; i < parts.Length; i++)
                {
                    string key = string.Join("\\", parts.Take(i + 1));
                    bool last = i == parts.Length - 1;

                    ArcNode node;
                    if (!dirs.TryGetValue(key, out node))
                    {
                        node = new ArcNode
                        {
                            FullPath = key,
                            Name = parts[i],
                            IsFolder = !last || isFolder,
                            Size = 0
                        };
                        if (node.IsFolder) node.Children = new ObservableCollection<ArcNode>();

                        dirs[key] = node;
                        if (parent.Children == null) parent.Children = new ObservableCollection<ArcNode>();
                        parent.Children.Add(node);
                    }

                    if (last && !node.IsFolder)
                    {
                        node.Size = e.Size;
                    }
                    parent = node;
                }
            }

            // 排序：目录在前，然后按名字
            SortRecursive(top.Children);
            return root;
        }

        /// <summary>
        /// zip 归档会记录一条"压缩包自身路径"的 0 字节条目，不是真实内容，预览里应剔除。
        /// </summary>
        static bool IsSelfReference(string path)
        {
            if (path == null) return false;
            string p = path.ToLowerInvariant();
            return p.EndsWith(".zip") || p.EndsWith(".rar") || p.EndsWith(".7z")
                || p.EndsWith(".tar") || p.EndsWith(".gz");
        }

        static void SortRecursive(ObservableCollection<ArcNode> nodes)
        {
            if (nodes == null) return;
            var sorted = nodes.OrderByDescending(n => n.IsFolder)
                              .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                              .ToList();
            nodes.Clear();
            foreach (var n in sorted)
            {
                if (n.Children != null) SortRecursive(n.Children);
                nodes.Add(n);
            }
        }

        /// <summary>收集所有勾选的叶子路径。</summary>
        public static void CollectChecked(IEnumerable<ArcNode> nodes, List<string> outPaths)
        {
            foreach (var n in nodes)
            {
                if (n.IsFolder)
                {
                    if (n.Children != null) CollectChecked(n.Children, outPaths);
                }
                else if (n.IsChecked)
                {
                    outPaths.Add(n.FullPath);
                }
            }
        }

        /// <summary>把树的第一层默认展开，便于一眼看到内容。</summary>
        public static void ExpandTop(IEnumerable<ArcNode> nodes, int depth)
        {
            if (depth <= 0) return;
            foreach (var n in nodes)
            {
                if (n.IsFolder)
                {
                    n.IsExpanded = true;
                    if (n.Children != null) ExpandTop(n.Children, depth - 1);
                }
            }
        }
        /// <summary>把 "#RRGGBB" 转成 Brush，供 XAML 绑定用。</summary>

    }

    public class HexToBrushConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter,
                              System.Globalization.CultureInfo culture)
        {
            string hex = value as string;
            if (string.IsNullOrEmpty(hex)) return System.Windows.Media.Brushes.Gray;
            try
            {
                var c = (System.Windows.Media.Color)
                    System.Windows.Media.ColorConverter.ConvertFromString(hex);
                var b = new System.Windows.Media.SolidColorBrush(c);
                b.Freeze();
                return b;
            }
            catch { return System.Windows.Media.Brushes.Gray; }
        }

        public object ConvertBack(object value, Type targetType, object parameter,
                                  System.Globalization.CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
