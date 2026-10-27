// Güç Sayacı - Windows için hafif, arka planda çalışan enerji ve maliyet sayacı
// Copyright (C) 2026 Yusuf Polat
//
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version. See LICENSE.
//
// Derleme: build.cmd (Windows'ta yerleşik .NET Framework 4.x csc.exe, C# 5)
//
// Ölçülen (gerçek) kaynaklar:
//   * Pil deşarj hızı (CallNtPowerInformation/SystemBatteryState) -> dizüstü pildeyken toplam sistem gücü
//   * Pil şarj hızı                                                -> prizdeyken pile giden güç
//   * NVIDIA NVML (nvml.dll)                                        -> NVIDIA ekran kartı gücü
// Model girdileri (yönetici yetkisi gerektirmez):
//   * CPU kullanımı (GetSystemTimes), ekran parlaklığı/durumu (power notification, yedek: WMI),
//     disk meşguliyeti ve HDD/SSD türü (IOCTL), ağ trafiği (NetworkInterface)
// Kalibrasyon (yalnızca pilli cihazlar): pildeyken  ölçüm - (sabit modeller) = diğer + kCpu*cpu + kEkran*parlaklık
//   doğrusal regresyonu, varsayılanlara doğru zayıf ön bilgiyle (ridge) çözülür.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Güç Sayacı")]
[assembly: AssemblyProduct("Güç Sayacı")]
[assembly: AssemblyDescription("Bilgisayarın elektrik tüketimi ve maliyet sayacı")]
[assembly: AssemblyCopyright("Copyright (C) 2026 Yusuf Polat - GPL-3.0")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Info
{
    public const string Version = "1.0.0";
    public const string Url = "https://github.com/yusuf-polat/guc-sayaci";
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_BATTERY_STATE
    {
        public byte AcOnLine, BatteryPresent, Charging, Discharging, Spare1, Spare2, Spare3, Tag;
        public uint MaxCapacity, RemainingCapacity;
        public int Rate; // mW; şarjda +, deşarjda -
        public uint EstimatedTime, DefaultAlert1, DefaultAlert2;
    }

    [DllImport("powrprof.dll")]
    public static extern uint CallNtPowerInformation(int level, IntPtr inBuf, uint inLen,
        out SYSTEM_BATTERY_STATE outBuf, uint outLen);

    public static bool BatteryState(out SYSTEM_BATTERY_STATE bs)
    {
        return CallNtPowerInformation(5, IntPtr.Zero, 0, out bs,
            (uint)Marshal.SizeOf(typeof(SYSTEM_BATTERY_STATE))) == 0;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inLen,
        byte[] outBuf, uint outLen, out uint returned, IntPtr overlapped);

    [DllImport("user32.dll")]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid guid, int flags);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("psapi.dll")]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    // Kullanılmayan sayfaları işletim sistemine geri ver; dokunulan birkaç sayfa ucuzca geri gelir
    public static void TrimMemory(bool collect)
    {
        if (collect) GC.Collect();
        EmptyWorkingSet(GetCurrentProcess());
    }
}

// Donanım profili: varsayılan model sabitleri bilgisayara göre seçilir
class Hardware
{
    public bool Laptop;
    public string CpuName = "?";
    public double KCpuAc;        // prizde CPU %100 yükte ek güç (W)
    public double KCpuBatt;      // pilde (güç sınırı düşük) ön bilgi
    public double OtherW;        // anakart, yonga seti, USB, ses vb.
    public double FanBaseW;
    public double Efficiency;    // adaptör / PSU verimi
    public bool NvmlPresent;

    public static Hardware Detect()
    {
        Hardware h = new Hardware();
        Native.SYSTEM_BATTERY_STATE bs;
        h.Laptop = Native.BatteryState(out bs) && bs.BatteryPresent != 0;
        try
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                if (k != null) h.CpuName = ((k.GetValue("ProcessorNameString") as string) ?? "?").Trim();
        }
        catch { }
        h.KCpuAc = EstimateCpuW(h.CpuName, h.Laptop);
        h.KCpuBatt = Math.Max(8, Math.Min(45, h.KCpuAc * 0.45));
        h.OtherW = h.Laptop ? 5 : 20;
        h.FanBaseW = h.Laptop ? 0.3 : 1.5;
        h.Efficiency = h.Laptop ? 0.88 : 0.85;
        h.NvmlPresent = File.Exists(Path.Combine(Environment.SystemDirectory, "nvml.dll"));
        return h;
    }

    // İşlemci model son ekinden kabaca tam yük gücü (TDP sınıfı)
    static double EstimateCpuW(string name, bool laptop)
    {
        string n = Regex.Replace(name.ToUpperInvariant(), @"\((R|TM)\)", " ");
        Match m = null;
        foreach (Match x in Regex.Matches(n, @"\b\d{3,5}([A-Z]{0,3})\b")) m = x;
        string s = m != null ? m.Groups[1].Value : "";
        if (s.Contains("HX")) return 90;
        if (s.StartsWith("H")) return n.Contains("RYZEN") ? 45 : 55;
        if (s.StartsWith("U")) return 20;
        if (s.StartsWith("Y")) return 9;
        if (s.StartsWith("P")) return 30;
        if (s.StartsWith("V")) return 17;
        if (s.StartsWith("T")) return 35;
        if (s.StartsWith("K")) return 150;
        if (s.StartsWith("X")) return 110;
        if (s.StartsWith("G")) return 55;
        return laptop ? 45 : 65;
    }
}

