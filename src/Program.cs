// Dynamic Island for Windows — entry point, settings, native interop, self-test.
// Language level is C# 5 so it builds with the csc.exe that ships inside Windows (no SDK needed).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

[assembly: AssemblyTitle("Dynamic Island")]
[assembly: AssemblyProduct("Dynamic Island")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace DynamicIsland
{
    static class Program
    {
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland");

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run();

            bool created;
            var mutex = new Mutex(true, "DynamicIsland.SingleInstance.v1", out created);
            // Launching the exe again opens Settings in the running instance.
            var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "DynamicIsland.ShowSettings.v1");
            if (!created) { showSettings.Set(); return 0; }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // An overlay should never take the desktop down with it: log and keep going.
            app.DispatcherUnhandledException += (s, e) => { Log(e.Exception); e.Handled = true; };
            try
            {
                var island = new IslandWindow(Settings.Load());
                island.Show();
                ThreadPool.RegisterWaitForSingleObject(showSettings,
                    (st, timedOut) => island.Dispatcher.BeginInvoke(new Action(island.OpenSettings)), null, -1, false);
            }
            catch (Exception ex)
            {
                Program.Log(ex);
                MessageBox.Show("Dynamic Island failed to start:\n" + ex.Message, "Dynamic Island", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }

        public static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var path = Path.Combine(DataDir, "error.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + ex + Environment.NewLine);
            }
            catch { }
        }

        public static string Resource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var r = new StreamReader(s)) return r.ReadToEnd();
        }
    }

    class Settings
    {
        public bool HoverExpand = true;
        public bool Blur = true;
        public bool Notifications = true;
        public bool MediaToasts = true;
        public bool HideFullscreen = true;
        public double Tint = 0.55;   // 0..1 darkness of the glass
        public double Offset = 8;    // DIPs from the top edge of the screen

        static string FilePath { get { return Path.Combine(Program.DataDir, "settings.ini"); } }

        public static Settings Load()
        {
            try { return File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) : new Settings(); }
            catch (Exception ex) { Program.Log(ex); return new Settings(); }
        }

        public void Save()
        {
            try { Directory.CreateDirectory(Program.DataDir); File.WriteAllText(FilePath, Serialize()); }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static Settings Parse(string text)
        {
            var s = new Settings();
            foreach (var line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var f = typeof(Settings).GetField(line.Substring(0, eq).Trim());
                if (f == null) continue;
                try { f.SetValue(s, Convert.ChangeType(line.Substring(eq + 1).Trim(), f.FieldType, CultureInfo.InvariantCulture)); }
                catch (FormatException) { } // keep default for a hand-edited bad value
            }
            s.Tint = Math.Max(0, Math.Min(1, s.Tint));
            s.Offset = Math.Max(0, Math.Min(40, s.Offset));
            return s;
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            foreach (var f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                sb.Append(f.Name).Append('=').Append(Convert.ToString(f.GetValue(this), CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "DynamicIsland";

        public static bool Startup
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunName) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(RunName, "\"" + Assembly.GetExecutingAssembly().Location + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
        }
    }

    // Critically-ish damped spring: interruptible, keeps velocity when retargeted mid-flight.
    class Spring
    {
        public double Value, Velocity, Target;
        const double K = 320, C = 28; // zeta ~0.78 -> a hint of overshoot, like iOS

        public Spring(double v) { Value = Target = v; }
        public void Step(double dt)
        {
            Velocity += (-K * (Value - Target) - C * Velocity) * dt;
            Value += Velocity * dt;
        }
        public bool Settled { get { return Math.Abs(Value - Target) < 0.2 && Math.Abs(Velocity) < 0.5; } }
        public void Snap() { Value = Target; Velocity = 0; }
    }

    static class SelfTest
    {
        public static int Run()
        {
            try
            {
                var sp = new Spring(0) { Target = 100 };
                double peak = 0; int steps = 0;
                while (!sp.Settled && steps < 240 * 3) { sp.Step(1 / 240.0); peak = Math.Max(peak, sp.Value); steps++; }
                Check(sp.Settled, "spring settles within 3s");
                Check(peak < 110, "spring overshoot under 10%");

                var a = new Settings { HoverExpand = false, Tint = 0.3, Offset = 12 };
                var b = Settings.Parse(a.Serialize());
                Check(!b.HoverExpand && b.Tint == 0.3 && b.Offset == 12, "settings round-trip");
                Check(Settings.Parse("Tint=7\nOffset=abc\nBogus=1").Tint == 1, "settings clamp and ignore junk");

                // XAML is parsed at runtime, so a typo would only show up as a crash on launch.
                System.Windows.Markup.XamlReader.Parse(Program.Resource("Island.xaml"));
                System.Windows.Markup.XamlReader.Parse(Program.Resource("Settings.xaml"));
                return 0;
            }
            catch (Exception ex) { Program.Log(ex); return 1; }
        }

        static void Check(bool ok, string what) { if (!ok) throw new Exception("self-test failed: " + what); }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

        [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hwnd, IntPtr hrgn, bool redraw);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS { public int L, R, T, B; }
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Non-layered window + glass frame over the whole client: transparent WPF pixels see through
        // (to the composition backdrop, then the desktop) while the window region limits hit-testing to the pill.
        public static void MakeOverlay(IntPtr hwnd)
        {
            const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            var m = new MARGINS { L = -1, R = -1, T = -1, B = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            int noRound = 1; // DWMWCP_DONOTROUND; Windows 10 just ignores it
            DwmSetWindowAttribute(hwnd, 33, ref noRound, 4);
        }

        public static void KeepOnTop(IntPtr hwnd)
        {
            // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
        }

        // True while a fullscreen game/video/presentation owns the island's monitor.
        public static bool FullscreenActive(IntPtr self)
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == self) return false;
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            var c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;
            var mon = MonitorFromWindow(fg, 2);
            if (mon != MonitorFromWindow(self, 2)) return false; // fullscreen on another monitor: island stays
            int state;
            if (SHQueryUserNotificationState(out state) == 0 && state >= 2 && state <= 4) return true; // busy fullscreen, D3D, presentation
            const int WS_CAPTION = 0x00C00000;
            if ((GetWindowLong(fg, -16) & WS_CAPTION) == WS_CAPTION) return false; // a normal maximized window, not borderless fullscreen
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            RECT r;
            if (!GetMonitorInfo(mon, ref mi) || !GetWindowRect(fg, out r)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }

        public static void TrimMemory() { SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); }
    }
}
