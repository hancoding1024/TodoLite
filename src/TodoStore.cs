using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TodoLite
{
    /// <summary>
    /// 任务项契约。只有 id 和文本，刻意不含任何时间/日期类字段。
    /// </summary>
    public interface ITaskItem
    {
        string Id { get; set; }
        string Text { get; set; }
    }

    /// <summary>TODO 任务：{ "id": "...", "text": "...", "done": false, "star": false }</summary>
    public class TodoItem : ITaskItem
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public bool Done { get; set; }
        public bool Star { get; set; }

        public TodoItem()
        {
            this.Id = Guid.NewGuid().ToString();
            this.Text = string.Empty;
            this.Done = false;
            this.Star = false;
        }

        public TodoItem(string text)
            : this()
        {
            this.Text = text;
        }

        public static TodoItem Create(string id, string text, bool done)
        {
            return Create(id, text, done, false);
        }

        public static TodoItem Create(string id, string text, bool done, bool star)
        {
            TodoItem item = new TodoItem();
            if (id != null && id.Length > 0) item.Id = id;
            item.Text = text == null ? string.Empty : text;
            item.Done = done;
            item.Star = star;
            return item;
        }
    }

    /// <summary>一栏独立列表：自带标题与条目。</summary>
    public class TodoPane
    {
        public string Title { get; set; }
        public List<TodoItem> Items { get; set; }

        public TodoPane()
        {
            this.Title = string.Empty;
            this.Items = new List<TodoItem>();
        }

        public TodoPane(string title)
            : this()
        {
            this.Title = title == null ? string.Empty : title;
        }
    }

    /// <summary>
    /// 数据文件的读取与写入。
    /// 文件的绝对路径优先取 exe 同目录的 todo-data.json，不可写时回退到 %APPDATA%\TodoLite\。
    /// 结构为 v3：draft + 一栏或多栏（每栏有标题与条目），栏数可按需拆分/合并。
    /// </summary>
    public class TodoStore
    {
        public const int CurrentVersion = 3;
        public const int MaxPanes = 4;
        public const int MaxTitleLength = 40;
        private const string FileName = "todo-data.json";

        private readonly string _dataFilePath;
        private readonly List<TodoPane> _panes = new List<TodoPane>();

        /// <summary>收件箱里还没提交的草稿文本。</summary>
        public string Draft { get; private set; }

        /// <summary>加载时发现文件损坏并已备份。</summary>
        public bool CorruptOnLoad { get; private set; }

        /// <summary>加载时把旧版单栏/收件箱数据迁移成了新结构，需要落盘一次。</summary>
        public bool MigratedOnLoad { get; private set; }

        public string DataFilePath
        {
            get { return _dataFilePath; }
        }

        /// <summary>当前栏数（1 ~ MaxPanes），由用户按需拆分/合并。</summary>
        public int PaneCount
        {
            get { return _panes.Count; }
        }

        public bool CanAddPane
        {
            get { return _panes.Count < MaxPanes; }
        }

        public TodoStore()
        {
            this.Draft = string.Empty;
            _panes.Add(new TodoPane(DefaultTitle(0)));
            _dataFilePath = ResolveDataFilePath();
        }

        public static string DefaultTitle(int pane)
        {
            if (pane <= 0) return "TODO";
            if (pane == 1) return "\u6e05\u5355";
            return "\u6e05\u5355 " + pane;
        }

        public void SetDraft(string value)
        {
            this.Draft = value == null ? string.Empty : value;
        }

        /// <summary>返回指定栏；索引非法时返回 null。</summary>
        public TodoPane PaneAt(int pane)
        {
            if (pane < 0 || pane >= _panes.Count) return null;
            return _panes[pane];
        }

        public int IndexOf(int pane, TodoItem item)
        {
            TodoPane p = PaneAt(pane);
            if (p == null || item == null) return -1;
            return p.Items.IndexOf(item);
        }

        // ------------------------------------------------------------------
        // 路径解析
        // ------------------------------------------------------------------
        private static string ResolveDataFilePath()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (DirectoryWritable(exeDir))
                return Path.Combine(exeDir, FileName);

            string appData = string.Empty;
            try
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            catch (Exception)
            {
                appData = string.Empty;
            }

            if (appData.Length == 0)
                return Path.Combine(exeDir, FileName);

            string dir = Path.Combine(appData, "TodoLite");
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception)
            {
                // 创建失败时仍返回该路径，保存阶段会再次报错，由界面提示一次。
            }
            return Path.Combine(dir, FileName);
        }

        private static bool DirectoryWritable(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            string probe = Path.Combine(dir, ".todolite.write-probe");
            try
            {
                using (FileStream fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.WriteByte(0);
                }
                File.Delete(probe);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 加载
        // ------------------------------------------------------------------
        public void Load()
        {
            this.Draft = string.Empty;
            this.CorruptOnLoad = false;
            this.MigratedOnLoad = false;
            ResetPanes();

            if (!File.Exists(_dataFilePath)) return;

            try
            {
                string json = File.ReadAllText(_dataFilePath, Encoding.UTF8);
                if (json == null || json.Trim().Length == 0) return;

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 32 * 1024 * 1024;
                TodoData data = serializer.Deserialize<TodoData>(json);
                if (data == null)
                {
                    HandleCorrupt();
                    return;
                }

                bool hasPanes = data.Panes != null && data.Panes.Count > 0;
                if (hasPanes)
                {
                    _panes.Clear();

                    for (int i = 0; i < data.Panes.Count; i++)
                    {
                        TodoPane source = data.Panes[i];
                        if (source == null) continue;

                        if (_panes.Count >= MaxPanes)
                        {
                            // 超出上限：并入最后一栏，绝不丢内容。
                            AppendItems(_panes[_panes.Count - 1], source);
                            this.MigratedOnLoad = true;
                            continue;
                        }

                        TodoPane pane = new TodoPane(SanitizeTitle(source.Title, DefaultTitle(_panes.Count)));
                        AppendItems(pane, source);
                        _panes.Add(pane);
                    }

                    if (_panes.Count == 0) _panes.Add(new TodoPane(DefaultTitle(0)));
                }
                else if (data.Todo != null)
                {
                    // v2 单栏：按原顺序整体搬进第 1 栏。
                    for (int i = 0; i < data.Todo.Count; i++)
                    {
                        TodoItem item = SanitizeItem(data.Todo[i]);
                        if (item != null) _panes[0].Items.Add(item);
                    }
                    this.MigratedOnLoad = true;
                }

                if (data.Draft != null) this.Draft = data.Draft;

                // 旧版数据里的收件箱条目：逐条转成未完成的 TODO，追加到第一栏末尾，不丢内容。
                if (data.Inbox != null && data.Inbox.Count > 0)
                {
                    for (int i = 0; i < data.Inbox.Count; i++)
                    {
                        TodoItem legacy = data.Inbox[i];
                        if (legacy == null) continue;
                        string text = legacy.Text == null ? string.Empty : legacy.Text;
                        if (text.Trim().Length == 0) continue;
                        _panes[0].Items.Add(TodoItem.Create(legacy.Id, text, false));
                    }
                    this.MigratedOnLoad = true;
                }

                NormalizeAll();

                // 末尾空着且仍叫默认名的栏不保留，避免「拆分」后的空栏一直常驻。
                while (_panes.Count > 1)
                {
                    int last = _panes.Count - 1;
                    TodoPane trailing = _panes[last];
                    if (trailing.Items.Count > 0) break;
                    if (trailing.Title != DefaultTitle(last)) break;
                    _panes.RemoveAt(last);
                    this.MigratedOnLoad = true;
                }
            }
            catch (Exception)
            {
                HandleCorrupt();
            }
        }

        private void ResetPanes()
        {
            _panes.Clear();
            _panes.Add(new TodoPane(DefaultTitle(0)));
        }

        private static void AppendItems(TodoPane target, TodoPane source)
        {
            if (target == null || source == null || source.Items == null) return;
            for (int i = 0; i < source.Items.Count; i++)
            {
                TodoItem item = SanitizeItem(source.Items[i]);
                if (item != null) target.Items.Add(item);
            }
        }

        private static TodoItem SanitizeItem(TodoItem item)
        {
            if (item == null) return null;
            if (item.Id == null || item.Id.Length == 0) item.Id = Guid.NewGuid().ToString();
            if (item.Text == null) item.Text = string.Empty;
            return item;
        }

        private void HandleCorrupt()
        {
            ResetPanes();
            this.Draft = string.Empty;
            this.CorruptOnLoad = true;

            try
            {
                string backup = _dataFilePath + ".bak";
                if (File.Exists(backup)) File.Delete(backup);
                if (File.Exists(_dataFilePath)) File.Move(_dataFilePath, backup);
            }
            catch (Exception)
            {
                // 备份失败不影响空数据启动。
            }
        }

        // ------------------------------------------------------------------
        // 排序与增删改
        // ------------------------------------------------------------------
        /// <summary>0 = 未完成且星标，1 = 未完成，2 = 已完成。</summary>
        private static int Group(TodoItem item)
        {
            if (item.Done) return 2;
            return item.Star ? 0 : 1;
        }

        /// <summary>稳定分组排序：同组内保持原有相对顺序。</summary>
        public void Normalize(int pane)
        {
            TodoPane p = PaneAt(pane);
            if (p == null || p.Items.Count < 2) return;

            List<TodoItem> sorted = new List<TodoItem>(p.Items.Count);
            for (int g = 0; g < 3; g++)
            {
                for (int i = 0; i < p.Items.Count; i++)
                {
                    if (Group(p.Items[i]) == g) sorted.Add(p.Items[i]);
                }
            }

            p.Items.Clear();
            p.Items.AddRange(sorted);
        }

        public void NormalizeAll()
        {
            for (int i = 0; i < _panes.Count; i++) Normalize(i);
        }

        public TodoItem AddItem(int pane, string text)
        {
            TodoPane p = PaneAt(pane);
            if (p == null) return null;

            TodoItem item = new TodoItem(text);
            int insert = 0;
            for (int i = 0; i < p.Items.Count; i++)
            {
                if (!p.Items[i].Done) insert = i + 1;
            }
            p.Items.Insert(insert, item);
            Normalize(pane);
            return item;
        }

        public bool ToggleDone(int pane, int index)
        {
            TodoPane p = PaneAt(pane);
            if (p == null || index < 0 || index >= p.Items.Count) return false;

            TodoItem item = p.Items[index];
            item.Done = !item.Done;
            p.Items.RemoveAt(index);

            if (item.Done)
            {
                // 刚完成的条目直接沉到最末。
                p.Items.Add(item);
            }
            else
            {
                // 取消完成：回到未完成区的末尾。
                int insert = 0;
                for (int i = 0; i < p.Items.Count; i++)
                {
                    if (!p.Items[i].Done) insert = i + 1;
                }
                p.Items.Insert(insert, item);
            }

            Normalize(pane);
            return true;
        }

        public bool ToggleStar(int pane, int index)
        {
            TodoPane p = PaneAt(pane);
            if (p == null || index < 0 || index >= p.Items.Count) return false;
            p.Items[index].Star = !p.Items[index].Star;
            Normalize(pane);
            return true;
        }

        public TodoItem RemoveAt(int pane, int index)
        {
            TodoPane p = PaneAt(pane);
            if (p == null || index < 0 || index >= p.Items.Count) return null;
            TodoItem item = p.Items[index];
            p.Items.RemoveAt(index);
            return item;
        }

        public int ClearDone(int pane)
        {
            TodoPane p = PaneAt(pane);
            if (p == null) return 0;
            int removed = 0;
            for (int i = p.Items.Count - 1; i >= 0; i--)
            {
                if (p.Items[i].Done)
                {
                    p.Items.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        public bool RenamePane(int pane, string title)
        {
            TodoPane p = PaneAt(pane);
            if (p == null) return false;
            string clean = SanitizeTitle(title, null);
            if (clean == null || clean.Length == 0) return false;
            if (clean == p.Title) return false;
            p.Title = clean;
            return true;
        }

        /// <summary>在 index 处插入一个空栏，返回新栏索引；已达上限返回 -1。</summary>
        public int InsertPane(int index)
        {
            if (!CanAddPane) return -1;
            if (index < 0) index = 0;
            if (index > _panes.Count) index = _panes.Count;
            _panes.Insert(index, new TodoPane(DefaultTitle(index)));
            return index;
        }

        /// <summary>把 from 栏的内容并入 to 栏后删除 from 栏。</summary>
        public bool MergePane(int from, int to)
        {
            if (from < 0 || from >= _panes.Count) return false;
            if (to < 0 || to >= _panes.Count || to == from) return false;

            TodoPane source = _panes[from];
            TodoPane target = _panes[to];
            for (int i = 0; i < source.Items.Count; i++) target.Items.Add(source.Items[i]);
            _panes.RemoveAt(from);

            Normalize(to > from ? to - 1 : to);
            return true;
        }

        /// <summary>删除一栏，内容并到左邻栏（首栏并入右邻栏），最后一栏不允许删除。</summary>
        public bool RemovePane(int index)
        {
            if (_panes.Count <= 1) return false;
            int target = index > 0 ? index - 1 : index + 1;
            return MergePane(index, target);
        }

        /// <summary>把插入位置钳制到该条目所属分组区间，保证星标在最前、已完成在最末。</summary>
        public int ClampInsert(int pane, TodoItem item, int insertIndex)
        {
            TodoPane p = PaneAt(pane);
            if (p == null || item == null) return 0;

            int group = Group(item);
            int start = 0;
            int end = 0;
            for (int i = 0; i < p.Items.Count; i++)
            {
                int g = Group(p.Items[i]);
                if (g < group) start++;
                if (g <= group) end++;
            }

            if (insertIndex < start) insertIndex = start;
            if (insertIndex > end) insertIndex = end;
            if (insertIndex < 0) insertIndex = 0;
            if (insertIndex > p.Items.Count) insertIndex = p.Items.Count;
            return insertIndex;
        }

        /// <summary>
        /// 把 item 从 fromPane 移到 toPane 的第 insertIndex 个空缺处（目标列表仍包含该条目时的坐标）。
        /// </summary>
        public bool MoveItem(int fromPane, TodoItem item, int toPane, int insertIndex)
        {
            TodoPane from = PaneAt(fromPane);
            TodoPane to = PaneAt(toPane);
            if (from == null || to == null || item == null) return false;

            int fromIndex = from.Items.IndexOf(item);
            if (fromIndex < 0) return false;

            from.Items.RemoveAt(fromIndex);
            if (fromPane == toPane && insertIndex > fromIndex) insertIndex--;

            insertIndex = ClampInsert(toPane, item, insertIndex);
            if (insertIndex < 0) insertIndex = 0;
            if (insertIndex > to.Items.Count) insertIndex = to.Items.Count;
            to.Items.Insert(insertIndex, item);

            Normalize(fromPane);
            if (toPane != fromPane) Normalize(toPane);
            return true;
        }

        public int UndoneCount(int pane)
        {
            TodoPane p = PaneAt(pane);
            if (p == null) return 0;
            int count = 0;
            for (int i = 0; i < p.Items.Count; i++)
            {
                if (!p.Items[i].Done) count++;
            }
            return count;
        }

        private static string SanitizeTitle(string value, string fallback)
        {
            if (value == null) return fallback;
            StringBuilder sb = new StringBuilder(value.Length);
            bool space = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    space = sb.Length > 0;
                    continue;
                }
                if (space)
                {
                    sb.Append(' ');
                    space = false;
                }
                sb.Append(c);
                if (sb.Length >= MaxTitleLength) break;
            }
            string result = sb.ToString().Trim();
            if (result.Length == 0) return fallback;
            return result;
        }

        // ------------------------------------------------------------------
        // 保存
        // ------------------------------------------------------------------
        public bool Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(_dataFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                NormalizeAll();
                string json = ToJson();
                string temp = _dataFilePath + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));

                if (File.Exists(_dataFilePath))
                {
                    try
                    {
                        File.Replace(temp, _dataFilePath, null);
                        return true;
                    }
                    catch (Exception)
                    {
                        // 某些文件系统不支持 Replace，退化为直接覆盖。
                        File.WriteAllText(_dataFilePath, json, new UTF8Encoding(false));
                        try { File.Delete(temp); }
                        catch (Exception) { }
                        return true;
                    }
                }

                File.Move(temp, _dataFilePath);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 手写序列化：字段名与顺序完全固定，杜绝多出任何多余字段。
        /// </summary>
        public string ToJson()
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\r\n");
            sb.Append("  \"version\": ").Append(CurrentVersion).Append(",\r\n");
            sb.Append("  \"draft\": ").Append(Quote(Draft)).Append(",\r\n");

            sb.Append("  \"panes\": [");
            for (int p = 0; p < _panes.Count; p++)
            {
                TodoPane pane = _panes[p];
                if (p > 0) sb.Append(',');
                sb.Append("\r\n    { \"title\": ").Append(Quote(pane.Title)).Append(", \"items\": [");

                for (int i = 0; i < pane.Items.Count; i++)
                {
                    TodoItem item = pane.Items[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("\r\n      { \"id\": ").Append(Quote(item.Id));
                    sb.Append(", \"text\": ").Append(Quote(item.Text));
                    sb.Append(", \"done\": ").Append(item.Done ? "true" : "false");
                    sb.Append(", \"star\": ").Append(item.Star ? "true" : "false").Append(" }");
                }

                if (pane.Items.Count > 0) sb.Append("\r\n    ");
                sb.Append("] }");
            }
            if (_panes.Count > 0) sb.Append("\r\n  ");
            sb.Append("]\r\n");
            sb.Append("}\r\n");
            return sb.ToString();
        }

        private static string Quote(string value)
        {
            if (value == null) return "\"\"";
            StringBuilder sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>反序列化中间结构，字段与 JSON 一一对应。</summary>
        private class TodoData
        {
            public int Version { get; set; }
            public string Draft { get; set; }
            public List<TodoPane> Panes { get; set; }

            /// <summary>仅用于读取 v2 单栏文件。</summary>
            public List<TodoItem> Todo { get; set; }

            /// <summary>仅用于读取旧版文件的收件箱字段。</summary>
            public List<TodoItem> Inbox { get; set; }
        }
    }
}