static class Nvml
{
    [DllImport("nvml.dll")] static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] static extern int nvmlShutdown();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr dev);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint mW);

    static List<IntPtr> devices;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Init()
    {
        try
        {
            if (nvmlInit_v2() != 0) return false;
            uint n;
            if (nvmlDeviceGetCount_v2(out n) != 0) return false;
            devices = new List<IntPtr>();
            for (uint i = 0; i < n; i++)
            {
                IntPtr d;
                if (nvmlDeviceGetHandleByIndex_v2(i, out d) == 0) devices.Add(d);
            }
            return devices.Count > 0;
        }
        catch { return false; } // nvml.dll yüklenemedi
    }

    // Watt; okunamazsa -1
    public static double ReadWatts()
    {
        if (devices == null) return -1;
        double sum = 0; bool any = false;
        foreach (IntPtr d in devices)
        {
            uint mW;
            if (nvmlDeviceGetPowerUsage(d, out mW) == 0) { sum += mW / 1000.0; any = true; }
        }
        return any ? sum : -1;
    }

    public static void Shutdown()
    {
        try { if (devices != null) nvmlShutdown(); } catch { }
    }
}

// Fiziksel disklerin meşgul oranı ve türü (yönetici gerektirmez; 0 erişim hakkıyla açılır)
class DiskMonitor
{
    const uint IOCTL_DISK_PERFORMANCE = 0x70020;
    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    readonly List<IntPtr> handles = new List<IntPtr>();
    readonly List<long> lastIdle = new List<long>(), lastQuery = new List<long>();
    public readonly List<bool> IsHdd = new List<bool>();
    readonly byte[] buf = new byte[88];

    public DiskMonitor()
    {
        for (int i = 0; i < 32; i++)
        {
            IntPtr h = Native.CreateFile(@"\\.\PhysicalDrive" + i, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) continue;
            long idle, query;
            if (!Query(h, out idle, out query)) continue;
            handles.Add(h); lastIdle.Add(idle); lastQuery.Add(query);
            IsHdd.Add(SeekPenalty(h));
        }
    }

    static bool SeekPenalty(IntPtr h)
    {
        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceSeekPenaltyProperty (7), QueryType = 0 }
        byte[] q = new byte[12];
        BitConverter.GetBytes(7).CopyTo(q, 0);
        byte[] o = new byte[12];
        uint r;
        if (!Native.DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, q, (uint)q.Length, o, (uint)o.Length, out r, IntPtr.Zero) || r < 9)
            return false;
        return o[8] != 0; // DEVICE_SEEK_PENALTY_DESCRIPTOR.IncursSeekPenalty
    }

    bool Query(IntPtr h, out long idle, out long query)
    {
        uint r;
        idle = query = 0;
        if (!Native.DeviceIoControl(h, IOCTL_DISK_PERFORMANCE, null, 0, buf, (uint)buf.Length, out r, IntPtr.Zero))
            return false;
        idle = BitConverter.ToInt64(buf, 32);  // IdleTime (100 ns)
        query = BitConverter.ToInt64(buf, 56); // QueryTime (100 ns)
        return true;
    }

    // her disk için 0..1 meşguliyet
    public double[] Busy()
    {
        double[] res = new double[handles.Count];
        for (int i = 0; i < handles.Count; i++)
        {
            long idle, query;
            if (!Query(handles[i], out idle, out query)) continue;
            long dq = query - lastQuery[i], di = idle - lastIdle[i];
            lastIdle[i] = idle; lastQuery[i] = query;
            if (dq > 0) res[i] = Math.Max(0, Math.Min(1, 1.0 - (double)di / dq));
        }
        return res;
    }
}

// Fiziksel ağ bağdaştırıcılarının trafiği (bayt/sn)
class NetMonitor
{
    class Nic { public NetworkInterface If; public bool Wifi; public long Last; }
    List<Nic> nics = new List<Nic>();
    DateTime lastRefresh = DateTime.MinValue;

    public double WifiBps, EthBps;
    public int WifiCount, EthCount;

    void Refresh()
    {
        var list = new List<Nic>();
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                bool wifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                if (!wifi && ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet) continue;
                string d = ni.Description;
                if (d.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    d.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    d.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    d.IndexOf("VirtualBox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    d.IndexOf("VPN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    d.IndexOf("TAP", StringComparison.Ordinal) >= 0 ||
                    d.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Nic n = new Nic(); n.If = ni; n.Wifi = wifi; n.Last = Bytes(ni);
                list.Add(n);
            }
        }
        catch { }
        nics = list;
        lastRefresh = DateTime.Now;
    }

    static long Bytes(NetworkInterface ni)
    {
        try { IPInterfaceStatistics s = ni.GetIPStatistics(); return s.BytesReceived + s.BytesSent; }
        catch { return 0; }
    }

    public void Sample(double dt)
    {
        if ((DateTime.Now - lastRefresh).TotalSeconds >= 60) Refresh();
        WifiBps = EthBps = 0; WifiCount = EthCount = 0;
        foreach (Nic n in nics)
        {
            long b = Bytes(n.If);
            double bps = dt > 0 && b >= n.Last ? (b - n.Last) / dt : 0;
            n.Last = b;
            if (n.Wifi) { WifiBps += bps; WifiCount++; } else { EthBps += bps; EthCount++; }
        }
    }
}

// Ekran: açık/kapalı/kısık + kapak (power notification) + dahili panel parlaklığı
// (önce Windows bildirimi; gelmezse dizüstünde yedek olarak WMI)
class ScreenMonitor : NativeWindow
{
    static Guid DisplayStateGuid = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
    static Guid LidGuid = new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
    static Guid BrightnessGuid = new Guid("8FFEE2C6-2D01-46BE-ADB9-398ADDC5B4FF"); // GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS

    readonly bool allowWmi;
    readonly DateTime started = DateTime.Now;
    volatile int displayState = 1; // 0 kapalı, 1 açık, 2 kısık
    volatile int lidOpen = 1;
    volatile int brightness = -1;   // 0..100, -1 bilinmiyor
    volatile bool brightnessNotified;
    DateTime lastPoll = DateTime.MinValue;
    int polling;

