using System.Drawing.Drawing2D;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI.Panels;

public sealed partial class FilesPanel
{
    internal sealed class FileMenuColorTable : ProfessionalColorTable
    {
        private readonly Func<UiPalette> _palette;
        public FileMenuColorTable(Func<UiPalette>? palette = null) => _palette = palette ?? (() => ModernUi.Palette);
        public override Color ToolStripDropDownBackground => _palette().Surface;
        // Windows 11 draws its own outline around a rounded menu window; a square inner border would
        // show at the corners. Hover is a soft fill without an accent outline, as in Win11 menus.
        public override Color MenuBorder => _palette().IsHighContrast ? _palette().Border : WindowsAppearance.IsWindows11 ? _palette().Surface : _palette().Border;
        public override Color MenuItemBorder => _palette().IsHighContrast ? _palette().Border : _palette().SurfaceRaised;
        public override Color MenuItemSelected => _palette().IsHighContrast ? SystemColors.Highlight : _palette().SurfaceRaised;
        public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override Color ImageMarginGradientBegin => _palette().Surface;
        public override Color ImageMarginGradientMiddle => _palette().Surface;
        public override Color ImageMarginGradientEnd => _palette().Surface;
        public override Color SeparatorDark => _palette().Border;
        public override Color SeparatorLight => _palette().Surface;
    }

    /// <summary>效果图的分段筛选「全部 | 图片 | 视频 | 文档」：一个圆角容器，选中段填主蓝，未选中段之间
    /// 有细分隔线。左右方向键切换。</summary>
    private sealed class SegmentedFilter : Control
    {
        private readonly string[] _labels;
        private int _selectedIndex;

        public event EventHandler? SelectedIndexChanged;

        public SegmentedFilter(string[] labels)
        {
            _labels = labels;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            Font = new Font(ModernUi.UiFont, 10F);
            AccessibleRole = AccessibleRole.PageTabList;
            AccessibleName = "文件类型筛选";
        }

        /// <summary>Narrow-window fallback: segments may shrink to fit just their two-character labels.</summary>
        public bool Compact { get; set; }

        public int MinimumWidth => _labels.Length * (Compact ? 44 : 56);
        public int MaximumWidth => _labels.Length * 76;

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                value = Math.Clamp(value, 0, _labels.Length - 1);
                if (value == _selectedIndex) return;
                _selectedIndex = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private Rectangle Segment(int index)
        {
            var inner = Rectangle.Inflate(ClientRectangle, -3, -3);
            int left = inner.Left + inner.Width * index / _labels.Length;
            int right = inner.Left + inner.Width * (index + 1) / _labels.Length;
            return Rectangle.FromLTRB(left, inner.Top, right, inner.Bottom);
        }

        protected override void OnPaintBackground(PaintEventArgs e) =>
            ButtonRenderer.DrawParentBackground(e.Graphics, ClientRectangle, this);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var outer = Rectangle.Inflate(ClientRectangle, -1, -1);
            outer.Width -= 1;
            outer.Height -= 1;
            using (var path = RoundedCard(outer, 8))
            using (var fill = new SolidBrush(ModernUi.Surface))
            using (var border = new Pen(ModernUi.Border))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using var divider = new Pen(ModernUi.Border);
            for (int i = 0; i < _labels.Length; i++)
            {
                var segment = Segment(i);
                bool selected = i == _selectedIndex;
                if (selected)
                {
                    using var path = RoundedCard(segment, 6);
                    using var accent = new SolidBrush(ModernUi.Accent);
                    g.FillPath(accent, path);
                }
                else if (i > 0 && i - 1 != _selectedIndex)
                {
                    g.DrawLine(divider, segment.Left, segment.Top + 6, segment.Left, segment.Bottom - 6);
                }
                TextRenderer.DrawText(g, _labels[i], Font, segment, selected ? ModernUi.AccentForeground : ModernUi.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(Segment(_selectedIndex), -3, -3));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            for (int i = 0; i < _labels.Length; i++)
                if (Segment(i).Contains(e.Location)) SelectedIndex = i;
        }

        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Left or Keys.Right || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Left or Keys.Right)
            {
                SelectedIndex += e.KeyCode == Keys.Left ? -1 : 1;
                e.Handled = true;
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }
    /// <summary>「搜索文件名」: a borderless text box inside a rounded field with a magnifier glyph.</summary>
    private sealed class SearchBox : Control
    {
        private readonly TextBox _text;

        public SearchBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            _text = ModernUi.SetBackgroundRole(ModernUi.SetForegroundRole(new TextBox
            {
                BorderStyle = BorderStyle.None, AccessibleName = "搜索文件名",
                Font = new Font(ModernUi.UiFont, 10F), PlaceholderText = "搜索文件名",
            }, UiForegroundRole.Text), UiBackgroundRole.Surface);
            _text.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
            _text.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                _text.Clear();
                e.SuppressKeyPress = true;
            };
            Controls.Add(_text);
            Cursor = Cursors.IBeam;
            AccessibleName = "搜索文件名";
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string Text
        {
            get => _text?.Text ?? "";
            set { if (_text != null) _text.Text = value; }
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _text.Focus(); }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_text == null) return;
            int left = 30;
            _text.Bounds = new Rectangle(left, (Height - _text.PreferredHeight) / 2, Math.Max(10, Width - left - 10), _text.PreferredHeight);
        }

        protected override void OnPaintBackground(PaintEventArgs e) =>
            ButtonRenderer.DrawParentBackground(e.Graphics, ClientRectangle, this);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = RoundedCard(rect, 8))
            using (var fill = new SolidBrush(ModernUi.Surface))
            using (var border = new Pen(_text.Focused ? ModernUi.Accent : ModernUi.Border))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using var pen = new Pen(ModernUi.Muted, 1.6F);
            int cy = Height / 2;
            g.DrawEllipse(pen, 10, cy - 7, 10, 10);
            g.DrawLine(pen, 18.5F, cy + 1.5F, 22F, cy + 5F);
        }
    }
}
