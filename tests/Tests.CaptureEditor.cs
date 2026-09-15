// SysDeck — тесты «Захват»: редактор и оверлей выбора области.
// Сборка и запуск: tests\run-tests.bat (компилирует src\*.cs и tests\*.cs).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SysDeck.Capture;

namespace SysDeck.Tests
{
    internal static partial class CaptureTests
    {
        private static void EditorHistory()
        {
            using (EditorDoc doc = new EditorDoc(new Bitmap(200, 100, PixelFormat.Format32bppRgb)))
            {
                T.Eq("a screen grab (32bppRgb) is kept as 32bppArgb", PixelFormat.Format32bppArgb, doc.Image.PixelFormat);
                T.Check("a fresh document is not dirty and has no history", !doc.Dirty && !doc.CanUndo && !doc.CanRedo);
                doc.Add(Step(1, 1));
                doc.Add(Step(2, 2));
                doc.Add(Step(3, 3));
                T.Check("undo twice leaves one shape", doc.Undo() && doc.Undo() && doc.Shapes.Count == 1);
                T.Check("redo brings the second back", doc.Redo() && doc.Shapes.Count == 2 && doc.CanRedo);
                doc.Add(Step(9, 9));
                T.Check("a new edit after undo drops the redo branch", !doc.CanRedo && doc.Shapes.Count == 3);

                doc.MarkSaved();
                T.Check("saved state is clean", !doc.Dirty);
                doc.Undo();
                T.Check("undo past the save is dirty", doc.Dirty);
                doc.Redo();
                T.Check("redo back to the saved state is clean again", !doc.Dirty);

                // Правка на месте (перетаскивание) и откат по Esc.
                CapShape moved = doc.Shapes[0];
                EditSnapshot before = doc.Capture();
                moved.Offset(50, 50);
                doc.Revert(before);
                T.Check("Esc during a drag restores the position without a history step",
                        Near(((StepShape)doc.Shapes[0]).Center, 1, 1) && !doc.Dirty);

                StrokeShape stroke = new StrokeShape();
                stroke.Points.Add(new PointF(0, 0));
                doc.Add(stroke);
                doc.Change(delegate { stroke.Points.Add(new PointF(10, 10)); });
                doc.Undo();
                T.Eq("undo restores a stroke's own point list, not a shared one", 1, ((StrokeShape)doc.Shapes[doc.Shapes.Count - 1]).Points.Count);

                for (int i = 0; i < EditorDoc.MaxHistory + 20; i++) doc.Add(Step(i, i));
                T.Eq("history is capped", EditorDoc.MaxHistory, doc.UndoDepth);
            }

            using (EditorDoc doc = new EditorDoc(new Bitmap(200, 100, PixelFormat.Format32bppArgb)))
            {
                doc.Add(Step(50, 40));
                T.Check("crop to the same size is not an edit", !doc.Crop(new Rectangle(0, 0, 200, 100)));
                T.Check("crop outside the image is not an edit", !doc.Crop(new Rectangle(500, 500, 10, 10)));
                T.Check("crop applies", doc.Crop(new Rectangle(20, 10, 100, 60)));
                T.Eq("the image takes the cropped size", new Size(100, 60), doc.Size);
                T.Check("shapes move with the crop origin", Near(((StepShape)doc.Shapes[0]).Center, 30, 30));
                T.Eq("the pre-crop image is kept for undo", 2, doc.LiveImages);
                doc.Undo();
                T.Check("undo of a crop restores size and coordinates", doc.Size == new Size(200, 100) && Near(((StepShape)doc.Shapes[0]).Center, 50, 40));
                doc.Add(Step(1, 1));
                T.Eq("an image only the dropped redo branch used is released", 1, doc.LiveImages);
            }
        }

