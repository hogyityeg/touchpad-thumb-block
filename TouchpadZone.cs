// TouchpadZone - ignores touchpad contacts that land in the left part of a Precision Touchpad.
//
// How it works:
//  * A background thread receives the touchpad's raw HID reports (Raw Input, usage page 0x0D / usage 0x05)
//    and tracks every finger. A finger whose first touch lands inside the zone is "blocked" for its lifetime.
//  * A low-level mouse hook swallows the cursor movement / clicks / taps that Windows synthesizes
//    while the only fingers on the pad are blocked ones.
//
// Build: see build.ps1 (uses the C# compiler that ships with Windows' .NET Framework).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    // ---- tunables -------------------------------------------------------------------------------
    const long MoveRecentMs = 150;      // a blocked finger counts as "moving" for this long after it moved
    const long TapGraceMs = 450;        // tap clicks arrive shortly after the finger lifts
    const long TapMaxDurationMs = 400;  // touches longer than this are not taps

    // ---- state ----------------------------------------------------------------------------------
    static readonly object StateLock = new object();
    static readonly Dictionary<IntPtr, Touchpad> Devices = new Dictionary<IntPtr, Touchpad>();
    static double ZoneFraction = 1.0 / 3.0;
    static volatile bool Enabled = true;
    static readonly bool[] SuppressedDown = new bool[5]; // L, R, M, X1, X2
    static StreamWriter LogWriter;
    static string SettingsPath;

    class Contact
    {
        public bool Blocked;
        public int AnchorX, AnchorY;
        public long LastMoveMs, DownMs;
    }

    class Finger { public uint Id; public bool Tip; public int X, Y; }

    class Touchpad
    {
        public IntPtr Handle, Preparsed;
        public string Name;
        public readonly List<ushort> FingerLinks = new List<ushort>();
        public int XMin, XMax, YMin, YMax, JitterUnits;
        public bool HasContactCount;
        public ushort ContactCountLink;
        // hybrid-mode frame assembly
        public int Remaining;
        public readonly List<Finger> Frame = new List<Finger>();
        // tracked contacts (guarded by StateLock)
        public readonly Dictionary<uint, Contact> Contacts = new Dictionary<uint, Contact>();
        public bool ButtonDown;
        public long LastReportMs;
        public bool InSession, SessionBlocked, SessionAllowed;
        public long SessionStartMs, LastSessionEndMs, LastSessionDurationMs;
        public bool LastSessionBlockedOnly;
    }

    static long NowMs() { return Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency; }

    static void Log(string s)
    {
        if (LogWriter == null) return;
        lock (LogWriter) LogWriter.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s);
    }

    // ---- entry ----------------------------------------------------------------------------------
    [STAThread]
    static int Main(string[] args)
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        SettingsPath = Path.Combine(exeDir, "TouchpadZone.ini");
        LoadSettings();

        bool capsOnly = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            if (a == "--log") OpenLog(Path.Combine(exeDir, "TouchpadZone.log"));
            else if (a == "--caps") capsOnly = true;
            else if (a == "--zone" && i + 1 < args.Length)
                ZoneFraction = Clamp(double.Parse(args[++i], CultureInfo.InvariantCulture));
        }

        if (capsOnly)
        {
            OpenLog(Path.Combine(exeDir, "TouchpadZone.log"));
            DumpCaps();
            LogWriter.Flush();
            return 0;
        }

        bool created;
        using (var mutex = new Mutex(true, "Local\\TouchpadZone-single-instance", out created))
        {
            if (!created) return 1;

            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;

            var rawThread = new Thread(RawInputThread);
            rawThread.IsBackground = true;
            rawThread.Priority = ThreadPriority.Highest;
            rawThread.SetApartmentState(ApartmentState.STA);
            rawThread.Start();

            HookDelegate = HookProc;
            HookHandle = SetWindowsHookEx(WH_MOUSE_LL, HookDelegate, GetModuleHandle(null), 0);
            if (HookHandle == IntPtr.Zero)
            {
                MessageBox.Show("Could not install mouse hook (error " + Marshal.GetLastWin32Error() + ").", "TouchpadZone");
                return 2;
            }

            using (var tray = new Tray()) Application.Run();

            UnhookWindowsHookEx(HookHandle);
        }
        return 0;
    }

    static double Clamp(double f) { return Math.Max(0.05, Math.Min(0.95, f)); }

    static void OpenLog(string path)
    {
        if (LogWriter != null) return;
        LogWriter = new StreamWriter(path, false, Encoding.UTF8);
        LogWriter.AutoFlush = true;
    }

    static void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            foreach (string line in File.ReadAllLines(SettingsPath))
            {
                string[] kv = line.Split('=');
                if (kv.Length != 2) continue;
                if (kv[0].Trim() == "zone") ZoneFraction = Clamp(double.Parse(kv[1].Trim(), CultureInfo.InvariantCulture));
            }
        }
        catch { }
    }

    static void SaveSettings()
    {
        try { File.WriteAllText(SettingsPath, "zone=" + ZoneFraction.ToString("0.###", CultureInfo.InvariantCulture) + "\r\n"); }
        catch { }
    }

    // ---- raw input ------------------------------------------------------------------------------
    class RawWindow : NativeWindow
    {
        public RawWindow()
        {
            CreateHandle(new CreateParams());
            var dev = new RAWINPUTDEVICE[1];
            dev[0].usUsagePage = 0x0D; // digitizer
            dev[0].usUsage = 0x05;     // touch pad
            dev[0].dwFlags = RIDEV_INPUTSINK;
            dev[0].hwndTarget = Handle;
            if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                Log("RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                try { HandleRawInput(m.LParam); }
                catch (Exception ex) { Log("error: " + ex); }
            }
            base.WndProc(ref m);
        }
    }

    static void RawInputThread()
    {
        var w = new RawWindow();
        Application.Run();
        GC.KeepAlive(w);
    }

    static void HandleRawInput(IntPtr hRawInput)
    {
        int headerSize = 8 + 2 * IntPtr.Size;
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)headerSize);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, (uint)headerSize) != size) return;
            if (Marshal.ReadInt32(buf, 0) != RIM_TYPEHID) return;
            IntPtr hDevice = Marshal.ReadIntPtr(buf, 8);
            int sizeHid = Marshal.ReadInt32(buf, headerSize);
            int count = Marshal.ReadInt32(buf, headerSize + 4);

            Touchpad tp = GetDevice(hDevice);
            if (tp == null) return;

            for (int i = 0; i < count; i++)
            {
                byte[] report = new byte[sizeHid];
                Marshal.Copy(buf + headerSize + 8 + i * sizeHid, report, 0, sizeHid);
                ProcessReport(tp, report);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static Touchpad GetDevice(IntPtr hDevice)
    {
        Touchpad tp;
        lock (StateLock) if (Devices.TryGetValue(hDevice, out tp)) return tp;
        tp = OpenTouchpad(hDevice);
        if (tp == null) return null;
        lock (StateLock) Devices[hDevice] = tp;
        Log("touchpad " + tp.Name + "  X " + tp.XMin + ".." + tp.XMax + "  Y " + tp.YMin + ".." + tp.YMax +
            "  fingers/report " + tp.FingerLinks.Count + "  contactCount " + tp.HasContactCount);
        return tp;
    }

    static Touchpad OpenTouchpad(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return null;
        IntPtr pp = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, pp, ref size) == unchecked((uint)-1)) { Marshal.FreeHGlobal(pp); return null; }

        var tp = new Touchpad { Handle = hDevice, Preparsed = pp, Name = DeviceName(hDevice) };

        HIDP_CAPS caps;
        if (HidP_GetCaps(pp, out caps) != HIDP_STATUS_SUCCESS) return null;
        ushort n = caps.NumberInputValueCaps;
        IntPtr vc = Marshal.AllocHGlobal(VALUE_CAPS_SIZE * Math.Max(1, (int)n));
        try
        {
            HidP_GetValueCaps(HidP_Input, vc, ref n, pp);
            for (int i = 0; i < n; i++)
            {
                int o = i * VALUE_CAPS_SIZE;
                ushort page = (ushort)Marshal.ReadInt16(vc, o + 0);
                ushort link = (ushort)Marshal.ReadInt16(vc, o + 6);
                ushort bitSize = (ushort)Marshal.ReadInt16(vc, o + 18);
                int lmin = Marshal.ReadInt32(vc, o + 40);
                int lmax = Marshal.ReadInt32(vc, o + 44);
                ushort usage = (ushort)Marshal.ReadInt16(vc, o + 56); // NotRange.Usage / Range.UsageMin
                if (lmax <= lmin && bitSize > 0 && bitSize < 32) { lmin = 0; lmax = (1 << bitSize) - 1; }

                if (page == 0x01 && usage == 0x30)
                {
                    if (!tp.FingerLinks.Contains(link)) tp.FingerLinks.Add(link);
                    tp.XMin = lmin; tp.XMax = lmax;
                }
                else if (page == 0x01 && usage == 0x31) { tp.YMin = lmin; tp.YMax = lmax; }
                else if (page == 0x0D && usage == 0x54) { tp.HasContactCount = true; tp.ContactCountLink = link; }
            }
        }
        finally { Marshal.FreeHGlobal(vc); }

        tp.FingerLinks.Sort();
        tp.JitterUnits = Math.Max(2, (tp.XMax - tp.XMin) / 200);
        return tp.FingerLinks.Count > 0 ? tp : null;
    }

    static string DeviceName(IntPtr hDevice)
    {
        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0) return "?";
        IntPtr p = Marshal.AllocHGlobal((int)size * 2);
        try
        {
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, p, ref size);
            return Marshal.PtrToStringUni(p);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    static readonly ushort[] usageBuf = new ushort[64];

    static bool HasUsage(Touchpad tp, ushort page, ushort link, ushort usage, byte[] report)
    {
        uint len = (uint)usageBuf.Length;
        if (HidP_GetUsages(HidP_Input, page, link, usageBuf, ref len, tp.Preparsed, report, (uint)report.Length) != HIDP_STATUS_SUCCESS)
            return false;
        for (int i = 0; i < len; i++) if (usageBuf[i] == usage) return true;
        return false;
    }

    static uint Value(Touchpad tp, ushort page, ushort link, ushort usage, byte[] report, out bool ok)
    {
        uint v;
        ok = HidP_GetUsageValue(HidP_Input, page, link, usage, out v, tp.Preparsed, report, (uint)report.Length) == HIDP_STATUS_SUCCESS;
        return v;
    }

    static void ProcessReport(Touchpad tp, byte[] report)
    {
        bool ok;
        int count = tp.FingerLinks.Count;
        if (tp.HasContactCount)
        {
            uint cc = Value(tp, 0x0D, tp.ContactCountLink, 0x54, report, out ok);
            count = ok ? (int)cc : 0;
        }

        if (count > 0) { tp.Remaining = count; tp.Frame.Clear(); }
        else if (tp.Remaining <= 0)
        {
            tp.Frame.Clear();
            FinishFrame(tp, false);
            return;
        }

        bool button = HasUsage(tp, 0x09, 0, 0x01, report);

        int n = Math.Min(tp.FingerLinks.Count, tp.Remaining);
        for (int i = 0; i < n; i++)
        {
            ushort link = tp.FingerLinks[i];
            var f = new Finger();
            f.Tip = HasUsage(tp, 0x0D, link, 0x42, report);
            f.Id = Value(tp, 0x0D, link, 0x51, report, out ok);
            f.X = (int)Value(tp, 0x01, link, 0x30, report, out ok);
            f.Y = (int)Value(tp, 0x01, link, 0x31, report, out ok);
            tp.Frame.Add(f);
        }
        tp.Remaining -= n;
        if (tp.Remaining <= 0) FinishFrame(tp, button);
    }

    static void FinishFrame(Touchpad tp, bool button)
    {
        long now = NowMs();
        lock (StateLock)
        {
            tp.LastReportMs = now;
            tp.ButtonDown = button;
            double zoneX = tp.XMin + (tp.XMax - tp.XMin) * ZoneFraction;
            var seen = new HashSet<uint>();

            foreach (var f in tp.Frame)
            {
                if (!f.Tip) continue;
                seen.Add(f.Id);
                Contact c;
                if (!tp.Contacts.TryGetValue(f.Id, out c))
                {
                    c = new Contact { Blocked = f.X < zoneX, AnchorX = f.X, AnchorY = f.Y, LastMoveMs = now, DownMs = now };
                    tp.Contacts[f.Id] = c;
                    Log("down  id " + f.Id + "  x " + f.X + " y " + f.Y + (c.Blocked ? "  BLOCKED" : ""));
                }
                else if (Math.Abs(f.X - c.AnchorX) + Math.Abs(f.Y - c.AnchorY) > tp.JitterUnits)
                {
                    c.AnchorX = f.X; c.AnchorY = f.Y; c.LastMoveMs = now;
                }
            }

            if (tp.Contacts.Count != seen.Count)
            {
                var gone = new List<uint>();
                foreach (var id in tp.Contacts.Keys) if (!seen.Contains(id)) gone.Add(id);
                foreach (var id in gone) { tp.Contacts.Remove(id); Log("up    id " + id); }
            }

            if (tp.Contacts.Count > 0)
            {
                if (!tp.InSession)
                {
                    tp.InSession = true; tp.SessionStartMs = now;
                    tp.SessionBlocked = tp.SessionAllowed = false;
                }
                foreach (var c in tp.Contacts.Values)
                    if (c.Blocked) tp.SessionBlocked = true; else tp.SessionAllowed = true;
            }
            else if (tp.InSession)
            {
                tp.InSession = false;
                tp.LastSessionEndMs = now;
                tp.LastSessionDurationMs = now - tp.SessionStartMs;
                tp.LastSessionBlockedOnly = tp.SessionBlocked && !tp.SessionAllowed;
            }
        }
    }

    // ---- mouse hook -----------------------------------------------------------------------------
    static LowLevelMouseProc HookDelegate;
    static IntPtr HookHandle;

    static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            try
            {
                if (ShouldSuppress((int)wParam, lParam)) return (IntPtr)1;
            }
            catch { }
        }
        return CallNextHookEx(HookHandle, nCode, wParam, lParam);
    }

    static int ButtonIndex(int msg, IntPtr lParam, out bool down)
    {
        down = false;
        switch (msg)
        {
            case WM_LBUTTONDOWN: down = true; return 0;
            case WM_LBUTTONUP: return 0;
            case WM_RBUTTONDOWN: down = true; return 1;
            case WM_RBUTTONUP: return 1;
            case WM_MBUTTONDOWN: down = true; return 2;
            case WM_MBUTTONUP: return 2;
            case WM_XBUTTONDOWN: down = true; return XIndex(lParam);
            case WM_XBUTTONUP: return XIndex(lParam);
        }
        return -1;
    }

    static int XIndex(IntPtr lParam)
    {
        int mouseData = Marshal.ReadInt32(lParam, 8);
        return ((mouseData >> 16) & 0xFFFF) == 1 ? 3 : 4;
    }

    static bool ShouldSuppress(int msg, IntPtr lParam)
    {
        long now = NowMs();
        lock (StateLock)
        {
            int blocked = 0, allowed = 0;
            bool blockedMoving = false, blockedYoung = true, hidButton = false, tapWindow = false;

            foreach (var tp in Devices.Values)
            {
                if (now - tp.LastReportMs > 2000) continue; // stale (e.g. device went to sleep mid-touch)
                foreach (var c in tp.Contacts.Values)
                {
                    if (!c.Blocked) { allowed++; continue; }
                    blocked++;
                    if (now - c.LastMoveMs < MoveRecentMs) blockedMoving = true;
                    if (now - c.DownMs > TapMaxDurationMs) blockedYoung = false;
                }
                hidButton |= tp.ButtonDown;
                if (!tp.InSession && tp.LastSessionBlockedOnly &&
                    now - tp.LastSessionEndMs < TapGraceMs && tp.LastSessionDurationMs < TapMaxDurationMs)
                    tapWindow = true;
            }

            bool onlyBlocked = blocked > 0 && allowed == 0;

            if (msg == WM_MOUSEMOVE || msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
                return onlyBlocked && blockedMoving;

            bool down;
            int b = ButtonIndex(msg, lParam, out down);
            if (b < 0) return false;
            if (!down)
            {
                // always pair ups with their downs so no button gets stuck
                bool s = SuppressedDown[b];
                SuppressedDown[b] = false;
                return s;
            }
            bool suppress =
                (onlyBlocked && hidButton) ||               // physical click with only a blocked finger on the pad
                (onlyBlocked && blockedYoung) ||            // tap click racing the finger-lift report
                (blocked + allowed == 0 && tapWindow);      // tap click right after a blocked finger lifted
            SuppressedDown[b] = suppress;
            if (suppress) Log("suppressed click " + b);
            return suppress;
        }
    }

    // ---- tray -----------------------------------------------------------------------------------
    class Tray : IDisposable
    {
        readonly NotifyIcon icon = new NotifyIcon();
        readonly ToolStripMenuItem enabledItem = new ToolStripMenuItem("Enabled");
        readonly List<ToolStripMenuItem> zoneItems = new List<ToolStripMenuItem>();
        Icon current;

        public Tray()
        {
            var menu = new ContextMenuStrip();
            enabledItem.Checked = true;
            enabledItem.CheckOnClick = true;
            enabledItem.CheckedChanged += delegate { Enabled = enabledItem.Checked; Refresh(); };
            menu.Items.Add(enabledItem);

            var zone = new ToolStripMenuItem("Blocked zone width");
            foreach (int pct in new[] { 20, 25, 33, 40, 50 })
            {
                var item = new ToolStripMenuItem("Left " + pct + "%");
                item.Tag = pct == 33 ? 1.0 / 3.0 : pct / 100.0;
                item.Click += delegate(object s, EventArgs e)
                {
                    lock (StateLock) ZoneFraction = (double)((ToolStripMenuItem)s).Tag;
                    SaveSettings();
                    Refresh();
                };
                zone.DropDownItems.Add(item);
                zoneItems.Add(item);
            }
            menu.Items.Add(zone);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Application.Exit(); });

            icon.ContextMenuStrip = menu;
            icon.DoubleClick += delegate { enabledItem.Checked = !enabledItem.Checked; };
            Refresh();
            icon.Visible = true;
        }

        void Refresh()
        {
            foreach (var item in zoneItems) item.Checked = Math.Abs((double)item.Tag - ZoneFraction) < 0.01;
            icon.Text = "TouchpadZone: " + (Enabled ? "left " + Math.Round(ZoneFraction * 100) + "% blocked" : "disabled");
            Icon old = current;
            current = MakeIcon(Enabled, ZoneFraction);
            icon.Icon = current;
            if (old != null) { DestroyIcon(old.Handle); old.Dispose(); }
        }

        static Icon MakeIcon(bool on, double zone)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var pad = new Rectangle(2, 6, 28, 20);
                int zw = (int)Math.Round(pad.Width * zone);
                using (var fill = new SolidBrush(on ? Color.FromArgb(220, 60, 50) : Color.FromArgb(150, 150, 150)))
                    g.FillRectangle(fill, pad.X, pad.Y, zw, pad.Height);
                using (var pen = new Pen(on ? Color.White : Color.FromArgb(200, 200, 200), 2.5f))
                    g.DrawRectangle(pen, pad);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        public void Dispose() { icon.Visible = false; icon.Dispose(); }
    }

    // ---- diagnostics ----------------------------------------------------------------------------
    static void DumpCaps()
    {
        uint num = 0;
        uint entry = (uint)(IntPtr.Size * 2);
        GetRawInputDeviceList(IntPtr.Zero, ref num, entry);
        IntPtr list = Marshal.AllocHGlobal((int)(num * entry));
        GetRawInputDeviceList(list, ref num, entry);
        for (int i = 0; i < num; i++)
        {
            IntPtr h = Marshal.ReadIntPtr(list, (int)(i * entry));
            int type = Marshal.ReadInt32(list, (int)(i * entry) + IntPtr.Size);
            if (type != RIM_TYPEHID) continue;
            uint size = 32;
            IntPtr info = Marshal.AllocHGlobal(32);
            Marshal.WriteInt32(info, 0, 32);
            GetRawInputDeviceInfo(h, RIDI_DEVICEINFO, info, ref size);
            ushort page = (ushort)Marshal.ReadInt16(info, 20), usage = (ushort)Marshal.ReadInt16(info, 22);
            Marshal.FreeHGlobal(info);
            if (page != 0x0D || usage != 0x05) continue;
            Touchpad tp = OpenTouchpad(h);
            if (tp == null) { Log("touchpad found but could not parse: " + DeviceName(h)); continue; }
            Log("touchpad " + tp.Name);
            Log("  X " + tp.XMin + ".." + tp.XMax + "  Y " + tp.YMin + ".." + tp.YMax);
            Log("  finger collections: " + string.Join(",", tp.FingerLinks) + "  contact count: " + tp.HasContactCount +
                " (link " + tp.ContactCountLink + ")");
            Log("  zone: X < " + (tp.XMin + (tp.XMax - tp.XMin) * ZoneFraction).ToString("0"));
        }
    }

    // ---- interop --------------------------------------------------------------------------------
    const int WM_INPUT = 0x00FF;
    const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
              WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
              WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C, WM_MOUSEHWHEEL = 0x020E;
    const int WH_MOUSE_LL = 14;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const uint RID_INPUT = 0x10000003;
    const uint RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000b;
    const int RIM_TYPEHID = 2;
    const int HidP_Input = 0;
    const int HIDP_STATUS_SUCCESS = 0x00110000;
    const int VALUE_CAPS_SIZE = 72;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command, IntPtr data, ref uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceList(IntPtr list, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int reportType, IntPtr caps, ref ushort length, IntPtr preparsed);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint value, IntPtr preparsed, byte[] report, uint reportLength);
    [DllImport("hid.dll")] static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        [In, Out] ushort[] usageList, ref uint usageLength, IntPtr preparsed, byte[] report, uint reportLength);
}
