using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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

        [DllImport("user32.dll")]
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

        List<Finger> current = new List<Finger>();
        List<Finger> pending = new List<Finger>();
        int remaining;

        public int XMax { get; private set; }
        public int YMax { get; private set; }
        public uint ScanTime { get; private set; }
        public int ContactCount { get; private set; }
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
        public bool Feed(byte[] report)
        {
            List<ushort> links;
            if (!fingerLinks.TryGetValue(report[0], out links)) return false;

            uint rlen = (uint)report.Length;
            uint count;
            bool hasCount = Native.HidP_GetUsageValue(Native.HidP_Input, PageDigitizer, 0,
                UsageContactCount, out count, preparsed, report, rlen) == Native.HIDP_STATUS_SUCCESS;

            if (!hasCount)
            {
                // Contact Count が無い構成: 1レポート=1フレーム、全スロット有効とみなす
                pending.Clear();
                remaining = links.Count;
                ContactCount = links.Count;
            }
            else if (count > 0)
            {
                pending.Clear();
                remaining = (int)count;
                ContactCount = (int)count;
                uint st;
                if (Native.HidP_GetUsageValue(Native.HidP_Input, PageDigitizer, 0,
                    UsageScanTime, out st, preparsed, report, rlen) == Native.HIDP_STATUS_SUCCESS)
                    ScanTime = st;
            }

            int valid = Math.Min(links.Count, remaining);
            for (int i = 0; i < valid; i++) ReadFinger(links[i], report, rlen);
            remaining -= valid;

            if (valid == 0 && remaining == 0)
            {
                // 全員離れた空フレーム
                current.Clear();
                ContactCount = 0;
                return true;
            }
            if (remaining == 0)
            {
                List<Finger> t = current; current = pending; pending = t;
                return true;
            }
            return false;
        }

        void ReadFinger(ushort link, byte[] report, uint rlen)
        {
            uint x, y, id;
            if (Native.HidP_GetUsageValue(Native.HidP_Input, PageGenericDesktop, link, UsageX,
                out x, preparsed, report, rlen) != Native.HIDP_STATUS_SUCCESS) return;
            if (Native.HidP_GetUsageValue(Native.HidP_Input, PageGenericDesktop, link, UsageY,
                out y, preparsed, report, rlen) != Native.HIDP_STATUS_SUCCESS) return;
            if (Native.HidP_GetUsageValue(Native.HidP_Input, PageDigitizer, link, UsageContactId,
                out id, preparsed, report, rlen) != Native.HIDP_STATUS_SUCCESS) return;

            ushort[] usages = new ushort[16];
            uint ulen = (uint)usages.Length;
            bool tip = false;
            if (Native.HidP_GetUsages(Native.HidP_Input, PageDigitizer, link, usages, ref ulen,
                preparsed, report, rlen) == Native.HIDP_STATUS_SUCCESS)
            {
                for (int k = 0; k < ulen; k++)
                    if (usages[k] == UsageTipSwitch) { tip = true; break; }
            }
            if (!tip) return;

            Finger f = new Finger();
            f.Id = (int)id;
            f.X = (int)x;
            f.Y = (int)y;
            pending.Add(f);
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

        readonly Gestures.GestureEngine engine = new Gestures.GestureEngine(BuildSpecs());
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly List<string> gestureLog = new List<string>();
        string flashText = "";
        long flashUntilMs;

        // 実行は既定で OFF。テスト中に意図せずキー入力が飛ばないようにする
        readonly CheckBox chkExec = new CheckBox();
        readonly Gestures.GestureDispatcher dispatcher =
            new Gestures.GestureDispatcher(BuildBindings(), new Gestures.Win32InputSender());

        static IEnumerable<Gestures.GestureBinding> BuildBindings()
        {
            return new Gestures.GestureBinding[] {
                new Gestures.GestureBinding(
                    new Gestures.SlotSwipeSpec("3本指・左だけ←", 3, 0, Gestures.SwipeDirection.Left),
                    new Gestures.HotkeyAction("Alt+Left")),
                new Gestures.GestureBinding(
                    new Gestures.SlotSwipeSpec("3本指・左だけ→", 3, 0, Gestures.SwipeDirection.Right),
                    new Gestures.HotkeyAction("Alt+Right")),
                new Gestures.GestureBinding(
                    new Gestures.SlotSwipeSpec("3本指・右だけ↑", 3, 2, Gestures.SwipeDirection.Up),
                    new Gestures.HotkeyAction("VolumeUp")),
                new Gestures.GestureBinding(
                    new Gestures.SlotSwipeSpec("3本指・右だけ↓", 3, 2, Gestures.SwipeDirection.Down),
                    new Gestures.HotkeyAction("VolumeDown")),
            };
        }

        void AddLog(string line)
        {
            gestureLog.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (gestureLog.Count > 8) gestureLog.RemoveAt(gestureLog.Count - 1);
        }

        static IEnumerable<Gestures.SlotSwipeSpec> BuildSpecs()
        {
            // 2〜4本指 × 左から k 番目の指だけ × 4方向 をすべて登録する
            string[] dirs = { "←", "→", "↑", "↓" };
            Gestures.SwipeDirection[] dirVals = {
                Gestures.SwipeDirection.Left, Gestures.SwipeDirection.Right,
                Gestures.SwipeDirection.Up, Gestures.SwipeDirection.Down };
            for (int n = 2; n <= 4; n++)
                for (int slot = 0; slot < n; slot++)
                    for (int d = 0; d < 4; d++)
                    {
                        string who = n == 3 ? (slot == 0 ? "左" : (slot == 1 ? "中" : "右"))
                                            : "左から" + (slot + 1) + "番目";
                        string name = string.Format("{0}本指・{1}だけ{2}", n, who, dirs[d]);
                        yield return new Gestures.SlotSwipeSpec(name, n, slot, dirVals[d]);
                    }
        }

        // 完成した1フレームを判定器に渡す。座標は「パッド幅 = 1.0」に正規化 (Y も XMax で割る)
        void RunGestures(PtpDevice dev)
        {
            double scale = dev.XMax > 0 ? dev.XMax : 1.0;
            List<Gestures.Contact> contacts = new List<Gestures.Contact>();
            foreach (Finger f in dev.Fingers)
                contacts.Add(new Gestures.Contact(f.Id, f.X / scale, f.Y / scale));

            Gestures.Frame frame = new Gestures.Frame(clock.ElapsedMilliseconds, contacts);
            // 割り当て済みのジェスチャーは、チェックが入っているときだけ SendInput で実行する
            HashSet<string> executed = new HashSet<string>();
            if (chkExec.Checked)
            {
                try
                {
                    foreach (Gestures.SlotSwipeSpec s in dispatcher.Feed(frame)) executed.Add(s.Name);
                }
                catch (Exception ex)
                {
                    AddLog("実行エラー: " + ex.Message);
                }
            }

            foreach (Gestures.SlotSwipeSpec s in engine.Feed(frame))
            {
                AddLog(s.Name + (executed.Contains(s.Name) ? "  ▶実行" : ""));
                flashText = s.Name;
                flashUntilMs = clock.ElapsedMilliseconds + 1200;
            }
        }

        static readonly Color[] Palette = {
            Color.FromArgb(230, 80, 80), Color.FromArgb(60, 160, 90), Color.FromArgb(70, 120, 220),
            Color.FromArgb(220, 160, 40), Color.FromArgb(150, 90, 200)
        };

        public MainForm()
        {
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

            Controls.Add(canvas);
            Controls.Add(info);

            chkExec.Text = "ジェスチャーでキー操作を実行する (SendInput)";
            chkExec.Dock = DockStyle.Bottom;
            chkExec.Height = 30;
            chkExec.Padding = new Padding(8, 0, 0, 0);
            Controls.Add(chkExec);

            timer.Interval = 16;
            timer.Tick += OnTick;
            timer.Start();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            CountPtpDevices();

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
                    if (page == 0x0D && usage == 0x05) ptpDeviceCount++;
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
            if (!dirty) return;
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
            sb.AppendLine("発火したジェスチャー");
            sb.AppendLine("-----------------------------");
            if (gestureLog.Count == 0) sb.AppendLine("(まだありません)");
            foreach (string line in gestureLog) sb.AppendLine(line);
            info.Text = sb.ToString();
        }

        void OnPaintCanvas(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float xmax = (active != null && active.XMax > 0) ? active.XMax : 1500f;
            float ymax = (active != null && active.YMax > 0) ? active.YMax : 1000f;
            float aspect = xmax / ymax;

            float margin = 20f;
            float aw = canvas.Width - margin * 2;
            float ah = canvas.Height - margin * 2;
            if (aw <= 0 || ah <= 0) return;
            if (aw / ah > aspect) aw = ah * aspect; else ah = aw / aspect;
            float left = (canvas.Width - aw) / 2f;
            float top = (canvas.Height - ah) / 2f;

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
            foreach (PtpDevice d in devices.Values) if (d != null) d.Dispose();
            devices.Clear();
            base.OnFormClosed(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new MainForm());
        }
    }
}