        // ---------- редактор: геометрия фигур ----------
        private static void EditorGeometry()
        {
            using (Bitmap src = new Bitmap(200, 100, PixelFormat.Format32bppArgb))
            {
                src.SetPixel(10, 20, Color.Red);
                using (EditorDoc doc = new EditorDoc((Bitmap)src.Clone()))
                {
                    doc.Add(Step(10.5f, 20.5f));
                    doc.Rotate(true);
                    T.Eq("rotating right swaps the sides", new Size(100, 200), doc.Size);
                    Color moved = doc.Image.GetPixel(100 - 1 - 20, 10);
                    T.Check("the pixel lands where RotateFlip put it", moved.ToArgb() == Color.Red.ToArgb(), moved.ToString());
                    T.Check("a shape at a pixel centre follows the same pixel", Near(((StepShape)doc.Shapes[0]).Center, 100 - 20.5f, 10.5f),
                            ((StepShape)doc.Shapes[0]).Center.ToString());
                    doc.Rotate(false);
                    T.Check("rotating left undoes rotating right", doc.Size == new Size(200, 100) && Near(((StepShape)doc.Shapes[0]).Center, 10.5f, 20.5f));
                    T.Check("the pixel is back in place", doc.Image.GetPixel(10, 20).ToArgb() == Color.Red.ToArgb());
                }
            }

            LineShape line = new LineShape();
            line.A = new PointF(0, 0);
            line.B = new PointF(100, 0);
            line.Width = 4;
            T.Check("a click on the line hits it", line.HitTest(new PointF(50, 3), 2));
            T.Check("a click beside the line misses", !line.HitTest(new PointF(50, 10), 2));
            line.Arrow = true;
            T.Check("the arrow head is hit beside its tip", line.HitTest(new PointF(95, 7), 1));
            PointF[] head;
            line.ArrowGeometry(out head);
            T.Check("the arrow head ends at the arrow's end point", Near(head[0], 100, 0));

            BoxShape ellipse = new BoxShape();
            ellipse.Ellipse = true;
            ellipse.Rect = new RectangleF(0, 0, 100, 50);
            T.Check("an outlined ellipse is hit on its edge", ellipse.HitTest(new PointF(50, 1), 3));
            T.Check("an outlined ellipse is not hit in its hollow middle", !ellipse.HitTest(new PointF(50, 25), 3));
            T.Check("an outlined ellipse is not hit at the corner of its box", !ellipse.HitTest(new PointF(2, 2), 3));
            ellipse.Fill = true;
            T.Check("a filled ellipse is hit in the middle", ellipse.HitTest(new PointF(50, 25), 3));

            TextShape text = new TextShape();
            text.Origin = new PointF(100, 100);
            text.Text = "Привет\nмир";
            RectangleF tb = text.Bounds;
            T.Check("two text lines are taller than one", tb.Height > text.FontSize * 1.5f && tb.Contains(110, 120), tb.ToString());
            RectangleF before = text.Bounds;
            text.Map(delegate(PointF p) { return new PointF(1000 - p.Y, p.X); });
            RectangleF after = text.Bounds;
            T.Check("text keeps its size after a rotation (stays upright)", Math.Abs(after.Width - before.Width) < 0.5f && Math.Abs(after.Height - before.Height) < 0.5f);
            T.Check("the text centre follows the rotation",
                    Near(new PointF(after.X + after.Width / 2, after.Y + after.Height / 2), 1000 - (before.Y + before.Height / 2), before.X + before.Width / 2));

            // Живая проверка: угол области скрытия, перетянутый на противоположную сторону, давал ширину 0 — область
            // сохранялась невидимой и ничего не скрывала.
            RectangleF squashed = EditGeometry.Resize(new RectangleF(200, 245, 360, 45), 6, 360, 45);
            T.Check("a handle dragged exactly onto the opposite side leaves a visible area", squashed.Width >= 1 && squashed.Height >= 1, squashed.ToString());

            PointF snapped = EditorCanvas.Snap45(new PointF(0, 0), new PointF(10, 1));
            T.Check("Shift snaps a nearly flat line to horizontal", Math.Abs(snapped.Y) < 0.001f && snapped.X > 10, snapped.ToString());
            T.Check("Shift makes a square towards the cursor", Near(EditorCanvas.SquareCorner(new PointF(10, 10), new PointF(0, 30)), -10, 30));
            T.Eq("zooming in from 90% stops at 100%", 1f, EditorCanvas.NextZoom(0.9f, 1));
            T.Eq("zooming out from 110% stops at 100%", 1f, EditorCanvas.NextZoom(1.1f, -1));
            T.Eq("zoom is capped", 16f, EditorCanvas.NextZoom(16f, 1));
        }

        // ---------- редактор: скрытие данных ----------
        // Шахматка 1px чёрное/белое: после размытия или пикселизации в области не остаётся ни одного чистого чёрного
        // или белого пикселя, а вне области не меняется ни один.
        private static Bitmap Checker(int w, int h)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    b.SetPixel(x, y, (x + y) % 2 == 0 ? Color.Black : Color.White);
            return b;
        }

        private static string RedactionProblems(Bitmap result, Bitmap original, Rectangle area)
        {
            int outside = 0, pure = 0;
            for (int y = 0; y < result.Height; y++)
                for (int x = 0; x < result.Width; x++)
                {
                    int c = result.GetPixel(x, y).ToArgb();
                    if (area.Contains(x, y))
                    {
                        if (c == Color.Black.ToArgb() || c == Color.White.ToArgb()) pure++;
                    }
                    else if (c != original.GetPixel(x, y).ToArgb()) outside++;
                }
            return outside == 0 && pure == 0 ? null : "changed outside: " + outside + ", original pixels inside: " + pure;
        }

