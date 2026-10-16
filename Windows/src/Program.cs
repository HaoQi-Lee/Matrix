using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// Matrix digital rain screensaver for Windows.
// Entry point: parses the command line the OS passes to .scr files:
//   /s        run fullscreen saver
//   /c[:hwnd] show config dialog (we have none, show a notice)
//   /p hwnd   settings preview (not supported, exit)
//   /a        password (obsolete, exit)
//   no args   treat as /s (double-click in Explorer may pass nothing)

namespace MatrixSaver
{
    static class Program
    {
        static bool _exiting;
        static int _engineSeed;

        [STAThread]
        static void Main(string[] args)
        {
            string arg = "";
            try
            {
                if (args.Length > 0)
                {
                    arg = args[0].Trim().ToLowerInvariant().TrimStart('/', '-');
                    int colon = arg.IndexOf(':');
                    if (colon >= 0) arg = arg.Substring(0, colon);
                }

                if (arg == "c")
                {
                    MessageBox.Show(
                        "Matrix screensaver: no settings available.",
                        "Matrix",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
                if (arg == "p" || arg == "a") return;

                if (arg == "smoke")
                {
                    RunSmoke();
                    return;
                }

                RunSaver();
            }
            catch
            {
                // A screensaver must never pop an error dialog on the user's
                // screen; fail silently.
            }
        }

        static void RunSaver()
        {
            Application.EnableVisualStyles();

            var forms = new System.Collections.Generic.List<MatrixForm>();
            foreach (Screen s in Screen.AllScreens)
                forms.Add(new MatrixForm(s.Bounds));

            Cursor.Hide();
            try
            {
                foreach (MatrixForm f in forms) f.Show();
                forms[0].Activate();            // ensure keyboard focus
                Application.Run();              // returns after RequestExit
            }
            finally
            {
                Cursor.Show();
                foreach (MatrixForm f in forms) f.Dispose();
            }
        }

        // Called by any MatrixForm on user input; shuts down every window.
        internal static void RequestExit()
        {
            if (_exiting) return;
            _exiting = true;
            Application.Exit();
        }

        // Distinct seed per engine so monitors do not rain in lockstep.
        internal static int NextEngineSeed()
        {
            return unchecked(Environment.TickCount +
                System.Threading.Interlocked.Increment(ref _engineSeed));
        }

        // Self-check: run the engine 3000 frames on a 120x60 grid and
        // validate column invariants every frame. Writes the result to
        // smoke-result.txt next to the exe (winexe has no console).
        static void RunSmoke()
        {
            const int Cols = 120, Rows = 60, Frames = 3000;
            string path = AppDomain.CurrentDomain.BaseDirectory + "smoke-result.txt";
            try
            {
                var engine = new RainEngine(Cols, Rows, 12345);
                long cells = 0;
                for (int i = 0; i < Frames; i++)
                {
                    engine.Step(0.033f);
                    for (int c = 0; c < Cols; c++)
                    {
                        Column col = engine.GetColumn(c);
                        if (col.Len < RainEngine.MinLen || col.Len > RainEngine.MaxLen)
                            throw new Exception("col len out of range: " + col.Len);
                        if (col.Filled < 0 || col.Filled > col.Len)
                            throw new Exception("col filled out of range: " + col.Filled);
                        if (col.Y < 0 || col.Y - col.Len > Rows)
                            throw new Exception("col y out of range: " + col.Y);
                        for (int k = 0; k < col.Filled; k++)
                            if (col.Glyphs[k] < 0 || col.Glyphs[k] >= RainEngine.Charset.Length)
                                throw new Exception("glyph out of range: " + col.Glyphs[k]);
                        cells += col.Filled;
                    }
                }
                if (cells < 2000000 || cells > 10000000)
                    throw new Exception("cell count out of band: " + cells);
                System.IO.File.WriteAllText(path, "PASS " + cells + " cells");
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(path, "FAIL " + ex.Message);
            }
        }
    }

    // One fullscreen borderless window per monitor. Closes the whole
    // saver on any key, click, or mouse move beyond the threshold.
    class MatrixForm : Form
    {
        Point _mouseStart;
        RainEngine _engine;
        Font _font;
        int _cellWI, _cellHI;                  // cell size in pixels
        int _pitch, _xOffset;                  // column pitch, glyph centering
        int _rows;
        Bitmap _buffer;
        readonly Dictionary<int, Bitmap[]> _sprites = new Dictionary<int, Bitmap[]>();
        readonly Timer _timer = new Timer();
        bool _busy;
        DateTime _last = DateTime.UtcNow;

        public MatrixForm(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;                    // key: place on ITS monitor
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            KeyPreview = true;

            // MS Gothic: digits and halfwidth katakana share one advance
            // width. GenericTypographic measures the true advance (~8.3px
            // at 12pt) without the default format's padding.
            _font = new Font("MS Gothic", 12f, FontStyle.Bold);
            using (Graphics g = CreateGraphics())
            {
                _cellWI = (int)Math.Ceiling(g.MeasureString("0", _font, PointF.Empty,
                    StringFormat.GenericTypographic).Width);
                _cellHI = (int)Math.Ceiling(_font.GetHeight(g));
            }

            // Tuned pitch 1.25x (was 1.5x) on user request: denser rain,
            // more streams on screen at once.
            _pitch = (int)Math.Ceiling(_cellWI * 1.25f);
            _xOffset = (_pitch - _cellWI) / 2;
            int cols = Bounds.Width / _pitch;
            _rows = Bounds.Height / _cellHI;
            if (cols < 1) cols = 1;
            if (_rows < 1) _rows = 1;
            _engine = new RainEngine(cols, _rows, Program.NextEngineSeed());
            _buffer = new Bitmap(Bounds.Width, Bounds.Height);

            _timer.Interval = 33;               // ~30 fps
            _timer.Tick += OnTick;
        }

        // Lazily rasterize each charset glyph at one brightness level:
        // key -1 = white head, 0..63 = green fading toward black.
        // Sprites are drawn with DrawImage, which is far cheaper than
        // re-rasterizing text every frame. Each sprite is pre-composited
        // on opaque black so the per-cell blits in RenderFrame can use
        // SourceCopy and skip alpha blending entirely.
        Bitmap[] LevelSprites(int level)
        {
            Bitmap[] arr;
            if (_sprites.TryGetValue(level, out arr)) return arr;
            arr = new Bitmap[RainEngine.Charset.Length];
            Color color = level < 0
                ? Color.FromArgb(255, 255, 255)
                // Clamp: 255 * 64 / 63 overflows the byte range at the
                // brightest level (63 = full green, right behind head).
                : Color.FromArgb(0, Math.Min(255, 255 * (level + 1) / 63), 0);
            using (SolidBrush b = new SolidBrush(color))
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    Bitmap bmp = new Bitmap(_cellWI, _cellHI);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Black);    // opaque-on-black sprite
                        g.DrawString(RainEngine.Charset[i].ToString(), _font, b,
                            0f, 0f, StringFormat.GenericTypographic);
                    }
                    arr[i] = bmp;
                }
            }
            _sprites[level] = arr;
            return arr;
        }

        // Classic full-frame render: clear the back buffer, redraw every
        // visible cell of every column, then blit once (memory-DC style).
        void RenderFrame()
        {
            using (Graphics g = Graphics.FromImage(_buffer))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                // Sprites are opaque-on-black and the background under every
                // cell is black: SourceCopy skips alpha blending for
                // pixel-identical output at a fraction of the cost.
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.Clear(Color.Black);
                for (int c = 0; c < _engine.Cols; c++)
                {
                    Column col = _engine.GetColumn(c);
                    int x = c * _pitch + _xOffset;
                    for (int k = 0; k < col.Filled; k++)
                    {
                        int row = col.Y - k;
                        if (row < 0 || row >= _rows) continue;
                        int level = k == 0
                            ? -1
                            : (255 * (col.Len - k + 1) / col.Len) * 63 / 255;
                        g.DrawImage(LevelSprites(level)[col.Glyphs[k]],
                            x, row * _cellHI);
                    }
                }
            }
            using (Graphics sg = CreateGraphics())
                sg.DrawImageUnscaled(_buffer, 0, 0);
        }

        void OnTick(object sender, EventArgs e)
        {
            if (_busy) return;                  // never stack frames
            _busy = true;
            try
            {
                DateTime now = DateTime.UtcNow;
                float dt = (float)(now - _last).TotalSeconds;
                if (dt > 0.2f) dt = 0.2f;       // clamp after stalls
                _last = now;
                _engine.Step(dt);
                RenderFrame();
            }
            catch
            {
                Program.RequestExit();          // render failure: bail out silently
            }
            finally
            {
                _busy = false;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            // Capture cursor position at start so a tiny accidental
            // jiggle right after launch does not kill the saver.
            _mouseStart = Cursor.Position;
            _timer.Start();
            base.OnLoad(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // Surface lost (RDP reconnect, driver reset): blit the last
            // complete frame; the next tick redraws everything anyway.
            e.Graphics.DrawImageUnscaled(_buffer, 0, 0);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Program.RequestExit();              // any window gone: exit saver
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            Program.RequestExit();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Program.RequestExit();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point p = Cursor.Position;
            int dx = p.X - _mouseStart.X;
            int dy = p.Y - _mouseStart.Y;
            if (dx * dx + dy * dy > 100)        // > 10 px, squared to skip sqrt
                Program.RequestExit();
        }
    }

    // One column of falling glyphs, modeled after the classic Win32
    // digital rain: the stream grows from a single char, the bottom
    // (newest) char is white, older chars fade linearly to black.
    class Column
    {
        public int Y;              // bottom row of the stream (newest char)
        public int Len;            // stream length in rows, 8..25
        public float WaitTime;     // seconds between row steps (speed)
        public float Elapsed;      // time accumulated since the last step
        public int Filled;         // chars currently in the stream, 0..Len
        public int[] Glyphs;       // Glyphs[0] = newest (bottom)
    }

    // Pure simulation core. Knows nothing about windows or GDI.
    class RainEngine
    {
        public const int MinLen = 8;
        public const int MaxLen = 25;
        public const float Tick = 0.033f;   // speed quantization base

        // Halfwidth katakana + digits: every glyph has the same advance
        // width in MS Gothic, so the grid stays aligned. Kept as \uFFxx
        // escapes so the source file stays pure ASCII.
        public static readonly string Charset =
            "0123456789" +
            "\uFF71\uFF72\uFF73\uFF74\uFF75\uFF76\uFF77\uFF78\uFF79\uFF7A" +
            "\uFF7B\uFF7C\uFF7D\uFF7E\uFF7F\uFF80\uFF81\uFF82\uFF83\uFF84" +
            "\uFF85\uFF86\uFF87\uFF88\uFF89\uFF8A\uFF8B\uFF8C\uFF8D\uFF8E" +
            "\uFF8F\uFF90\uFF91\uFF92\uFF93\uFF94\uFF95\uFF96\uFF97\uFF98" +
            "\uFF99\uFF9A\uFF9B\uFF9C\uFF9D";

        readonly Random _rng;
        readonly Column[] _cols;
        public int Cols { get; private set; }
        public int Rows { get; private set; }

        public RainEngine(int cols, int rows, int seed)
        {
            Cols = cols;
            Rows = rows;
            _rng = new Random(seed);
            _cols = new Column[cols];
            for (int c = 0; c < cols; c++)
            {
                Column col = new Column();
                col.Glyphs = new int[MaxLen];
                Reset(col);
                _cols[c] = col;
            }
        }

        public Column GetColumn(int c)
        {
            return _cols[c];
        }

        void Reset(Column col)
        {
            col.Len = MinLen + _rng.Next(MaxLen - MinLen + 1);   // 8..25
            // Classic quantized speed: wait 1..4 ticks between row steps
            // (tuned up from 1..6 on user request: faster average fall).
            col.WaitTime = (1 + _rng.Next(4)) * Tick;
            col.Elapsed = 0f;
            col.Filled = 0;
            // Classic respawn scatter: 2 of 3 streams start at a random
            // mid-screen row, the rest at the top.
            col.Y = _rng.Next(3) != 0 ? _rng.Next(Rows) : 0;
        }

        // Advance the simulation by dt seconds.
        public void Step(float dt)
        {
            if (dt > 0.2f) dt = 0.2f;
            if (dt < 0f) dt = 0f;
            for (int c = 0; c < _cols.Length; c++)
            {
                Column col = _cols[c];
                col.Elapsed += dt;
                while (col.Elapsed >= col.WaitTime)
                {
                    col.Elapsed -= col.WaitTime;
                    // Push a new glyph at the bottom; when full, drop the oldest.
                    if (col.Filled < col.Len)
                    {
                        col.Glyphs[col.Filled] = _rng.Next(Charset.Length);
                        col.Filled++;
                    }
                    else
                    {
                        Array.Copy(col.Glyphs, 1, col.Glyphs, 0, col.Len - 1);
                        col.Glyphs[col.Len - 1] = _rng.Next(Charset.Length);
                    }
                    col.Y++;
                    // Whole stream left the screen: respawn immediately.
                    if (col.Y - col.Len > Rows)
                        Reset(col);
                }
            }
        }
    }
}
