using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TouchpadViewer
{
    // ---------------------------------------------------------------
    // Win32 / HID P/Invoke
    // ---------------------------------------------------------------
    static class Native
    {
        public const int WM_INPUT = 0x00FF;
        public const int WM_INPUT_DEVICE_CHANGE = 0x00FE;

        public const uint RID_INPUT = 0x10000003;
        public const uint RIDI_DEVICENAME = 0x20000007;
        public const uint RIDI_DEVICEINFO = 0x2000000B;
        public const uint RIDI_PREPARSEDDATA = 0x20000005;

        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RIDEV_DEVNOTIFY = 0x00002000;

        public const int RIM_TYPEHID = 2;
        public const int HidP_Input = 0;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        // 二重起動したとき、2つ目から1つ目へ「ウィンドウを表示して」と伝えるための登録メッセージ
        public static readonly uint WM_SHOW_EXISTING = RegisterWindowMessage("TouchpadGestures.ShowWindow");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICELIST
        {
            public IntPtr Device;
            public uint Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        // sizeof == 72 (Range / NotRange union は先頭の ushort だけ使う)
        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            public byte IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            public byte IsRange;
            public byte IsStringRange;
            public byte IsDesignatorRange;
            public byte IsAbsolute;
            public byte HasNull;
            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;
            public ushort R0, R1, R2, R3, R4;
            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            public ushort UsageMin, UsageMax;
            public ushort StringMin, StringMax;
            public ushort DesignatorMin, DesignatorMax;
            public ushort DataIndexMin, DataIndexMax;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterRawInputDevices(
            [In] RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll")]
        public static extern uint GetRawInputData(
            IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll")]
        public static extern uint GetRawInputDeviceList(
            [Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);

        // 既定 (Ansi) だと GetRawInputDeviceInfoA が呼ばれ、デバイス名が ANSI で返って文字化けする
        [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
        public static extern uint GetRawInputDeviceInfo(
            IntPtr device, uint command, IntPtr data, ref uint size);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

        [DllImport("hid.dll")]
        public static extern int HidP_GetValueCaps(
            int reportType, [Out] HIDP_VALUE_CAPS[] caps, ref ushort length, IntPtr preparsed);

        [DllImport("hid.dll")]
        public static extern int HidP_GetUsageValue(
            int reportType, ushort usagePage, ushort linkCollection, ushort usage,
            out uint value, IntPtr preparsed, byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        public static extern int HidP_GetUsages(
            int reportType, ushort usagePage, ushort linkCollection,
            [Out] ushort[] usageList, ref uint usageLength,
            IntPtr preparsed, byte[] report, uint reportLength);
    }

    // ---------------------------------------------------------------
    // 1本の指の状態
    // ---------------------------------------------------------------
    struct Finger
    {
        public int Id;
        public int X;
        public int Y;
    }

    // ---------------------------------------------------------------
    // PTP デバイス 1台分: Preparsed Data を保持し、レポートを解釈する
    // ---------------------------------------------------------------
    class PtpDevice : IDisposable
    {
        const ushort PageGenericDesktop = 0x01;
        const ushort PageDigitizer = 0x0D;
        const ushort UsageX = 0x30;
        const ushort UsageY = 0x31;
        const ushort UsageTipSwitch = 0x42;
        const ushort UsageContactId = 0x51;
        const ushort UsageContactCount = 0x54;
        const ushort UsageScanTime = 0x56;

        readonly IntPtr preparsed;
        // ReportID -> 指スロットごとの LinkCollection
        readonly Dictionary<byte, List<ushort>> fingerLinks = new Dictionary<byte, List<ushort>>();

        // Hybrid モードのレポート分割の組み立ては、HID に依存しない Gestures 側で行う
        readonly Gestures.PtpFrameAssembler assembler = new Gestures.PtpFrameAssembler();
        readonly List<Finger> current = new List<Finger>();
        uint simScanTime;

        public int XMax { get; private set; }
        public int YMax { get; private set; }
        public uint ScanTime { get { return assembler.ScanTime; } }
        public int ContactCount { get { return assembler.ContactCount; } }
        public string Name { get; private set; }
        public List<Finger> Fingers { get { return current; } }

        PtpDevice(IntPtr preparsed) { this.preparsed = preparsed; }

        public static PtpDevice Open(IntPtr hDevice)
        {
            uint size = 0;
            Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
            if (size == 0) return null;

            IntPtr pre = Marshal.AllocHGlobal((int)size);
            if (Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_PREPARSEDDATA, pre, ref size) == uint.MaxValue)
            {
                Marshal.FreeHGlobal(pre);
                return null;
            }

            PtpDevice dev = new PtpDevice(pre);
            if (!dev.LoadCaps())
            {
                dev.Dispose();
                return null;
            }
            dev.Name = ReadName(hDevice);
            return dev;
        }

        bool LoadCaps()
        {
            Native.HIDP_CAPS caps;
            if (Native.HidP_GetCaps(preparsed, out caps) != Native.HIDP_STATUS_SUCCESS) return false;
            if (caps.UsagePage != PageDigitizer || caps.Usage != 0x05) return false;

            ushort n = caps.NumberInputValueCaps;
            Native.HIDP_VALUE_CAPS[] vc = new Native.HIDP_VALUE_CAPS[n];
            if (Native.HidP_GetValueCaps(Native.HidP_Input, vc, ref n, preparsed) != Native.HIDP_STATUS_SUCCESS)
                return false;

            for (int i = 0; i < n; i++)
            {
                if (vc[i].UsagePage != PageGenericDesktop) continue;
                bool isX = Covers(vc[i], UsageX);
                bool isY = Covers(vc[i], UsageY);
                if (!isX && !isY) continue;

                if (isX) XMax = Math.Max(XMax, vc[i].LogicalMax);
                if (isY) YMax = Math.Max(YMax, vc[i].LogicalMax);

                // LinkCollection 0 はトップレベル。指ごとのコレクションは 1 以上
                if (isX && vc[i].LinkCollection != 0)
                {
                    List<ushort> links;
                    if (!fingerLinks.TryGetValue(vc[i].ReportID, out links))
                    {
                        links = new List<ushort>();
                        fingerLinks[vc[i].ReportID] = links;
                    }
                    if (!links.Contains(vc[i].LinkCollection)) links.Add(vc[i].LinkCollection);
                }
            }
            foreach (List<ushort> l in fingerLinks.Values) l.Sort();
            return fingerLinks.Count > 0;
        }

        static bool Covers(Native.HIDP_VALUE_CAPS c, ushort usage)
        {
            if (c.IsRange != 0) return usage >= c.UsageMin && usage <= c.UsageMax;
            return c.UsageMin == usage; // NotRange.Usage は UsageMin と同じオフセット
        }

        static string ReadName(IntPtr hDevice)
        {
            uint chars = 0;
            Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICENAME, IntPtr.Zero, ref chars);
            if (chars == 0) return "";
            IntPtr buf = Marshal.AllocHGlobal((int)chars * 2);
            try
            {
                Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICENAME, buf, ref chars);
                return Marshal.PtrToStringUni(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // 1レポートを食わせる。1フレーム(全指)が揃ったら true。
        // Hybrid モードでは 1フレームが複数レポートに分割され、
        // 先頭レポートだけ Contact Count に総数が入り、続きは 0 になる。
        // 組み立ては Gestures.PtpFrameAssembler (単体テスト済み) が行い、ここは HID の解読だけ。
        public bool Feed(byte[] report)
        {
            List<ushort> links;
            if (!fingerLinks.TryGetValue(report[0], out links)) return false;

            uint rlen = (uint)report.Length;

            int? count = null;
            uint c;
            if (Native.HidP_GetUsageValue(Native.HidP_Input, PageDigitizer, 0,
                UsageContactCount, out c, preparsed, report, rlen) == Native.HIDP_STATUS_SUCCESS)
                count = (int)c;

            uint? scan = null;
            uint st;
            if (Native.HidP_GetUsageValue(Native.HidP_Input, PageDigitizer, 0,
                UsageScanTime, out st, preparsed, report, rlen) == Native.HIDP_STATUS_SUCCESS)
                scan = st;

            List<Gestures.TouchSlot> slots = new List<Gestures.TouchSlot>();
            foreach (ushort link in links) slots.Add(ReadSlot(link, report, rlen));

            return FeedReport(new Gestures.PtpReport(count, scan, slots));
        }

        // 解読済みレポートを組み立て器に渡し、フレームが完成したら current を更新する
        bool FeedReport(Gestures.PtpReport report)
        {
            if (!assembler.Feed(report)) return false;

            current.Clear();
            foreach (Gestures.TouchSlot s in assembler.Fingers)
            {
                Finger f = new Finger();
                f.Id = s.Id;
                f.X = s.X;
                f.Y = s.Y;
                current.Add(f);
            }
            return true;
        }

        // 読み取りに失敗したスロットは「触れていない」扱いにする
        Gestures.TouchSlot ReadSlot(ushort link, byte[] report, uint rlen)
        {
            Gestures.TouchSlot none = new Gestures.TouchSlot(0, 0, 0, false);

            uint x, y, id;
            if (Native.HidP_GetUsageValue(Native.HidP_Input, PageGenericDesktop, link, UsageX,
                out x, preparsed, report, rlen) != Native.HIDP_STATUS_SUCCESS) return none;
            if (Native.HidP_GetUsageValue(Native.HidP_Input, PageGenericDesktop, link, UsageY,
                out y, preparsed, report, rlen) != Native.HIDP_STATUS_SUCCESS) return none;
            if (Native.HidP_GetUsageValue(Native.HidP_Input, PageDigitizer, link, UsageContactId,
                out id, preparsed, report, rlen) != Native.HIDP_STATUS_SUCCESS) return none;

            ushort[] usages = new ushort[16];
            uint ulen = (uint)usages.Length;
            bool tip = false;
            if (Native.HidP_GetUsages(Native.HidP_Input, PageDigitizer, link, usages, ref ulen,
                preparsed, report, rlen) == Native.HIDP_STATUS_SUCCESS)
            {
                for (int k = 0; k < ulen; k++)
                    if (usages[k] == UsageTipSwitch) { tip = true; break; }
            }
            return new Gestures.TouchSlot((int)id, (int)x, (int)y, tip);
        }

        // ---- 疑似デバイス: 実機なしで画面・判定・SendInput を通すためのもの ----
        public static PtpDevice CreateSimulated(int xMax, int yMax)
        {
            PtpDevice dev = new PtpDevice(IntPtr.Zero);
            dev.XMax = xMax;
            dev.YMax = yMax;
            dev.Name = "(simulated)";
            return dev;
        }

        // 指の一覧を、実機と同じ「スロット数で分割した Hybrid レポート」に変換して流し込む。
        // 先頭レポートに総数を入れ、続きは 0。余ったスロットはパディング。
        public void SimulateFrame(IList<Finger> fingers, int slotsPerReport)
        {
            int n = fingers.Count;
            int idx = 0;
            bool first = true;
            simScanTime += 160; // 約16ms (単位は100μs)
            do
            {
                List<Gestures.TouchSlot> slots = new List<Gestures.TouchSlot>();
                for (int k = 0; k < slotsPerReport; k++, idx++)
                {
                    if (idx < n)
                        slots.Add(new Gestures.TouchSlot(fingers[idx].Id, fingers[idx].X, fingers[idx].Y, true));
                    else
                        slots.Add(new Gestures.TouchSlot(0, 0, 0, false));
                }
                FeedReport(new Gestures.PtpReport(first ? n : 0, first ? (uint?)simScanTime : null, slots));
                first = false;
            } while (idx < n);
        }

        public void Dispose()
        {
            if (preparsed != IntPtr.Zero) Marshal.FreeHGlobal(preparsed);
        }
    }

    // ---------------------------------------------------------------
    // 表示ウィンドウ
    // ---------------------------------------------------------------
    class MainForm : Form
    {
        readonly Dictionary<IntPtr, PtpDevice> devices = new Dictionary<IntPtr, PtpDevice>();
        readonly Panel canvas = new Panel();
        readonly Label info = new Label();
        readonly Timer timer = new Timer();

        PtpDevice active;
        int ptpDeviceCount;
        long frames;
        long framesLastSec;
        int fps;
        int tick;
        bool dirty = true;
        string registerError = "";

        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly List<string> gestureLog = new List<string>();
        string flashText = "";
        long flashUntilMs;
        // 連続発火の回数 (指を全部離すまでの合計)。画面に「× N」と出すための集計
        readonly Dictionary<string, int> repeatTotals = new Dictionary<string, int>();

        // 実行は既定で OFF。テスト中に意図せずキー入力が飛ばないようにする
        readonly CheckBox chkExec = new CheckBox();
        readonly Gestures.Win32InputSender sender = new Gestures.Win32InputSender();
        Gestures.GestureDispatcher dispatcher;

        // 疑似指モード: マウスで指を置き、実機と同じ経路 (分割レポート → 組み立て → 判定 → SendInput) に流す
        readonly CheckBox chkSim = new CheckBox();
        readonly PtpDevice simDevice = PtpDevice.CreateSimulated(1500, 1000);
        readonly List<Finger> simFingers = new List<Finger>();
        bool simMode;
        bool simEmptyPending; // 全員離れたことを 1 回だけ流す
        int simNextId = 1;
        int simDrag = -1;
        const int SimMaxFingers = 5;
        const int SimSlotsPerReport = 4; // 5本指が2レポートに分割され、Hybrid の経路を通る

        // 割り当ての設定ファイル。場所は Gestures.ConfigLocation が決める:
        //   コマンドラインの --config  >  exe の隣に保存した場所 (「設定ファイルの場所を選ぶ」)  >  既定 (AppData)
        // 既定の AppData は、Claude など MSIX 版アプリの中から起動すると別のコピーに振り替えられて見える。
        // 常用する場所は、AppData の外に置いて「設定ファイルの場所を選ぶ」で指定するのがよい
        readonly string[] cmdArgs;
        string configPath;
        string configSource;

        static string DefaultConfigPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TouchpadGestures", "gestures.json");
            }
        }

        string ConfigPath { get { return configPath; } }

        void ResolveConfigLocation()
        {
            Gestures.ConfigLocationInfo loc =
                Gestures.ConfigLocation.Resolve(cmdArgs, AppContext.BaseDirectory, DefaultConfigPath);
            configPath = loc.Path;
            configSource = loc.Source;
        }

        static string ShortPath(string path)
        {
            const int max = 46;
            return path.Length <= max ? path : "…" + path.Substring(path.Length - (max - 1));
        }

        // 設定を読み直して dispatcher を作り替える。失敗したら直前の設定を維持して false を返す
        bool LoadBindings()
        {
            string path = ConfigPath;
            bool ok = false;
            try
            {
                if (!File.Exists(path))
                {
                    // 見本を作るのは既定の場所だけ。選んだ場所にファイルが無いのは、場所の間違いの可能性が高い
                    if (!string.Equals(path, DefaultConfigPath, StringComparison.OrdinalIgnoreCase))
                        throw new FileNotFoundException("設定ファイルが見つかりません。", path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, Gestures.GestureConfig.SampleJson, new UTF8Encoding(false));
                    AddLog("設定ファイルの見本を作成しました");
                }
                IReadOnlyList<Gestures.GestureBinding> bindings = Gestures.GestureConfig.Load(path);
                List<string> ignoreApps = Gestures.GestureConfig.LoadIgnoreApps(path);
                ReleaseHolds();
                dispatcher = new Gestures.GestureDispatcher(bindings, sender, Gestures.ForegroundApp.GetProcessName,
                    Gestures.KeyboardState.GetModifiers, Gestures.KeyboardState.GetHeldModifierKeys, ignoreApps);
                AddLog("設定を読み込みました: " + bindings.Count + "件" +
                    (ignoreApps.Count == 0 ? "" : " (除外アプリ " + ignoreApps.Count + "件)"));
                ok = true;
            }
            catch (Exception ex)
            {
                if (dispatcher == null)
                    dispatcher = new Gestures.GestureDispatcher(new Gestures.GestureBinding[0], sender);
                AddLog("設定の読み込みに失敗しました");
                MessageBox.Show(this, path + "\r\n\r\n" + ex.Message,
                    "設定エラー (直前の設定を維持します)", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            dirty = true;
            return ok;
        }

        // 押し続けている動作 (Win + 左ボタンなど) があれば離す。キーやボタンが押しっぱなしにならないようにする
        void ReleaseHolds()
        {
            if (dispatcher == null) return;
            try { dispatcher.ReleaseAll(); }
            catch (Exception ex) { AddLog("押し続けの解放に失敗: " + ex.Message); }
        }

        // 連続発火の回数を数えるためのキー (「本数・動かす指・方向」。名前は設定で変えられるため使わない)
        static string SpecKey(Gestures.IGestureSpec s)
        {
            return s.Key;
        }

        void AddLog(string line)
        {
            gestureLog.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (gestureLog.Count > 8) gestureLog.RemoveAt(gestureLog.Count - 1);
        }

        long lastSuppressLogMs = -1000;

        // 完成した1フレームを判定器に渡す。座標は「パッド幅 = 1.0」に正規化 (Y も XMax で割る)
        void RunGestures(PtpDevice dev)
        {
            double scale = dev.XMax > 0 ? dev.XMax : 1.0;
            List<Gestures.Contact> contacts = new List<Gestures.Contact>();
            foreach (Finger f in dev.Fingers)
                contacts.Add(new Gestures.Contact(f.Id, f.X / scale, f.Y / scale));

            Gestures.Frame frame = new Gestures.Frame(clock.ElapsedMilliseconds, contacts);
            if (contacts.Count == 0) repeatTotals.Clear();

            // 判定は、設定ファイルの割り当て (しきい値もそのまま) で常に行う。画面の表示もこの結果。
            // チェックが入っていないときは、SendInput による実行だけを行わない
            bool execute = chkExec.Checked;
            IReadOnlyList<Gestures.IGestureSpec> fired;
            try
            {
                fired = dispatcher.Feed(frame, execute);
            }
            catch (Exception ex)
            {
                AddLog("実行エラー: " + ex.Message);
                return;
            }

            // 除外アプリで抑制したときは、ログにだけ残す (連続発火で流れないよう、1 秒に 1 回まで)
            if (dispatcher.LastSuppressed && clock.ElapsedMilliseconds - lastSuppressLogMs >= 1000)
            {
                lastSuppressLogMs = clock.ElapsedMilliseconds;
                AddLog("除外アプリのため実行しません  [" + dispatcher.LastApp + "]");
            }

            // 連続発火のときは、同じ定義が発火回数ぶん並ぶ
            foreach (Gestures.IGestureSpec s in fired)
            {
                string key = SpecKey(s);
                int total;
                repeatTotals.TryGetValue(key, out total);
                repeatTotals[key] = ++total;
                if (total == 1)
                    AddLog(s.Name + (execute ? "  ▶実行" : "") +
                        (dispatcher.LastApp == null ? "" : "  [" + dispatcher.LastApp + "]") +
                        (dispatcher.LastModifiers == Gestures.KeyModifiers.None ? ""
                            : "  <" + Gestures.KeyModifierNames.Format(dispatcher.LastModifiers) + ">"));
                flashText = total > 1 ? s.Name + " ×" + total : s.Name; // 連続発火は「× 回数」
                flashUntilMs = clock.ElapsedMilliseconds + 1200;
            }
        }

        static readonly Color[] Palette = {
            Color.FromArgb(230, 80, 80), Color.FromArgb(60, 160, 90), Color.FromArgb(70, 120, 220),
            Color.FromArgb(220, 160, 40), Color.FromArgb(150, 90, 200)
        };

        public MainForm(bool startInTray, string[] args)
        {
            cmdArgs = args == null ? new string[0] : args;
            ResolveConfigLocation();

            Text = "Touchpad Viewer (PTP Raw Input)";
            ClientSize = new Size(960, 560);
            DoubleBuffered = true;

            info.Dock = DockStyle.Right;
            info.Width = 420;
            info.Font = new Font("Consolas", 11f);
            info.Padding = new Padding(8);

            canvas.Dock = DockStyle.Fill;
            canvas.BackColor = Color.FromArgb(32, 32, 36);
            typeof(Panel).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(canvas, true, null);
            canvas.Paint += OnPaintCanvas;
            canvas.MouseDown += OnCanvasMouseDown;
            canvas.MouseMove += OnCanvasMouseMove;
            canvas.MouseUp += delegate { simDrag = -1; };

            Controls.Add(canvas);
            Controls.Add(info);

            chkExec.Text = "ジェスチャーでキー操作を実行する (SendInput)";
            chkExec.Dock = DockStyle.Fill;
            chkExec.Padding = new Padding(8, 0, 0, 0);

            Button btnEdit = new Button();
            btnEdit.Text = "ジェスチャーを編集…";
            btnEdit.Dock = DockStyle.Right;
            btnEdit.Width = 150;
            btnEdit.Click += delegate { EditGestures(); };

            Button btnReload = new Button();
            btnReload.Text = "設定を再読み込み";
            btnReload.Dock = DockStyle.Right;
            btnReload.Width = 140;
            btnReload.Click += delegate { LoadBindings(); };

            Button btnOpen = new Button();
            btnOpen.Text = "設定ファイルを開く";
            btnOpen.Dock = DockStyle.Right;
            btnOpen.Width = 150;
            btnOpen.Click += delegate { OpenConfig(); };

            Button btnChoose = new Button();
            btnChoose.Text = "場所を選ぶ…";
            btnChoose.Dock = DockStyle.Right;
            btnChoose.Width = 110;
            btnChoose.Click += delegate { ChooseConfig(); };

            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 34;
            bar.Controls.Add(chkExec);

            chkSim.Text = "疑似指モード";
            chkSim.Dock = DockStyle.Left;
            chkSim.Width = 130;
            chkSim.Padding = new Padding(8, 0, 0, 0);
            chkSim.CheckedChanged += OnSimToggled;
            bar.Controls.Add(chkSim);
            bar.Controls.Add(btnReload);
            bar.Controls.Add(btnEdit);
            bar.Controls.Add(btnOpen);
            bar.Controls.Add(btnChoose);
            Controls.Add(bar);

            timer.Interval = 16;
            timer.Tick += OnTick;
            timer.Start();

            BuildTray();
            FormClosing += OnFormClosingToTray;

            // 非表示のまま起動しても Raw Input を受け取れるよう、ここでウィンドウハンドルだけ先に作る。
            // (登録は OnHandleCreated で行う。通常の Show では Load まで待つことになり、トレイ起動では来ない)
            allowVisible = !startInTray;
            CreateHandle();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            CountPtpDevices();
            if (!bindingsLoaded)
            {
                bindingsLoaded = true;
                LoadBindings();
            }

            Native.RAWINPUTDEVICE[] rid = new Native.RAWINPUTDEVICE[1];
            rid[0].UsagePage = 0x0D; // Digitizer
            rid[0].Usage = 0x05;     // Touch Pad
            // INPUTSINK: 非アクティブでも受け取る / DEVNOTIFY: 抜き差し通知
            rid[0].Flags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY;
            rid[0].Target = Handle;
            if (!Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
                registerError = "RegisterRawInputDevices 失敗: " + Marshal.GetLastWin32Error();
        }

        void CountPtpDevices()
        {
            ptpDeviceCount = 0;
            uint n = 0;
            uint itemSize = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICELIST));
            Native.GetRawInputDeviceList(null, ref n, itemSize);
            if (n == 0) return;
            Native.RAWINPUTDEVICELIST[] list = new Native.RAWINPUTDEVICELIST[n];
            Native.GetRawInputDeviceList(list, ref n, itemSize);

            for (int i = 0; i < n; i++)
            {
                if (list[i].Type != Native.RIM_TYPEHID) continue;
                // RID_DEVICE_INFO: cbSize(4) type(4) vendor(4) product(4) version(4) usagePage(2) usage(2) ...
                IntPtr buf = Marshal.AllocHGlobal(32);
                try
                {
                    Marshal.WriteInt32(buf, 0, 32);
                    uint sz = 32;
                    if (Native.GetRawInputDeviceInfo(list[i].Device, Native.RIDI_DEVICEINFO, buf, ref sz) == uint.MaxValue)
                        continue;
                    int page = (ushort)Marshal.ReadInt16(buf, 20);
                    int usage = (ushort)Marshal.ReadInt16(buf, 22);
                    // 用途が Touch Pad でも、PTP の構成 (指ごとの X/Y) を持たない HID がある
                    // (例: ベンダー定義のデータだけを持つ別インターフェース)。解読できたものだけ数える
                    if (page == 0x0D && usage == 0x05 && GetDevice(list[i].Device) != null) ptpDeviceCount++;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_INPUT)
            {
                HandleRawInput(m.LParam);
            }
            else if ((uint)m.Msg == Native.WM_SHOW_EXISTING)
            {
                ShowFromTray(); // 2つ目のインスタンスからの依頼
            }
            else if (m.Msg == Native.WM_INPUT_DEVICE_CHANGE)
            {
                // 抜き差し: キャッシュを捨てて再取得させる
                foreach (PtpDevice d in devices.Values) if (d != null) d.Dispose();
                devices.Clear();
                active = null;
                CountPtpDevices();
                dirty = true;
            }
            base.WndProc(ref m);
        }

        void HandleRawInput(IntPtr hRawInput)
        {
            if (simMode) return; // 疑似指モード中は実機の入力と混ぜない

            uint headerSize = (uint)(8 + 2 * IntPtr.Size); // RAWINPUTHEADER
            uint size = 0;
            Native.GetRawInputData(hRawInput, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(hRawInput, Native.RID_INPUT, buf, ref size, headerSize) != size) return;
                if (Marshal.ReadInt32(buf, 0) != Native.RIM_TYPEHID) return;

                IntPtr hDevice = Marshal.ReadIntPtr(buf, 8);
                PtpDevice dev = GetDevice(hDevice);
                if (dev == null) return;

                int off = (int)headerSize;
                int sizeHid = Marshal.ReadInt32(buf, off);
                int count = Marshal.ReadInt32(buf, off + 4);
                for (int i = 0; i < count; i++)
                {
                    byte[] report = new byte[sizeHid];
                    Marshal.Copy(IntPtr.Add(buf, off + 8 + i * sizeHid), report, 0, sizeHid);
                    if (dev.Feed(report))
                    {
                        active = dev;
                        frames++;
                        dirty = true;
                        RunGestures(dev);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        PtpDevice GetDevice(IntPtr hDevice)
        {
            PtpDevice dev;
            if (!devices.TryGetValue(hDevice, out dev))
            {
                dev = PtpDevice.Open(hDevice); // PTP でなければ null をキャッシュ
                devices[hDevice] = dev;
            }
            return dev;
        }

        void OnTick(object sender, EventArgs e)
        {
            // 実機は指が止まっていても一定間隔でレポートを送ってくる。疑似指もそれに合わせて流し続ける
            // (判定の「落ち着くまで待つ」などの時間条件が、フレームの到着で進むため)
            if (simMode && (simFingers.Count > 0 || simEmptyPending)) PushSim();

            if (++tick >= 60) // 約1秒
            {
                tick = 0;
                fps = (int)(frames - framesLastSec);
                framesLastSec = frames;
                dirty = true;
            }
            if (flashText.Length > 0 && clock.ElapsedMilliseconds >= flashUntilMs)
            {
                flashText = ""; // 表示時間が過ぎたので消す
                dirty = true;
            }
            if (!dirty || !Visible) return; // トレイ常駐中 (非表示) は画面を更新しない。dirty は残すので表示時に反映される
            dirty = false;
            UpdateInfo();
            canvas.Invalidate();
        }

        // X 昇順に並べる (左→右)。3本のときは L / M / R と表示
        List<Finger> SortedFingers()
        {
            List<Finger> list = new List<Finger>();
            if (active != null) list.AddRange(active.Fingers);
            list.Sort(delegate (Finger a, Finger b) { return a.X.CompareTo(b.X); });
            return list;
        }

        static string RankLabel(int index, int total)
        {
            if (total == 3) return index == 0 ? "L" : (index == 1 ? "M" : "R");
            return (index + 1).ToString();
        }

        void UpdateInfo()
        {
            StringBuilder sb = new StringBuilder();
            if (simMode)
            {
                sb.AppendLine("【疑似指モード】");
                sb.AppendLine("左クリック : 指を置く / ドラッグで動かす");
                sb.AppendLine("右クリック : 指を離す (空所で全員)");
                sb.AppendLine();
            }
            sb.AppendLine("設定ファイル (" + configSource + ")");
            sb.AppendLine("  " + ShortPath(configPath));
            sb.AppendLine("PTP デバイス数 : " + ptpDeviceCount);
            if (registerError.Length > 0) sb.AppendLine(registerError);
            sb.AppendLine("フレーム/秒    : " + fps);
            sb.AppendLine("総フレーム     : " + frames);

            if (active == null)
            {
                sb.AppendLine();
                sb.AppendLine(ptpDeviceCount == 0
                    ? "PTP タッチパッドが見つかりません。\r\n接続して触れてください。"
                    : "タッチパッドに触れてください。");
            }
            else
            {
                List<Finger> fs = SortedFingers();
                sb.AppendLine("ScanTime       : " + active.ScanTime);
                sb.AppendLine("座標範囲       : " + active.XMax + " x " + active.YMax);
                sb.AppendLine();
                sb.AppendLine("指の本数: " + fs.Count);
                sb.AppendLine("-----------------------------");
                for (int i = 0; i < fs.Count; i++)
                {
                    sb.AppendLine(string.Format("[{0}] ID={1,-2} X={2,5} Y={3,5}",
                        RankLabel(i, fs.Count), fs[i].Id, fs[i].X, fs[i].Y));
                }
            }
            sb.AppendLine();
            sb.AppendLine("発火したジェスチャー (設定ファイルの割り当て)");
            sb.AppendLine("-----------------------------");
            if (gestureLog.Count == 0) sb.AppendLine("(まだありません)");
            foreach (string line in gestureLog) sb.AppendLine(line);
            info.Text = sb.ToString();
        }

        // ---- トレイ常駐 ----
        NotifyIcon tray;
        ToolStripMenuItem miExec;
        ToolStripMenuItem miAuto;
        Icon iconOff;
        Icon iconOn;
        bool allowVisible = true;
        bool exiting;
        bool balloonShown;
        bool bindingsLoaded;
        readonly Gestures.AutoStart autoStart = new Gestures.AutoStart();

        // 起動中の exe。`dotnet X.dll` で起動した場合は dotnet.exe になるので自動実行には使えない
        static string ExePath { get { return Environment.ProcessPath; } }

        static bool IsApphost()
        {
            return !string.Equals(Path.GetFileNameWithoutExtension(ExePath), "dotnet",
                StringComparison.OrdinalIgnoreCase);
        }

        // トレイ起動のとき、Application.Run が最初に Show しても表示しない
        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(allowVisible && value);
        }

        // 小さなパッドの絵 + 右下の状態ランプ (灰: キー操作の実行 OFF / 緑: ON)
        static Icon MakeIcon(Color lamp)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush body = new SolidBrush(Color.FromArgb(70, 70, 80)))
                    g.FillRectangle(body, 2, 5, 28, 22);
                using (Pen edge = new Pen(Color.FromArgb(200, 200, 210), 2f))
                    g.DrawRectangle(edge, 2, 5, 28, 22);
                using (Brush finger = new SolidBrush(Color.FromArgb(235, 235, 240)))
                {
                    g.FillEllipse(finger, 5, 10, 5, 5);
                    g.FillEllipse(finger, 13, 10, 5, 5);
                    g.FillEllipse(finger, 21, 10, 5, 5);
                }
                using (Brush dot = new SolidBrush(lamp))
                    g.FillEllipse(dot, 19, 16, 12, 12);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        void BuildTray()
        {
            iconOff = MakeIcon(Color.FromArgb(140, 140, 150));
            iconOn = MakeIcon(Color.FromArgb(60, 200, 90));
            Icon = iconOff;

            ToolStripMenuItem miShow = new ToolStripMenuItem("ウィンドウを表示");
            miShow.Click += delegate { ShowFromTray(); };

            miExec = new ToolStripMenuItem("ジェスチャーでキー操作を実行");
            miExec.Click += delegate { chkExec.Checked = !chkExec.Checked; };

            ToolStripMenuItem miEdit = new ToolStripMenuItem("ジェスチャーを編集…");
            miEdit.Click += delegate { ShowFromTray(); EditGestures(); };

            ToolStripMenuItem miReload = new ToolStripMenuItem("設定を再読み込み");
            miReload.Click += delegate { LoadBindings(); };

            ToolStripMenuItem miOpen = new ToolStripMenuItem("設定ファイルを開く");
            miOpen.Click += delegate { OpenConfig(); };

            ToolStripMenuItem miChoose = new ToolStripMenuItem("設定ファイルの場所を選ぶ…");
            miChoose.Click += delegate { ChooseConfig(); };

            ToolStripMenuItem miResetPath = new ToolStripMenuItem("設定ファイルの場所を既定に戻す");
            miResetPath.Click += delegate { ResetConfigLocation(); };

            miAuto = new ToolStripMenuItem("Windows 起動時に自動実行");
            miAuto.Click += OnToggleAutoStart;

            ToolStripMenuItem miExit = new ToolStripMenuItem("終了");
            miExit.Click += delegate { ExitApp(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] {
                miShow, new ToolStripSeparator(),
                miExec, miEdit, miReload, miOpen, miChoose, miResetPath, new ToolStripSeparator(),
                miAuto, new ToolStripSeparator(),
                miExit });
            menu.Opening += delegate { RefreshAutoStartItem(); };

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.Visible = true;

            chkExec.CheckedChanged += delegate { UpdateTrayState(); if (!chkExec.Checked) ReleaseHolds(); };
            UpdateTrayState();
        }

        void UpdateTrayState()
        {
            bool on = chkExec.Checked;
            miExec.Checked = on;
            tray.Icon = on ? iconOn : iconOff;
            tray.Text = "Touchpad Gestures (実行: " + (on ? "ON" : "OFF") + ")"; // 63文字まで
        }

        void RefreshAutoStartItem()
        {
            miAuto.Enabled = IsApphost();
            miAuto.Checked = miAuto.Enabled && autoStart.IsEnabledFor(ExePath);
        }

        void OnToggleAutoStart(object s, EventArgs e)
        {
            try
            {
                if (autoStart.IsEnabledFor(ExePath))
                {
                    autoStart.Disable();
                    tray.ShowBalloonTip(2000, "Touchpad Gestures", "自動実行を解除しました。", ToolTipIcon.Info);
                }
                else
                {
                    autoStart.Enable(ExePath, "--tray");
                    tray.ShowBalloonTip(3000, "Touchpad Gestures",
                        "自動実行を登録しました。\r\n" + ExePath, ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "自動実行の設定に失敗しました", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // 設定ファイルの場所を選ぶ。読み込めたときだけ、exe の隣に保存して、次回以降も使う
        void ChooseConfig()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "gestures.json の場所を選ぶ";
                dlg.Filter = "設定ファイル (*.json)|*.json|すべてのファイル (*.*)|*.*";
                dlg.CheckFileExists = true;
                try
                {
                    string dir = Path.GetDirectoryName(ConfigPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                }
                catch (ArgumentException) { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string oldPath = configPath, oldSource = configSource;
                configPath = dlg.FileName;
                configSource = Gestures.ConfigLocation.SourceSettings;
                if (!LoadBindings())
                {
                    // 読めなかったので、元の場所に戻す (保存しない)
                    configPath = oldPath;
                    configSource = oldSource;
                    return;
                }
                try
                {
                    Gestures.ConfigLocation.Save(AppContext.BaseDirectory, configPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "場所を保存できませんでした (今回の起動だけ有効です)",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        // 保存した場所を消して、既定 (または --config) に戻す
        void ResetConfigLocation()
        {
            try
            {
                Gestures.ConfigLocation.Clear(AppContext.BaseDirectory);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "場所の設定を消せませんでした", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ResolveConfigLocation();
            LoadBindings();
        }

        // 設定画面で割り当てを編集する。保存されたら読み直して反映する
        void EditGestures()
        {
            if (!File.Exists(ConfigPath)) LoadBindings(); // 見本を作る
            if (!File.Exists(ConfigPath)) return;
            List<Gestures.BindingEntry> entries;
            try
            {
                entries = Gestures.GestureConfig.LoadEntries(ConfigPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ConfigPath + "\r\n\r\n" + ex.Message + "\r\n\r\n" +
                    "設定ファイルの形式が不正なため、設定画面を開けません。「設定ファイルを開く」で直してください。",
                    "設定画面を開けません", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            using (SettingsForm form = new SettingsForm(ConfigPath, entries, AppContext.BaseDirectory))
            {
                if (form.ShowDialog(this) == DialogResult.OK) LoadBindings();
            }
        }

        void OpenConfig()
        {
            if (!File.Exists(ConfigPath)) LoadBindings(); // 見本を作る
            if (File.Exists(ConfigPath))
                Process.Start(new ProcessStartInfo(ConfigPath) { UseShellExecute = true });
        }

        void ShowFromTray()
        {
            allowVisible = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        // ×ボタンでは終了せずトレイへ隠す。終了はトレイメニューの「終了」。
        // 外部から WM_CLOSE を送られた場合 (CloseReason が UserClosing とは限らない) も隠す。
        // OS のシャットダウンと Application.Exit だけは通す
        void OnFormClosingToTray(object s, FormClosingEventArgs e)
        {
            if (exiting || e.CloseReason == CloseReason.WindowsShutDown
                || e.CloseReason == CloseReason.ApplicationExitCall) return;
            e.Cancel = true;
            Hide();
            if (!balloonShown)
            {
                balloonShown = true;
                tray.ShowBalloonTip(2500, "Touchpad Gestures",
                    "トレイに常駐しています。終了はトレイアイコンの右クリックから。", ToolTipIcon.Info);
            }
        }

        void ExitApp()
        {
            exiting = true;
            ReleaseHolds();
            tray.Visible = false; // 終了後にアイコンが残らないように
            tray.Dispose();
            Application.Exit();
        }

        float PadXMax() { return (active != null && active.XMax > 0) ? active.XMax : 1500f; }
        float PadYMax() { return (active != null && active.YMax > 0) ? active.YMax : 1000f; }

        // 描画とマウス操作で共有する、パッドを表す矩形 (画面座標)
        RectangleF PadRect()
        {
            float aspect = PadXMax() / PadYMax();
            float margin = 20f;
            float aw = canvas.Width - margin * 2;
            float ah = canvas.Height - margin * 2;
            if (aw <= 0 || ah <= 0) return RectangleF.Empty;
            if (aw / ah > aspect) aw = ah * aspect; else ah = aw / aspect;
            return new RectangleF((canvas.Width - aw) / 2f, (canvas.Height - ah) / 2f, aw, ah);
        }

        void OnSimToggled(object s, EventArgs e)
        {
            simMode = chkSim.Checked;
            simFingers.Clear();
            simDrag = -1;
            if (simMode)
            {
                active = simDevice;
                simEmptyPending = true;
                AddLog("疑似指モードを開始しました");
            }
            else
            {
                // 全員離れたフレームを流して判定器をリセットしてから、疑似デバイスを外す
                simDevice.SimulateFrame(simFingers, SimSlotsPerReport);
                RunGestures(simDevice);
                active = null;
                AddLog("疑似指モードを終了しました");
            }
            dirty = true;
        }

        // 疑似指の現在位置を 1 フレーム分、実機と同じ経路に流す
        void PushSim()
        {
            simEmptyPending = false;
            simDevice.SimulateFrame(simFingers, SimSlotsPerReport);
            active = simDevice;
            frames++;
            dirty = true;
            RunGestures(simDevice);
        }

        // 画面上の点 → パッド座標の指 (パッドの外は縁に丸める)
        Finger MakeSimFinger(int id, Point p, RectangleF r)
        {
            float nx = Math.Max(0f, Math.Min(1f, (p.X - r.Left) / r.Width));
            float ny = Math.Max(0f, Math.Min(1f, (p.Y - r.Top) / r.Height));
            Finger f = new Finger();
            f.Id = id;
            f.X = (int)(nx * PadXMax());
            f.Y = (int)(ny * PadYMax());
            return f;
        }

        // クリック位置に近い疑似指のインデックス (無ければ -1)
        int HitSimFinger(Point p, RectangleF r)
        {
            for (int i = simFingers.Count - 1; i >= 0; i--)
            {
                float fx = r.Left + simFingers[i].X / PadXMax() * r.Width;
                float fy = r.Top + simFingers[i].Y / PadYMax() * r.Height;
                float dx = p.X - fx, dy = p.Y - fy;
                if (dx * dx + dy * dy <= 28f * 28f) return i;
            }
            return -1;
        }

        void OnCanvasMouseDown(object s, MouseEventArgs e)
        {
            if (!simMode) return;
            RectangleF r = PadRect();
            if (r.Width <= 0) return;

            int hit = HitSimFinger(e.Location, r);
            if (e.Button == MouseButtons.Right)
            {
                if (hit >= 0) simFingers.RemoveAt(hit); else simFingers.Clear();
                simEmptyPending = simFingers.Count == 0;
                simDrag = -1;
            }
            else if (e.Button == MouseButtons.Left)
            {
                if (hit < 0 && simFingers.Count < SimMaxFingers)
                {
                    simFingers.Add(MakeSimFinger(simNextId++, e.Location, r));
                    hit = simFingers.Count - 1;
                }
                simDrag = hit;
            }
        }

        void OnCanvasMouseMove(object s, MouseEventArgs e)
        {
            if (!simMode || simDrag < 0 || simDrag >= simFingers.Count) return;
            if ((e.Button & MouseButtons.Left) == 0) return;
            RectangleF r = PadRect();
            if (r.Width <= 0) return;
            simFingers[simDrag] = MakeSimFinger(simFingers[simDrag].Id, e.Location, r);
        }

        void OnPaintCanvas(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float xmax = PadXMax();
            float ymax = PadYMax();
            RectangleF pad = PadRect();
            if (pad.Width <= 0) return;
            float left = pad.Left, top = pad.Top, aw = pad.Width, ah = pad.Height;

            using (Brush padBrush = new SolidBrush(Color.FromArgb(52, 52, 60)))
            using (Pen padPen = new Pen(Color.FromArgb(120, 120, 130), 2f))
            {
                g.FillRectangle(padBrush, left, top, aw, ah);
                g.DrawRectangle(padPen, left, top, aw, ah);
            }

            List<Finger> fs = SortedFingers();
            using (Font font = new Font("Consolas", 11f, FontStyle.Bold))
            {
                for (int i = 0; i < fs.Count; i++)
                {
                    float px = left + fs[i].X / xmax * aw;
                    float py = top + fs[i].Y / ymax * ah;
                    Color c = Palette[fs[i].Id % Palette.Length];
                    using (Brush b = new SolidBrush(Color.FromArgb(200, c)))
                        g.FillEllipse(b, px - 20, py - 20, 40, 40);
                    using (Brush t = new SolidBrush(Color.White))
                        g.DrawString(RankLabel(i, fs.Count) + ":" + fs[i].Id, font, t, px + 22, py - 10);
                }
            }

            if (flashText.Length > 0)
            {
                using (Font big = new Font("Meiryo UI", 22f, FontStyle.Bold))
                using (Brush shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                using (Brush text = new SolidBrush(Color.FromArgb(255, 255, 220, 80)))
                {
                    SizeF sz = g.MeasureString(flashText, big);
                    float tx = (canvas.Width - sz.Width) / 2f;
                    g.FillRectangle(shadow, tx - 12, 8, sz.Width + 24, sz.Height + 8);
                    g.DrawString(flashText, big, text, tx, 12);
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ReleaseHolds();
            foreach (PtpDevice d in devices.Values) if (d != null) d.Dispose();
            devices.Clear();
            base.OnFormClosed(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool startInTray = Array.IndexOf(args, "--tray") >= 0;

            bool created;
            using (System.Threading.Mutex single = new System.Threading.Mutex(true, "Local\\TouchpadGestures.SingleInstance", out created))
            {
                if (!created)
                {
                    // 既に常駐中: 手動で起動し直したときだけ、既存のウィンドウを出してもらう
                    if (!startInTray)
                        Native.PostMessage((IntPtr)0xFFFF, Native.WM_SHOW_EXISTING, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                Application.EnableVisualStyles();
                Application.Run(new MainForm(startInTray, args));
            }
        }
    }
}