        private static void EditorRedaction()
        {
            Rectangle area = new Rectangle(16, 12, 40, 30);
            foreach (bool pixelate in new bool[] { true, false })
            {
                string kind = pixelate ? "pixelate" : "blur";
                using (Bitmap original = Checker(80, 60))
                using (EditorDoc doc = new EditorDoc((Bitmap)original.Clone()))
                {
                    RedactShape r = new RedactShape();
                    r.Pixelate = pixelate;
                    r.Rect = area;
                    doc.Add(r);
                    T.Check(kind + ": the document raster itself is not touched", RedactionProblems(doc.Image, original, Rectangle.Empty) == null);
                    using (Bitmap output = doc.Render(null))
                    {
                        string problems = RedactionProblems(output, original, area);
                        T.Check(kind + ": only the area changes and no original pixel survives in it", problems == null, problems);
                    }
                }
            }

            // Фигура под скрытием скрывается вместе с растром, фигура поверх — остаётся чёткой.
            using (EditorDoc doc = new EditorDoc(new Bitmap(64, 64, PixelFormat.Format32bppArgb)))
            {
                using (Graphics g = Graphics.FromImage(doc.Image)) g.Clear(Color.Black);
                LineShape under = new LineShape();
                under.A = new PointF(0, 32);
                under.B = new PointF(64, 32);
                under.Width = 3;
                under.Color = Color.White;
                doc.Add(under);
                RedactShape r = new RedactShape();
                r.Pixelate = true;
                r.Rect = new RectangleF(16, 16, 32, 32);
                doc.Add(r);
                LineShape over = (LineShape)under.Clone();
                over.A = new PointF(0, 40);
                over.B = new PointF(64, 40);
                doc.Add(over);
                using (Bitmap output = doc.Render(null))
                {
                    T.Check("a line drawn before the redaction is redacted too", output.GetPixel(30, 32).ToArgb() != Color.White.ToArgb(), output.GetPixel(30, 32).ToString());
                    T.Check("the same line outside the area is untouched", output.GetPixel(4, 32).ToArgb() == Color.White.ToArgb());
                    T.Check("a line drawn after the redaction stays crisp", output.GetPixel(30, 40).ToArgb() == Color.White.ToArgb(), output.GetPixel(30, 40).ToString());
                }
                using (Bitmap live = doc.Render(r))
                    T.Check("the canvas can render without the shape being dragged", live.GetPixel(30, 32).ToArgb() == Color.White.ToArgb());
            }
        }

        // ---------- редактор: настройки ----------
        private static void EditorSettings()
        {
            Color c;
            T.Check("#1E88E5 parses", HexColor.TryParse(" #1e88e5 ", out c) && c.ToArgb() == Color.FromArgb(30, 136, 229).ToArgb());
            T.Eq("a colour is written as #RRGGBB", "#1E88E5", HexColor.Format(Color.FromArgb(30, 136, 229)));
            T.Check("a colour name is not a hex colour", !HexColor.TryParse("red", out c));

            CapSettings s = new CapSettings();
            s.After = ShotAfter.OpenEditor;
            s.EditorColor = "#43A047";
            s.EditorWidth = 8;
            s.EditorFontSize = 48;
            s.EditorFill = true;
            s.EditorTool = EditTool.Pixelate;
            CapSettings back = CapSettings.FromJson(s.ToJson());
            T.Check("editor preferences survive a round trip",
                    back.After == ShotAfter.OpenEditor && back.EditorColor == "#43A047" && back.EditorWidth == 8 && back.EditorFontSize == 48
                    && back.EditorFill && back.EditorTool == EditTool.Pixelate);
            CapSettings odd = CapSettings.FromJson("{\"EditorColor\": \"red\", \"EditorWidth\": 999, \"EditorFontSize\": 1, \"EditorTool\": \"Crop\"}");
            T.Eq("a bad colour keeps the default", "#E53935", odd.EditorColor);
            T.Eq("a huge width is clamped", 40, odd.EditorWidth);
            T.Eq("a tiny font is clamped", 8, odd.EditorFontSize);
            T.Eq("the editor never opens in crop mode", EditTool.Arrow, odd.EditorTool);

            EditorStyle style = EditorStyle.From(back);
            CapSettings copy = new CapSettings();
            style.CopyTo(copy);
            T.Check("the editor style maps to settings and back", copy.EditorColor == "#43A047" && copy.EditorWidth == 8 && copy.EditorFill && copy.EditorTool == EditTool.Pixelate);
        }

