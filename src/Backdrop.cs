// Live background blur clipped to the pill, drawn by DWM through Windows.UI.Composition.
// The accent-policy blur ignores window regions on Windows 11 (fills the whole window rect), and a
// composition target on the WPF window itself hides WPF's content, so the blur lives in its own
// window directly underneath the island (the island is owned by it, which keeps it on top).
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.UI.Composition;

namespace DynamicIsland
{
    class Backdrop
    {
        [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ICompositorDesktopInterop
        {
            void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DispatcherQueueOptions { public int Size, ThreadType, ApartmentType; }

        [DllImport("CoreMessaging.dll")] static extern int CreateDispatcherQueueController(DispatcherQueueOptions o, out IntPtr controller);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        static IntPtr queue; // the compositor needs a DispatcherQueue on this thread for the app's lifetime
        Window win;
        public IntPtr Hwnd;
        Windows.UI.Composition.CompositionTarget target;
        SpriteVisual visual;
        CompositionRoundedRectangleGeometry shape;

        // Null on Windows 10 (no host backdrop for Win32 windows there) or on any failure: caller falls back to opaque glass.
        public static Backdrop TryCreate(Window island)
        {
            var b = new Backdrop();
            try
            {
                b.win = new Window
                {
                    Title = "Dynamic Island Glass", WindowStyle = WindowStyle.None, Background = Brushes.Transparent,
                    Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                    Width = island.Width, Height = island.Height, Left = island.Left, Top = island.Top,
                };
                b.Hwnd = new WindowInteropHelper(b.win).EnsureHandle();
                HwndSource.FromHwnd(b.Hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                Native.MakeOverlay(b.Hwnd);
                int on = 1;
                if (DwmSetWindowAttribute(b.Hwnd, 17 /* DWMWA_USE_HOSTBACKDROPBRUSH, Win11+ */, ref on, 4) != 0) { b.win.Close(); return null; }
                if (queue == IntPtr.Zero)
                    CreateDispatcherQueueController(new DispatcherQueueOptions { Size = 12, ThreadType = 2 /* current */, ApartmentType = 2 /* STA */ }, out queue);
                var comp = new Compositor();
                IntPtr raw;
                ((ICompositorDesktopInterop)(object)comp).CreateDesktopWindowTarget(b.Hwnd, false, out raw);
                b.target = (Windows.UI.Composition.CompositionTarget)Marshal.GetObjectForIUnknown(raw);
                Marshal.Release(raw);
                b.shape = comp.CreateRoundedRectangleGeometry();
                b.visual = comp.CreateSpriteVisual();
                b.visual.Brush = comp.CreateHostBackdropBrush();
                b.visual.Clip = comp.CreateGeometricClip(b.shape);
                b.target.Root = b.visual;
                b.win.Show();
                island.Owner = b.win;
                return b;
            }
            catch (Exception ex)
            {
                Program.Log(ex);
                if (b.win != null) b.win.Close();
                return null;
            }
        }

        public void Place(double left, double top) { win.Left = left; win.Top = top; }

        // All values in physical pixels relative to the window (same frame as the island's).
        public void Update(int x0, int y0, int x1, int y1, int d)
        {
            visual.Offset = new Vector3(x0, y0, 0);
            visual.Size = new Vector2(x1 - x0, y1 - y0);
            shape.Size = visual.Size;
            shape.CornerRadius = new Vector2(d / 2f, d / 2f);
            Native.SetWindowRgn(Hwnd, Native.CreateRoundRectRgn(x0, y0, x1 + 1, y1 + 1, d, d), true); // clicks outside pass through
        }

        public bool Visible { set { win.Visibility = value ? Visibility.Visible : Visibility.Hidden; } }
    }
}
