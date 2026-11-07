using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using WindBotAIEditor.Services;

namespace WindBotRulings
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 검증용(창 없이):  --selftest <EDOPro폴더> <카드ID>
            if (args != null && args.Length >= 3 && args[0] == "--selftest")
            {
                Environment.ExitCode = SelfTest.Run(args[1], args[2]);
                return;
            }
            // 화면을 PNG 로 저장(창을 화면 밖에 띄웠다 바로 닫는다) — 디자인 확인용
            if (args != null && args.Length >= 2 && args[0] == "--screenshot")
            {
                Environment.ExitCode = Shot(args[1], args);
                return;
            }

            // 폼 생성만 검사(창 표시 없음):  --uicheck
            if (args != null && args.Length >= 1 && args[0] == "--uicheck")
            {
                try
                {
                    using var f = new MainForm();
                    Console.WriteLine("UICHECK OK");
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("UICHECK FAIL: " + ex.Message);
                    LogCrash(ex);
                    Environment.ExitCode = 1;
                }
                return;
            }

            try
            {
                ApplicationConfiguration.Initialize();
                Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e) { LogCrash(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { LogCrash(e.ExceptionObject as Exception); };
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                MessageBox.Show("시작 중 오류가 발생했습니다.\r\n\r\n" + ex.Message +
                                "\r\n\r\n로그: %LOCALAPPDATA%\\WindBotAIEditor\\rulings\\startup-error.txt",
                                "WindBot 재정 검색기", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 화면을 PNG 로 저장한다. WinForms 는 Show() 없이는 자식이 안 그려지므로
        /// **화면 밖(-4000,-4000)** 에 잠깐 띄워 렌더한 뒤 바로 닫는다.
        ///   --screenshot out.png [--light] [--demo]
        /// </summary>
        private static int Shot(string path, string[] args)
        {
            try
            {
                ApplicationConfiguration.Initialize();
                using var f = new MainForm();
                f.PrepareShot();
                if (Array.IndexOf(args, "--light") >= 0) f.SetDark(false);
                else if (Array.IndexOf(args, "--dark") >= 0) f.SetDark(true);
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.Show();
                Application.DoEvents();
                if (Array.IndexOf(args, "--demo") >= 0) { f.Demo(); Application.DoEvents(); }
                System.Threading.Thread.Sleep(150);
                Application.DoEvents();

                string full = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(full, System.Drawing.Imaging.ImageFormat.Png);
                }
                f.Hide();
                Console.WriteLine("SCREENSHOT OK " + full + " " + f.Width + "x" + f.Height);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("SCREENSHOT FAIL " + ex.Message);
                LogCrash(ex);
                return 1;
            }
        }

        private static void LogCrash(Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(RulingsStore.Dir, "startup-error.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + (ex == null ? "(null)" : ex.ToString()),
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }

    internal sealed class Settings
    {
        public string EdoproRoot { get; set; } = "";
        public string LastSearch { get; set; } = "";
        public bool DarkMode { get; set; } = false;
        /// <summary>좌측 목록이 차지하는 비율 (0.47 = 스샷 2 비율)</summary>
        public double SplitRatio { get; set; } = 0.47;

        private static string PathOf => Path.Combine(RulingsStore.Dir, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(PathOf))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf, Encoding.UTF8)) ?? new Settings();
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try { File.WriteAllText(PathOf, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false)); }
            catch { }
        }

        /// <summary>최근 본 카드 (앞이 최신, 최대 50)</summary>
        public List<int> Recent { get; set; } = new List<int>();
        /// <summary>즐겨찾기 카드</summary>
        public List<int> Favorites { get; set; } = new List<int>();
    }

    internal sealed class MainForm : Form
    {
        private readonly Settings _cfg = Settings.Load();
        private RulingsData _cur = null;
        private bool _dark;
        private bool _loading;
        private bool _shot;   // --screenshot: 자동 DB 열기/온라인 수집 금지
        private bool _suppressSelect;   // 데모/스크린샷에서 선택 이벤트로 온라인 수집이 도는 것 방지

        private TextBox _txtRoot, _txtSearch;
        private ListView _list;
        private TabControl _tabs;
        private TextBox _txtFacts, _txtOfficial, _txtYugi, _txtMemo;
        private Label _lblStatus, _lblRoot, _lblSearch;
        private Button _btnFetch, _btnBrowse, _btnOpen, _btnSearch, _btnOpenSrc, _btnSaveMemo, _btnCache, _btnDark, _btnFav;
        private ComboBox _cmbMode;   // 검색 / 최근 / 즐겨찾기
        private Panel _top, _bar;
        private SplitContainer _split;
        private StatusStrip _bottom;
        private ToolStripStatusLabel _strip;

        private Label _emptyHint;

        // 팔레트 (2026-11-07: 한 곳에서 관리 — ListView/Tab 소유자 그리기도 같은 값을 쓴다)
        private Palette Pal { get { return _dark ? Palette.Dark : Palette.Light; } }

        public MainForm()
        {
            Text = "WindBot 재정 검색기";
            Width = 1080; Height = 780;
            MinimumSize = new Size(920, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Malgun Gothic", 9F);
            _dark = _cfg.DarkMode;

        #region UI 구성 (컨트롤 생성·배치)

            // ---- 상단 (2026-11-07: 절대배치 → TableLayoutPanel, 리사이즈 대응) ----
            _top = new Panel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(8) };

            var topGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 7,
                RowCount = 2,
                BackColor = Color.Transparent
            };
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));   // 라벨
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // 입력
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));   // 버튼
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));  // 버튼
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));  // 버튼
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));  // 모드
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));  // 다크
            topGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            topGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            _lblRoot = Ui.Text("EDOPro 폴더", SystemColors.ControlText);
            _lblRoot.Dock = DockStyle.Fill;
            _txtRoot = Ui.Input(_cfg.EdoproRoot, Color.White, SystemColors.ControlText);
            _txtRoot.Dock = DockStyle.Fill;
            _txtRoot.Margin = new Padding(0, 4, 6, 4);
            _btnBrowse = new Button { Text = "폴더 선택", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 3) };
            _btnOpen = new Button { Text = "카드 DB 열기", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 3) };
            _btnDark = new Button { Text = "다크 모드", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };

            _lblSearch = Ui.Text("카드 검색", SystemColors.ControlText);
            _lblSearch.Dock = DockStyle.Fill;
            _txtSearch = Ui.Input(_cfg.LastSearch, Color.White, SystemColors.ControlText);
            _txtSearch.Dock = DockStyle.Fill;
            _txtSearch.Margin = new Padding(0, 4, 6, 4);
            _btnSearch = new Button { Text = "검색 (Enter)", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 6, 3) };
            _cmbMode = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 6, 4) };
            _cmbMode.Items.AddRange(new object[] { "검색 결과", "최근 본 카드", "즐겨찾기" });
            _cmbMode.SelectedIndex = 0;
            _lblStatus = Ui.Text("카드 DB 를 먼저 여세요", Color.DimGray);
            _lblStatus.Dock = DockStyle.Fill;
            _lblStatus.Margin = new Padding(0, 4, 0, 4);
            _lblStatus.Visible = false;   // 상태는 하단 상태바에만 (상단 중복 제거)

            topGrid.Controls.Add(_lblRoot, 0, 0);
            topGrid.Controls.Add(_txtRoot, 1, 0);
            topGrid.Controls.Add(_btnBrowse, 2, 0);
            topGrid.Controls.Add(_btnOpen, 3, 0);
            topGrid.SetColumnSpan(_btnOpen, 1);
            topGrid.Controls.Add(_btnDark, 6, 0);
            topGrid.Controls.Add(_lblSearch, 0, 1);
            topGrid.Controls.Add(_txtSearch, 1, 1);
            topGrid.Controls.Add(_btnSearch, 2, 1);
            topGrid.Controls.Add(_cmbMode, 3, 1);
            topGrid.SetColumnSpan(_lblStatus, 3);
            topGrid.Controls.Add(_lblStatus, 4, 1);
            _top.Controls.Add(topGrid);

            // ---- 본문 ----
            _split = new SplitContainer { Dock = DockStyle.Fill };   // MinSize/거리는 Shown(실제 크기 확정)에서 설정 — 생성자에서 하면 예외로 앱이 죽는다

            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, GridLines = false };
            _list.Columns.Add("ID", 84);
            _list.Columns.Add("카드명", 190);
            _list.Columns.Add("구분", 300);
            _list.OwnerDraw = true;                 // 다크 모드에서 기본 흰 헤더/행 제거
            _emptyHint = Ui.Text("EDOPro 폴더를 선택하고 [카드 DB 열기] 를 누르면 카드가 나옵니다.", Color.DimGray);
            _emptyHint.Dock = DockStyle.Fill;
            _emptyHint.TextAlign = ContentAlignment.MiddleCenter;
            _emptyHint.Font = Ui.Small;
            _split.Panel1.Controls.Add(_emptyHint);
            _split.Panel1.Controls.Add(_list);
            _split.Panel1.Resize += delegate { SizeListColumns(); };

            _tabs = new TabControl { Dock = DockStyle.Fill };
            _txtFacts = MakeText(); _txtOfficial = MakeText(); _txtYugi = MakeText();
            _txtMemo = MakeText(); _txtMemo.ReadOnly = false;

            var pFacts = new TabPage("요약 (스크립트 사실)") { Padding = new Padding(2) };
            pFacts.Controls.Add(_txtFacts);
            var pOff = new TabPage("공식 Q&A (일본어)") { Padding = new Padding(2) };
            pOff.Controls.Add(_txtOfficial);
            var pYugi = new TabPage("Yugipedia (영문)") { Padding = new Padding(2) };
            pYugi.Controls.Add(_txtYugi);
            var pMemo = new TabPage("내 메모 (한국어)") { Padding = new Padding(2) };
            pMemo.Controls.Add(_txtMemo);
            _tabs.TabPages.AddRange(new[] { pFacts, pOff, pYugi, pMemo });
            WireOwnerDraw();

            _bar = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4, 3, 4, 3) };
            _btnFetch = new Button { Text = "온라인 수집 (공식→Yugipedia)", Left = 4, Top = 3, Width = 210, Height = 26 };
            _btnOpenSrc = new Button { Text = "원문 열기", Left = 222, Top = 3, Width = 90, Height = 26 };
            _btnSaveMemo = new Button { Text = "메모 저장", Left = 318, Top = 3, Width = 90, Height = 26 };
            _btnCache = new Button { Text = "캐시 폴더", Left = 414, Top = 3, Width = 90, Height = 26 };
            _btnFav = new Button { Text = "☆ 즐겨찾기", Left = 510, Top = 3, Width = 104, Height = 26 };
            _bar.Controls.AddRange(new Control[] { _btnFetch, _btnOpenSrc, _btnSaveMemo, _btnCache, _btnFav });

            _split.Panel2.Controls.Add(_tabs);
            _split.Panel2.Controls.Add(_bar);

            _bottom = new StatusStrip();
            _strip = new ToolStripStatusLabel("준비");
            _bottom.Items.Add(_strip);

            Controls.Add(_split);
            Controls.Add(_top);
            Controls.Add(_bottom);

            // ---- 이벤트 ----
            _btnBrowse.Click += delegate
            {
                using var dlg = new FolderBrowserDialog { Description = "EDOPro.exe 가 있는 폴더를 선택하세요 (예: D:\\edopro)" };
                if (Directory.Exists(_txtRoot.Text)) dlg.SelectedPath = _txtRoot.Text;
                if (dlg.ShowDialog() == DialogResult.OK) { _txtRoot.Text = dlg.SelectedPath; OpenDb(); }
            };
            _btnOpen.Click += delegate { OpenDb(); };
            _btnSearch.Click += delegate { DoSearch(); };
            _cmbMode.SelectedIndexChanged += delegate { if (!_loading) ReloadList(); };
            _btnFav.Click += delegate { ToggleFavorite(); };
            _txtSearch.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSearch(); } };
            _list.SelectedIndexChanged += delegate { if (!_suppressSelect) ShowSelected(); };
            _list.DoubleClick += delegate { DoSearch(); };
            _btnFetch.Click += async delegate { await FetchAsync(); };
            _btnSaveMemo.Click += delegate { SaveMemo(); };
            _btnCache.Click += delegate { try { Process.Start(new ProcessStartInfo { FileName = RulingsStore.Dir, UseShellExecute = true }); } catch { } };
            _btnOpenSrc.Click += delegate
            {
                var url = (_tabs.SelectedIndex == 1) ? _cur?.OfficialUrl
                        : (_tabs.SelectedIndex == 2) ? _cur?.YugipediaUrl : "";
                if (string.IsNullOrWhiteSpace(url)) { MessageBox.Show("이 탭에는 원문 링크가 없습니다."); return; }
                try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
            };
            _btnDark.Click += delegate { _dark = !_dark; _cfg.DarkMode = _dark; _cfg.Save(); ApplyTheme(); };

            FormClosing += delegate
            {
                try { _cfg.SplitRatio = (double)_split.SplitterDistance / Math.Max(1, _split.Width); } catch { }
                _cfg.Save();
            };

            Shown += delegate
            {
                ApplyTheme();
                if (_shot) return;      // 스크린샷 모드: 자동 DB 열기/검색(=온라인 수집) 금지 — 멈춤 방지
                if (Directory.Exists(_cfg.EdoproRoot)) OpenDb();
                if (!string.IsNullOrWhiteSpace(_cfg.LastSearch)) DoSearch();
                else if (_cfg.Recent.Count > 0) { _loading = true; _cmbMode.SelectedIndex = 1; _loading = false; ReloadList(); }
            };
        }

        private static TextBox MakeText()
        {
            return new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = true,
                ReadOnly = true,
                Font = new Font("Consolas", 9.5F)
            };
        }

        /// <summary>WinForms TextBox 는 \n 만으로는 줄바꿈되지 않는다 → \r\n 으로 정규화</summary>
        private static void SetText(TextBox box, string text)
        {
            var t = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            box.Text = t;
            box.SelectionStart = 0;
            box.SelectionLength = 0;
        }