        // ---------- редактор: сохранение настоящим окном ----------
        // Окно не показывается. Первое сохранение нового снимка — файл по шаблону в папке программы; перезапись
        // существующего файла отправляет прежний в Корзину, поэтому здесь не проверяется (тесты Корзину не трогают).
        private static void EditorSave()
        {
            string root = Fx.MakeDir(Fx.Root, "capture-editor");
            CapSettings s = new CapSettings();
            s.ShotFolder = root;
            s.PerAppFolders = true;
            s.NameTemplate = "{app}";
            Rectangle area = new Rectangle(10, 10, 30, 20);
            using (Bitmap original = Checker(60, 40))
            using (EditorForm form = new EditorForm((Bitmap)original.Clone(), "Test App", null, null, s))
            {
                RedactShape r = new RedactShape();
                r.Pixelate = true;
                r.Rect = area;
                form.Doc.Add(r);
                T.Check("an edited shot is dirty before saving", form.Doc.Dirty);
                T.Check("the editor saves a new shot", form.Save(false));
                string expected = Path.Combine(Path.Combine(root, "Test App"), "Test App.png");
                T.Eq("the file goes to the program's folder by the name template", expected, form.FilePath);
                T.Check("the document is clean after saving", !form.Doc.Dirty);
                if (File.Exists(expected))
                    using (Bitmap saved = ImageStore.LoadUnlocked(expected))
                    {
                        string problems = RedactionProblems(saved, original, area);
                        T.Check("the saved file keeps no original pixels under the pixelation", problems == null, problems);
                    }
                T.Check("no temporary file is left next to it", Directory.GetFiles(Path.GetDirectoryName(expected)).Length == 1);
            }
        }

        // ---------- редактор: мышь по настоящему холсту ----------
        // Сообщения мыши уходят в скрытое окно через SendMessage: курсор не двигается, фокус ни у кого не отнимается.
        // Снимок крупнее окна, поэтому холст уменьшен — ровно тот масштаб, при котором ручки задевали соседние фигуры.
        private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, MK_LBUTTON = 1, WM_KEYDOWN = 0x0100;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static void EditorMouse()
        {
            CapSettings s = new CapSettings();
            s.ShotFolder = Fx.MakeDir(Fx.Root, "capture-editor-mouse");
            using (EditorForm form = new EditorForm(new Bitmap(1600, 900), "Test App", null, null, s))
            {
                form.ClientSize = new Size(900, 600);
                EditorCanvas canvas = form.Canvas;
                canvas.FitView();
                T.Check("the canvas shows the shot scaled down", canvas.Zoom < 0.7f, canvas.Zoom.ToString());

                form.Bar.SelectTool(EditTool.Pixelate);
                MouseDrag(canvas, new PointF(200, 245), new PointF(560, 290));
                // Размытие начинается в 5 px от нижнего левого угла только что нарисованной области.
                form.Bar.SelectTool(EditTool.Blur);
                MouseDrag(canvas, new PointF(200, 295), new PointF(560, 340));
                T.Eq("a blur started next to a pixelation corner is a new shape", 2, form.Doc.Shapes.Count);
                T.Check("the pixelation next to it keeps its size", Math.Abs(RedactRect(form, 0).Height - 45) < 1, RedactRect(form, 0).ToString());

                form.Bar.SelectTool(EditTool.Select);
                MouseDrag(canvas, new PointF(380, 268), new PointF(380, 268));
                int depth = form.Doc.UndoDepth;
                MouseDrag(canvas, new PointF(380, 290), new PointF(380, 245));
                T.Check("a handle dragged onto the opposite side leaves the area as it was, with no history step",
                        Math.Abs(RedactRect(form, 0).Height - 45) < 1 && form.Doc.UndoDepth == depth && form.Doc.Shapes.Count == 2,
                        RedactRect(form, 0) + " undo " + form.Doc.UndoDepth);

                MouseDrag(canvas, new PointF(380, 268), new PointF(380, 268));
                MouseDrag(canvas, new PointF(380, 290), new PointF(380, 310));
                T.Check("the Select tool still resizes by a handle", Math.Abs(RedactRect(form, 0).Height - 65) < 1, RedactRect(form, 0).ToString());
            }
        }