    public ScreenMonitor(bool allowWmiFallback)
    {
        allowWmi = allowWmiFallback;
        CreateHandle(new CreateParams()); // görünmez üst düzey pencere
        Native.RegisterPowerSettingNotification(Handle, ref DisplayStateGuid, 0);
        Native.RegisterPowerSettingNotification(Handle, ref LidGuid, 0);
        Native.RegisterPowerSettingNotification(Handle, ref BrightnessGuid, 0);
    }

    public bool BrightnessNotified { get { return brightnessNotified; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x218 && m.WParam.ToInt64() == 0x8013) // WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE
        {
            Guid g = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
            int data = Marshal.ReadInt32(m.LParam, 20);
            if (g == DisplayStateGuid) displayState = data;
            else if (g == LidGuid) lidOpen = data;
            else if (g == BrightnessGuid && data >= 0 && data <= 100) { brightness = data; brightnessNotified = true; }
        }
        base.WndProc(ref m);
    }

    public void Poll()
    {
        // bildirim geliyorsa WMI hiç yüklenmez; gelmezse 10 sn sonra başla, 60 sn'de bir yokla
        if (!allowWmi || brightnessNotified || (DateTime.Now - started).TotalSeconds < 10) return;
        if ((DateTime.Now - lastPoll).TotalSeconds < 60) return;
        lastPoll = DateTime.Now;
        if (Interlocked.Exchange(ref polling, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { int b = WmiBrightness.Read(); if (!brightnessNotified) brightness = b; }
            finally { Interlocked.Exchange(ref polling, 0); }
        });
    }

    public bool DisplayOn { get { return displayState != 0; } }
    public bool InternalOn { get { return displayState != 0 && lidOpen != 0; } }
    public bool Dimmed { get { return displayState == 2; } }
    public int Brightness { get { return brightness; } }

    // dahili panel için etkin parlaklık 0..1 (kapalıysa 0)
    public double Level
    {
        get
        {
            if (!InternalOn) return 0;
            double b = brightness >= 0 ? brightness / 100.0 : 0.5;
            return Dimmed ? b * 0.5 : b;
        }
    }

    public string StateText
    {
        get
        {
            if (lidOpen == 0) return "kapak kapalı";
            if (displayState == 0) return "kapalı";
            string b = brightness >= 0 ? "%" + brightness : "parlaklık ?";
            return Dimmed ? b + " (kısık)" : b;
        }
    }
}

// System.Management yalnızca gerçekten gerekirse yüklensin diye ayrı sınıfta
static class WmiBrightness
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Read()
    {
        try
        {
            using (var s = new System.Management.ManagementObjectSearcher(@"root\wmi",
                "SELECT CurrentBrightness, Active FROM WmiMonitorBrightness"))
                foreach (System.Management.ManagementObject o in s.Get())
                    if (Convert.ToBoolean(o["Active"])) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return -1;
    }
}

// Bileşen indeksleri
static class C
{
    public const int Ekran = 0, Cpu = 1, Gpu = 2, Ram = 3, Disk = 4, Ag = 5, Fan = 6, Diger = 7,
        Monitor = 8, Sarj = 9, Kayip = 10, N = 11;
    public const int Bilgisayar = 8;  // 0..7: bilgisayarın kendi tüketimi
    public const int Tuketiciler = 9; // 0..8: maliyetin dağıtıldığı tüketiciler
    public static readonly string[] Ad = {
        "Ekran (dahili)", "İşlemci (CPU)", "Ekran kartı (NVIDIA)", "RAM", "Diskler", "Ağ (Wi-Fi/Ethernet)",
        "Fanlar", "Diğer (anakart, USB, ses, ışıklar)", "Harici monitör", "Pil şarjı", "Adaptör / PSU kaybı" };
    public static readonly string[] Key = {
        "ekran", "cpu", "gpu", "ram", "disk", "ag", "fan", "diger", "monitor", "sarj", "kayip" };
}

class DayRecord
{
    public double SystemWh, WallWh, MeasuredSec, TotalSec;
    public double[] Comp = new double[C.N]; // Wh
}

class Store
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GucSayaci");
    string DataPath { get { return Path.Combine(Dir, "data.csv"); } }
    string ConfigPath { get { return Path.Combine(Dir, "config.ini"); } }

    public SortedDictionary<DateTime, DayRecord> Days = new SortedDictionary<DateTime, DayRecord>();
    public Dictionary<string, double> Cfg = new Dictionary<string, double>();

    public double Get(string k, double def)
    {
        double v; return Cfg.TryGetValue(k, out v) ? v : def;
    }

    public void Load()
    {
        Directory.CreateDirectory(Dir);
        if (File.Exists(ConfigPath))
            foreach (string line in File.ReadAllLines(ConfigPath))
            {
                int i = line.IndexOf('=');
                double v;
                if (i > 0 && double.TryParse(line.Substring(i + 1), NumberStyles.Float, Inv, out v))
                    Cfg[line.Substring(0, i).Trim()] = v;
            }
        if (!File.Exists(DataPath)) return;

        // sütunlar başlıktan eşlenir; eski/yeni sürüm dosyaları birlikte okunabilir
        string[] lines = File.ReadAllLines(DataPath);
        if (lines.Length == 0) return;
        string[] head = lines[0].Split(';');
        int[] map = new int[head.Length];
        for (int i = 0; i < head.Length; i++) map[i] = Array.IndexOf(C.Key, head[i].Trim());
        for (int li = 1; li < lines.Length; li++)
        {
            string[] p = lines[li].Split(';');
            DateTime d;
            if (p.Length < 5 || !DateTime.TryParseExact(p[0], "yyyy-MM-dd", Inv, DateTimeStyles.None, out d)) continue;
            try
            {
                DayRecord r = new DayRecord();
                r.SystemWh = double.Parse(p[1], Inv); r.WallWh = double.Parse(p[2], Inv);
                r.MeasuredSec = double.Parse(p[3], Inv); r.TotalSec = double.Parse(p[4], Inv);
                for (int i = 5; i < p.Length && i < map.Length; i++)
                    if (map[i] >= 0) r.Comp[map[i]] = double.Parse(p[i], Inv);
                Days[d] = r;
            }
            catch (FormatException) { }
        }
    }

    public DayRecord Day(DateTime d)
    {
        DayRecord r;
        if (!Days.TryGetValue(d.Date, out r)) { r = new DayRecord(); Days[d.Date] = r; }
        return r;
    }

    public void Save()
    {
        StringBuilder sb = new StringBuilder("tarih;bilgisayar_wh;priz_wh;olculen_sn;toplam_sn");
        foreach (string k in C.Key) sb.Append(';').Append(k);
        sb.Append('\n');
        foreach (KeyValuePair<DateTime, DayRecord> kv in Days)
        {
            DayRecord r = kv.Value;
            sb.AppendFormat(Inv, "{0:yyyy-MM-dd};{1:0.###};{2:0.###};{3:0};{4:0}",
                kv.Key, r.SystemWh, r.WallWh, r.MeasuredSec, r.TotalSec);
            for (int c = 0; c < C.N; c++) sb.AppendFormat(Inv, ";{0:0.###}", r.Comp[c]);
            sb.Append('\n');
        }
        WriteAtomic(DataPath, sb.ToString());

        sb.Clear();
        foreach (KeyValuePair<string, double> kv in Cfg)
            sb.AppendFormat(Inv, "{0}={1:R}\n", kv.Key, kv.Value);
        WriteAtomic(ConfigPath, sb.ToString());
    }

    static void WriteAtomic(string path, string content)
    {
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        catch (IOException) { } // bir sonraki kayıtta tekrar denenir
    }
}