/// <summary>좌우 비율을 창 폭에 맞춰 적용 (px 저장 금지 — 창 폭이 바뀌면 오른쪽이 좁아진다)</summary>
        #endregion

        #region 레이아웃·테마 (비율·다크모드)

        private void ApplySplitRatio()
        {
            try
            {
                double r = _cfg.SplitRatio;
                if (r < 0.25 || r > 0.72) r = 0.47;
                int usable = _split.Width - _split.SplitterWidth;
                // 최소 크기는 실제 폭이 확정된 뒤에만 (150px 기본 크기에서 설정하면 예외)
                if (usable > 260 + 470 + 40)
                {
                    _split.Panel1MinSize = 260;
                    _split.Panel2MinSize = 470;
                }
                int want = (int)(usable * r);
                int min = Math.Max(_split.Panel1MinSize, 200);
                int max = usable - Math.Max(_split.Panel2MinSize, 300);
                if (max <= min) return;
                _split.SplitterDistance = Math.Min(Math.Max(want, min), max);
            }
            catch { }
        }

        private void ApplyTheme()
        {
            var p = Pal;

            BackColor = p.Bg;
            ForeColor = p.Fg;
            _top.BackColor = p.Bg;
            _bar.BackColor = p.Bg;
            _lblRoot.ForeColor = p.Fg;
            _lblSearch.ForeColor = p.Fg;
            _lblStatus.ForeColor = p.Sub;

            // 주 동작은 강조, 나머지는 보조
            p.StyleButton(_btnFetch, true);
            p.StyleButton(_btnSearch, true);
            foreach (var b in new[] { _btnBrowse, _btnOpen, _btnDark, _btnOpenSrc, _btnSaveMemo, _btnCache, _btnFav })
                p.StyleButton(b);

            _txtRoot.BackColor = p.Bg2; _txtRoot.ForeColor = p.Fg;
            _txtSearch.BackColor = p.Bg2; _txtSearch.ForeColor = p.Fg;
            _cmbMode.BackColor = p.Bg2; _cmbMode.ForeColor = p.Fg;

            _list.BackColor = p.Bg2; _list.ForeColor = p.Fg;

            _split.BackColor = p.Border;   // 스플리터(가운데 선)가 보이도록
            _split.Panel1.BackColor = p.Bg;
            _split.Panel2.BackColor = p.Bg;

            _tabs.BackColor = p.Bg;
            foreach (TabPage tp in _tabs.TabPages) { tp.BackColor = p.Bg2; tp.ForeColor = p.Fg; }

            foreach (var t in new[] { _txtFacts, _txtOfficial, _txtYugi })
            {
                t.BackColor = p.Bg2;
                t.ForeColor = p.Fg;
            }
            _txtMemo.BackColor = _dark ? Color.FromArgb(30, 30, 32) : Color.White;
            _txtMemo.ForeColor = p.Fg;

            _bottom.BackColor = p.Bg;
            _bottom.ForeColor = p.Fg;
            _strip.ForeColor = p.Fg;
            if (_emptyHint != null) _emptyHint.ForeColor = p.Sub;

            _btnDark.Text = _dark ? "라이트 모드" : "다크 모드";

            UpdateEmptyHint();
            SizeListColumns();
            _list.Invalidate();
            _tabs.Invalidate();
        }

        /// <summary>--screenshot: Shown 에서 자동 DB 열기/온라인 수집을 하지 않게 한다.</summary>
        public void PrepareShot()
        {
            _shot = true;
        }

        /// <summary>--screenshot 용: 테마를 강제로 바꾼다.</summary>
        public void SetDark(bool dark)
        {
            _dark = dark;
            ApplyTheme();
        }

        /// <summary>--screenshot --demo : 카드 목록/요약/공식 탭에 예시 내용을 채워 화면을 확인한다.</summary>
        public void Demo()
        {
            _loading = true;
            _cmbMode.SelectedIndex = 0;
            _loading = false;
            _list.BeginUpdate();
            _list.Items.Clear();
            string[][] rows =
            {
                new[] { "14558128", "하루 우라라", "몬스터 · Lv1 · 빛/마법사 · 0/0" },
                new[] { "23434538", "증식의 G", "몬스터 · Lv3 · 땅/곤충 · 500/200" },
                new[] { "59438931", "유령토끼", "몬스터 · Lv4 · 빛/전사 · 0/1700" },
                new[] { "97268402", "이펙트 뵐러", "몬스터 · Lv1 · 빛/마법사/튜너 · 0/0" },
                new[] { "10045474", "무한포영", "함정 · 속공" }
            };
            foreach (var r in rows)
            {
                var it = new ListViewItem(r[0]);
                it.SubItems.Add(r[1]);
                it.SubItems.Add(r[2]);
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            _suppressSelect = true;
            _list.Items[0].Selected = true;
            _suppressSelect = false;
            SetText(_txtFacts, "[스크립트 사실]\r\n· 효과 종류: 유발 즉시 / 묘지 발동\r\n· 1턴 1번, 덱에서 패로 1장\r\n· 코스트: 패 1장 버리기");
            SetText(_txtOfficial, "[공식 Q&A 3건]\r\nQ. 상대가 드로우 단계에 발동한 경우에도 적용되나요?\r\nA. 네, 어느 단계에서 발동해도 동일하게 처리합니다.");
            _list.Focus();
            UpdateEmptyHint();
            Status("데모 · 카드 5건 · 재정 캐시 42건 · cid 12950");
        }

        /// <summary>목록 열 폭을 패널 폭에 맞춘다(가로 스크롤 방지).</summary>
        private void SizeListColumns()
        {
            if (_list == null || _list.Columns.Count < 3) return;
            int w = Math.Max(240, _split.Panel1.ClientSize.Width - 4);
            int id = 90;
            int name = (int)(w * 0.34);
            _list.Columns[0].Width = id;
            _list.Columns[1].Width = name;
            _list.Columns[2].Width = Math.Max(140, w - id - name - 22);
        }

        /// <summary>목록이 비었을 때만 안내 문구를 보여준다.</summary>
        private void UpdateEmptyHint()
        {
            if (_emptyHint == null) return;
            bool empty = _list == null || _list.Items.Count == 0;
            _emptyHint.Visible = empty;
            if (empty) _emptyHint.BringToFront(); else _list.BringToFront();
        }

        /// <summary>
        /// ListView/TabControl 소유자 그리기 (2026-11-07).
        /// 다크 테마에서 기본 컨트롤은 **흰 헤더·흰 행·회색 탭**으로 그려져 앱과 따로 놀았다.
        /// </summary>
        private void WireOwnerDraw()
        {
            _list.DrawColumnHeader += delegate (object s, DrawListViewColumnHeaderEventArgs e)
            {
                var p = Pal;
                using (var b = new SolidBrush(p.Bg2)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var pen = new Pen(p.Border)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, _list.Font, r, p.Sub,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
            _list.DrawItem += delegate (object s, DrawListViewItemEventArgs e) { e.DrawDefault = false; };
            _list.DrawSubItem += delegate (object s, DrawListViewSubItemEventArgs e)
            {
                var p = Pal;
                bool sel = e.Item.Selected;
                var back = sel ? p.Sel : (_dark ? p.Card : Color.White);
                using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
                var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _list.Font, r, sel ? Color.White : p.Fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };

            _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            _tabs.ItemSize = new Size(176, 26);
            _tabs.DrawItem += delegate (object s, DrawItemEventArgs e)
            {
                var p = Pal;
                bool sel = e.Index == _tabs.SelectedIndex;
                using (var b = new SolidBrush(sel ? p.Bg2 : p.Bg)) e.Graphics.FillRectangle(b, e.Bounds);
                if (sel)
                {
                    using (var b = new SolidBrush(p.Accent))
                        e.Graphics.FillRectangle(b, new Rectangle(e.Bounds.X, e.Bounds.Bottom - 2, e.Bounds.Width, 2));
                }
                TextRenderer.DrawText(e.Graphics, _tabs.TabPages[e.Index].Text, _tabs.Font, e.Bounds,
                    sel ? p.Fg : p.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
        }

        #endregion

        #region 카드 검색·목록 (검색/최근/즐겨찾기)

        private string EdoproRoot => (_txtRoot.Text ?? "").Trim().Trim('"');
        private void Status(string s) { _strip.Text = s; _lblStatus.Text = s; }

        private void OpenDb()
        {
            try
            {
                var root = EdoproRoot;
                if (!Directory.Exists(root)) { Status("폴더가 없습니다"); return; }
                var wb = Path.Combine(root, "WindBot");
                var paths = CardDatabase.FindCandidates(Directory.Exists(wb) ? wb : root);
                bool ok = CardDatabase.Instance.OpenMany(paths);
                _cfg.EdoproRoot = root; _cfg.Save();
                Status(ok
                    ? "카드 DB " + CardDatabase.Instance.Paths.Count + "개 · 별칭 " + CardDatabase.Instance.AliasGroupCount + "그룹"
                    : "cdb 를 찾지 못했습니다");
            }
            catch (Exception ex) { Status("DB 오류: " + ex.Message); }
        }

        /// <summary>검색 결과를 "한 화면에 구분되어" 보이도록 정보 열을 채운다</summary>
        private static string InfoOf(CardDatabase.CardInfo c)
        {
            var sb = new StringBuilder();
            bool monster = (c.Type & 0x1) != 0;
            bool spell = (c.Type & 0x2) != 0;
            bool trap = (c.Type & 0x4) != 0;
            sb.Append(monster ? "몬스터" : spell ? "마법" : trap ? "함정" : "기타");
            if (monster)
            {
                sb.Append(" · Lv").Append(c.Level);
                sb.Append(" · ").Append(c.Atk).Append("/").Append(c.Def);
                if ((c.Type & 0x4000) != 0) sb.Append(" · 토큰");
                if ((c.Type & 0x800000) != 0) sb.Append(" · 엑시즈");
                else if ((c.Type & 0x2000) != 0) sb.Append(" · 싱크로");
                else if ((c.Type & 0x40) != 0) sb.Append(" · 융합");
                else if ((c.Type & 0x4000000) != 0) sb.Append(" · 링크");
                else if ((c.Type & 0x1000000) != 0) sb.Append(" · 펜듈럼");
            }
            if (!string.IsNullOrWhiteSpace(c.Source)) sb.Append(" · ").Append(c.Source);
            return sb.ToString();
        }

        private void DoSearch()
        {
            if (_cmbMode.SelectedIndex != 0) { _loading = true; _cmbMode.SelectedIndex = 0; _loading = false; }
            ReloadList();
        }

        /// <summary>모드(검색/최근/즐겨찾기)에 따라 목록을 다시 채운다</summary>
        private void ReloadList()
        {
            try
            {
                int mode = _cmbMode.SelectedIndex;
                List<CardDatabase.CardInfo> results;
                string label;

                if (mode == 1)
                {
                    results = new List<CardDatabase.CardInfo>();
                    foreach (var id in _cfg.Recent) { var info = CardDatabase.Instance.Info(id); if (info != null) results.Add(info); }
                    label = "최근 본 카드";
                }
                else if (mode == 2)
                {
                    results = new List<CardDatabase.CardInfo>();
                    foreach (var id in _cfg.Favorites) { var info = CardDatabase.Instance.Info(id); if (info != null) results.Add(info); }
                    label = "즐겨찾기";
                }
                else
                {
                    var kw = (_txtSearch.Text ?? "").Trim();
                    if (kw.Length == 0) { Status("검색어를 입력하세요"); return; }
                    _cfg.LastSearch = kw; _cfg.Save();
                    results = CardDatabase.Instance.Search(kw, 300);
                    label = "검색 결과";
                }

                // 동명 카드 구분용 번호
                var nameCount = results.GroupBy(r => r.Name ?? "").ToDictionary(g => g.Key, g => g.Count());
                var nameSeen = new Dictionary<string, int>();

                _list.BeginUpdate();
                _list.Items.Clear();
                foreach (var c in results)
                {
                    var nm = c.Name ?? "";
                    var it = new ListViewItem(c.Id.ToString());
                    it.SubItems.Add(nm);
                    string info = InfoOf(c);
                    if (nameCount.TryGetValue(nm, out var cnt) && cnt > 1)
                    {
                        nameSeen[nm] = (nameSeen.TryGetValue(nm, out var k) ? k : 0) + 1;
                        info = "동명 " + nameSeen[nm] + "/" + cnt + " · " + info;
                    }
                    it.SubItems.Add(info);
                    it.Tag = c.Id;
                    _list.Items.Add(it);
                }
                _list.EndUpdate();

                UpdateEmptyHint();
                Status(label + " " + _list.Items.Count + "건 — 항목을 선택하면 아래에 표시됩니다");
                if (_list.Items.Count > 0) { _list.Items[0].Selected = true; _list.Select(); }
                else { _cur = null; ClearView(); }
            }
            catch (Exception ex) { Status("목록 오류: " + ex.Message); }
        }

        /// <summary>최근 본 카드 기록(앞이 최신, 최대 50)</summary>
        private void AddRecent(int id)
        {
            try
            {
                _cfg.Recent.Remove(id);
                _cfg.Recent.Insert(0, id);
                if (_cfg.Recent.Count > 50) _cfg.Recent.RemoveRange(50, _cfg.Recent.Count - 50);
                _cfg.Save();
            }
            catch { }
        }

        private void ToggleFavorite()
        {
            if (_cur == null) return;
            try
            {
                if (_cfg.Favorites.Contains(_cur.Id)) _cfg.Favorites.Remove(_cur.Id);
                else _cfg.Favorites.Insert(0, _cur.Id);
                _cfg.Save();
                UpdateFavButton();
                if (_cmbMode.SelectedIndex == 2) ReloadList();
                Status((_cfg.Favorites.Contains(_cur.Id) ? "즐겨찾기 추가: " : "즐겨찾기 해제: ") + _cur.Id);
            }
            catch { }
        }

        private void UpdateFavButton()
        {
            bool fav = _cur != null && _cfg.Favorites.Contains(_cur.Id);
            _btnFav.Text = fav ? "★ 즐겨찾기" : "☆ 즐겨찾기";
        }

        private void ClearView()
        {
            SetText(_txtFacts, ""); SetText(_txtOfficial, ""); SetText(_txtYugi, ""); SetText(_txtMemo, "");
        }

        private static string BuildOfficialText(RulingsData d)
        {
            if (!string.IsNullOrWhiteSpace(d.OfficialText) && d.OfficialText.Contains("[공식 Q&A]"))
                return d.OfficialText;

            var sb = new StringBuilder();
            if (d.OfficialCids != null && d.OfficialCids.Count > 0)
            {
                sb.AppendLine("[온라인 수집] 을 누르면 공식 Q&A 본문을 가져옵니다.");
                sb.AppendLine("cid: " + string.Join(", ", d.OfficialCids));
                sb.AppendLine();
                sb.AppendLine("공식 페이지: https://www.db.yugioh-card.com/yugiohdb/faq_search.action?ope=4&cid="
                              + d.OfficialCids[0] + "&request_locale=ja");
            }
            else
            {
                sb.AppendLine("아직 수집하지 않았습니다 (cid 없음).");
                sb.AppendLine("[온라인 수집] 을 누르면 Yugipedia 를 거쳐 공식 Q&A(cid)를 찾습니다.");
                sb.AppendLine();
                sb.AppendLine("공식 DB 직접 검색: https://www.db.yugioh-card.com/yugiohdb/faq_search.action?ope=2&request_locale=ja");
            }
            return sb.ToString();
        }

        #endregion

        #region 재정 수집·표시 (수집/메모/탭 채우기)

        private void ShowSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            int id = (int)_list.SelectedItems[0].Tag;
            try
            {
                _cur = RulingsStore.Load(id);
                _cur.Id = id;
                if (string.IsNullOrWhiteSpace(_cur.NameKr)) _cur.NameKr = CardDatabase.Instance.NameOf(id);
                if (string.IsNullOrWhiteSpace(_cur.FactsText)) _cur.FactsText = ScriptFacts.Build(EdoproRoot, id);

                SetText(_txtFacts, _cur.FactsText);
                SetText(_txtOfficial, BuildOfficialText(_cur));
                SetText(_txtYugi, string.IsNullOrWhiteSpace(_cur.YugipediaText)
                    ? "(아직 수집 안 됨)\r\n\r\n[온라인 수집] 을 누르면 Yugipedia 'Card Rulings:' 페이지를 읽습니다."
                    : _cur.YugipediaText);
                SetText(_txtMemo, _cur.Memo);
                AddRecent(id);
                UpdateFavButton();
                Status(id + " · " + _cur.NameKr
                    + (string.IsNullOrWhiteSpace(_cur.NameEn) ? "" : " / " + _cur.NameEn)
                    + (string.IsNullOrWhiteSpace(_cur.FetchedAt) ? "" : " · 수집 " + _cur.FetchedAt));
            }
            catch (Exception ex) { Status("표시 오류: " + ex.Message); }
        }

        private void SaveMemo()
        {
            if (_cur == null) return;
            _cur.Memo = _txtMemo.Text;
            RulingsStore.Save(_cur);
            Status("메모 저장: " + _cur.Id);
        }

        private async Task FetchAsync()
        {
            if (_cur == null) return;
            try
            {
                _btnFetch.Enabled = false;
                Status("영문명 확인 중…");
                if (string.IsNullOrWhiteSpace(_cur.NameEn))
                    _cur.NameEn = await RulingsFetcher.ResolveEnglishNameAsync(_cur.Id);

                Status("Yugipedia 재정 가져오는 중…");
                var (wikiText, wikiUrl, wikiRaw, cids) = await RulingsFetcher.FetchYugipediaAsync(_cur.NameEn);
                if (!string.IsNullOrWhiteSpace(wikiText))
                {
                    _cur.YugipediaText = wikiText;
                    _cur.YugipediaUrl = wikiUrl;
                }

                _cur.OfficialCids = cids ?? new List<int>();
                if (_cur.OfficialCids.Count > 0)
                {
                    Status("공식 Q&A 목록 가져오는 중…");
                    var (listText, listUrl, qCount) = await RulingsFetcher.FetchOfficialAllAsync(_cur.OfficialCids[0], null);
                    _cur.OfficialUrl = listUrl;
                    if (!string.IsNullOrWhiteSpace(listText)) _cur.OfficialText = listText;
                    Status("공식 Q&A " + qCount + "건 수집 (본문 포함)");
                }

                // cid 로 못 찾았으면 **일본어 카드명으로 공식 Q&A 키워드 검색**(ope=2) 으로 재시도
                if (string.IsNullOrWhiteSpace(_cur.OfficialText) || !_cur.OfficialText.Contains("[공식 Q&A]"))
                {
                    Status("일본어 카드명 확인 중…");
                    if (string.IsNullOrWhiteSpace(_cur.NameJa))
                        _cur.NameJa = await RulingsFetcher.ResolveJapaneseNameAsync(_cur.NameEn);
                    if (!string.IsNullOrWhiteSpace(_cur.NameJa))
                    {
                        Status("공식 Q&A 키워드 검색: " + _cur.NameJa);
                        var (t2, u2, c2) = await RulingsFetcher.FetchOfficialAllAsync(0, _cur.NameJa);
                        if (c2 > 0) { _cur.OfficialText = t2; _cur.OfficialUrl = u2; }
                        Status("공식 Q&A " + c2 + "건 (키워드 검색)");
                    }
                }

                _cur.FetchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                _cur.Memo = _txtMemo.Text;
                RulingsStore.Save(_cur);
                ShowSelected();

                bool any = !string.IsNullOrWhiteSpace(_cur.YugipediaText) || _cur.OfficialCids.Count > 0;
                Status(any
                    ? "수집 완료 · " + _cur.FetchedAt + (_cur.OfficialCids.Count > 0 ? " (cid " + _cur.OfficialCids.Count + "건)" : "")
                    : "수집 실패 — 영문명/페이지 없음 (내 메모를 사용하세요)");
            }
            catch (Exception ex) { Status("수집 오류: " + ex.Message); }
            finally { _btnFetch.Enabled = true; }
        }
    }

    /// <summary>창 없이 코어 검증</summary>
    internal static class SelfTest
    {
        public static int Run(string edoproRoot, string idText)
        {
            var log = new StringBuilder();
            void L(string s) { log.AppendLine(s); Console.WriteLine(s); }
            try
            {
                if (!int.TryParse(idText, out var id)) { L("카드ID 파싱 실패"); return 2; }
                var wb = Path.Combine(edoproRoot, "WindBot");
                var paths = CardDatabase.FindCandidates(Directory.Exists(wb) ? wb : edoproRoot);
                bool ok = CardDatabase.Instance.OpenMany(paths);
                L("cdb " + CardDatabase.Instance.Paths.Count + "개 열림=" + ok + " · " + CardDatabase.Instance.NameOf(id));

                var d = RulingsStore.Load(id);
                d.Id = id;
                d.NameKr = CardDatabase.Instance.NameOf(id);
                d.FactsText = ScriptFacts.Build(edoproRoot, id);
                L("스크립트 사실: " + d.FactsText.Length + "자");

                d.NameEn = RulingsFetcher.ResolveEnglishNameAsync(id).GetAwaiter().GetResult();
                L("영문명: " + (d.NameEn.Length == 0 ? "(없음)" : d.NameEn));

                var (wiki, wurl, wraw, cids) = RulingsFetcher.FetchYugipediaAsync(d.NameEn).GetAwaiter().GetResult();
                d.YugipediaText = wiki; d.YugipediaUrl = wurl; d.OfficialCids = cids ?? new List<int>();
                L("Yugipedia: " + wiki.Length + "자 · cid " + d.OfficialCids.Count + "건");

                if (d.OfficialCids.Count > 0)
                {
                    var (lt, lu, qc) = RulingsFetcher.FetchOfficialAllAsync(d.OfficialCids[0], null).GetAwaiter().GetResult();
                    d.OfficialText = lt; d.OfficialUrl = lu;
                    L("공식 Q&A 전문(cid): " + qc + "건 / " + lt.Length + "자");
                }
                if (string.IsNullOrWhiteSpace(d.OfficialText) || !d.OfficialText.Contains("[공식 Q&A]"))
                {
                    d.NameJa = RulingsFetcher.ResolveJapaneseNameAsync(d.NameEn).GetAwaiter().GetResult();
                    L("일본어명: " + (d.NameJa.Length == 0 ? "(없음)" : d.NameJa));
                    if (!string.IsNullOrWhiteSpace(d.NameJa))
                    {
                        var (t2, u2, c2) = RulingsFetcher.FetchOfficialAllAsync(0, d.NameJa).GetAwaiter().GetResult();
                        if (c2 > 0) { d.OfficialText = t2; d.OfficialUrl = u2; }
                        L("공식 Q&A 전문(키워드): " + c2 + "건 / " + t2.Length + "자");
                    }
                }

                d.FetchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                RulingsStore.Save(d);
                L("캐시: " + RulingsStore.PathOf(id));
                try { File.WriteAllText(Path.Combine(RulingsStore.Dir, "selftest-last.txt"), log.ToString(), new UTF8Encoding(false)); } catch { }
                return (wiki.Length > 0) ? 0 : 3;
            }
            catch (Exception ex) { L("오류: " + ex); return 9; }
        }
        #endregion
    }
}