        // ---------- правки прямо в оверлее ----------
        // Оверлей не показывается: мышь — SendMessage в его окно (выделение, панель, ручки области) и в холст поверх
        // выделения. Сценарий пользователя: две рамки разного цвета, ручка уже нарисованной фигуры при инструменте
        // рисования, выделение щелчком и удаление, область растянута после рисования, «Сохранить».
        private static void OverlayInlineEdit()
        {
            Color mark = Color.FromArgb(0, 200, 0);
            Bitmap frame = new Bitmap(1600, 900, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(frame)) g.Clear(Color.FromArgb(90, 90, 90));
            frame.SetPixel(250, 70, mark);
            OverlayResult result = null;
            Color first, second;
            using (RegionOverlay overlay = new RegionOverlay(frame, new Rectangle(0, 0, 1600, 900), new List<MonitorInfo>(), new List<WindowCandidate>(), Rectangle.Empty))
            {
                overlay.Finished += delegate(RegionOverlay o, OverlayResult r) { result = r; };
                OverlayDrag(overlay, new Point(300, 100), new Point(1000, 500));
                Rectangle sel = (Rectangle)Field(overlay, "_sel");
                OverlayClick(overlay, "Tool:Rect");
                EditorCanvas canvas = (EditorCanvas)Field(overlay, "_canvas");
                T.Check("a tool on the overlay panel opens a canvas right over the selection", canvas != null && canvas.Bounds == sel, sel.ToString());
                if (canvas == null) return;
                EditorDoc doc = (EditorDoc)Field(overlay, "_doc");
                first = (Color)Field(PanelButtonOf(overlay, "Color:0"), "Color");
                second = (Color)Field(PanelButtonOf(overlay, "Color:2"), "Color");

                OverlayClick(overlay, "Color:0");
                MouseDrag(canvas, new PointF(20, 20), new PointF(220, 160));
                OverlayClick(overlay, "Color:2");
                MouseDrag(canvas, new PointF(300, 60), new PointF(500, 260));
                T.Check("a colour picked after a box applies to the next box only",
                        doc.Shapes.Count == 2 && doc.Shapes[0].Color.ToArgb() == first.ToArgb() && doc.Shapes[1].Color.ToArgb() == second.ToArgb(), Describe(doc));

                MouseDrag(canvas, new PointF(220, 160), new PointF(260, 200));
                T.Check("with a drawing tool the handle of an earlier box resizes it",
                        doc.Shapes.Count == 2 && Near(BoxRect(doc, 0), 20, 20, 240, 180), Describe(doc));

                MouseDrag(canvas, new PointF(20, 80), new PointF(20, 80));
                OverlayClick(overlay, "Delete");
                T.Check("a click on a box selects it and the panel deletes it",
                        doc.Shapes.Count == 1 && doc.Shapes[0].Color.ToArgb() == second.ToArgb(), Describe(doc));
                OverlayClick(overlay, "Undo");
                T.Eq("undo on the panel brings the box back", 2, doc.Shapes.Count);

                OverlayDrag(overlay, sel.Location, new Point(sel.X - 60, sel.Y - 40));
                sel = (Rectangle)Field(overlay, "_sel");
                T.Check("the region grows with the drawing on it: shapes stay where they are on screen",
                        canvas.Bounds == sel && doc.Size == sel.Size && Near(BoxRect(doc, 0), 80, 60, 240, 180) && Near(BoxRect(doc, 1), 360, 100, 200, 200),
                        sel + " " + Describe(doc));
                T.Check("the grown canvas shows the newly uncovered part of the frame", doc.Image.GetPixel(10, 10).ToArgb() == mark.ToArgb());
                // История, записанная до растягивания, отматывается в тех же местах экрана — в обе стороны.
                OverlayClick(overlay, "Undo");
                T.Check("undo after growing the region takes the resize back at the box's moved place",
                        doc.Shapes.Count == 2 && Near(BoxRect(doc, 0), 80, 60, 200, 140), Describe(doc));
                OverlayClick(overlay, "Redo");
                OverlayClick(overlay, "Redo");
                T.Check("redo after growing the region deletes the box at its moved place",
                        doc.Shapes.Count == 1 && Near(BoxRect(doc, 0), 360, 100, 200, 200), Describe(doc));
                OverlayClick(overlay, "Undo");

                OverlayClick(overlay, "Action:Save");
                Application.DoEvents();
                T.Check("Save hands the drawing over with the region",
                        result != null && result.Action == OverlayAction.Default && result.Area == sel && result.Doc == doc,
                        result == null ? "no result" : result.Action + " " + result.Area);
            }
            if (result == null || result.Doc == null) return;
            using (EditorDoc doc = result.Doc)
            using (Bitmap output = doc.Render(null))
            {
                Color edge = output.GetPixel(80, 150);
                T.Check("the saved picture has the first box in its own colour after the overlay is closed", edge.ToArgb() == first.ToArgb(), edge.ToString());
                T.Check("the saved picture keeps the frame under the drawing", output.GetPixel(10, 10).ToArgb() == mark.ToArgb());
            }
        }