class Meter
{
    // Genel model sabitleri (tipik değerler; cihaza özgü olanlar Hardware'den gelir)
    const double ScreenBaseW = 1.0;     // dahili panel elektroniği (ekran açıkken sabit)
    const double DefKScreen = 4.0;      // dahili panel arka ışığı %100 parlaklıkta
    const double CpuIdleW = 1.5;
    const double RamBaseW = 1.2, RamLoadW = 1.0;
    const double SsdIdleW = 0.15, SsdActiveW = 3.0;
    const double HddIdleW = 4.0, HddActiveW = 2.0;
    const double WifiIdleW = 0.3, WifiActiveW = 1.7, WifiFullBps = 40e6;
    const double EthIdleW = 0.4, EthActiveW = 0.6, EthFullBps = 100e6;
    const double FanPerHeatW = 0.03, FanMaxW = 8;

    const int BucketSamples = 6;        // 6 x 5 sn = 30 sn'lik kalibrasyon penceresi
    const double PriorOther = 1, PriorK = 5; // ön bilginin örnek cinsinden ağırlığı

    readonly Store store;
    public readonly Hardware Hw;
    readonly DiskMonitor disk = new DiskMonitor();
    readonly NetMonitor net = new NetMonitor();
    public readonly ScreenMonitor Screen;
    long lastIdle, lastKernel, lastUser;
    readonly bool gpuOk;

    // kalibrasyon kovası
    int bN; double bX1, bX2, bY;

    public readonly double[] W = new double[C.N];
    public readonly string[] Src = new string[C.N];
    public double SystemW, WallW, CpuUtil, GpuW = -1;
    public bool OnBattery, Measured;

    public Meter(Store s)
    {
        store = s;
        Hw = Hardware.Detect();
        Screen = new ScreenMonitor(Hw.Laptop);
        Native.GetSystemTimes(out lastIdle, out lastKernel, out lastUser);
        gpuOk = store.Get("gpu_olc", 1) != 0 && Hw.NvmlPresent && Nvml.Init();
    }

    public double Tariff { get { return store.Get("tarife_tl_kwh", 3.24); } }
    public double Efficiency { get { return store.Get("adaptor_verimi", Hw.Efficiency); } }
    public double KCpuAc { get { return store.Get("k_cpu_w", Hw.KCpuAc); } }
    public double MonitorW { get { return store.Get("harici_monitor_w", 0); } }
    public int CalibrationSamples { get { return (int)store.Get("r_n", 0); } }
    public bool GpuOk { get { return gpuOk; } }

    // Pil ölçümlerinden: y = diğer + kCpu*x1 + kEkran*x2  (ridge ön bilgili en küçük kareler)
    public void Fit(out double other, out double kCpuBatt, out double kScreen)
    {
        double n = store.Get("r_n", 0), s1 = store.Get("r_s1", 0), s2 = store.Get("r_s2", 0), sy = store.Get("r_sy", 0);
        double s11 = store.Get("r_s11", 0), s22 = store.Get("r_s22", 0), s12 = store.Get("r_s12", 0);
        double s1y = store.Get("r_s1y", 0), s2y = store.Get("r_s2y", 0);
        double[,] a = {
            { n + PriorOther, s1, s2 },
            { s1, s11 + PriorK, s12 },
            { s2, s12, s22 + PriorK } };
        double[] b = { sy + PriorOther * Hw.OtherW, s1y + PriorK * Hw.KCpuBatt, s2y + PriorK * DefKScreen };
        double[] x = Solve3(a, b);
        if (x == null) x = new double[] { Hw.OtherW, Hw.KCpuBatt, DefKScreen };
        other = Clamp(x[0], 0, 60);
        kCpuBatt = Clamp(x[1], 3, 200);
        kScreen = Clamp(x[2], 0.5, 15);
    }

