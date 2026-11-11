using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace TodoLite
{
    /// <summary>
    /// 收件箱卡片：一整块多行文本输入区 + 右下角「记下」。
    /// Enter 提交（Shift+Enter 换行），提交时整段文本压成一条交给上层。
    /// </summary>
    public class InboxCompose : Control
    {
        private static readonly Regex Whitespace = new Regex("\\s+", RegexOptions.Compiled);

        private readonly SoftTextBox _box;
        private string _title = "\u6536\u4ef6\u7bb1";
        private string _targetPaneName = "TODO";
        private string _buttonText = "\u8bb0\u4e0b";

        private bool _buttonHover;
        private bool _buttonPressed;
        private bool _loading;

        /// <summary>提交请求，参数是规范化后的单行文本。</summary>
        public event EventHandler<TextEventArgs> SubmitRequested;

        /// <summary>用户输入发生变化（用于草稿保存）。</summary>
        public event EventHandler DraftChanged;

        public InboxCompose()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.CardBack;
            TabStop = false;

            _box = new SoftTextBox();
            _box.Multiline = true;
            _box.WordWrap = true;
            _box.ScrollBars = ScrollBars.None;
            _box.BorderStyle = BorderStyle.None;
            _box.Font = Ui.Body;
            _box.ForeColor = Ui.TextPrimary;
            _box.BackColor = Ui.CardBack;
            _box.ShortcutsEnabled = true;
            _box.KeyDown += BoxKeyDown;
            _box.TextChanged += BoxTextChanged;
            _box.GotFocus += BoxFocusChanged;
            _box.LostFocus += BoxFocusChanged;
            Controls.Add(_box);
        }

        // ------------------------------------------------------------------
        // 公开属性
        // ------------------------------------------------------------------
        public string Title
        {
            get { return _title; }
            set { _title = value == null ? string.Empty : value; Invalidate(); }
        }

        /// <summary>新条目将进入的栏名，用于空态提示。</summary>
        public string TargetPaneName
        {
            get { return _targetPaneName; }
            set
            {
                string v = value == null || value.Length == 0 ? "TODO" : value;
                if (_targetPaneName == v) return;
                _targetPaneName = v;
                Invalidate();
            }
        }

        public string DraftText
        {
            get { return _box.Text; }
        }

        public bool TextHasFocus
        {
            get { return _box.Focused; }
        }

        /// <summary>初始化草稿，不触发 DraftChanged。</summary>
        public void SetDraft(string text)
        {
            _loading = true;
            _box.Text = NormalizeNewlines(text);
            _loading = false;
            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;
        }

        public void FocusCompose()
        {
            _box.Focus();
            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;
        }

        // ------------------------------------------------------------------
        // 尺寸与布局
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
        private int FooterHeight { get { return Scale(26); } }
        private int FooterTop { get { return Height - Scale(14) - FooterHeight; } }
        private int BoxTop { get { return HeaderBottom + Scale(10); } }

        private Rectangle BoxRect()
        {
            int w = Width - Pad * 2;
            int h = FooterTop - Scale(10) - BoxTop;
            if (h < Scale(40)) h = Scale(40);
            return new Rectangle(Pad, BoxTop, Math.Max(0, w), h);
        }

        private Rectangle ButtonRect()
        {
            int w = TextRenderer.MeasureText(_buttonText, Ui.Body).Width + Scale(22);
            return new Rectangle(Width - Pad - w, FooterTop, w, FooterHeight);
        }

        private void LayoutBox()
        {
            Rectangle r = BoxRect();
            _box.Bounds = r;
            _box.Visible = r.Width > 0 && r.Height > 0;
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
            LayoutBox();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
            LayoutBox();
            Invalidate();
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

            Color border = _box.Focused ? Ui.InputBorder : Ui.CardBorder;
            using (GraphicsPath path = Ui.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Scale(8)))
            using (Pen pen = new Pen(border, 1f))
            {
                g.DrawPath(pen, path);
            }

            Rectangle header = new Rectangle(Pad, HeaderTop, Math.Max(0, Width - Pad * 2), HeaderHeight);
            TextFormatFlags titleFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                       | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                                       | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, _title, Ui.Title, header, Ui.TextPrimary, titleFlags);

            if (_box.TextLength == 0)
            {
                Rectangle left = new Rectangle(Pad, FooterTop, Math.Max(0, ButtonRect().X - Pad - Scale(8)), FooterHeight);
                // 输入框获得焦点时提示右对齐，避免和左侧光标重叠。
                TextFormatFlags emptyFlags = (_box.Focused ? TextFormatFlags.Right : TextFormatFlags.Left)
                                           | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                                           | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                string hint = "\u5199\u4e0b\u8981\u505a\u7684\u4e8b\uff0c\u6309 Enter \u8bb0\u5165\u300c" + _targetPaneName + "\u300d";
                TextRenderer.DrawText(g, hint, Ui.Small, left, Ui.TextFaint, emptyFlags);
            }

            Color buttonColor = (_buttonHover || _buttonPressed) ? Ui.TextPrimary : Ui.TextMuted;
            TextFormatFlags buttonFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, _buttonText, Ui.Body, ButtonRect(), buttonColor, buttonFlags);
        }

        // ------------------------------------------------------------------
        // 鼠标
        // ------------------------------------------------------------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (ButtonRect().Contains(e.Location))
            {
                _buttonPressed = true;
                Capture = true;
                Invalidate();
                return;
            }
            _box.Focus();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hover = ButtonRect().Contains(e.Location);
            if (hover != _buttonHover)
            {
                _buttonHover = hover;
                Cursor = hover ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool pressed = _buttonPressed;
            _buttonPressed = false;
            if (Capture) Capture = false;
            if (pressed && ButtonRect().Contains(e.Location)) Submit();
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _buttonHover = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        // ------------------------------------------------------------------
        // 输入
        // ------------------------------------------------------------------
        private void BoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift && !e.Alt)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                Submit();
            }
        }

        private void BoxTextChanged(object sender, EventArgs e)
        {
            _box.ScrollCaretIntoView();
            Invalidate();
            if (_loading) return;
            if (DraftChanged != null) DraftChanged(this, EventArgs.Empty);
        }

        private void BoxFocusChanged(object sender, EventArgs e)
        {
            Invalidate();
        }

        private void Submit()
        {
            string text = Normalize(_box.Text);
            if (text.Length == 0)
            {
                _box.Focus();
                return;
            }

            // 先清空：TextChanged 会先把草稿置空，随后上层新增 TODO 并保存，一次落盘。
            _box.Clear();
            if (SubmitRequested != null) SubmitRequested(this, new TextEventArgs(text));
            _box.Focus();
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return Whitespace.Replace(text, " ").Trim();
        }

        /// <summary>把任意换行写法统一成 CRLF，免得从文件读回的草稿挤成一行。</summary>
        private static string NormalizeNewlines(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string unified = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return unified.Replace("\n", "\r\n");
        }

        /// <summary>
        /// 不显示滚动条的多行输入框：自己处理滚轮，并保证光标始终在可见区域内，
        /// 长文本也不会失去可读性。
        /// </summary>
        private class SoftTextBox : TextBox
        {
            private const int WM_MOUSEWHEEL = 0x020A;
            private const int EM_LINESCROLL = 0x00B6;
            private const int EM_SCROLLCARET = 0x00B7;

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_MOUSEWHEEL)
                {
                    int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                    int lines = -(delta / 120) * 3;
                    if (lines != 0)
                    {
                        SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)lines);
                        return;
                    }
                }
                base.WndProc(ref m);
            }

            public void ScrollCaretIntoView()
            {
                if (!IsHandleCreated) return;
                SendMessage(Handle, EM_SCROLLCARET, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }
}