        // ---------- «Сохранить» и «Отмена» оверлея — подписанные кнопки ----------
        // Значки без подписи не читаются как «сохранить» и «отменить». Кнопки проверяются
        // тем же путём, что и мышь пользователя: сообщения в окно оверлея; Enter и Esc — через PreProcessMessage,
        // как их передаёт цикл сообщений.
        private static void OverlayLabelledButtons()
        {
            {
                Rectangle all = new Rectangle(0, 0, 1600, 900);
                using (RegionOverlay overlay = new RegionOverlay(OverlayFrame(), all, new List<MonitorInfo>(), new List<WindowCandidate>(), Rectangle.Empty))
                {
                    OverlayDrag(overlay, new Point(300, 100), new Point(1000, 500));
                    object save = PanelButtonOf(overlay, "Action:Save"), cancel = PanelButtonOf(overlay, "Action:Cancel"), copy = PanelButtonOf(overlay, "Action:Copy");
                    Rectangle rs = BoundsOf(save), rc = BoundsOf(cancel), rcopy = BoundsOf(copy);
                    string saveText = OptField(save, "Caption") as string, cancelText = OptField(cancel, "Caption") as string;
                    T.Check("Save on the overlay panel is a labelled button, not a bare icon",
                            !string.IsNullOrEmpty(saveText) && rs.Width > rs.Height * 2, saveText + " " + rs);
                    T.Check("Save is the accent (primary) button", true.Equals(OptField(save, "Primary")));
                    T.Check("Cancel on the overlay panel is a labelled button", !string.IsNullOrEmpty(cancelText) && rc.Width > rc.Height * 3 / 2, cancelText + " " + rc);
                    T.Check("the labelled buttons close the panel: icons, then Save, then Cancel",
                            !rcopy.IsEmpty && rcopy.Right <= rs.Left && rs.Right <= rc.Left && rs.Y == rc.Y, rcopy + " " + rs + " " + rc);
                    Rectangle panel = (Rectangle)overlay.GetType().GetMethod("PanelRect", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(overlay, null);
                    T.Check("the wider panel still fits the screen", all.Contains(panel), panel.ToString());
                }

                T.Eq("a click on the labelled Cancel cancels", OverlayAction.Cancel, OverlayOutcome(all, delegate(RegionOverlay o) { OverlayClick(o, "Action:Cancel"); }));
                T.Eq("Enter still saves", OverlayAction.Default, OverlayOutcome(all, delegate(RegionOverlay o) { OverlayKey(o, Keys.Enter); }));
                T.Eq("Esc still cancels", OverlayAction.Cancel, OverlayOutcome(all, delegate(RegionOverlay o) { OverlayKey(o, Keys.Escape); }));

                // Монитор уже панели с подписями: главные кнопки сжимаются до значков, панель не вылезает за край.
                List<MonitorInfo> narrow = new List<MonitorInfo>();
                MonitorInfo left = new MonitorInfo();
                left.Device = "narrow"; left.Bounds = left.WorkArea = new Rectangle(0, 0, 420, 900);
                MonitorInfo right = new MonitorInfo();
                right.Device = "wide"; right.Bounds = right.WorkArea = new Rectangle(420, 0, 1180, 900);
                narrow.Add(left);
                narrow.Add(right);
                using (RegionOverlay overlay = new RegionOverlay(OverlayFrame(), all, narrow, new List<WindowCandidate>(), Rectangle.Empty))
                {
                    OverlayDrag(overlay, new Point(20, 100), new Point(400, 500));
                    Rectangle rs = BoundsOf(PanelButtonOf(overlay, "Action:Save"));
                    T.Check("on a monitor too narrow for captions Save falls back to an icon", false.Equals(OptField(overlay, "_captions")) && rs.Width == rs.Height,
                            rs.ToString());
                }

                using (RegionOverlay overlay = new RegionOverlay(OverlayFrame(), all, new List<MonitorInfo>(), new List<WindowCandidate>(), Rectangle.Empty, true))
                {
                    OverlayDrag(overlay, new Point(300, 100), new Point(1000, 500));
                    object record = PanelButtonOf(overlay, "Action:Record");
                    Rectangle rr = BoundsOf(record), rc = BoundsOf(PanelButtonOf(overlay, "Action:Cancel"));
                    T.Check("the video overlay labels Start recording, next to Cancel",
                            !string.IsNullOrEmpty(OptField(record, "Caption") as string) && rr.Width > rr.Height * 2 && rr.Right <= rc.Left, rr + " " + rc);
                }
            }
        }

        // Оверлей забирает кадр себе и освобождает его при закрытии — у каждого оверлея свой кадр.
        private static Bitmap OverlayFrame() { return new Bitmap(1600, 900, PixelFormat.Format32bppArgb); }

        private static OverlayAction? OverlayOutcome(Rectangle all, Action<RegionOverlay> act)
        {
            OverlayResult result = null;
            using (RegionOverlay overlay = new RegionOverlay(OverlayFrame(), all, new List<MonitorInfo>(), new List<WindowCandidate>(), Rectangle.Empty))
            {
                overlay.Finished += delegate(RegionOverlay o, OverlayResult r) { result = r; };
                OverlayDrag(overlay, new Point(300, 100), new Point(1000, 500));
                act(overlay);
                Application.DoEvents();
            }
            if (result == null) return null;
            if (result.Doc != null) result.Doc.Dispose();
            return result.Action;
        }

        private static void OverlayKey(Control target, Keys key)
        {
            Message m = Message.Create(target.Handle, WM_KEYDOWN, new IntPtr((int)key), IntPtr.Zero);
            target.PreProcessMessage(ref m);
        }

        private static Rectangle BoundsOf(object panelButton)
        {
            return panelButton == null ? Rectangle.Empty : (Rectangle)Field(panelButton, "Bounds");
        }

        // Поле, которого может не быть (проверка нового поведения на старом дереве даёт провал, а не исключение).
        private static object OptField(object o, string name)
        {
            if (o == null) return null;
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f == null ? null : f.GetValue(o);
        }

        // ---------- уведомление о снимке: показывается и открывает галерею ----------
        // Путь из ранней сборки: settings.json ранней сборки («ToastEnabled»: false, без «Version») → ToastHost.Apply → Show.
        // До исправления уведомление молча отбрасывалось. Окно показывается на секунду в углу основного монитора и
        // закрывается щелчком по «Галерее»; Проводник не открывается — событие перехватывает тест, а не агент.
        private static void ToastGallery()
        {
            string dir = Fx.MakeDir(Fx.Root, "capture-toast");
            string shots = Fx.MakeDir(dir, "shots"), videos = Fx.MakeDir(dir, "videos");
            string saved = Path.Combine(Fx.MakeDir(shots, "app"), "shot.png");
            using (Bitmap b = new Bitmap(8, 8, PixelFormat.Format32bppRgb)) b.Save(saved, ImageFormat.Png);
            string file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, "{\"ToastEnabled\": false, \"DeferToastsInFullscreen\": false, \"ShotFolder\": \"" + shots.Replace("\\", "\\\\")
                                    + "\", \"VideoFolder\": \"" + videos.Replace("\\", "\\\\") + "\"}");
            CapSettings s = CapSettings.Load(file);

            MethodInfo pick = typeof(AgentApp).GetMethod("GalleryFolder", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            T.Check("the agent opens the root screenshots folder for a saved shot",
                    pick != null && shots.Equals(pick.Invoke(null, new object[] { s, saved })));
            T.Check("the agent opens the root videos folder for a saved video",
                    pick != null && videos.Equals(pick.Invoke(null, new object[] { s, Path.Combine(videos, "clip.mp4") })));

            Screen screen = Screen.PrimaryScreen;
            MonitorInfo mon = new MonitorInfo();
            mon.Device = screen.DeviceName; mon.Bounds = screen.Bounds; mon.WorkArea = screen.WorkingArea; mon.Primary = true;
            string asked = null;
            ToastHost host = new ToastHost();
            try
            {
                host.Apply(s);
                EventInfo ev = typeof(ToastHost).GetEvent("GalleryRequested", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ev != null) ev.AddEventHandler(host, (Action<string>)delegate(string p) { asked = p; });
                host.Show(ToastInfo.Saved(new Bitmap(64, 36, PixelFormat.Format32bppPArgb), saved, true), mon);
                IList open = (IList)Field(host, "_open");
                T.Eq("a saved-shot toast shows with settings written by an early build", 1, open.Count);
                if (open.Count != 1) return;
                ToastWindow w = (ToastWindow)open[0];

                Rectangle gallery = Rectangle.Empty;
                List<Rectangle> others = new List<Rectangle>();
                foreach (object kv in (IList)Field(w, "_buttons"))
                {
                    string key = kv.GetType().GetProperty("Key").GetValue(kv, null).ToString();
                    Rectangle r = (Rectangle)kv.GetType().GetProperty("Value").GetValue(kv, null);
                    if (key == "Gallery") gallery = r; else others.Add(r);
                }
                bool apart = true;
                foreach (Rectangle r in others) apart &= !r.IntersectsWith(gallery);
                T.Check("the toast has a Gallery button inside the window, clear of the other buttons",
                        !gallery.IsEmpty && w.ClientRectangle.Contains(gallery) && apart, gallery + " in " + w.ClientSize);
                Font title = (Font)Field(w, "_titleFont");
                int textLeft = (int)w.GetType().GetProperty("TextLeft", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(w, null);
                int textRight = (int)w.GetType().GetMethod("S", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(w, new object[] { 46f });
                int need = TextRenderer.MeasureText(Tr.S("Снимок сохранён и скопирован", "Screenshot saved and copied"), title, Size.Empty,
                                                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
                T.Check("the toast title fits without an ellipsis", need <= w.Width - textRight - textLeft, need + " > " + (w.Width - textRight - textLeft));
                if (gallery.IsEmpty) return;

                IntPtr at = new IntPtr(((gallery.Y + gallery.Height / 2) << 16) | ((gallery.X + gallery.Width / 2) & 0xFFFF));
                SendMessage(w.Handle, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), at);
                SendMessage(w.Handle, WM_LBUTTONUP, IntPtr.Zero, at);
                Application.DoEvents();
                T.Eq("a click on Gallery asks the agent to open the gallery for this file", saved, asked);
                T.Check("the toast closes after Gallery", w.IsClosingToast);
            }
            finally { host.Dispose(); }
        }

        private static object Field(object o, string name)
        {
            return o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);
        }

        // Кнопка панели оверлея по имени: «Tool:Rect», «Color:2» (номер цвета на панели), «Action:Save», «Delete».
        private static object PanelButtonOf(RegionOverlay overlay, string key)
        {
            int colors = 0;
            foreach (object b in (IList)Field(overlay, "_buttons"))
            {
                string name = Field(b, "Kind").ToString();
                if (name == "Tool") name += ":" + Field(b, "Tool");
                else if (name == "Action") name += ":" + Field(b, "Action");
                else if (name == "Color") name += ":" + colors++;
                if (name == key) return b;
            }
            return null;
        }

        private static void OverlayClick(RegionOverlay overlay, string key)
        {
            object b = PanelButtonOf(overlay, key);
            Rectangle r = b == null ? Rectangle.Empty : (Rectangle)Field(b, "Bounds");
            Point c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            if (r.IsEmpty) T.Check("the overlay panel shows " + key, false);
            else OverlayDrag(overlay, c, c);
        }

        private static void OverlayDrag(Control target, Point from, Point to)
        {
            IntPtr h = target.Handle;
            for (int i = 0; i <= 6; i++)
            {
                IntPtr at = new IntPtr(((from.Y + (to.Y - from.Y) * i / 6) << 16) | ((from.X + (to.X - from.X) * i / 6) & 0xFFFF));
                if (i == 0) SendMessage(h, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), at);
                SendMessage(h, WM_MOUSEMOVE, new IntPtr(MK_LBUTTON), at);
                if (i == 6) SendMessage(h, WM_LBUTTONUP, IntPtr.Zero, at);
            }
        }

        private static RectangleF BoxRect(EditorDoc doc, int index)
        {
            BoxShape b = doc.Shapes.Count > index ? doc.Shapes[index] as BoxShape : null;
            return b != null ? b.Rect : RectangleF.Empty;
        }

        private static bool Near(RectangleF r, float x, float y, float w, float h)
        {
            return Math.Abs(r.X - x) < 1 && Math.Abs(r.Y - y) < 1 && Math.Abs(r.Width - w) < 1 && Math.Abs(r.Height - h) < 1;
        }

        private static string Describe(EditorDoc doc)
        {
            StringBuilder sb = new StringBuilder(doc.Size + ":");
            foreach (CapShape s in doc.Shapes) sb.Append(' ').Append(s.Bounds).Append(' ').Append(s.Color.Name);
            return sb.ToString();
        }

        private static RectangleF RedactRect(EditorForm form, int index)
        {
            RedactShape r = form.Doc.Shapes.Count > index ? form.Doc.Shapes[index] as RedactShape : null;
            return r != null ? r.Rect : RectangleF.Empty;
        }

        // Точка снимка переводится в точку холста тем же масштабом и сдвигом, которыми холст рисует.
        private static void MouseDrag(EditorCanvas canvas, PointF from, PointF to)
        {
            PointF pan = (PointF)Field(canvas, "_pan");
            IntPtr h = canvas.Handle;
            for (int i = 0; i <= 6; i++)
            {
                float x = (from.X + (to.X - from.X) * i / 6) * canvas.Zoom + pan.X, y = (from.Y + (to.Y - from.Y) * i / 6) * canvas.Zoom + pan.Y;
                IntPtr at = new IntPtr(((int)Math.Round(y) << 16) | ((int)Math.Round(x) & 0xFFFF));
                if (i == 0) SendMessage(h, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), at);
                SendMessage(h, WM_MOUSEMOVE, new IntPtr(MK_LBUTTON), at);
                if (i == 6) SendMessage(h, WM_LBUTTONUP, IntPtr.Zero, at);
            }
        }
    }
}