    static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }

    static double[] Solve3(double[,] a, double[] b)
    {
        // Gauss-Jordan eleme, kısmi pivotlama
        const int n = 3;
        double[,] m = new double[n, n + 1];
        for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) m[i, j] = a[i, j]; m[i, n] = b[i]; }
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
            if (Math.Abs(m[p, c]) < 1e-12) return null;
            for (int j = 0; j <= n; j++) { double t = m[c, j]; m[c, j] = m[p, j]; m[p, j] = t; }
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                double f = m[r, c] / m[c, c];
                for (int j = c; j <= n; j++) m[r, j] -= f * m[c, j];
            }
        }
        return new double[] { m[0, n] / m[0, 0], m[1, n] / m[1, 1], m[2, n] / m[2, 2] };
    }

    double ReadCpu()
    {
        long idle, kernel, user;
        Native.GetSystemTimes(out idle, out kernel, out user);
        long dIdle = idle - lastIdle, dTotal = (kernel - lastKernel) + (user - lastUser);
        lastIdle = idle; lastKernel = kernel; lastUser = user;
        if (dTotal <= 0) return 0;
        return Math.Max(0, Math.Min(1, 1.0 - (double)dIdle / dTotal));
    }

    public void ResetBaselines()
    {
        Native.GetSystemTimes(out lastIdle, out lastKernel, out lastUser);
        disk.Busy(); net.Sample(0);
        ResetBucket();
    }

    void ResetBucket() { bN = 0; bX1 = bX2 = bY = 0; }

    public void Sample(double dt)
    {
        CpuUtil = ReadCpu();
        GpuW = gpuOk ? Nvml.ReadWatts() : -1;
        double gpu = Math.Max(0, GpuW);
        double[] busy = disk.Busy();
        net.Sample(dt);
        Screen.Poll();

        Native.SYSTEM_BATTERY_STATE bs;
        bool batOk = Native.BatteryState(out bs);
        bool rateValid = batOk && bs.BatteryPresent != 0 && bs.Rate != int.MinValue;
        OnBattery = batOk && bs.BatteryPresent != 0 && bs.AcOnLine == 0;
        Measured = OnBattery && rateValid && bs.Rate < 0;

        double other, kCpuBatt, kScreen;
        Fit(out other, out kCpuBatt, out kScreen);
        double kCpu = OnBattery ? kCpuBatt : KCpuAc;
        double level = Hw.Laptop ? Screen.Level : 0;
        double scrOn = Hw.Laptop && Screen.InternalOn ? 1 : 0;
        bool calibrated = CalibrationSamples >= 10;

        // --- bileşen modelleri ---
        W[C.Ekran] = scrOn * ScreenBaseW + kScreen * level;
        double cpuLoad = kCpu * CpuUtil;
        W[C.Cpu] = CpuIdleW + cpuLoad;
        W[C.Gpu] = gpu;
        W[C.Ram] = RamBaseW + RamLoadW * CpuUtil;
        double dsk = 0, busyMax = 0; int hdd = 0;
        for (int i = 0; i < busy.Length; i++)
        {
            bool isHdd = disk.IsHdd[i];
            if (isHdd) hdd++;
            dsk += isHdd ? HddIdleW + HddActiveW * busy[i] : SsdIdleW + SsdActiveW * busy[i];
            busyMax = Math.Max(busyMax, busy[i]);
        }
        W[C.Disk] = dsk;
        W[C.Ag] = net.WifiCount * WifiIdleW + WifiActiveW * Math.Min(1, net.WifiBps / WifiFullBps)
                + net.EthCount * EthIdleW + EthActiveW * Math.Min(1, net.EthBps / EthFullBps);
        W[C.Fan] = Math.Min(FanMaxW, Hw.FanBaseW + FanPerHeatW * (cpuLoad + gpu));
        W[C.Diger] = other;
        W[C.Monitor] = Screen.DisplayOn ? MonitorW : 0; // kendi prizinden beslenir
        W[C.Sarj] = 0; W[C.Kayip] = 0;

        Src[C.Ekran] = Hw.Laptop ? "model, " + Screen.StateText + (calibrated ? ", kalibre" : "") : "dahili ekran yok";
        Src[C.Cpu] = "model, yük %" + (CpuUtil * 100).ToString("0");
        Src[C.Gpu] = gpuOk ? "ÖLÇÜM (NVML)" : "NVIDIA yok / okunamıyor";
        Src[C.Ram] = "model";
        Src[C.Disk] = "model, " + busy.Length + " disk" + (hdd > 0 ? " (" + hdd + " HDD)" : "") +
                      ", meşgul %" + (busyMax * 100).ToString("0");
        Src[C.Ag] = "model, " + ((net.WifiBps + net.EthBps) / 1024).ToString("0") + " KB/s";
        Src[C.Fan] = "model (ısı yükü)";
        Src[C.Diger] = calibrated ? "pil ölçümünden kalibre" : "varsayılan";
        Src[C.Monitor] = MonitorW > 0 ? "ayar: " + MonitorW.ToString("0") + " W" : "ayarlanmadı (menü)";
        Src[C.Sarj] = Hw.Laptop ? "ÖLÇÜM (pil)" : "pil yok";
        Src[C.Kayip] = "model, verim %" + (Efficiency * 100).ToString("0");

        if (Measured)
        {
            // Gerçek toplam: pilden çekilen güç. Model dışı kalan kısım "Diğer"e gider.
            double total = -bs.Rate / 1000.0;
            double fixedPart = W[C.Gpu] + W[C.Disk] + W[C.Ag] + W[C.Ram] + W[C.Fan] + CpuIdleW + scrOn * ScreenBaseW;
            AddCalibration(CpuUtil, level, total - fixedPart);

            double est = 0;
            for (int c = 0; c < C.Diger; c++) est += W[c];
            double residual = total - est;
            if (residual >= 0) W[C.Diger] = residual;
            else
            {
                // model fazla tahmin etti: ölçülmeyen bileşenleri orantılı küçült (GPU ölçüm, dokunma)
                W[C.Diger] = 0;
                double scalable = est - W[C.Gpu], target = Math.Max(0, total - W[C.Gpu]);
                double f = scalable > 0 ? target / scalable : 0;
                for (int c = 0; c < C.Diger; c++) if (c != C.Gpu) W[c] *= f;
            }
            Src[C.Diger] = "ÖLÇÜM − diğer bileşenler";
            SystemW = total;
            WallW = W[C.Monitor];
        }
        else
        {
            ResetBucket();
            SystemW = 0;
            for (int c = 0; c < C.Bilgisayar; c++) SystemW += W[c];
            if (!OnBattery)
            {
                if (rateValid && bs.Rate > 0) W[C.Sarj] = bs.Rate / 1000.0;
                W[C.Kayip] = (SystemW + W[C.Sarj]) * (1 / Efficiency - 1);
                WallW = SystemW + W[C.Sarj] + W[C.Kayip] + W[C.Monitor];
            }
            else WallW = W[C.Monitor];
        }
    }

    void AddCalibration(double x1, double x2, double y)
    {
        bN++; bX1 += x1; bX2 += x2; bY += y;
        if (bN < BucketSamples) return;
        x1 = bX1 / bN; x2 = bX2 / bN; y = bY / bN;
        ResetBucket();

        string[] keys = { "r_n", "r_s1", "r_s2", "r_sy", "r_s11", "r_s22", "r_s12", "r_s1y", "r_s2y" };
        if (store.Get("r_n", 0) >= 2000) // eski örnekleri yavaşça unut
            foreach (string k in keys) store.Cfg[k] = store.Get(k, 0) * 0.5;
        double[] add = { 1, x1, x2, y, x1 * x1, x2 * x2, x1 * x2, x1 * y, x2 * y };
        for (int i = 0; i < keys.Length; i++) store.Cfg[keys[i]] = store.Get(keys[i], 0) + add[i];
    }

    public void Close() { Nvml.Shutdown(); Screen.DestroyHandle(); }
}

