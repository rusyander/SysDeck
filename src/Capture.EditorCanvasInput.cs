// SysDeck — «Захват»: холст редактора — мышь, кадрирование, текст, стиль и история.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SysDeck.Capture
{
    internal sealed partial class EditorCanvas : Control
    {
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_textBox != null) CommitText();
            if (!Focused && WindowActive) Focus();
            PointF image = ToImage(e.Location);
            _downClient = e.Location;
            if (_drag != Drag.None) return;
            if (!_inline && (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && _space)))
            {
                _drag = Drag.Pan;
                _panMouse = e.Location;
                _panStart = _pan;
                Capture = true;
                UpdateCursor(e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            Capture = true;
            if (_style.Tool == EditTool.Crop) { CropDown(e, image); return; }

            CapShape owner;
            int handle = HandleAt(e.Location, out owner);
            if (handle >= 0) { _selected = owner; BeginEdit(Drag.Handle, image, handle); return; }
            switch (_style.Tool)
            {
                case EditTool.Select:
                {
                    CapShape hit = ShapeAt(image);
                    _selected = hit;
                    if (hit != null)
                    {
                        TextShape text = hit as TextShape;
                        if (e.Clicks >= 2 && text != null) { Capture = false; BeginText(text, PointF.Empty); return; }
                        BeginEdit(Drag.Move, image, -1);
                    }
                    RaiseChanged();
                    return;
                }
                case EditTool.Text:
                {
                    Capture = false;
                    BeginText(ShapeAt(image) as TextShape, image);
                    return;
                }
                case EditTool.Step:
                {
                    Capture = false;
                    StepShape step = new StepShape();
                    ApplyStyleTo(step);
                    step.Center = image;
                    EditSnapshot before = _doc.Capture();
                    step.Number = _doc.NextStep;
                    _doc.Shapes.Add(step);
                    _doc.NextStep++;
                    _doc.Commit(before);
                    _selected = step;
                    RaiseChanged();
                    return;
                }
            }
            _selected = null;
            _live = NewShape(_style.Tool, image);
            if (_live == null) { Capture = false; return; }
            _drag = Drag.Draw;
            _dragStart = image;
            Invalidate();
        }

        private void BeginEdit(Drag drag, PointF image, int handle)
        {
            _before = _doc.Capture();
            _drag = drag;
            _dragStart = image;
            _lastImage = image;
            _handle = handle;
            _moved = false;
            RectOf(_selected, out _startRect);
            LineShape line = _selected as LineShape;
            if (line != null) { _startA = line.A; _startB = line.B; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            PointF image = ToImage(e.Location);
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            switch (_drag)
            {
                case Drag.Pan:
                    _pan = new PointF(_panStart.X + e.X - _panMouse.X, _panStart.Y + e.Y - _panMouse.Y);
                    ClampPan();
                    RaiseView();
                    return;
                case Drag.Draw:
                    UpdateLive(image, shift);
                    Invalidate();
                    return;
                case Drag.Move:
                    if (!_moved && EditGeometry.Distance(e.Location, _downClient) < S(3)) return;
                    _moved = true;
                    _selected.Offset(image.X - _lastImage.X, image.Y - _lastImage.Y);
                    _lastImage = image;
                    Invalidate();
                    return;
                case Drag.Handle:
                    _moved = true;
                    ResizeSelected(image, shift);
                    Invalidate();
                    return;
                case Drag.CropNew:
                case Drag.CropMove:
                case Drag.CropHandle:
                    CropDrag(image, shift);
                    RaiseChanged();
                    return;
            }
            CapShape hover = null;
            if (_inline) HandleAt(e.Location, out hover);
            if (hover == null && (_style.Tool == EditTool.Select || _inline)) hover = ShapeAt(image);
            if (hover != _hover) { _hover = hover; Invalidate(); }
            UpdateCursor(e.Location);
        }

        private void UpdateLive(PointF image, bool shift)
        {
            StrokeShape stroke = _live as StrokeShape;
            if (stroke != null)
            {
                if (shift)
                {
                    PointF first = stroke.Points[0];
                    stroke.Points.Clear();
                    stroke.Points.Add(first);
                    stroke.Points.Add(image);
                }
                else if (EditGeometry.Distance(stroke.Points[stroke.Points.Count - 1], image) >= 1.5f / _zoom) stroke.Points.Add(image);
                return;
            }
            LineShape line = _live as LineShape;
            if (line != null)
            {
                line.B = shift ? Snap45(line.A, image) : image;
                return;
            }
            PointF corner = shift ? SquareCorner(_dragStart, image) : image;
            RectangleF rect = EditGeometry.Normalize(_dragStart, corner);
            if (_live is RedactShape) rect = RectangleF.Intersect(rect, new RectangleF(PointF.Empty, _doc.Size));
            SetRect(_live, rect);
        }

        private void ResizeSelected(PointF image, bool shift)
        {
            float dx = image.X - _dragStart.X, dy = image.Y - _dragStart.Y;
            LineShape line = _selected as LineShape;
            if (line != null)
            {
                if (_handle == 0)
                {
                    PointF a = new PointF(_startA.X + dx, _startA.Y + dy);
                    line.A = shift ? Snap45(line.B, a) : a;
                }
                else
                {
                    PointF b = new PointF(_startB.X + dx, _startB.Y + dy);
                    line.B = shift ? Snap45(line.A, b) : b;
                }
                return;
            }
            SetRect(_selected, EditGeometry.Resize(_startRect, _handle, dx, dy));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Drag drag = _drag;
            if (drag == Drag.None) return;
            if (drag == Drag.Pan ? e.Button != MouseButtons.Middle && e.Button != MouseButtons.Left : e.Button != MouseButtons.Left) return;
            _drag = Drag.None;
            Capture = false;
            switch (drag)
            {
                case Drag.Draw: FinishLive(); break;
                case Drag.Move:
                case Drag.Handle:
                    // Рамку, сжатую ручкой до черты, не оставляем: невидимая фигура только мешала бы попадать мышью.
                    if (_moved && drag == Drag.Handle && TooSmall(_selected)) { _doc.Revert(_before); _selected = null; _moved = false; }
                    if (_moved) _doc.Commit(_before);
                    _before = null;
                    _moved = false;
                    RaiseChanged();
                    break;
                case Drag.CropNew:
                case Drag.CropMove:
                case Drag.CropHandle:
                    if (_crop.Width < 2 || _crop.Height < 2) _crop = Rectangle.Empty;
                    RaiseChanged();
                    break;
            }
            UpdateCursor(e.Location);
        }

        private void FinishLive()
        {
            CapShape shape = _live;
            _live = null;
            if (shape == null) return;
            if (!TooSmall(shape))
            {
                _doc.Add(shape);
                _selected = shape is StrokeShape ? null : shape;
            }
            // На холсте оверлея нет «Выбора»: щелчок без протягивания выделяет фигуру под курсором (ручки, Delete).
            else if (_inline) _selected = ShapeAt(_dragStart);
            RaiseChanged();
        }

        private bool TooSmall(CapShape shape)
        {
            float min = 2f / _zoom;
            LineShape line = shape as LineShape;
            if (line != null) return EditGeometry.Distance(line.A, line.B) < min;
            RectangleF rect;
            return RectOf(shape, out rect) && (rect.Width < min || rect.Height < min);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_style.Tool == EditTool.Crop && !_crop.IsEmpty && ToClient(_crop).Contains(e.Location)) ApplyCrop();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != null) { _hover = null; Invalidate(); }
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            if (_textBox == null && _drag != Drag.None && !Capture) CancelDrag();
        }

        // ---------- обрезка ----------

        private void CropDown(MouseEventArgs e, PointF image)
        {
            if (!_crop.IsEmpty)
            {
                int h = CropHandleAt(e.Location);
                if (h >= 0)
                {
                    _drag = Drag.CropHandle;
                    _handle = h;
                    _cropStart = _crop;
                    _dragStart = image;
                    return;
                }
                if (ToClient(_crop).Contains(e.Location))
                {
                    if (e.Clicks >= 2) { Capture = false; return; }
                    _drag = Drag.CropMove;
                    _cropStart = _crop;
                    _dragStart = image;
                    return;
                }
            }
            _drag = Drag.CropNew;
            _dragStart = ClampToImage(image);
            _crop = Rectangle.Empty;
            RaiseChanged();
        }

        private void CropDrag(PointF image, bool shift)
        {
            Rectangle bounds = new Rectangle(Point.Empty, _doc.Size);
            switch (_drag)
            {
                case Drag.CropNew:
                {
                    PointF p = ClampToImage(shift ? SquareCorner(_dragStart, image) : image);
                    _crop = Round(EditGeometry.Normalize(_dragStart, p), bounds);
                    break;
                }
                case Drag.CropMove:
                {
                    int x = _cropStart.X + (int)Math.Round(image.X - _dragStart.X);
                    int y = _cropStart.Y + (int)Math.Round(image.Y - _dragStart.Y);
                    x = Math.Max(0, Math.Min(bounds.Width - _cropStart.Width, x));
                    y = Math.Max(0, Math.Min(bounds.Height - _cropStart.Height, y));
                    _crop = new Rectangle(x, y, _cropStart.Width, _cropStart.Height);
                    break;
                }
                case Drag.CropHandle:
                    _crop = Round(EditGeometry.Resize(_cropStart, _handle, image.X - _dragStart.X, image.Y - _dragStart.Y), bounds);
                    break;
            }
        }

        private static Rectangle Round(RectangleF r, Rectangle bounds)
        {
            Rectangle ri = Rectangle.FromLTRB((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
            return Rectangle.Intersect(ri, bounds);
        }

        public void ApplyCrop()
        {
            if (_crop.Width < 1 || _crop.Height < 1) return;
            Rectangle area = _crop;
            _crop = Rectangle.Empty;
            _selected = null;
            _hover = null;
            if (_doc.Crop(area))
            {
                FitView();
                if (CropApplied != null) CropApplied();
            }
            RaiseChanged();
        }

        // ---------- текст ----------

        private void BeginText(TextShape existing, PointF at)
        {
            CommitText();
            _textBefore = _doc.Capture();
            _textDirty = false;
            if (existing != null)
            {
                _editing = existing;
                _editingNew = false;
                _selected = existing;
            }
            else
            {
                _editing = new TextShape();
                ApplyStyleTo(_editing);
                _editing.Origin = at;
                _editingNew = true;
                _selected = null;
            }
            TextBox box = new TextBox();
            box.Multiline = true;
            box.AcceptsReturn = true;
            box.WordWrap = false;
            box.BorderStyle = BorderStyle.None;
            box.ScrollBars = ScrollBars.None;
            box.Text = existing != null ? existing.Text.Replace("\n", "\r\n") : "";
            box.KeyDown += TextKeyDown;
            box.TextChanged += delegate { PlaceTextBox(); };
            box.Leave += delegate { if (IsHandleCreated && !IsDisposed) BeginInvoke((MethodInvoker)delegate { if (_textBox == box) CommitText(); }); };
            _textBox = box;
            StyleTextBox();
            PlaceTextBox();
            Controls.Add(box);
            if (WindowActive) box.Focus();
            box.SelectAll();
            RaiseChanged();
        }

        private void TextKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CancelText();
            }
            else if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CommitText();
            }
        }

        private void StyleTextBox()
        {
            if (_textBox == null) return;
            Color c = _editing.Color;
            if (_editing.Fill)
            {
                _textBox.BackColor = c;
                _textBox.ForeColor = CapShape.Contrast(c);
            }
            else
            {
                _textBox.ForeColor = c;
                _textBox.BackColor = CapShape.Contrast(c) == Color.Black ? Color.FromArgb(34, 34, 34) : Color.FromArgb(236, 236, 236);
            }
        }

        private void PlaceTextBox()
        {
            if (_textBox == null) return;
            float px = Math.Max(6f, _editing.FontSize * _zoom);
            if (_textFont == null || Math.Abs(_textFont.Size - px) > 0.01f)
            {
                Font old = _textFont;
                _textFont = new Font(EditorFonts.Family, px, FontStyle.Bold, GraphicsUnit.Pixel);
                _textBox.Font = _textFont;
                if (old != null) old.Dispose();
            }
            Size text = TextRenderer.MeasureText(_textBox.Text.Length == 0 ? "W" : _textBox.Text + "W", _textFont);
            PointF at = ToClient(_editing.Origin);
            _textBox.Bounds = new Rectangle((int)at.X, (int)at.Y, Math.Max(S(40), text.Width + S(6)), text.Height + S(4));
        }

        public void CommitText()
        {
            if (_textBox == null) return;
            TextBox box = _textBox;
            TextShape shape = _editing;
            string text = box.Text.Replace("\r\n", "\n").TrimEnd();
            CloseTextBox();
            if (_editingNew)
            {
                if (text.Trim().Length > 0)
                {
                    shape.Text = text;
                    _doc.Shapes.Add(shape);
                    _doc.Commit(_textBefore);
                    _selected = shape;
                }
            }
            else if (text.Trim().Length == 0)
            {
                _doc.Shapes.Remove(shape);
                _doc.Commit(_textBefore);
                _selected = null;
            }
            else if (text != shape.Text || _textDirty)
            {
                shape.Text = text;
                _doc.Commit(_textBefore);
            }
            _textBefore = null;
            RaiseChanged();
        }

        private void CancelText()
        {
            if (_textBox == null) return;
            bool revert = !_editingNew && _textDirty;
            CloseTextBox();
            if (revert) { _doc.Revert(_textBefore); _selected = null; }
            _textBefore = null;
            RaiseChanged();
        }

        private void CloseTextBox()
        {
            TextBox box = _textBox;
            _textBox = null;
            _editing = null;
            bool hadFocus = box.Focused;
            Controls.Remove(box);
            box.Dispose();
            if (hadFocus && CanFocus) Focus();
        }

        // ---------- команды окна ----------

        public void ToolChanged()
        {
            CommitText();
            if (_style.Tool != EditTool.Crop) _crop = Rectangle.Empty;
            else _selected = null;
            _hover = null;
            UpdateCursor(PointToClient(Cursor.Position));
            RaiseChanged();
        }

        private static bool Applies(CapShape shape, StyleField f)
        {
            switch (f)
            {
                case StyleField.Color: return !(shape is RedactShape);
                case StyleField.Width: return shape is StrokeShape || shape is LineShape || shape is BoxShape || shape is StepShape;
                case StyleField.Fill: return shape is BoxShape || shape is TextShape;
                case StyleField.FontSize: return shape is TextShape;
            }
            return false;
        }

        private void Assign(CapShape shape, StyleField f)
        {
            switch (f)
            {
                case StyleField.Color: shape.Color = _style.Color; break;
                case StyleField.Width: shape.Width = _style.Width; break;
                case StyleField.Fill: shape.Fill = _style.Fill; break;
                case StyleField.FontSize: ((TextShape)shape).FontSize = _style.FontSize; break;
            }
        }

        // Смена цвета, толщины, заливки или размера текста меняет и выделенную фигуру (одним шагом отмены).
        public void ApplyStyleField(StyleField f)
        {
            if (_editing != null)
            {
                if (!Applies(_editing, f)) return;
                Assign(_editing, f);
                _textDirty = true;
                StyleTextBox();
                PlaceTextBox();
                return;
            }
            CapShape shape = _selected;
            if (shape == null || !Applies(shape, f)) return;
            _doc.Change(delegate { Assign(shape, f); });
            RaiseChanged();
        }

        public void DeleteSelected()
        {
            if (_selected == null) return;
            _doc.Remove(_selected);
            _selected = null;
            _hover = null;
            RaiseChanged();
        }

        public void Nudge(int dx, int dy)
        {
            if (_selected == null || _drag != Drag.None) return;
            CapShape shape = _selected;
            _doc.Change(delegate { shape.Offset(dx, dy); });
            RaiseChanged();
        }

        public void Escape()
        {
            if (_drag != Drag.None) { CancelDrag(); return; }
            if (_style.Tool == EditTool.Crop && !_crop.IsEmpty) { _crop = Rectangle.Empty; RaiseChanged(); return; }
            if (_selected != null) { _selected = null; RaiseChanged(); }
        }

        public void EnterPressed()
        {
            if (_style.Tool == EditTool.Crop) ApplyCrop();
        }

        private void CancelDrag()
        {
            Drag drag = _drag;
            _drag = Drag.None;
            Capture = false;
            switch (drag)
            {
                case Drag.Draw: _live = null; break;
                case Drag.Move:
                case Drag.Handle:
                    if (_moved) { _doc.Revert(_before); _selected = null; }
                    _before = null;
                    _moved = false;
                    break;
                case Drag.CropNew:
                case Drag.CropMove:
                case Drag.CropHandle:
                    _crop = drag == Drag.CropNew ? Rectangle.Empty : _cropStart;
                    break;
            }
            RaiseChanged();
        }

        private void AfterHistory(Size before)
        {
            _selected = null;
            _hover = null;
            _crop = Rectangle.Empty;
            if (_doc.Size != before) FitView();
            RaiseChanged();
        }

        public void Undo()
        {
            CommitText();
            if (_drag != Drag.None) CancelDrag();
            Size size = _doc.Size;
            if (_doc.Undo()) AfterHistory(size);
        }

        public void Redo()
        {
            CommitText();
            if (_drag != Drag.None) CancelDrag();
            Size size = _doc.Size;
            if (_doc.Redo()) AfterHistory(size);
        }

        public void Rotate(bool clockwise)
        {
            CommitText();
            if (_drag != Drag.None) CancelDrag();
            _doc.Rotate(clockwise);
            _crop = Rectangle.Empty;
            FitView();
            RaiseChanged();
        }
    }
}
