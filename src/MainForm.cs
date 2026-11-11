using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TodoLite
{
    public class MainForm : Form
    {
        private readonly TodoStore _store = new TodoStore();
        private readonly InboxCompose _compose;
        private readonly List<TaskListView> _lists = new List<TaskListView>();
        private readonly List<ContextMenuStrip> _menus = new List<ContextMenuStrip>();
        private readonly Timer _draftTimer;

        private bool _saveWarned;
        private int _activePane;

        private int _dragPane = -1;
        private int _dragIndex = -1;
        private Point _dragOrigin;
        private bool _dragging;
        private int _dropPane = -1;
        private int _dropIndex = -1;

        public MainForm()
        {
            float scale = ScaleFactor();

            Text = "TodoLite";
            BackColor = Ui.WindowBack;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            KeyPreview = true;
            ClientSize = new Size((int)(900 * scale), (int)(600 * scale));
            MinimumSize = new Size((int)(680 * scale), (int)(520 * scale));
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            ShowIcon = true;
            Icon = LoadAppIcon();

            _compose = new InboxCompose();
            _compose.Title = "\u6536\u4ef6\u7bb1";
            Controls.Add(_compose);

            _compose.SubmitRequested += ComposeSubmit;
            _compose.DraftChanged += ComposeDraftChanged;

            _draftTimer = new Timer();
            _draftTimer.Interval = 800;
            _draftTimer.Tick += DraftTimerTick;

            _store.Load();
            EnsurePaneViews();

            _compose.SetDraft(_store.Draft);
            // 控件里会把换行规范化，这里同步回去，保持内存与界面一致。
            _store.SetDraft(_compose.DraftText);

            _activePane = 0;
            RefreshViews();
        }

        /// <summary>从嵌入资源读取 app.ico 作为窗口图标，保持标题栏与任务栏一致。</summary>
        private static Icon LoadAppIcon()
        {
            try
            {
                Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("TodoLite.app.ico");
                if (stream == null) return null;
                using (stream)
                {
                    return new Icon(stream, SystemInformation.IconSize);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private float ScaleFactor()
        {
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    float dpi = g.DpiX;
                    if (dpi <= 0) return 1f;
                    return dpi / 96f;
                }
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        private int Scale(int value)
        {
            float factor = ScaleFactor();
            return (int)Math.Round(value * factor);
        }

        // ------------------------------------------------------------------
        // 生命周期与布局
        // ------------------------------------------------------------------
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            LayoutCards();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCards();
            for (int i = 0; i < _lists.Count; i++) _lists[i].CanSplit = CanSplitPane();
        }

        /// <summary>上方收件箱约 1/3，下方按当前栏数等宽并排。</summary>
        private void LayoutCards()
        {
            if (_compose == null || _lists.Count == 0) return;

            int pad = Scale(24);
            int gap = Scale(18);
            int colGap = Scale(18);
            int width = ClientSize.Width - pad * 2;
            int height = ClientSize.Height - pad * 2;
            if (width < Scale(80) || height < Scale(120)) return;

            int minTop = Scale(130);
            int minBottom = Scale(150);
            int top = (int)Math.Round(height * 0.32);
            if (top < minTop) top = minTop;
            if (top > height - minBottom - gap) top = height - minBottom - gap;
            if (top < Scale(70)) top = Math.Max(Scale(60), height / 3);
            int bottom = height - top - gap;
            if (bottom < Scale(60)) bottom = Math.Max(Scale(40), bottom);

            _compose.SetBounds(pad, pad, width, top);

            int count = _lists.Count;
            int totalGap = colGap * (count - 1);
            int colWidth = (width - totalGap) / count;
            if (colWidth < Scale(80)) colWidth = Scale(80);

            int listTop = pad + top + gap;
            for (int i = 0; i < count; i++)
            {
                int x = pad + i * (colWidth + colGap);
                int w = (i == count - 1) ? width - (x - pad) : colWidth;
                _lists[i].SetBounds(x, listTop, Math.Max(0, w), bottom);
            }
        }

        /// <summary>当前窗口宽度是否还放得下再拆一栏。</summary>
        private bool CanSplitPane()
        {
            if (!_store.CanAddPane) return false;

            int pad = Scale(24);
            int colGap = Scale(18);
            int width = ClientSize.Width - pad * 2;
            int count = _lists.Count + 1;
            if (count <= 1) return true;
            int each = (width - colGap * (count - 1)) / count;
            return each >= Scale(180);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (_store.MigratedOnLoad)
            {
                // 旧版数据已升级为新结构，落盘一次完成格式升级。
                Save();
            }

            _compose.FocusCompose();

            if (_store.CorruptOnLoad)
            {
                MessageBox.Show(this,
                    "\u6570\u636e\u6587\u4ef6\u5df2\u635f\u574f\uff0c\u5df2\u5907\u4efd\u4e3a todo-data.json.bak\uff0c\u7a0b\u5e8f\u5df2\u4ee5\u7a7a\u5217\u8868\u542f\u52a8\u3002",
                    "TodoLite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _draftTimer.Stop();
            if (_compose != null) _store.SetDraft(_compose.DraftText);
            Save();
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------------
        // 栏视图
        // ------------------------------------------------------------------
        private void EnsurePaneViews()
        {
            while (_lists.Count < _store.PaneCount)
            {
                TaskListView list = CreateList();
                _lists.Add(list);
                Controls.Add(list);
            }

            while (_lists.Count > _store.PaneCount && _lists.Count > 1)
            {
                int last = _lists.Count - 1;
                TaskListView list = _lists[last];
                _lists.RemoveAt(last);
                Controls.Remove(list);
                list.Dispose();

                ContextMenuStrip menu = _menus[last];
                _menus.RemoveAt(last);
                menu.Dispose();
            }

            if (_activePane >= _lists.Count) _activePane = _lists.Count - 1;
            if (_activePane < 0) _activePane = 0;
            LayoutCards();
        }

        private TaskListView CreateList()
        {
            TaskListView list = new TaskListView();
            list.Command += ListCommand;
            list.TextEdited += ListTextEdited;
            list.TitleEdited += ListTitleEdited;
            list.SplitRequested += ListSplitRequested;
            list.MouseDown += ListMouseDown;
            list.MouseMove += ListMouseMove;
            list.MouseUp += ListMouseUp;
            list.GotFocus += ListGotFocus;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = Ui.Body;
            menu.BackColor = Ui.CardBack;
            menu.ForeColor = Ui.TextPrimary;
            menu.ShowImageMargin = false;

            ToolStripMenuItem split = new ToolStripMenuItem("\u62c6\u5206\u65b0\u680f");
            split.Click += delegate { SplitPane(PaneOf(list)); };

            ToolStripMenuItem merge = new ToolStripMenuItem("\u5408\u5e76\u672c\u680f");
            merge.Click += delegate { MergePane(PaneOf(list)); };

            ToolStripMenuItem rename = new ToolStripMenuItem("\u91cd\u547d\u540d\u672c\u680f");
            rename.Click += delegate { _lists[PaneOf(list)].BeginEditTitle(); };

            ToolStripMenuItem clear = new ToolStripMenuItem("\u6e05\u9664\u672c\u680f\u5df2\u5b8c\u6210");
            clear.Click += delegate { ClearDone(PaneOf(list)); };

            menu.Items.Add(split);
            menu.Items.Add(merge);
            menu.Items.Add(rename);
            menu.Items.Add(clear);
            menu.Opening += MenuOpening;

            list.ContextMenuStrip = menu;
            _menus.Add(menu);
            return list;
        }

        private void SplitPane(int pane)
        {
            if (pane < 0 || pane >= _lists.Count) return;
            if (!CanSplitPane()) return;

            int index = _store.InsertPane(pane + 1);
            if (index < 0) return;

            EnsurePaneViews();
            Save();
            RefreshViews();
            SetActivePane(index);
            _lists[index].Focus();
        }

        private void MergePane(int pane)
        {
            if (pane < 0 || pane >= _lists.Count) return;
            if (_store.PaneCount <= 1) return;

            int target = pane > 0 ? pane - 1 : pane + 1;
            if (!_store.MergePane(pane, target)) return;

            EnsurePaneViews();
            Save();
            RefreshViews();

            int active = pane < _lists.Count ? pane : _lists.Count - 1;
            if (active < 0) active = 0;
            SetActivePane(active);
            _lists[active].Focus();
        }

        // ------------------------------------------------------------------
        // 视图刷新
        // ------------------------------------------------------------------
        private void RefreshViews()
        {
            bool canSplit = CanSplitPane();

            for (int pane = 0; pane < _lists.Count; pane++)
            {
                TodoPane source = _store.PaneAt(pane);
                TaskListView list = _lists[pane];
                if (source == null || list == null) continue;

                List<ITaskItem> items = new List<ITaskItem>(source.Items.Count);
                int undone = 0;
                for (int i = 0; i < source.Items.Count; i++)
                {
                    TodoItem item = source.Items[i];
                    items.Add(item);
                    if (!item.Done) undone++;
                }

                list.Title = source.Title;
                list.Count = undone;
                list.CanSplit = canSplit;
                list.Active = pane == _activePane;
                list.SetItems(items);
            }

            TodoPane active = _store.PaneAt(_activePane);
            if (active != null) _compose.TargetPaneName = active.Title;
        }

        private void Save()
        {
            if (_store.Save()) return;
            if (_saveWarned) return;
            _saveWarned = true;
            MessageBox.Show(this,
                "\u65e0\u6cd5\u5199\u5165\u6570\u636e\u6587\u4ef6\uff1a\r\n" + _store.DataFilePath,
                "TodoLite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ------------------------------------------------------------------
        // 栏与选中
        // ------------------------------------------------------------------
        private int PaneOf(object sender)
        {
            for (int i = 0; i < _lists.Count; i++)
            {
                if (ReferenceEquals(_lists[i], sender)) return i;
            }
            return _activePane;
        }

        private int FocusedPane()
        {
            for (int i = 0; i < _lists.Count; i++)
            {
                if (_lists[i] != null && _lists[i].Focused) return i;
            }
            return _activePane;
        }

        private void SetActivePane(int pane)
        {
            if (pane < 0) pane = 0;
            if (pane >= _lists.Count) pane = _lists.Count - 1;
            if (pane < 0) return;

            _activePane = pane;
            for (int i = 0; i < _lists.Count; i++)
            {
                if (_lists[i] != null) _lists[i].Active = i == pane;
            }

            TodoPane source = _store.PaneAt(pane);
            if (source != null) _compose.TargetPaneName = source.Title;
        }

        private void ListGotFocus(object sender, EventArgs e)
        {
            SetActivePane(PaneOf(sender));
        }

        private void ListSplitRequested(object sender, EventArgs e)
        {
            SplitPane(PaneOf(sender));
        }

        private TodoItem ResolveItem(int pane, int index, ITaskItem item)
        {
            TodoItem todo = item as TodoItem;
            if (todo != null && _store.IndexOf(pane, todo) >= 0) return todo;

            TodoPane source = _store.PaneAt(pane);
            if (source != null && index >= 0 && index < source.Items.Count) return source.Items[index];
            return null;
        }

        private void SelectItem(int pane, TodoItem item)
        {
            if (item == null || pane < 0 || pane >= _lists.Count) return;
            int index = _store.IndexOf(pane, item);
            if (index >= 0) _lists[pane].SelectedIndex = index;
        }

        // ------------------------------------------------------------------
        // 收件箱：提交与草稿
        // ------------------------------------------------------------------
        private void ComposeSubmit(object sender, TextEventArgs e)
        {
            if (e.Text == null || e.Text.Length == 0) return;

            TodoItem item = _store.AddItem(_activePane, e.Text);
            _store.SetDraft(_compose.DraftText);
            Save();
            RefreshViews();
            SelectItem(_activePane, item);
        }

        private void ComposeDraftChanged(object sender, EventArgs e)
        {
            _store.SetDraft(_compose.DraftText);
            _draftTimer.Stop();
            _draftTimer.Start();
        }

        private void DraftTimerTick(object sender, EventArgs e)
        {
            _draftTimer.Stop();
            _store.SetDraft(_compose.DraftText);
            Save();
        }

        // ------------------------------------------------------------------
        // 行内按钮：勾选 / 星标 / 删除
        // ------------------------------------------------------------------
        private void ListCommand(object sender, TaskItemEventArgs e)
        {
            int pane = PaneOf(sender);
            TodoItem item = ResolveItem(pane, e.Index, e.Item);
            if (item == null) return;

            int index = _store.IndexOf(pane, item);
            if (index < 0) return;

            if (e.Command == TaskCommand.Delete)
            {
                _store.RemoveAt(pane, index);
                Save();
                RefreshViews();
                int count = _lists[pane].ItemCount;
                _lists[pane].SelectedIndex = index >= count ? count - 1 : index;
            }
            else if (e.Command == TaskCommand.Toggle)
            {
                _store.ToggleDone(pane, index);
                Save();
                RefreshViews();
                SelectItem(pane, item);
            }
            else if (e.Command == TaskCommand.Star)
            {
                _store.ToggleStar(pane, index);
                Save();
                RefreshViews();
                SelectItem(pane, item);
            }
        }

        private void ListTextEdited(object sender, TaskItemEventArgs e)
        {
            if (e.Text == null) return;
            int pane = PaneOf(sender);
            TodoItem item = ResolveItem(pane, e.Index, e.Item);
            if (item == null) return;

            item.Text = e.Text;
            Save();
            RefreshViews();
        }

        private void ListTitleEdited(object sender, TextEventArgs e)
        {
            int pane = PaneOf(sender);
            bool renamed = _store.RenamePane(pane, e.Text);
            if (renamed) Save();
            RefreshViews();
        }

        private void MenuOpening(object sender, CancelEventArgs e)
        {
            ContextMenuStrip menu = sender as ContextMenuStrip;
            if (menu == null) return;

            int pane = PaneOf(menu.SourceControl);
            TodoPane source = _store.PaneAt(pane);

            bool hasDone = false;
            if (source != null)
            {
                for (int i = 0; i < source.Items.Count; i++)
                {
                    if (source.Items[i].Done) { hasDone = true; break; }
                }
            }

            if (menu.Items.Count >= 4)
            {
                menu.Items[0].Enabled = CanSplitPane();
                menu.Items[1].Enabled = _store.PaneCount > 1;
                menu.Items[3].Enabled = hasDone;
            }

            SetActivePane(pane);
        }

        private void ClearDone(int pane)
        {
            if (_store.ClearDone(pane) <= 0) return;
            Save();
            RefreshViews();
        }

        // ------------------------------------------------------------------
        // 快捷键：F2 编辑、Delete 删除、Space 完成、S 星标
        // ------------------------------------------------------------------
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.D1) || keyData == (Keys.Control | Keys.NumPad1))
            {
                _compose.FocusCompose();
                return true;
            }
            if (keyData == (Keys.Control | Keys.D2) || keyData == (Keys.Control | Keys.NumPad2))
            {
                SetActivePane(0);
                _lists[0].Focus();
                return true;
            }
            if (keyData == (Keys.Control | Keys.D3) || keyData == (Keys.Control | Keys.NumPad3))
            {
                if (_lists.Count < 2) return true;
                SetActivePane(1);
                _lists[1].Focus();
                return true;
            }

            bool typing = _compose.TextHasFocus;
            for (int i = 0; i < _lists.Count; i++)
            {
                if (_lists[i] != null && _lists[i].IsEditing) typing = true;
            }
            if (typing) return base.ProcessCmdKey(ref msg, keyData);

            if (keyData == Keys.F2)
            {
                _lists[FocusedPane()].BeginEditSelected();
                return true;
            }
            if (keyData == Keys.Delete)
            {
                DeleteSelected();
                return true;
            }
            if (keyData == Keys.Space)
            {
                ToggleSelected(TaskCommand.Toggle);
                return true;
            }
            if (keyData == Keys.S)
            {
                ToggleSelected(TaskCommand.Star);
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ToggleSelected(TaskCommand command)
        {
            int pane = FocusedPane();
            if (pane < 0 || pane >= _lists.Count) return;

            int index = _lists[pane].SelectedIndex;
            TodoPane source = _store.PaneAt(pane);
            if (source == null || index < 0 || index >= source.Items.Count) return;

            TodoItem item = source.Items[index];

            if (command == TaskCommand.Toggle) _store.ToggleDone(pane, index);
            else _store.ToggleStar(pane, index);

            Save();
            RefreshViews();
            SelectItem(pane, item);
        }

        private void DeleteSelected()
        {
            int pane = FocusedPane();
            if (pane < 0 || pane >= _lists.Count) return;

            int index = _lists[pane].SelectedIndex;
            TodoPane source = _store.PaneAt(pane);
            if (source == null || index < 0 || index >= source.Items.Count) return;

            _store.RemoveAt(pane, index);
            Save();
            RefreshViews();
            int count = _lists[pane].ItemCount;
            _lists[pane].SelectedIndex = index >= count ? count - 1 : index;
        }

        // ------------------------------------------------------------------
        // 拖拽（栏内重排 + 跨栏移动）
        // ------------------------------------------------------------------
        private void ListMouseDown(object sender, MouseEventArgs e)
        {
            int pane = PaneOf(sender);
            SetActivePane(pane);
            // 只清理上一次拖拽的残留，不能清行内按钮的按下状态（否则点击会失效）。
            ResetDragState();
            if (e.Button != MouseButtons.Left) return;

            TaskListView list = _lists[pane];
            if (list.PressedButton != TaskCommand.None) return;

            int index;
            if (!list.TryHitRow(e.Location, out index)) return;

            _dragPane = pane;
            _dragIndex = index;
            _dragOrigin = list.PointToScreen(e.Location);
        }

        private void ListMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragIndex < 0 || _dragPane < 0 || _dragPane >= _lists.Count) return;

            TaskListView source = _lists[_dragPane];
            if (!_dragging)
            {
                Point screen = source.PointToScreen(e.Location);
                Size limit = SystemInformation.DragSize;
                if (Math.Abs(screen.X - _dragOrigin.X) < limit.Width
                    && Math.Abs(screen.Y - _dragOrigin.Y) < limit.Height) return;

                _dragging = true;
                source.DragActive = true;
                source.SetDragSource(_dragIndex);
                source.Cursor = Cursors.Default;
            }

            UpdateDropTarget();
        }

        private void UpdateDropTarget()
        {
            Point screen = Cursor.Position;
            int target = -1;
            for (int i = 0; i < _lists.Count; i++)
            {
                if (_lists[i] == null) continue;
                Rectangle bounds = _lists[i].RectangleToScreen(_lists[i].ClientRectangle);
                if (bounds.Contains(screen)) { target = i; break; }
            }

            for (int i = 0; i < _lists.Count; i++)
            {
                if (_lists[i] == null) continue;
                bool over = i == target;
                _lists[i].IsDragOver = over;
                if (!over) _lists[i].ClearDropIndicator();
            }

            _dropPane = target;
            _dropIndex = -1;

            if (target < 0) return;

            TaskListView list = _lists[target];
            Point local = list.PointToClient(screen);
            ITaskItem dragged = _lists[_dragPane].ItemAt(_dragIndex);
            int insert = list.GetInsertIndex(local, dragged);
            _dropIndex = insert;
            list.SetDropIndicator(insert);
            list.AutoScrollForDrag(local);
        }

        private void ListMouseUp(object sender, MouseEventArgs e)
        {
            if (_dragging && _dragPane >= 0 && _dragIndex >= 0 && _dropPane >= 0 && _dropIndex >= 0)
            {
                int fromPane = _dragPane;
                int fromIndex = _dragIndex;
                int toPane = _dropPane;
                int insert = _dropIndex;

                TodoItem item = _lists[fromPane].ItemAt(fromIndex) as TodoItem;
                FinishDrag();

                if (item != null) MoveItemBetween(fromPane, item, toPane, insert);
                return;
            }

            FinishDrag();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            FinishDrag();
            base.OnMouseUp(e);
        }

        private void ResetDragState()
        {
            for (int i = 0; i < _lists.Count; i++)
            {
                if (_lists[i] == null) continue;
                _lists[i].DragActive = false;
                _lists[i].IsDragOver = false;
                _lists[i].ClearDragSource();
                _lists[i].ClearDropIndicator();
            }
            _dragging = false;
            _dragPane = -1;
            _dragIndex = -1;
            _dropPane = -1;
            _dropIndex = -1;
        }

        private void FinishDrag()
        {
            ResetDragState();
            for (int i = 0; i < _lists.Count; i++)
            {
                if (_lists[i] == null) continue;
                _lists[i].ClearPressedState();
            }
        }

        private void MoveItemBetween(int fromPane, TodoItem item, int toPane, int insertIndex)
        {
            if (!_store.MoveItem(fromPane, item, toPane, insertIndex))
            {
                RefreshViews();
                return;
            }

            Save();
            RefreshViews();
            SelectItem(toPane, item);
            SetActivePane(toPane);
            _lists[toPane].Focus();
        }
    }
}