class SummaryForm : Form
{
    readonly Store store; readonly Meter meter;
    readonly Label live = new Label();
    readonly ListView comps = new ListView();
    readonly ListView list = new ListView();
    static readonly CultureInfo Tr = new CultureInfo("tr-TR");

    float dpi = 1;
    int S(int px) { return (int)Math.Round(px * dpi); } // 96 DPI piksel -> ekran pikseli

    public SummaryForm(Store s, Meter m, Icon icon)
    {
        store = s; meter = m;
        using (Graphics g = CreateGraphics()) dpi = g.DpiX / 96f;
        Text = "Güç Sayacı " + Info.Version; Icon = icon;
        Width = S(840); Height = S(700); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        live.Dock = DockStyle.Top; live.Height = S(78); live.Padding = new Padding(S(10), S(8), S(10), 0);

        comps.Dock = DockStyle.Top; comps.Height = S(282); comps.View = View.Details;
        comps.FullRowSelect = true; comps.GridLines = true;
        comps.Columns.Add("Bileşen", S(240));
        comps.Columns.Add("Şimdi (W)", S(80), HorizontalAlignment.Right);
        comps.Columns.Add("Pay", S(55), HorizontalAlignment.Right);
        comps.Columns.Add("Bugün (Wh)", S(85), HorizontalAlignment.Right);
        comps.Columns.Add("Bugün (₺)", S(75), HorizontalAlignment.Right);
        comps.Columns.Add("Kaynak", S(260));
        for (int c = 0; c < C.N; c++)
        {
            ListViewItem it = new ListViewItem(C.Ad[c]);
            for (int j = 0; j < 5; j++) it.SubItems.Add("");
            comps.Items.Add(it);
        }

        list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.GridLines = true;
        list.Columns.Add("Tarih", S(140));
        list.Columns.Add("Bilgisayar (kWh)", S(115), HorizontalAlignment.Right);
        list.Columns.Add("Prizden (kWh)", S(105), HorizontalAlignment.Right);
        list.Columns.Add("Maliyet (₺)", S(90), HorizontalAlignment.Right);
        list.Columns.Add("Ekran (kWh)", S(90), HorizontalAlignment.Right);
        list.Columns.Add("Ölçüm %", S(70), HorizontalAlignment.Right);
        list.Columns.Add("Süre (sa)", S(70), HorizontalAlignment.Right);

        Splitter sp = new Splitter(); sp.Dock = DockStyle.Top; sp.Height = S(6);
        Controls.Add(list); Controls.Add(sp); Controls.Add(comps); Controls.Add(live);
        RefreshView(true);
    }

    public void RefreshView(bool full)
    {
        string mode = meter.Measured ? "toplam PİL ÖLÇÜMÜ (gerçek)"
            : meter.OnBattery ? "pilde"
            : meter.Hw.Laptop ? "prizde: bileşen modeli" + (meter.GpuOk ? " + GPU ölçümü" : "")
            : "masaüstü: bileşen modeli" + (meter.GpuOk ? " + GPU ölçümü" : "");
        string calib;
        if (meter.Hw.Laptop)
        {
            double other, kCpuB, kScr;
            meter.Fit(out other, out kCpuB, out kScr);
            calib = string.Format(Tr, "Kalibrasyon: {0} örnek → diğer {1:0.0} W, CPU(pil) {2:0} W, ekran %100 = {3:0.0} W",
                meter.CalibrationSamples, other, kCpuB, kScr + 1.0);
        }
        else calib = "Pil yok: kalibrasyon yapılamaz, değerler model tahminidir";
        live.Text = string.Format(Tr,
            "Şimdi: {0:0.0} W bilgisayar  |  {1:0.0} W prizden  —  {2}\n{3}\n" +
            "CPU: {4} (tam yük {5:0} W)  |  tarife {6:0.00} ₺/kWh  |  verim %{7:0}",
            meter.SystemW, meter.WallW, mode, calib, meter.Hw.CpuName, meter.KCpuAc, meter.Tariff, meter.Efficiency * 100);

        DayRecord today = store.Day(DateTime.Now);
        double consumersWh = 0;
        for (int c = 0; c < C.Tuketiciler; c++) consumersWh += today.Comp[c];
        double todayCost = today.WallWh / 1000 * meter.Tariff;
        double nowTotal = meter.SystemW + meter.W[C.Monitor];
        if (nowTotal <= 0) nowTotal = 1;

        comps.BeginUpdate();
        for (int c = 0; c < C.N; c++)
        {
            ListViewItem it = comps.Items[c];
            it.SubItems[1].Text = meter.W[c].ToString("0.00", Tr);
            it.SubItems[2].Text = c < C.Tuketiciler ? (meter.W[c] / nowTotal * 100).ToString("0", Tr) + "%" : "";
            it.SubItems[3].Text = today.Comp[c].ToString("0.0", Tr);
            // maliyet: prizden çekilen toplam (kayıp dahil) tüketicilere enerji payına göre dağıtılır
            it.SubItems[4].Text = c < C.Tuketiciler && consumersWh > 0
                ? (todayCost * today.Comp[c] / consumersWh).ToString("0.00", Tr) : "—";
            string src = meter.Src[c] ?? "";
            it.SubItems[5].Text = src;
            it.ForeColor = src.Contains("ÖLÇÜM") ? Color.FromArgb(0, 120, 60) : SystemColors.WindowText;
        }
        comps.EndUpdate();

        if (full) RefreshDays();
    }

