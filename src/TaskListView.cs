using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TodoLite
{
    public enum TaskCommand
    {
        None = 0,
        Toggle = 1,
        Delete = 2,
        Star = 3
    }

    public class TaskItemEventArgs : EventArgs
    {
        public ITaskItem Item { get; set; }
        public int Index { get; set; }
        public TaskCommand Command { get; set; }
        public string Text { get; set; }

        public TaskItemEventArgs(ITaskItem item, int index)
        {
            this.Item = item;
            this.Index = index;
            this.Command = TaskCommand.None;
            this.Text = null;
        }
    }

    public class TextEventArgs : EventArgs
    {
        public string Text { get; private set; }

        public TextEventArgs(string text)
        {
            this.Text = text;
        }
    }

    /// <summary>统一的配色与字体，保持"一张好纸"的温暖极简。</summary>
    internal static class Ui
    {
        public static readonly Color WindowBack = Color.FromArgb(246, 245, 244);
        public static readonly Color CardBack = Color.FromArgb(255, 255, 255);
        public static readonly Color CardBorder = Color.FromArgb(234, 234, 234);
        public static readonly Color TextPrimary = Color.FromArgb(97, 93, 89);
        public static readonly Color TextMuted = Color.FromArgb(154, 148, 142);
        public static readonly Color TextDone = Color.FromArgb(183, 177, 171);
        public static readonly Color TextFaint = Color.FromArgb(201, 196, 191);
        public static readonly Color RowHover = Color.FromArgb(245, 243, 241);
        public static readonly Color RowSelect = Color.FromArgb(239, 237, 234);
        public static readonly Color RowSelectSoft = Color.FromArgb(245, 242, 238);
        public static readonly Color CheckBorder = Color.FromArgb(210, 205, 200);
        public static readonly Color InputBorder = Color.FromArgb(217, 213, 209);
        public static readonly Color Danger = Color.FromArgb(176, 74, 63);
        public static readonly Color Indicator = Color.FromArgb(63, 60, 57);
        public static readonly Color Star = Color.FromArgb(214, 168, 84);
        public static readonly Color StarIdle = Color.FromArgb(202, 197, 191);
        public static readonly Color StarRowTint = Color.FromArgb(255, 247, 231);
        public static readonly Color Ripple = Color.FromArgb(126, 120, 114);

        public static readonly Font Body;
        public static readonly Font BodyStrike;
        public static readonly Font Small;
        public static readonly Font Title;
        public static readonly Font Empty;

        static Ui()
        {
            string sans = PickFamily(new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" });
            if (sans == null) sans = "Microsoft Sans Serif";

            Body = Make(sans, 11F, FontStyle.Regular);
            BodyStrike = Make(sans, 11F, FontStyle.Strikeout);
            Small = Make(sans, 8.5F, FontStyle.Regular);
            Empty = Make(sans, 9F, FontStyle.Regular);

            string serif = PickFamily(new string[] { "Source Han Serif SC", "Noto Serif SC", "Songti SC", "SimSun" });
            if (serif == null) serif = sans;
            Title = Make(serif, 15F, FontStyle.Bold);
        }

        private static string PickFamily(string[] candidates)
        {
            FontFamily[] installed;
            try
            {
                installed = FontFamily.Families;
            }
            catch (Exception)
            {
                return null;
            }

            for (int i = 0; i < candidates.Length; i++)
            {
                for (int j = 0; j < installed.Length; j++)
                {
                    if (string.Equals(installed[j].Name, candidates[i], StringComparison.OrdinalIgnoreCase))
                        return candidates[i];
                }
            }
            return null;
        }

        private static Font Make(string family, float size, FontStyle style)
        {
            try
            {
                Font font = new Font(family, size, style);
                if (font.Name != null && font.Name.Length > 0) return font;
                font.Dispose();
            }
            catch (Exception)
            {
            }
            return new Font("Microsoft Sans Serif", size, style);
        }

        public static GraphicsPath RoundRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return path;
            }
            if (radius <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            int d = radius * 2;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>
    /// TODO 列表卡片：标题（可双击改名）、可滚动、可拖拽的任务行，全部自绘。
    /// 星标置顶、已完成沉底由上层数据排好序，这里只负责渲染与交互动效。
    /// </summary>
    public class TaskListView : Control
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int EM_SETMARGINS = 0x00D3;
        private const int EC_LEFTMARGIN = 0x0001;
        private const int EC_RIGHTMARGIN = 0x0002;

        private const float TauHover = 0.055f;
        private const float TauCheck = 0.085f;
        private const float TauStar = 0.08f;
        private const float TauAlpha = 0.10f;
        private const float TauOffset = 0.075f;
        private const float TauGhost = 0.045f;
        private const float TauScroll = 0.055f;
        private const float TauTheme = 0.06f;

        /// <summary>礼花配色：暖金为主，点缀低饱和的赭、苔绿、雾蓝，和整体纸感一致。</summary>
        private static readonly Color[] ConfettiColors = new Color[]
        {
            Color.FromArgb(214, 168, 84),
            Color.FromArgb(201, 143, 117),
            Color.FromArgb(148, 166, 140),
            Color.FromArgb(139, 156, 182),
            Color.FromArgb(222, 205, 176),
            Color.FromArgb(184, 156, 190)
        };

        private class RowFx
        {
            public float Alpha = 1f;
            public float OffsetY;
            public float CheckT;
            public float StarT;
            public float HoverT;
        }

        private class GhostRow
        {
            public string Text;
            public bool Done;
            public bool Star;
            public float Top;
            public int Height;
            public float Alpha = 1f;
        }

        private class Ripple
        {
            public Rectangle Bounds;
            public int X;
            public int Y;
            public float T;
        }

        /// <summary>完成时撒出的一枚纸屑。</summary>
        private class Confetti
        {
            public float X;
            public float Y;
            public float VX;
            public float VY;
            public float Gravity;
            public float Size;
            public float Angle;
            public float Spin;
            public float Age;
            public float Life;
            public Color Color;
        }

        private readonly List<ITaskItem> _items = new List<ITaskItem>();
        private readonly Dictionary<string, RowFx> _fx = new Dictionary<string, RowFx>();
        private readonly Dictionary<int, SolidBrush> _brushes = new Dictionary<int, SolidBrush>();
        private readonly List<GhostRow> _ghosts = new List<GhostRow>();
        private readonly List<Ripple> _ripples = new List<Ripple>();
        private readonly List<Confetti> _confetti = new List<Confetti>();
        private readonly Random _random = new Random();

        /// <summary>上一次渲染时的完成状态；用于识别「刚被勾选完成」而触发礼花。</summary>
        private readonly Dictionary<string, bool> _renderedDone = new Dictionary<string, bool>();
        private readonly TextBox _editor;
        private readonly TextBox _titleEditor;
        private readonly Timer _anim;
        private readonly Stopwatch _clock = new Stopwatch();

        private string _title = "TODO";
        private int _count;

        private int _selected = -1;
        private int _hover = -1;
        private int _scrollTarget;
        private float _scrollPos;
        private int _editIndex = -1;
        private int _dropIndex = -1;
        private int _dragSource = -1;

        private TaskCommand _pressedButton = TaskCommand.None;
        private TaskCommand _hoverButton = TaskCommand.None;
        private int _pressedIndex = -1;
        private bool _ending;

        private bool _active;
        private bool _draggingOver;
        private float _activeT;
        private float _dragOverT;
        private bool _keyboardSelection;
        private bool _editingTitle;
        private bool _titleHover;
        private bool _canSplit;
        private bool _splitHover;
        private bool _splitPressed;
        private bool _animating;
        private long _lastTick;

        public event EventHandler<TaskItemEventArgs> Command;
        public event EventHandler<TaskItemEventArgs> TextEdited;
        public event EventHandler<TextEventArgs> TitleEdited;
        public event EventHandler SplitRequested;

        public TaskListView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw
                   | ControlStyles.Selectable, true);
            BackColor = Ui.CardBack;
            TabStop = false;

            _editor = new TextBox();
            _editor.BorderStyle = BorderStyle.None;
            _editor.Font = Ui.Body;
            _editor.ForeColor = Ui.TextPrimary;
            _editor.BackColor = Ui.RowSelectSoft;
            _editor.AutoSize = false;
            _editor.Visible = false;
            _editor.KeyDown += EditorKeyDown;
            _editor.LostFocus += EditorLostFocus;
            Controls.Add(_editor);

            _titleEditor = new TextBox();
            _titleEditor.BorderStyle = BorderStyle.None;
            _titleEditor.Font = Ui.Title;
            _titleEditor.ForeColor = Ui.TextPrimary;
            _titleEditor.BackColor = Ui.RowHover;
            _titleEditor.AutoSize = false;
            _titleEditor.Visible = false;
            _titleEditor.KeyDown += TitleEditorKeyDown;
            _titleEditor.LostFocus += TitleEditorLostFocus;
            Controls.Add(_titleEditor);

            _anim = new Timer();
            _anim.Interval = 16;
            _anim.Tick += AnimTick;

            _clock.Start();
        }

        // ------------------------------------------------------------------
        // 公开属性
        // ------------------------------------------------------------------
        public string Title
        {
            get { return _title; }
            set
            {
                string v = value == null ? string.Empty : value;
                if (_title == v) return;
                _title = v;
                Invalidate();
            }
        }

        public int Count
        {
            get { return _count; }
            set
            {
                if (_count == value) return;
                _count = value;
                Invalidate();
            }
        }

        /// <summary>当前操作栏：边框与标题颜色变深，用来区分注意力所在。</summary>
        public bool Active
        {
            get { return _active; }
            set
            {
                if (_active == value) return;
                _active = value;
                EnsureAnimation();
                Invalidate();
            }
        }

        /// <summary>拖拽经过本栏时的高亮。</summary>
        public bool IsDragOver
        {
            get { return _draggingOver; }
            set
            {
                if (_draggingOver == value) return;
                _draggingOver = value;
                EnsureAnimation();
                Invalidate();
            }
        }

        /// <summary>是否显示标题右侧的「拆分」入口（栏数或宽度不够时隐藏）。</summary>
        public bool CanSplit
        {
            get { return _canSplit; }
            set
            {
                if (_canSplit == value) return;
                _canSplit = value;
                if (!value) _splitHover = false;
                Invalidate();
            }
        }

        public bool IsEditing
        {
            get { return _editIndex >= 0 || _editingTitle; }
        }

        public TaskCommand PressedButton
        {
            get { return _pressedButton; }
        }

        public bool DragActive { get; set; }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = value;
                if (v < -1) v = -1;
                if (v >= _items.Count) v = _items.Count - 1;
                if (_selected == v) return;
                _selected = v;
                if (v >= 0) EnsureVisible(v);
                Invalidate();
            }
        }

        public ITaskItem SelectedItem
        {
            get
            {
                if (_selected < 0 || _selected >= _items.Count) return null;
                return _items[_selected];
            }
        }

        public int ItemCount
        {
            get { return _items.Count; }
        }

        public List<ITaskItem> Items
        {
            get { return _items; }
        }

        public ITaskItem ItemAt(int index)
        {
            if (index < 0 || index >= _items.Count) return null;
            return _items[index];
        }

        public int ScrollTop
        {
            get { return (int)Math.Round(_scrollPos); }
        }

        // ------------------------------------------------------------------
        // 尺寸与布局（按系统 DPI 缩放）
        // ------------------------------------------------------------------
        private int Scale(int value)
        {
            int dpi = this.DeviceDpi;
            if (dpi <= 0) dpi = 96;
            return (int)Math.Round(value * dpi / 96.0);
        }

        private int Pad { get { return Scale(20); } }
        private int HeaderTop { get { return Scale(16); } }
        private int HeaderHeight { get { return Ui.Title.Height + Scale(4); } }
        private int HeaderBottom { get { return HeaderTop + HeaderHeight; } }

        public int ListTop { get { return HeaderBottom + Scale(10); } }
        public int ListBottom { get { return Height - Scale(14); } }

        private int MinRowHeight { get { return Scale(40); } }
        private int RowPadTop { get { return Scale(9); } }
        private int RowPadBottom { get { return Scale(9); } }

        private int CountWidth { get { return Scale(38); } }
        private int SplitWidth { get { return Scale(44); } }

        private Rectangle HeaderRect()
        {
            int reserved = CountWidth + (CanSplit ? SplitWidth : 0);
            return new Rectangle(Pad, HeaderTop, Math.Max(Scale(40), Width - Pad * 2 - reserved), HeaderHeight);
        }

        private Rectangle HeaderSplitRect()
        {
            return new Rectangle(Width - Pad - CountWidth - SplitWidth, HeaderTop, SplitWidth, HeaderHeight);
        }

        private Rectangle RowBoundsCore(int index)
        {
            int width = Math.Max(0, Width - Scale(20));
            if (index < 0 || index >= _rowTops.Length || index >= _rowHeights.Length)
                return new Rectangle(Scale(10), ListTop - ScrollTop, width, MinRowHeight);
            int top = ListTop + _rowTops[index] - ScrollTop;
            return new Rectangle(Scale(10), top, width, _rowHeights[index]);
        }

        private int LineCenterY(Rectangle row)
        {
            return row.Y + RowPadTop + Ui.Body.Height / 2;
        }

        private Rectangle CheckRect(Rectangle row)
        {
            int size = Scale(15);
            return new Rectangle(row.X + Scale(10), LineCenterY(row) - size / 2, size, size);
        }

        private Rectangle DeleteRect(Rectangle row)
        {
            int size = Scale(22);
            return new Rectangle(row.Right - Scale(6) - size, LineCenterY(row) - size / 2, size, size);
        }

        private Rectangle StarRect(Rectangle row)
        {
            // 与删除按钮保持同样大小的点击区域。
            int size = Scale(22);
            return new Rectangle(DeleteRect(row).X - Scale(4) - size, LineCenterY(row) - size / 2, size, size);
        }

        private Rectangle TextRect(Rectangle row)
        {
            int x = CheckRect(row).Right + Scale(10);
            int right = StarRect(row).X - Scale(6);
            int width = right - x;
            if (width < Scale(24)) width = Scale(24);
            int height = row.Height - RowPadTop - RowPadBottom;
            if (height < Ui.Body.Height) height = Ui.Body.Height;
            return new Rectangle(x, row.Y + RowPadTop, width, height);
        }

        // ------------------------------------------------------------------
        // 行高自适应：按当前栏宽测量换行后的文字高度
        // ------------------------------------------------------------------
        private class MeasuredRow
        {
            public string Text;
            public int Width;
            public int Height;
        }

        private int[] _rowTops = new int[0];
        private int[] _rowHeights = new int[0];
        private int _contentHeight;
        private readonly Dictionary<string, MeasuredRow> _measureCache = new Dictionary<string, MeasuredRow>();

        private int TextAreaWidth()
        {
            Rectangle probe = new Rectangle(Scale(10), 0, Math.Max(0, Width - Scale(20)), MinRowHeight);
            return TextRect(probe).Width;
        }

        private void UpdateMetrics()
        {
            int count = _items.Count;
            if (_rowTops.Length != count)
            {
                _rowTops = new int[count];
                _rowHeights = new int[count];
            }

            int width = TextAreaWidth();
            int y = 0;
            for (int i = 0; i < count; i++)
            {
                _rowTops[i] = y;
                _rowHeights[i] = MeasureRow(_items[i], width);
                y += _rowHeights[i];
            }
            _contentHeight = y;
        }

        private int MeasureRow(ITaskItem item, int width)
        {
            string text = item == null || item.Text == null ? string.Empty : item.Text;
            string key = IdOf(item);

            MeasuredRow cached;
            if (key.Length > 0 && _measureCache.TryGetValue(key, out cached)
                && cached.Width == width && string.Equals(cached.Text, text, StringComparison.Ordinal))
            {
                return cached.Height;
            }

            int textHeight;
            if (text.Length == 0)
            {
                textHeight = Ui.Body.Height;
            }
            else
            {
                Size size = TextRenderer.MeasureText(text, Ui.Body,
                    new Size(Math.Max(Scale(20), width), 100000),
                    TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                textHeight = size.Height;
            }

            int height = textHeight + RowPadTop + RowPadBottom;
            if (height < MinRowHeight) height = MinRowHeight;

            if (key.Length > 0)
            {
                cached = new MeasuredRow();
                cached.Text = text;
                cached.Width = width;
                cached.Height = height;
                _measureCache[key] = cached;
            }
            return height;
        }

        private int IndexAtContent(int contentY)
        {
            for (int i = 0; i < _rowTops.Length; i++)
            {
                if (contentY < _rowTops[i] + _rowHeights[i]) return i;
            }
            return -1;
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            using (GraphicsPath path = Ui.RoundRect(new Rectangle(0, 0, Width, Height), Scale(8)))
            {
                Region old = this.Region;
                this.Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
            UpdateMetrics();
            ClampScroll();
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _anim.Stop();
                _anim.Dispose();
                _clock.Stop();
                foreach (SolidBrush brush in _brushes.Values) brush.Dispose();
                _brushes.Clear();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------
        // 数据
        // ------------------------------------------------------------------
        public void SetItems(List<ITaskItem> items)
        {
            int oldScroll = ScrollTop;
            List<ITaskItem> oldItems = new List<ITaskItem>(_items);
            Dictionary<string, int> oldIndex = new Dictionary<string, int>();
            for (int i = 0; i < oldItems.Count; i++)
            {
                string id = IdOf(oldItems[i]);
                if (!oldIndex.ContainsKey(id)) oldIndex[id] = i;
            }

            // 条目对象与数据层共享，Done 已经被改过；这里用上一次渲染的快照判断变化。
            Dictionary<string, bool> oldDone = new Dictionary<string, bool>(_renderedDone);
            int[] oldTops = (int[])_rowTops.Clone();
            int[] oldHeights = (int[])_rowHeights.Clone();

            _items.Clear();
            if (items != null) _items.AddRange(items);
            UpdateMetrics();

            HashSet<string> alive = new HashSet<string>();
            for (int i = 0; i < _items.Count; i++)
            {
                string id = IdOf(_items[i]);
                alive.Add(id);

                RowFx fx = FxOf(id);
                TodoItem todo = _items[i] as TodoItem;

                int old;
                if (oldIndex.TryGetValue(id, out old))
                {
                    float oldY = (old < oldTops.Length ? oldTops[old] : 0) - oldScroll;
                    float newY = _rowTops[i] - ScrollTop;
                    fx.OffsetY += oldY - newY;
                }
                else
                {
                    fx.Alpha = 0f;
                    fx.OffsetY = Scale(10);
                    fx.CheckT = (todo != null && todo.Done) ? 1f : 0f;
                    fx.StarT = (todo != null && todo.Star) ? 1f : 0f;
                }
            }

            // 被删掉的行留一个原地淡出的残影，其余行同时上滑补位。
            for (int i = 0; i < oldItems.Count; i++)
            {
                string id = IdOf(oldItems[i]);
                int index;
                if (!oldIndex.TryGetValue(id, out index)) continue;
                if (alive.Contains(id)) continue;
                if (_ghosts.Count >= 24) break;

                TodoItem todo = oldItems[index] as TodoItem;
                GhostRow ghost = new GhostRow();
                ghost.Text = oldItems[index].Text;
                ghost.Done = todo != null && todo.Done;
                ghost.Star = todo != null && todo.Star;
                ghost.Top = ListTop + (float)((index < oldTops.Length ? oldTops[index] : 0) - oldScroll);
                ghost.Height = index < oldHeights.Length ? oldHeights[index] : MinRowHeight;
                ghost.Alpha = 1f;
                _ghosts.Add(ghost);
            }

            // 清理已经离场且没有残影的视觉状态。
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, RowFx> kv in _fx)
            {
                if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
            }
            for (int i = 0; i < dead.Count; i++) _fx.Remove(dead[i]);

            if (_editIndex >= 0)
            {
                _ending = true;
                _editor.Visible = false;
                _editIndex = -1;
                _ending = false;
            }

            if (_selected >= _items.Count) _selected = _items.Count - 1;
            if (_hover >= _items.Count) _hover = -1;
            if (_dragSource >= _items.Count) _dragSource = -1;
            if (_pressedIndex >= _items.Count) _pressedIndex = -1;

            CelebrateCompleted(oldIndex, oldDone, oldScroll, oldTops, oldHeights);

            _renderedDone.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                TodoItem rendered = _items[i] as TodoItem;
                if (rendered != null) _renderedDone[IdOf(_items[i])] = rendered.Done;
            }

            ClampScroll();
            EnsureAnimation();
            Invalidate();
        }

        /// <summary>发现「未完成 → 已完成」的条目，就在它原来那一行的勾选框位置撒一把礼花。</summary>
        private void CelebrateCompleted(Dictionary<string, int> oldIndex, Dictionary<string, bool> oldDone,
            int oldScroll, int[] oldTops, int[] oldHeights)
        {
            if (oldDone.Count == 0 || _confetti.Count >= 200) return;

            int top = ListTop;
            int bottom = ListBottom;
            if (bottom <= top) return;

            for (int i = 0; i < _items.Count; i++)
            {
                TodoItem todo = _items[i] as TodoItem;
                if (todo == null || !todo.Done) continue;

                string id = IdOf(_items[i]);
                bool wasDone;
                if (!oldDone.TryGetValue(id, out wasDone) || wasDone) continue;

                int old;
                if (!oldIndex.TryGetValue(id, out old)) continue;
                if (old >= oldTops.Length || old >= oldHeights.Length) continue;

                int rowTop = oldTops[old];
                int rowHeight = oldHeights[old];
                float y = top + rowTop - oldScroll + rowHeight / 2f;
                if (y < top - rowHeight || y > bottom + rowHeight) continue;
                if (y < top + Scale(8)) y = top + Scale(8);
                if (y > bottom - Scale(8)) y = bottom - Scale(8);

                // 从行中部迸发，避免纸屑贴着卡片左缘被圆角裁掉。
                float x = Width / 2f;
                SpawnConfetti(x, y);
            }
        }

        private void SpawnConfetti(float x, float y)
        {
            int count = 26 + _random.Next(10);
            for (int i = 0; i < count; i++)
            {
                if (_confetti.Count >= 240) break;

                Confetti piece = new Confetti();
                piece.X = x + (float)(_random.NextDouble() * 14 - 7);
                piece.Y = y + (float)(_random.NextDouble() * 8 - 4);

                double angle = -Math.PI / 2 + (_random.NextDouble() - 0.5) * 2.3;
                float speed = Scale(130) + (float)_random.NextDouble() * Scale(230);
                piece.VX = (float)(Math.Cos(angle) * speed);
                piece.VY = (float)(Math.Sin(angle) * speed);
                piece.Gravity = Scale(520) + (float)_random.NextDouble() * Scale(300);
                piece.Size = Scale(4) + (float)_random.NextDouble() * Scale(4);
                piece.Angle = (float)_random.NextDouble() * 360f;
                piece.Spin = (float)(_random.NextDouble() * 720 - 360);
                piece.Life = 0.65f + (float)_random.NextDouble() * 0.5f;
                piece.Color = ConfettiColors[_random.Next(ConfettiColors.Length)];
                _confetti.Add(piece);
            }

            EnsureAnimation();
            Invalidate();
        }

        private void UpdateConfetti(float dt, ref bool busy)
        {
            for (int i = _confetti.Count - 1; i >= 0; i--)
            {
                Confetti piece = _confetti[i];
                piece.Age += dt;
                if (piece.Age >= piece.Life)
                {
                    _confetti.RemoveAt(i);
                    continue;
                }

                busy = true;
                piece.VY += piece.Gravity * dt;
                piece.VX *= 1f - Math.Min(0.9f, 1.4f * dt);
                piece.VY *= 1f - Math.Min(0.9f, 0.35f * dt);
                piece.X += piece.VX * dt;
                piece.Y += piece.VY * dt;
                piece.Angle += piece.Spin * dt;
            }
        }

        private void PaintConfetti(Graphics g)
        {
            if (_confetti.Count == 0) return;

            for (int i = 0; i < _confetti.Count; i++)
            {
                Confetti piece = _confetti[i];
                float t = Clamp01(piece.Age / piece.Life);
                int alpha = (int)Math.Round(235 * (1f - t * t));
                if (alpha <= 0) continue;

                GraphicsState state = g.Save();
                g.TranslateTransform(piece.X, piece.Y);
                g.RotateTransform(piece.Angle);
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(alpha, piece.Color)))
                {
                    // 一半画成纸片，一半画成小圆点，旋转时更像真实纸屑。
                    if (i % 3 == 0)
                        g.FillEllipse(brush, -piece.Size / 2f, -piece.Size / 2f, piece.Size, piece.Size);
                    else
                        g.FillRectangle(brush, -piece.Size / 2f, -piece.Size / 4f, piece.Size, piece.Size / 2f);
                }
                g.Restore(state);
            }
        }

        private static string IdOf(ITaskItem item)
        {
            if (item == null || item.Id == null) return string.Empty;
            return item.Id;
        }

        private RowFx FxOf(string id)
        {
            RowFx fx;
            if (id == null) id = string.Empty;
            if (!_fx.TryGetValue(id, out fx))
            {
                fx = new RowFx();
                _fx[id] = fx;
            }
            return fx;
        }

        // ------------------------------------------------------------------
        // 命中测试（拖拽由 MainForm 复用）
        // ------------------------------------------------------------------
        public bool TryHitRow(Point p, out int index)
        {
            index = -1;
            if (p.Y < ListTop || p.Y >= ListBottom) return false;
            if (p.X < Scale(6) || p.X > Width - Scale(6)) return false;
            int i = IndexAtContent(p.Y - ListTop + ScrollTop);
            if (i < 0 || i >= _items.Count) return false;
            index = i;
            return true;
        }

        private int RawInsertIndex(Point p)
        {
            if (_items.Count == 0) return 0;
            int contentY = p.Y - ListTop + ScrollTop;
            for (int i = 0; i < _items.Count; i++)
            {
                if (contentY < _rowTops[i] + _rowHeights[i] / 2) return i;
            }
            return _items.Count;
        }

        private static int GroupOf(ITaskItem item)
        {
            TodoItem todo = item as TodoItem;
            if (todo == null) return 1;
            if (todo.Done) return 2;
            return todo.Star ? 0 : 1;
        }

        /// <summary>求出插入位置，并钳制到被拖条目所属分组区间内。</summary>
        public int GetInsertIndex(Point p, int dragIndex)
        {
            if (dragIndex < 0 || dragIndex >= _items.Count) return RawInsertIndex(p);
            return GetInsertIndex(p, _items[dragIndex]);
        }

        /// <summary>跨栏拖拽时也能按条目所属分组钳制落点，让指示线与真实结果一致。</summary>
        public int GetInsertIndex(Point p, ITaskItem dragged)
        {
            int index = RawInsertIndex(p);
            if (dragged == null) return index;

            int group = GroupOf(dragged);
            int start = 0;
            int end = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                int g = GroupOf(_items[i]);
                if (g < group) start++;
                if (g <= group) end++;
            }
            if (index < start) index = start;
            if (index > end) index = end;
            return index;
        }

        public void SetDropIndicator(int index)
        {
            if (_dropIndex == index) return;
            _dropIndex = index;
            Invalidate();
        }

        public void ClearDropIndicator()
        {
            if (_dropIndex < 0) return;
            _dropIndex = -1;
            Invalidate();
        }

        public void SetDragSource(int index)
        {
            _dragSource = index;
            Invalidate();
        }

        public void ClearDragSource()
        {
            _dragSource = -1;
            Invalidate();
        }

        public void ClearHover()
        {
            bool changed = _hover >= 0 || _hoverButton != TaskCommand.None || _titleHover || _splitHover;
            _hover = -1;
            _hoverButton = TaskCommand.None;
            _titleHover = false;
            _splitHover = false;
            if (changed)
            {
                EnsureAnimation();
                Invalidate();
            }
        }

        public void ClearPressedState()
        {
            _pressedButton = TaskCommand.None;
            _pressedIndex = -1;
        }

        public void NudgeScroll(int delta)
        {
            if (delta == 0) return;
            _scrollTarget += delta;
            ClampScroll();
            EnsureAnimation();
            Invalidate();
        }

        /// <summary>拖拽时贴近列表上下边缘自动滚动。</summary>
        public void AutoScrollForDrag(Point p)
        {
            int edge = Scale(24);
            if (p.Y < ListTop + edge) NudgeScroll(-Math.Max(Scale(6), MinRowHeight / 2));
            else if (p.Y > ListBottom - edge) NudgeScroll(Math.Max(Scale(6), MinRowHeight / 2));
        }

        // ------------------------------------------------------------------
        // 行内编辑
        // ------------------------------------------------------------------
        public void BeginEditSelected()
        {
            BeginEdit(_selected);
        }

        public void BeginEdit(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            EnsureVisible(index);

            Point p = PointToClient(Cursor.Position);
            int hit;
            if (TryHitRow(p, out hit) && hit == index)
            {
                Rectangle hr = RowBoundsCore(index);
                if (CheckRect(hr).Contains(p)) return;
                if (StarRect(hr).Contains(p)) return;
                if (DeleteRect(hr).Contains(p)) return;
            }

            _keyboardSelection = false;
            _editIndex = index;
            _selected = index;
            Rectangle row = RowBoundsCore(index);
            Rectangle textRect = TextRect(row);

            // 编辑框比文字高一些，并留出左右内边距，避免文字贴边显得拥挤
            int leftExt = Scale(10);
            int rightExt = Scale(4);
            int boxHeight = Math.Max(_editor.PreferredHeight, row.Height - Scale(8));
            _editor.Text = _items[index].Text;
            _editor.Bounds = new Rectangle(
                textRect.X - leftExt,
                row.Y + (row.Height - boxHeight) / 2,
                Math.Max(Scale(80), textRect.Width + leftExt + rightExt),
                boxHeight);
            _editor.Visible = true;
            _editor.BringToFront();
            ApplyEditorMargins();
            _editor.Focus();
            _editor.SelectAll();
            Invalidate();
        }

        private void ApplyEditorMargins()
        {
            try
            {
                int margin = Scale(8);
                IntPtr handle = _editor.Handle;
                IntPtr lParam = (IntPtr)((margin << 16) | (margin & 0xFFFF));
                SendMessage(handle, EM_SETMARGINS, (IntPtr)(EC_LEFTMARGIN | EC_RIGHTMARGIN), lParam);
            }
            catch (Exception)
            {
            }
        }

        private void EndEditing(bool commit)
        {
            if (_editIndex < 0 || _ending) return;
            _ending = true;
            int index = _editIndex;
            _editIndex = -1;
            string text = _editor.Text.Trim();
            bool hadFocus = _editor.Focused;
            _editor.Visible = false;
            _ending = false;

            if (commit && text.Length > 0 && index >= 0 && index < _items.Count && _items[index].Text != text)
            {
                if (TextEdited != null)
                {
                    TaskItemEventArgs args = new TaskItemEventArgs(_items[index], index);
                    args.Text = text;
                    TextEdited(this, args);
                }
            }

            if (hadFocus) this.Focus();
            Invalidate();
        }

        // ------------------------------------------------------------------
        // 标题就地改名
        // ------------------------------------------------------------------
        public void BeginEditTitle()
        {
            if (_editingTitle) return;
            if (_editIndex >= 0) EndEditing(true);

            _editingTitle = true;
            Rectangle rect = HeaderRect();
            _titleEditor.Text = _title;
            _titleEditor.Bounds = new Rectangle(
                rect.X - Scale(6),
                rect.Y - Scale(1),
                Math.Max(Scale(80), rect.Width + Scale(12)),
                Math.Max(_titleEditor.PreferredHeight, rect.Height + Scale(2)));
            _titleEditor.Visible = true;
            _titleEditor.BringToFront();
            _titleEditor.Focus();
            _titleEditor.SelectAll();
            Invalidate();
        }

        private void EndEditTitle(bool commit)
        {
            if (!_editingTitle) return;
            _editingTitle = false;
            string text = _titleEditor.Text == null ? string.Empty : _titleEditor.Text.Trim();
            _titleEditor.Visible = false;

            if (commit && text.Length > 0 && text != _title && TitleEdited != null)
                TitleEdited(this, new TextEventArgs(text));

            Invalidate();
        }

        private void TitleEditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                EndEditTitle(true);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                EndEditTitle(false);
            }
        }

        private void TitleEditorLostFocus(object sender, EventArgs e)
        {
            EndEditTitle(true);
        }

        // ------------------------------------------------------------------
        // 滚动
        // ------------------------------------------------------------------
        private void ClampScroll()
        {
            int view = ListBottom - ListTop;
            int content = _contentHeight;
            int max = content - view;
            if (max < 0) max = 0;
            if (_scrollTarget > max) _scrollTarget = max;
            if (_scrollTarget < 0) _scrollTarget = 0;
            if (Math.Abs(_scrollPos - _scrollTarget) < 0.5f) _scrollPos = _scrollTarget;
            else EnsureAnimation();
        }

        private void EnsureVisible(int index)
        {
            int view = ListBottom - ListTop;
            if (view <= 0) return;
            if (index < 0 || index >= _rowTops.Length) return;
            int top = _rowTops[index];
            int height = _rowHeights[index];
            if (top < _scrollTarget) _scrollTarget = top;
            else if (top + height > _scrollTarget + view) _scrollTarget = top + height - view;
            ClampScroll();
            EnsureAnimation();
        }

        // ------------------------------------------------------------------
        // 动效引擎
        // ------------------------------------------------------------------
        private void EnsureAnimation()
        {
            if (_animating || IsDisposed) return;
            _animating = true;
            _lastTick = _clock.ElapsedMilliseconds;
            _anim.Start();
        }

        private static float Approach(float current, float target, float dt, float tau, ref bool busy)
        {
            float delta = target - current;
            if (delta < 0f) delta = -delta;
            if (delta < 0.004f) return target;
            busy = true;
            return current + (target - current) * (1f - (float)Math.Exp(-dt / tau));
        }

        private static float ApproachPx(float current, float target, float dt, float tau, ref bool busy)
        {
            float delta = target - current;
            if (delta < 0f) delta = -delta;
            if (delta < 0.4f) return target;
            busy = true;
            return current + (target - current) * (1f - (float)Math.Exp(-dt / tau));
        }

        private void AnimTick(object sender, EventArgs e)
        {
            long now = _clock.ElapsedMilliseconds;
            float dt = (now - _lastTick) / 1000f;
            _lastTick = now;
            if (dt < 0.0005f) dt = 0.0005f;
            if (dt > 0.12f) dt = 0.12f;

            bool busy = false;

            if (Math.Abs(_scrollTarget - _scrollPos) > 0.4f)
            {
                _scrollPos += (_scrollTarget - _scrollPos) * (1f - (float)Math.Exp(-dt / TauScroll));
                busy = true;
            }
            else
            {
                _scrollPos = _scrollTarget;
            }

            for (int i = 0; i < _items.Count; i++)
            {
                ITaskItem item = _items[i];
                RowFx fx = FxOf(IdOf(item));
                TodoItem todo = item as TodoItem;

                fx.HoverT = Approach(fx.HoverT, i == _hover ? 1f : 0f, dt, TauHover, ref busy);
                fx.CheckT = Approach(fx.CheckT, (todo != null && todo.Done) ? 1f : 0f, dt, TauCheck, ref busy);
                fx.StarT = Approach(fx.StarT, (todo != null && todo.Star) ? 1f : 0f, dt, TauStar, ref busy);
                fx.Alpha = Approach(fx.Alpha, 1f, dt, TauAlpha, ref busy);
                fx.OffsetY = ApproachPx(fx.OffsetY, 0f, dt, TauOffset, ref busy);
            }

            for (int i = _ghosts.Count - 1; i >= 0; i--)
            {
                GhostRow ghost = _ghosts[i];
                ghost.Alpha = Approach(ghost.Alpha, 0f, dt, TauGhost, ref busy);
                if (ghost.Alpha <= 0.012f) _ghosts.RemoveAt(i);
            }

            for (int i = _ripples.Count - 1; i >= 0; i--)
            {
                Ripple ripple = _ripples[i];
                ripple.T += dt / 0.38f;
                if (ripple.T >= 1f) _ripples.RemoveAt(i);
                else busy = true;
            }

            UpdateConfetti(dt, ref busy);

            _activeT = Approach(_activeT, _active ? 1f : 0f, dt, TauTheme, ref busy);
            _dragOverT = Approach(_dragOverT, _draggingOver ? 1f : 0f, dt, TauTheme, ref busy);

            Invalidate();

            if (!busy)
            {
                _animating = false;
                _anim.Stop();
            }
        }

        // ------------------------------------------------------------------
        // 绘制辅助
        // ------------------------------------------------------------------
        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }

        private static Color Mix(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            int r = (int)Math.Round(a.R + (b.R - a.R) * t);
            int g = (int)Math.Round(a.G + (b.G - a.G) * t);
            int bl = (int)Math.Round(a.B + (b.B - a.B) * t);
            return Color.FromArgb(r, g, bl);
        }

        /// <summary>把颜色按不透明度压向卡片底色（TextRenderer 不支持半透明）。</summary>
        private static Color Fade(Color color, float alpha)
        {
            if (alpha >= 0.995f) return color;
            return Mix(Ui.CardBack, color, Clamp01(alpha));
        }

        private SolidBrush Brush(Color color)
        {
            int key = color.ToArgb();
            SolidBrush brush;
            if (_brushes.TryGetValue(key, out brush)) return brush;
            brush = new SolidBrush(color);
            _brushes[key] = brush;
            return brush;
        }

        private void FillRounded(Graphics g, Rectangle rect, Color color)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using (GraphicsPath path = Ui.RoundRect(rect, Scale(6)))
                g.FillPath(Brush(color), path);
        }

        private static float EaseOutCubic(float t)
        {
            float inv = 1f - Clamp01(t);
            return 1f - inv * inv * inv;
        }

        private static GraphicsPath StarPath(Rectangle box)
        {
            GraphicsPath path = new GraphicsPath();
            if (box.Width <= 0 || box.Height <= 0) return path;

            float cx = box.X + box.Width / 2f;
            float cy = box.Y + box.Height / 2f;
            float outer = Math.Min(box.Width, box.Height) / 2f;
            float inner = outer * 0.44f;

            PointF[] points = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double angle = -Math.PI / 2 + i * Math.PI / 5;
                float radius = (i % 2 == 0) ? outer : inner;
                points[i] = new PointF(
                    cx + (float)(Math.Cos(angle) * radius),
                    cy + (float)(Math.Sin(angle) * radius));
            }
            path.AddPolygon(points);
            path.CloseFigure();
            return path;
        }

        // ------------------------------------------------------------------
        // 绘制
        // ------------------------------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (SolidBrush bg = new SolidBrush(Ui.CardBack))
                g.FillRectangle(bg, ClientRectangle);

            float borderT = Clamp01(_activeT);
            if (_dragOverT > 0.01f) borderT = Math.Min(1f, borderT + 0.6f * _dragOverT);
            Color border = Mix(Ui.CardBorder, Ui.InputBorder, borderT);
            using (GraphicsPath path = Ui.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Scale(8)))
            using (Pen pen = new Pen(border, 1f))
            {
                g.DrawPath(pen, path);
            }

            PaintHeader(g);
            PaintRows(g);
            PaintGhosts(g);
            PaintRipples(g);
            PaintConfetti(g);
            PaintIndicator(g);
        }

        private void PaintHeader(Graphics g)
        {
            if (!_editingTitle)
            {
                Rectangle rect = HeaderRect();
                TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                                      | TextFormatFlags.NoPrefix;
                Color titleColor = Mix(Ui.TextMuted, Ui.TextPrimary, Clamp01(_activeT));
                TextRenderer.DrawText(g, _title, Ui.Title, rect, titleColor, flags);

                if (_titleHover)
                {
                    int width = Math.Min(rect.Width, TextRenderer.MeasureText(_title, Ui.Title).Width);
                    int y = rect.Bottom - Scale(1);
                    using (Pen pen = new Pen(Fade(Ui.TextFaint, 0.9f), 1f))
                        g.DrawLine(pen, rect.X, y, rect.X + width, y);
                }
            }

            Rectangle countRect = new Rectangle(Pad, HeaderTop, Math.Max(0, Width - Pad * 2), HeaderHeight);
            TextFormatFlags countFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                                       | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            Color countColor = Mix(Ui.TextFaint, Ui.TextMuted, Clamp01(_activeT));
            TextRenderer.DrawText(g, _count.ToString(), Ui.Small, countRect, countColor, countFlags);

            if (_canSplit)
            {
                Rectangle splitRect = HeaderSplitRect();
                TextFormatFlags splitFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                                           | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                Color splitColor = _splitHover
                    ? Mix(Ui.TextMuted, Ui.TextPrimary, 0.35f)
                    : Mix(Ui.TextFaint, Ui.TextMuted, Clamp01(_activeT) * 0.7f);
                TextRenderer.DrawText(g, "\u62c6\u5206", Ui.Small, splitRect, splitColor, splitFlags);
            }
        }

        private void PaintRows(Graphics g)
        {
            int top = ListTop;
            int bottom = ListBottom;
            if (bottom <= top) return;

            GraphicsState state = g.Save();
            g.SetClip(new Rectangle(0, top, Width, bottom - top));

            if (_dragOverT > 0.01f)
            {
                Rectangle area = new Rectangle(Scale(6), top, Math.Max(0, Width - Scale(12)), bottom - top);
                FillRounded(g, area, Mix(Ui.CardBack, Ui.StarRowTint, 0.85f * _dragOverT));
            }

            if (_items.Count == 0)
            {
                PaintEmpty(g, top, bottom);
            }
            else
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    Rectangle row = RowBoundsCore(i);
                    RowFx fx;
                    if (_fx.TryGetValue(IdOf(_items[i]), out fx))
                        row.Y += (int)Math.Round(fx.OffsetY);

                    if (row.Y >= bottom) break;
                    if (row.Y + row.Height <= top) continue;
                    PaintRow(g, i, row);
                }
            }

            g.Restore(state);
        }

        private void PaintEmpty(Graphics g, int top, int bottom)
        {
            int h = bottom - top;
            Rectangle area = new Rectangle(Pad, top, Math.Max(0, Width - Pad * 2), h);
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                  | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int offset = Scale(8);
            Rectangle main = new Rectangle(area.X, area.Y - offset, area.Width, area.Height);
            TextRenderer.DrawText(g, "\u6682\u65e0\u5f85\u529e", Ui.Empty, main, Ui.TextFaint, flags);
        }

        private void PaintRow(Graphics g, int index, Rectangle row)
        {
            ITaskItem item = _items[index];
            RowFx fx;
            if (!_fx.TryGetValue(IdOf(item), out fx)) fx = new RowFx();

            float alpha = Clamp01(fx.Alpha);
            if (alpha <= 0.01f) return;

            TodoItem todo = item as TodoItem;
            bool done = todo != null && todo.Done;
            bool starred = todo != null && todo.Star;
            bool hovered = index == _hover;

            if (starred && !done && fx.StarT > 0.01f)
                FillRounded(g, row, Mix(Ui.CardBack, Ui.StarRowTint, 0.85f * Clamp01(fx.StarT)));
            if (fx.HoverT > 0.01f)
                FillRounded(g, row, Mix(Ui.CardBack, Ui.RowHover, Clamp01(fx.HoverT)));

            bool selected = index == _selected && _keyboardSelection && Focused;
            if (selected)
                FillRounded(g, row, Ui.RowSelectSoft);

            if (starred && !done && fx.StarT > 0.01f)
            {
                int barW = Math.Max(Scale(2), 2);
                int barH = Math.Max(Scale(4), row.Height - Scale(14));
                Rectangle bar = new Rectangle(row.X + Scale(1), row.Y + (row.Height - barH) / 2, barW, barH);
                FillRounded(g, bar, Fade(Ui.Star, Clamp01(fx.StarT) * alpha));
            }

            if (selected)
            {
                int barW = Math.Max(Scale(2), 2);
                int barH = Math.Max(Scale(4), row.Height - Scale(16));
                Rectangle bar = new Rectangle(row.X + Scale(1), row.Y + (row.Height - barH) / 2, barW, barH);
                FillRounded(g, bar, Fade(Ui.Indicator, 0.55f));
            }

            PaintCheckbox(g, row, done, fx.CheckT, alpha);

            Rectangle textRect = TextRect(row);
            Color textColor = done ? Ui.TextDone : Ui.TextPrimary;
            if (index == _dragSource) textColor = Ui.TextFaint;
            Font font = done ? Ui.BodyStrike : Ui.Body;
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.Top
                                  | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, item.Text, font, textRect, Fade(textColor, alpha), flags);

            PaintStar(g, StarRect(row), fx.StarT, hovered || selected, alpha);

            Color deleteColor = (_hoverButton == TaskCommand.Delete && hovered)
                ? Ui.Danger
                : ((hovered || selected) ? Ui.TextMuted : Ui.TextFaint);
            PaintGlyph(g, DeleteRect(row), "\u00d7", Fade(deleteColor, alpha));
        }

        private void PaintCheckbox(Graphics g, Rectangle row, bool done, float progress, float alpha)
        {
            float p = Clamp01(progress);
            Rectangle box = CheckRect(row);
            float scale = 0.92f + 0.08f * p;
            int w = Math.Max(1, (int)Math.Round(box.Width * scale));
            int h = Math.Max(1, (int)Math.Round(box.Height * scale));
            Rectangle rect = new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);

            using (GraphicsPath path = Ui.RoundRect(rect, Scale(4)))
            {
                if (p > 0.02f)
                {
                    Color fill = Mix(Ui.CardBack, Ui.TextPrimary, p);
                    g.FillPath(Brush(Fade(fill, alpha)), path);
                }

                if (p < 0.98f)
                {
                    Color stroke = Mix(Ui.CheckBorder, Ui.TextPrimary, p);
                    using (Pen pen = new Pen(Fade(stroke, alpha * (1f - p * 0.9f)), 1f))
                        g.DrawPath(pen, path);
                }

                if (p > 0.32f)
                {
                    float cp = (p - 0.32f) / 0.68f;
                    using (Pen pen = new Pen(Fade(Color.White, alpha * Clamp01(cp)), Math.Max(1.4f, Scale(2) * 0.8f)))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;
                        int x = rect.X;
                        int y = rect.Y;
                        int bw = rect.Width;
                        int bh = rect.Height;
                        g.DrawLines(pen, new Point[]
                        {
                            new Point(x + (int)(bw * 0.26), y + (int)(bh * 0.52)),
                            new Point(x + (int)(bw * 0.43), y + (int)(bh * 0.70)),
                            new Point(x + (int)(bw * 0.75), y + (int)(bh * 0.32))
                        });
                    }
                }
            }
        }

        private void PaintStar(Graphics g, Rectangle box, float progress, bool hovered, float alpha)
        {
            // 点击区域与删除按钮一致，图形本身再缩小一点，视觉上与 × 大小相当。
            int inset = Scale(3);
            box.Inflate(-inset, -inset);

            float t = Clamp01(progress);
            float pop = 1f + 0.16f * (float)Math.Sin(Math.PI * t);
            int w = Math.Max(1, (int)Math.Round(box.Width * pop));
            int h = Math.Max(1, (int)Math.Round(box.Height * pop));
            Rectangle rect = new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);

            using (GraphicsPath path = StarPath(rect))
            {
                if (t > 0.02f)
                    g.FillPath(Brush(Fade(Mix(Ui.StarIdle, Ui.Star, t), alpha)), path);

                if (t < 0.98f)
                {
                    Color stroke = hovered ? Ui.TextMuted : Ui.StarIdle;
                    using (Pen pen = new Pen(Fade(stroke, alpha), 1f))
                        g.DrawPath(pen, path);
                }
            }
        }

        private void PaintGlyph(Graphics g, Rectangle rect, string glyph, Color color)
        {
            if (string.IsNullOrEmpty(glyph)) return;
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                  | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, glyph, Ui.Body, rect, color, flags);
        }

        private void PaintGhosts(Graphics g)
        {
            if (_ghosts.Count == 0) return;
            int top = ListTop;
            int bottom = ListBottom;
            if (bottom <= top) return;

            GraphicsState state = g.Save();
            g.SetClip(new Rectangle(0, top, Width, bottom - top));

            for (int i = 0; i < _ghosts.Count; i++)
            {
                GhostRow ghost = _ghosts[i];
                float a = Clamp01(ghost.Alpha);
                if (a <= 0.01f) continue;

                int ghostHeight = ghost.Height > 0 ? ghost.Height : MinRowHeight;
                Rectangle row = new Rectangle(Scale(10), (int)Math.Round(ghost.Top), Math.Max(0, Width - Scale(20)), ghostHeight);
                if (row.Y + row.Height < top || row.Y > bottom) continue;

                if (ghost.Star && !ghost.Done)
                    FillRounded(g, row, Mix(Ui.CardBack, Ui.StarRowTint, 0.85f * a));

                PaintCheckbox(g, row, ghost.Done, ghost.Done ? 1f : 0f, a);

                Rectangle textRect = TextRect(row);
                Color textColor = ghost.Done ? Ui.TextDone : Ui.TextPrimary;
                TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.Top
                                      | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(g, ghost.Text, ghost.Done ? Ui.BodyStrike : Ui.Body,
                    textRect, Fade(textColor, a), flags);
            }

            g.Restore(state);
        }

        private void PaintRipples(Graphics g)
        {
            if (_ripples.Count == 0) return;
            int top = ListTop;
            int bottom = ListBottom;
            if (bottom <= top) return;

            GraphicsState state = g.Save();
            g.SetClip(new Rectangle(0, top, Width, bottom - top));

            for (int i = 0; i < _ripples.Count; i++)
            {
                Ripple ripple = _ripples[i];
                float p = EaseOutCubic(ripple.T);
                int radius = (int)Math.Round(Scale(90) * p);
                int a = (int)Math.Round(15 * (1f - Clamp01(ripple.T)));
                if (radius <= 0 || a <= 0) continue;

                GraphicsState inner = g.Save();
                using (GraphicsPath path = Ui.RoundRect(ripple.Bounds, Scale(6)))
                {
                    g.SetClip(path, CombineMode.Intersect);
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(a, Ui.Ripple)))
                        g.FillEllipse(brush, ripple.X - radius, ripple.Y - radius, radius * 2, radius * 2);
                }
                g.Restore(inner);
            }

            g.Restore(state);
        }

        private void PaintIndicator(Graphics g)
        {
            if (_dropIndex < 0) return;
            int top = ListTop;
            int bottom = ListBottom;
            if (bottom <= top) return;

            int insertTop = _dropIndex < _rowTops.Length ? _rowTops[_dropIndex] : _contentHeight;
            int y = ListTop - ScrollTop + insertTop;
            int maxY = Math.Min(bottom, ListTop + Math.Max(0, _contentHeight - ScrollTop));
            if (maxY < top) maxY = top;
            if (y < top) y = top;
            if (y > maxY) y = maxY;

            using (Pen pen = new Pen(Ui.Indicator, Scale(2)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, Scale(14), y, Width - Scale(14), y);
            }
        }

        // ------------------------------------------------------------------
        // 鼠标
        // ------------------------------------------------------------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            _pressedButton = TaskCommand.None;
            _pressedIndex = -1;
            _keyboardSelection = false;

            if (e.Button == MouseButtons.Left)
            {
                if (_editingTitle)
                {
                    Rectangle tr = _titleEditor.Bounds;
                    if (!tr.Contains(e.Location)) EndEditTitle(true);
                }

                if (_editIndex >= 0)
                {
                    Rectangle er = _editor.Bounds;
                    if (!er.Contains(e.Location)) EndEditing(true);
                }

                if (_canSplit && HeaderSplitRect().Contains(e.Location))
                {
                    _splitPressed = true;
                    Capture = true;
                    Invalidate();
                    base.OnMouseDown(e);
                    return;
                }

                int index;
                if (TryHitRow(e.Location, out index))
                {
                    Focus();
                    _selected = index;
                    Rectangle row = RowBoundsCore(index);
                    if (CheckRect(row).Contains(e.Location))
                    {
                        _pressedButton = TaskCommand.Toggle;
                        _pressedIndex = index;
                    }
                    else if (DeleteRect(row).Contains(e.Location))
                    {
                        _pressedButton = TaskCommand.Delete;
                        _pressedIndex = index;
                    }
                    else if (StarRect(row).Contains(e.Location))
                    {
                        _pressedButton = TaskCommand.Star;
                        _pressedIndex = index;
                    }

                    AddRipple(row, e.Location);
                }

                Capture = true;
            }

            Invalidate();
            base.OnMouseDown(e);
        }

        private void AddRipple(Rectangle row, Point p)
        {
            if (_ripples.Count >= 3) _ripples.RemoveAt(0);
            Ripple ripple = new Ripple();
            ripple.Bounds = row;
            ripple.X = p.X;
            ripple.Y = p.Y;
            ripple.T = 0f;
            _ripples.Add(ripple);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            int index;
            bool overRow = TryHitRow(e.Location, out index);
            int newHover = overRow ? index : -1;
            TaskCommand newButton = overRow ? HitButton(index, e.Location) : TaskCommand.None;
            bool overSplit = _canSplit && HeaderSplitRect().Contains(e.Location);
            bool overHeader = !_editingTitle && !overSplit && HeaderRect().Contains(e.Location);

            bool changed = newHover != _hover || newButton != _hoverButton
                         || overHeader != _titleHover || overSplit != _splitHover;
            _hover = newHover;
            _hoverButton = newButton;
            _titleHover = overHeader;
            _splitHover = overSplit;

            if (!DragActive)
            {
                if (overSplit) Cursor = Cursors.Hand;
                else if (overHeader) Cursor = Cursors.IBeam;
                else Cursor = newButton != TaskCommand.None ? Cursors.Hand : Cursors.Default;
            }

            if (changed)
            {
                EnsureAnimation();
                Invalidate();
            }
        }

        private TaskCommand HitButton(int index, Point p)
        {
            if (index < 0 || index >= _items.Count) return TaskCommand.None;
            Rectangle row = RowBoundsCore(index);
            if (CheckRect(row).Contains(p)) return TaskCommand.Toggle;
            if (DeleteRect(row).Contains(p)) return TaskCommand.Delete;
            if (StarRect(row).Contains(p)) return TaskCommand.Star;
            return TaskCommand.None;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (Capture) Capture = false;

            if (_splitPressed)
            {
                _splitPressed = false;
                if (_canSplit && HeaderSplitRect().Contains(e.Location) && SplitRequested != null)
                    SplitRequested(this, EventArgs.Empty);
                Invalidate();
                base.OnMouseUp(e);
                return;
            }

            TaskCommand command = _pressedButton;
            int pressedIndex = _pressedIndex;
            _pressedButton = TaskCommand.None;
            _pressedIndex = -1;

            if (command != TaskCommand.None)
            {
                int index;
                if (TryHitRow(e.Location, out index) && index == pressedIndex && index < _items.Count && Command != null)
                {
                    TaskItemEventArgs args = new TaskItemEventArgs(_items[index], index);
                    args.Command = command;
                    Command(this, args);
                }
                Invalidate();
            }

            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (Capture) return;
            _hover = -1;
            _hoverButton = TaskCommand.None;
            _titleHover = false;
            _splitHover = false;
            Cursor = Cursors.Default;
            EnsureAnimation();
            Invalidate();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            if (DragActive) return;

            Point p = PointToClient(Cursor.Position);
            if (_canSplit && HeaderSplitRect().Contains(p)) return;
            if (HeaderRect().Contains(p))
            {
                BeginEditTitle();
                return;
            }

            int index;
            if (!TryHitRow(p, out index)) return;
            Rectangle row = RowBoundsCore(index);
            if (CheckRect(row).Contains(p)) return;
            if (StarRect(row).Contains(p)) return;
            if (DeleteRect(row).Contains(p)) return;
            BeginEdit(index);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _keyboardSelection = false;
            _scrollTarget -= (e.Delta / 120) * MinRowHeight * 2;
            ClampScroll();
            EnsureAnimation();
            Invalidate();
        }

        // ------------------------------------------------------------------
        // 键盘
        // ------------------------------------------------------------------
        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Down) MoveSelection(1);
            else if (e.KeyCode == Keys.Up) MoveSelection(-1);
        }

        private void MoveSelection(int delta)
        {
            if (_items.Count == 0) return;
            int next = _selected < 0 ? (delta > 0 ? 0 : _items.Count - 1) : _selected + delta;
            if (next < 0) next = 0;
            if (next >= _items.Count) next = _items.Count - 1;
            _selected = next;
            _keyboardSelection = true;
            EnsureVisible(next);
            EnsureAnimation();
            Invalidate();
        }

        private void EditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                EndEditing(true);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                EndEditing(false);
            }
        }

        private void EditorLostFocus(object sender, EventArgs e)
        {
            if (_ending) return;
            EndEditing(true);
        }
    }
}