    void RefreshDays()
    {
        list.BeginUpdate();
        list.Items.Clear();
        DateTime now = DateTime.Today;
        var months = new SortedDictionary<DateTime, DayRecord>();
        double t = meter.Tariff;
        var rows = new List<KeyValuePair<DateTime, DayRecord>>(store.Days);
        rows.Reverse();
        var dayItems = new List<ListViewItem>();
        foreach (KeyValuePair<DateTime, DayRecord> kv in rows)
        {
            DateTime m = new DateTime(kv.Key.Year, kv.Key.Month, 1);
            DayRecord agg;
            if (!months.TryGetValue(m, out agg)) { agg = new DayRecord(); months[m] = agg; }
            agg.SystemWh += kv.Value.SystemWh; agg.WallWh += kv.Value.WallWh;
            agg.MeasuredSec += kv.Value.MeasuredSec; agg.TotalSec += kv.Value.TotalSec;
            for (int c = 0; c < C.N; c++) agg.Comp[c] += kv.Value.Comp[c];
            if (kv.Key >= now.AddDays(-31)) dayItems.Add(Row(kv.Key.ToString("dd.MM.yyyy ddd", Tr), kv.Value, t));
        }
        list.Items.Add(new ListViewItem("── Aylar ──"));
        var ms = new List<KeyValuePair<DateTime, DayRecord>>(months); ms.Reverse();
        foreach (KeyValuePair<DateTime, DayRecord> kv in ms)
        {
            ListViewItem it = Row(kv.Key.ToString("MMMM yyyy", Tr), kv.Value, t);
            it.Font = new Font(list.Font, FontStyle.Bold);
            list.Items.Add(it);
        }
        list.Items.Add(new ListViewItem("── Günler (son 31) ──"));
        list.Items.AddRange(dayItems.ToArray());
        list.EndUpdate();
    }

    static ListViewItem Row(string label, DayRecord r, double tariff)
    {
        ListViewItem it = new ListViewItem(label);
        it.SubItems.Add((r.SystemWh / 1000).ToString("0.000", Tr));
        it.SubItems.Add((r.WallWh / 1000).ToString("0.000", Tr));
        it.SubItems.Add((r.WallWh / 1000 * tariff).ToString("0.00", Tr));
        it.SubItems.Add((r.Comp[C.Ekran] / 1000).ToString("0.000", Tr));
        it.SubItems.Add(r.TotalSec > 0 ? (r.MeasuredSec / r.TotalSec * 100).ToString("0", Tr) : "-");
        it.SubItems.Add((r.TotalSec / 3600).ToString("0.0", Tr));
        return it;
    }
}

class App : ApplicationContext
{
    const int IntervalMs = 5000;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly CultureInfo Tr = new CultureInfo("tr-TR");

    readonly Store store = new Store();
    readonly Meter meter;
    readonly Icon appIcon;
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Stopwatch clock = Stopwatch.StartNew();
    double lastT; DateTime lastSave = DateTime.Now;
    int ticks;
    SummaryForm form;
    ToolStripMenuItem autoStartItem;

    public App(bool showSummary)
    {
        store.Load();
        meter = new Meter(store);
        appIcon = LoadIcon();

        tray.Icon = appIcon;
        tray.Visible = true;
        tray.DoubleClick += delegate { ShowSummary(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Özet", null, delegate { ShowSummary(); });
        var settings = new ToolStripMenuItem("Ayarlar");
        settings.DropDownItems.Add("Elektrik tarifesi (₺/kWh)...", null, delegate { AskNumber("tarife_tl_kwh", "Faturanızdaki vergiler dahil kWh birim fiyatı (₺):", meter.Tariff, 0.01, 1000); });
        settings.DropDownItems.Add("Harici monitör gücü (W)...", null, delegate { AskNumber("harici_monitor_w", "Harici monitör(ler)in toplam gücü (W).\nEtiketinde veya kılavuzunda yazar; tipik: 15-35 W. Yoksa 0.", meter.MonitorW, 0, 1000); });
        settings.DropDownItems.Add("İşlemci tam yük gücü (W)...", null, delegate { AskNumber("k_cpu_w", "İşlemci %100 yükteyken ek güç (W).\nOtomatik tahmin: " + meter.Hw.KCpuAc.ToString("0") + " W (" + meter.Hw.CpuName + ")", meter.KCpuAc, 3, 500); });
        settings.DropDownItems.Add("Adaptör / PSU verimi...", null, delegate { AskNumber("adaptor_verimi", "Şarj adaptörü veya güç kaynağı (PSU) verimi (0,70 - 0,98):", meter.Efficiency, 0.5, 1); });
        menu.Items.Add(settings);
        autoStartItem = new ToolStripMenuItem("Windows ile başlat", null, delegate { ToggleAutoStart(); });
        autoStartItem.Checked = IsAutoStart();
        menu.Items.Add(autoStartItem);
        menu.Items.Add("Veri klasörünü aç", null, delegate { Process.Start("explorer.exe", store.Dir); });
        menu.Items.Add("Hakkında", null, delegate { About(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, delegate { ExitThread(); });
        tray.ContextMenuStrip = menu;

        meter.Sample(0);
        lastT = clock.Elapsed.TotalSeconds;
        timer.Interval = IntervalMs;
        timer.Tick += delegate { Tick(); };
        timer.Start();
        SystemEvents.PowerModeChanged += OnPowerMode;
        SystemEvents.SessionEnding += delegate { store.Save(); };
        UpdateTooltip();
        if (showSummary) ShowSummary();
        else Native.TrimMemory(true);
    }

    void OnPowerMode(object s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) store.Save();
        if (e.Mode == PowerModes.Resume) { meter.ResetBaselines(); lastT = clock.Elapsed.TotalSeconds; }
    }

    void Tick()
    {
        double t = clock.Elapsed.TotalSeconds, dt = t - lastT;
        lastT = t;
        meter.Sample(dt);
        // uyku/donma sonrası büyük boşlukları sayma
        if (dt > 0 && dt < IntervalMs / 1000.0 * 5)
        {
            DayRecord d = store.Day(DateTime.Now);
            d.SystemWh += meter.SystemW * dt / 3600;
            d.WallWh += meter.WallW * dt / 3600;
            for (int c = 0; c < C.N; c++) d.Comp[c] += meter.W[c] * dt / 3600;
            d.TotalSec += dt;
            if (meter.Measured) d.MeasuredSec += dt;
        }
        bool minute = (DateTime.Now - lastSave).TotalSeconds >= 60;
        if (minute)
        {
            store.Save(); lastSave = DateTime.Now;
            WriteStatus();
        }
        UpdateTooltip();
        ticks++;
        bool formVisible = form != null && !form.IsDisposed && form.Visible;
        if (formVisible) form.RefreshView(ticks % 6 == 0);
        else Native.TrimMemory(minute); // arka planda bellek ayak izini küçük tut
    }

    // anlık durum: %LOCALAPPDATA%\GucSayaci\durum.txt
    void WriteStatus()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(Tr, "{0:G}  bilgisayar {1:0.0} W, prizden {2:0.0} W, {3}\n", DateTime.Now, meter.SystemW, meter.WallW,
                meter.Measured ? "pil ölçümü" : (meter.OnBattery ? "pilde" : "prizde"));
            sb.AppendFormat("{0} | {1} | parlaklık: {2}\n", meter.Hw.Laptop ? "dizüstü" : "masaüstü", meter.Hw.CpuName,
                meter.Screen.BrightnessNotified ? "Windows bildirimi" : "WMI/bilinmiyor");
            for (int c = 0; c < C.N; c++)
                sb.AppendFormat(Tr, "{0,-36} {1,7:0.00} W   {2}\n", C.Ad[c], meter.W[c], meter.Src[c]);
            File.WriteAllText(Path.Combine(store.Dir, "durum.txt"), sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    void UpdateTooltip()
    {
        DayRecord d = store.Day(DateTime.Now);
        string s = string.Format(Tr, "{0:0} W{1} | Bugün {2:0.00} kWh {3:0.00}₺",
            meter.SystemW + meter.W[C.Monitor], meter.Measured ? "*" : "", d.WallWh / 1000, d.WallWh / 1000 * meter.Tariff);
        tray.Text = s.Length > 63 ? s.Substring(0, 63) : s; // NotifyIcon sınırı
    }

    void ShowSummary()
    {
        if (form == null || form.IsDisposed) form = new SummaryForm(store, meter, appIcon);
        else form.RefreshView(true);
        form.Show(); form.WindowState = FormWindowState.Normal; form.Activate();
    }

    void About()
    {
        if (MessageBox.Show("Güç Sayacı " + Info.Version + "\n\nBilgisayarınızın elektrik tüketimini ve maliyetini " +
            "arka planda sessizce ölçer.\n\nLisans: GPL-3.0\n" + Info.Url + "\n\nProje sayfası açılsın mı?",
            "Hakkında", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            Process.Start(Info.Url);
    }

    void AskNumber(string key, string prompt, double current, double min, double max)
    {
        string input = Microsoft.VisualBasic.Interaction.InputBox(prompt, "Güç Sayacı", current.ToString("0.###", Tr));
        if (string.IsNullOrWhiteSpace(input)) return;
        double v;
        if (double.TryParse(input.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= min && v <= max)
        {
            store.Cfg[key] = v; store.Save(); UpdateTooltip();
        }
        else MessageBox.Show("Geçersiz değer. Aralık: " + min + " - " + max, "Güç Sayacı");
    }

    static bool IsAutoStart()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue("GucSayaci") != null;
    }

    void ToggleAutoStart()
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (IsAutoStart()) k.DeleteValue("GucSayaci", false);
            else k.SetValue("GucSayaci", "\"" + Application.ExecutablePath + "\"");
        }
        autoStartItem.Checked = IsAutoStart();
    }

    static Icon LoadIcon()
    {
        try
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.ico"))
                if (s != null) return new Icon(s, SystemInformation.SmallIconSize);
        }
        catch { }
        return SystemIcons.Application;
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerMode;
        store.Save();
        meter.Close();
        tray.Visible = false; tray.Dispose();
        base.ExitThreadCore();
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool showSummary = Array.IndexOf(args, "--ozet") >= 0;
        bool created;
        using (new Mutex(true, "GucSayaci_TekOrnek", out created))
        {
            if (!created) return;
            try { Native.SetProcessDPIAware(); } catch { } // yüksek DPI'da bulanık görünmesin
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new App(showSummary));
        }
    }
}
