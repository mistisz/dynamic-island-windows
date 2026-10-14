// The island window: spring-driven morphing pill, glass rendering, activities, tray and settings.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace DynamicIsland
{
    class IslandWindow : Window
    {
        const double WinW = 480, WinH = 250; // fixed canvas; the visible shape is the window region
        enum Mode { Compact, Expanded, Toast }

        static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
        static readonly Brush GlyphBg = Frozen(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));

        readonly Settings S;
        readonly FrameworkElement root;
        readonly Grid island, compact, expClock, expMedia, toast;
        readonly Border glass, specular, rim, cArt, mArt, mProgress, tIcon;
        readonly Rectangle sheen;
        readonly TranslateTransform sheenX;
        readonly TextBlock cTime, cDate, bTime, bDate, mTime, mDate, mTitle, mArtist, mArtGlyph, tApp, tTitle, tBody, tGlyph;
        readonly StackPanel eq;
        readonly Button btnPlay;
        readonly BatteryView cBatt, bBatt, mBatt;
        readonly Spring sw = new Spring(196), sh = new Spring(34), sr = new Spring(17);
        readonly MediaService media;
        readonly NotificationService notes = new NotificationService();
        readonly Queue<ToastInfo> toasts = new Queue<ToastInfo>();
        readonly DispatcherTimer tick, hoverTimer, leaveTimer, toastTimer;

        Mode mode = Mode.Compact;
        FrameworkElement current;
        IntPtr hwnd;
        double dpi = 1;
        bool animating, hovered, hiddenForFullscreen, eqRunning, hasBattery;
        TimeSpan lastFrame;
        int ticks, idleTicks, lastPct = -1;
        bool? lastPlugged;
        string lastMinute, announcedTitle;
        WF.NotifyIcon tray;
        SettingsWindow settingsWin;
        Backdrop backdrop;
        bool backdropTried;

        public IslandWindow(Settings settings)
        {
            S = settings;
            Title = "Dynamic Island";
            WindowStyle = WindowStyle.None;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Width = WinW; Height = WinH;

            root = (FrameworkElement)XamlReader.Parse(Program.Resource("Island.xaml"));
            Content = root;
            island = F<Grid>("Island"); compact = F<Grid>("Compact"); expClock = F<Grid>("ExpClock");
            expMedia = F<Grid>("ExpMedia"); toast = F<Grid>("Toast");
            glass = F<Border>("Glass"); specular = F<Border>("Specular"); rim = F<Border>("Rim");
            cArt = F<Border>("CArt"); mArt = F<Border>("MArt"); mProgress = F<Border>("MProgress"); tIcon = F<Border>("TIcon");
            sheen = F<Rectangle>("Sheen"); sheenX = F<TranslateTransform>("SheenX");
            cTime = F<TextBlock>("CTime"); cDate = F<TextBlock>("CDate"); bTime = F<TextBlock>("BTime"); bDate = F<TextBlock>("BDate");
            mTime = F<TextBlock>("MTime"); mDate = F<TextBlock>("MDate"); mTitle = F<TextBlock>("MTitle"); mArtist = F<TextBlock>("MArtist");
            mArtGlyph = F<TextBlock>("MArtGlyph"); tApp = F<TextBlock>("TApp"); tTitle = F<TextBlock>("TTitle");
            tBody = F<TextBlock>("TBody"); tGlyph = F<TextBlock>("TGlyph");
            eq = F<StackPanel>("Eq"); btnPlay = F<Button>("BtnPlay");
            cBatt = new BatteryView(1); F<Border>("CBattHost").Child = cBatt;
            bBatt = new BatteryView(1.2); F<Border>("BBattHost").Child = bBatt;
            mBatt = new BatteryView(1); F<Border>("MBattHost").Child = mBatt;

            var eqBrush = Frozen(new LinearGradientBrush(Color.FromRgb(0xF2, 0xF7, 0xFF), Color.FromRgb(0x8E, 0xC9, 0xFF), 90));
            foreach (Rectangle r in eq.Children) { r.Fill = eqBrush; r.RenderTransform = new ScaleTransform(1, 0.3); }
            foreach (var p in Panels())
            {
                p.Opacity = 0;
                p.IsHitTestVisible = false;
                p.RenderTransformOrigin = new Point(0.5, 0);
                p.RenderTransform = new ScaleTransform();
            }

            media = new MediaService(Dispatcher);
            media.Changed += OnMedia;

            tick = Timer(1000, OnTick);
            hoverTimer = Timer(160, () => { hoverTimer.Stop(); if (hovered && mode == Mode.Compact) Morph(Mode.Expanded); });
            leaveTimer = Timer(450, () =>
            {
                leaveTimer.Stop();
                if (hovered) return;
                if (mode == Mode.Expanded) Morph(Mode.Compact);
                else if (mode == Mode.Toast) toastTimer.Start();
            });
            toastTimer = Timer(4500, NextToast);

            island.MouseEnter += (s, e) =>
            {
                hovered = true;
                leaveTimer.Stop();
                if (mode == Mode.Toast) toastTimer.Stop();
                else if (S.HoverExpand && mode == Mode.Compact) hoverTimer.Start();
            };
            island.MouseLeave += (s, e) => { hovered = false; hoverTimer.Stop(); leaveTimer.Start(); };
            island.MouseLeftButtonUp += (s, e) =>
            {
                if (mode == Mode.Toast) NextToast();
                else Morph(mode == Mode.Compact ? Mode.Expanded : Mode.Compact);
            };
            island.MouseRightButtonUp += (s, e) => OpenSettings();
            F<Button>("BtnPrev").Click += (s, e) => media.Previous();
            F<Button>("BtnNext").Click += (s, e) => media.Next();
            btnPlay.Click += (s, e) => media.PlayPause();

            SourceInitialized += OnSourceInitialized;
            SystemEvents.DisplaySettingsChanged += (s, e) => Dispatcher.BeginInvoke(new Action(ApplySettings));
            CreateTray();
        }

        T F<T>(string name) where T : class { return (T)root.FindName(name); }

        IEnumerable<FrameworkElement> Panels() { return new FrameworkElement[] { compact, expClock, expMedia, toast }; }

        DispatcherTimer Timer(int ms, Action a)
        {
            var t = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => a();
            return t;
        }

        static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
        static TimeSpan Ms(double ms) { return TimeSpan.FromMilliseconds(ms); }

        async void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
            Native.MakeOverlay(hwnd);
            dpi = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice.M11;
            UpdateClock();
            UpdateBattery();
            UpdateMediaUi();
            ApplySettings();
            Morph(Mode.Compact);
            tick.Start();
            media.Start();
            await notes.Start();
        }

        public bool NotificationsAllowed { get { return notes.Allowed; } }

        public void ApplySettings()
        {
            Left = (SystemParameters.PrimaryScreenWidth - WinW) / 2;
            Top = 0;
            island.Margin = new Thickness(0, S.Offset, 0, 0);
            if (S.Blur && !backdropTried && hwnd != IntPtr.Zero) { backdropTried = true; backdrop = Backdrop.TryCreate(this); }
            bool blur = S.Blur && backdrop != null;
            if (backdrop != null) { backdrop.Place(Left, Top); backdrop.Visible = blur && !hiddenForFullscreen; }
            // Without blur the glass would be see-through and unreadable, so go (nearly) opaque like the iPhone.
            double a = blur ? S.Tint : Math.Max(S.Tint, 0.94);
            glass.Background = new LinearGradientBrush(
                Color.FromArgb((byte)(a * 0.9 * 255), 0x10, 0x10, 0x16),
                Color.FromArgb((byte)(Math.Min(1, a * 1.12) * 255), 0, 0, 0), 90);
            if (!S.HideFullscreen && hiddenForFullscreen) SetHidden(false);
            ApplyGeometry();
        }

        // ---------- morphing ----------

        Size TargetSize(Mode m, out FrameworkElement panel, out double radius)
        {
            double w;
            if (m == Mode.Compact) { panel = compact; w = 196; radius = 17; }
            else if (m == Mode.Expanded)
            {
                panel = media.HasSession ? expMedia : expClock;
                w = media.HasSession ? 400 : 340;
                radius = media.HasSession ? 32 : 36;
            }
            else { panel = toast; w = 380; radius = 30; }
            panel.Width = w;
            if (panel == compact) return new Size(w, 34);
            panel.Height = double.NaN;
            panel.Measure(new Size(w, double.PositiveInfinity));
            panel.Height = Math.Ceiling(panel.DesiredSize.Height);
            return new Size(w, panel.Height);
        }

        void Morph(Mode m)
        {
            bool changed = m != mode;
            mode = m;
            FrameworkElement panel;
            double r;
            var size = TargetSize(m, out panel, out r);
            if (panel != current)
            {
                if (current != null) FadeOut(current);
                FadeIn(panel);
                current = panel;
            }
            sw.Target = size.Width; sh.Target = size.Height; sr.Target = r;
            StartAnimation();
            if (changed && m != Mode.Compact) Sweep();
            if (m == Mode.Expanded) UpdateProgress();
            UpdateEq();
            idleTicks = 0;
        }

        void StartAnimation()
        {
            if (animating) return;
            animating = true;
            lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }

        // Runs only while the shape is moving; idle island costs zero frames.
        void OnFrame(object sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == lastFrame) return;
            double dt = lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - lastFrame).TotalSeconds, 1 / 30.0);
            lastFrame = now;
            int n = (int)Math.Ceiling(dt * 240);
            for (int i = 0; i < n; i++) { sw.Step(dt / n); sh.Step(dt / n); sr.Step(dt / n); }
            if (sw.Settled && sh.Settled && sr.Settled)
            {
                sw.Snap(); sh.Snap(); sr.Snap();
                CompositionTarget.Rendering -= OnFrame;
                animating = false;
            }
            ApplyGeometry();
        }

        void ApplyGeometry()
        {
            double w = Math.Max(20, sw.Value), h = Math.Max(20, sh.Value), r = Math.Min(sr.Value, h / 2);
            island.Width = w; island.Height = h;
            glass.CornerRadius = specular.CornerRadius = rim.CornerRadius = new CornerRadius(r);
            sheen.Height = h;
            foreach (var p in Panels())
                if (!double.IsNaN(p.Width)) Canvas.SetLeft(p, Math.Round((w - p.Width) / 2));
            if (hwnd == IntPtr.Zero) return;
            int x0 = (int)Math.Round((WinW - w) / 2 * dpi), y0 = (int)Math.Round(S.Offset * dpi);
            int x1 = (int)Math.Round((WinW + w) / 2 * dpi), y1 = (int)Math.Round((S.Offset + h) * dpi);
            int d = (int)Math.Round(2 * r * dpi);
            Native.SetWindowRgn(hwnd, Native.CreateRoundRectRgn(x0, y0, x1 + 1, y1 + 1, d, d), true);
            if (backdrop != null) backdrop.Update(x0, y0, x1, y1, d);
        }

        void FadeIn(FrameworkElement p)
        {
            p.IsHitTestVisible = true;
            p.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(240)) { BeginTime = Ms(60), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            var sc = new DoubleAnimation(0.9, 1, Ms(420)) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } };
            var st = (ScaleTransform)p.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
            Blur(p, 12, 0, 300);
        }

        void FadeOut(FrameworkElement p)
        {
            p.IsHitTestVisible = false;
            p.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(130)));
            var sc = new DoubleAnimation(0.94, Ms(160));
            var st = (ScaleTransform)p.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, sc);
            Blur(p, 0, 8, 130);
        }

        // Blur is a transient effect: removed once done so text renders crisp and costs nothing at rest.
        static void Blur(FrameworkElement p, double from, double to, double ms)
        {
            var fx = new BlurEffect { Radius = from };
            p.Effect = fx;
            var a = new DoubleAnimation(from, to, Ms(ms));
            a.Completed += (s, e) => { if (p.Effect == fx) p.Effect = null; };
            fx.BeginAnimation(BlurEffect.RadiusProperty, a);
        }

        void Sweep()
        {
            sheen.Opacity = 1;
            var a = new DoubleAnimation(-160, sw.Target + 40, Ms(1100)) { BeginTime = Ms(100), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            a.Completed += (s, e) => sheen.Opacity = 0;
            sheenX.BeginAnimation(TranslateTransform.XProperty, a);
        }

        // ---------- activities ----------

        void Enqueue(ToastInfo t)
        {
            if (hiddenForFullscreen) return;
            if (toasts.Count >= 5) toasts.Dequeue();
            toasts.Enqueue(t);
            if (mode != Mode.Toast) NextToast();
        }

        void NextToast()
        {
            toastTimer.Stop();
            if (toasts.Count == 0) { Morph(hovered && S.HoverExpand ? Mode.Expanded : Mode.Compact); return; }
            var t = toasts.Dequeue();
            tApp.Text = t.App ?? "";
            tApp.Visibility = string.IsNullOrEmpty(t.App) ? Visibility.Collapsed : Visibility.Visible;
            tTitle.Text = t.Title ?? "";
            tBody.Text = t.Body ?? "";
            tBody.Visibility = string.IsNullOrEmpty(t.Body) ? Visibility.Collapsed : Visibility.Visible;
            if (t.Image != null)
            {
                tIcon.Background = new ImageBrush(t.Image) { Stretch = Stretch.UniformToFill };
                tGlyph.Text = "";
            }
            else
            {
                tIcon.Background = GlyphBg;
                tGlyph.Text = t.Glyph ?? "";
                tGlyph.Foreground = new SolidColorBrush(t.GlyphColor);
            }
            bool replacing = mode == Mode.Toast;
            Morph(Mode.Toast);
            if (replacing) FadeIn(toast);
            toastTimer.Interval = TimeSpan.FromSeconds(t.Seconds);
            if (!hovered) toastTimer.Start();
        }

        void OnMedia()
        {
            UpdateMediaUi();
            // Apps often publish the title before they start playing, so announce on whichever comes last.
            bool announce = media.Playing && media.Title != "" && media.Title != announcedTitle;
            if (announce) announcedTitle = media.Title;
            if (announce && S.MediaToasts && mode != Mode.Expanded)
                Enqueue(new ToastInfo { App = "Now playing", Title = media.Title, Body = media.Artist, Image = media.Art, Glyph = "", Seconds = 3 });
            else if (mode != Mode.Toast)
                Morph(mode); // session appeared/vanished: reshape
        }

        void UpdateMediaUi()
        {
            var art = media.Art != null ? new ImageBrush(media.Art) { Stretch = Stretch.UniformToFill } : null;
            cArt.Background = art;
            cArt.Visibility = media.HasSession && art != null ? Visibility.Visible : Visibility.Collapsed;
            mArt.Background = art ?? GlyphBg;
            mArtGlyph.Visibility = art == null ? Visibility.Visible : Visibility.Collapsed;
            mTitle.Text = string.IsNullOrEmpty(media.Title) ? "Not playing" : media.Title;
            mArtist.Text = media.Artist;
            btnPlay.Content = media.Playing ? "" : "";
            UpdateEq();
            UpdateProgress();
        }

        void UpdateEq()
        {
            eq.Visibility = media.HasSession ? Visibility.Visible : Visibility.Collapsed;
            cBatt.Visibility = !media.HasSession && hasBattery ? Visibility.Visible : Visibility.Collapsed;
            cDate.Visibility = !media.HasSession && !hasBattery ? Visibility.Visible : Visibility.Collapsed;
            bool run = media.HasSession && media.Playing && mode == Mode.Compact && !hiddenForFullscreen;
            if (run == eqRunning) return;
            eqRunning = run;
            var periods = new[] { 420, 330, 510, 380 };
            int i = 0;
            foreach (Rectangle r in eq.Children)
            {
                var st = (ScaleTransform)r.RenderTransform;
                if (run)
                {
                    var a = new DoubleAnimation(0.25, 1, Ms(periods[i++])) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                    Timeline.SetDesiredFrameRate(a, 30); // decorative: half the frames
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
                }
                else
                {
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    st.ScaleY = 0.3;
                }
            }
        }

        void UpdateProgress()
        {
            if (mode != Mode.Expanded || !media.HasSession) return;
            var track = (FrameworkElement)mProgress.Parent;
            if (track.ActualWidth <= 0) return;
            double target = track.ActualWidth * media.Progress;
            mProgress.BeginAnimation(WidthProperty, new DoubleAnimation(target, Ms(media.Playing ? 1000 : 200)));
        }

        void UpdateClock()
        {
            var now = DateTime.Now;
            var t = now.ToString("t");
            if (t == lastMinute) return;
            lastMinute = t;
            var md = now.ToString(CultureInfo.CurrentCulture.DateTimeFormat.MonthDayPattern);
            var day = now.ToString("dddd");
            day = char.ToUpper(day[0]) + day.Substring(1);
            cTime.Text = bTime.Text = mTime.Text = t;
            bDate.Text = day + ", " + md;
            mDate.Text = md;
            cDate.Text = now.ToString("ddd d");
        }

        void UpdateBattery()
        {
            var ps = WF.SystemInformation.PowerStatus;
            hasBattery = (ps.BatteryChargeStatus & WF.BatteryChargeStatus.NoSystemBattery) == 0;
            int pct = (int)Math.Round(Math.Min(1, ps.BatteryLifePercent) * 100);
            bool plugged = ps.PowerLineStatus == WF.PowerLineStatus.Online;
            foreach (var b in new[] { cBatt, bBatt, mBatt })
            {
                b.Update(pct, plugged);
                if (b != cBatt) b.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
            }
            if (hasBattery && lastPlugged.HasValue)
            {
                if (plugged != lastPlugged.Value)
                    Enqueue(new ToastInfo { Title = plugged ? "Charging" : "On battery", Body = pct + "%", Glyph = plugged ? "" : "", GlyphColor = plugged ? Green : Colors.White, Seconds = 2.5 });
                else if (!plugged && ((lastPct > 20 && pct <= 20) || (lastPct > 10 && pct <= 10)))
                    Enqueue(new ToastInfo { Title = "Low battery", Body = pct + "% remaining", Glyph = "", GlyphColor = Color.FromRgb(0xFF, 0x45, 0x3A), Seconds = 5 });
            }
            lastPlugged = plugged;
            lastPct = pct;
        }

        // ---------- housekeeping ----------

        void OnTick()
        {
            ticks++;
            if (S.HideFullscreen && ticks % 2 == 0)
            {
                bool fs = Native.FullscreenActive(hwnd);
                if (fs != hiddenForFullscreen) SetHidden(fs);
            }
            if (hiddenForFullscreen) return; // game mode: nothing but the 2s fullscreen probe

            UpdateClock();
            UpdateBattery();
            UpdateProgress();
            if (ticks % 2 == 0)
            {
                if (backdrop != null) Native.KeepOnTop(backdrop.Hwnd);
                Native.KeepOnTop(hwnd);
                PollNotifications();
            }
            if (mode == Mode.Compact && !animating && !hovered)
            {
                if (++idleTicks == 20) Native.TrimMemory();
            }
            else idleTicks = 0;
        }

        void SetHidden(bool hide)
        {
            hiddenForFullscreen = hide;
            if (hide)
            {
                toasts.Clear();
                toastTimer.Stop();
                if (mode != Mode.Compact) Morph(Mode.Compact);
                Visibility = Visibility.Hidden;
                UpdateEq();
                Native.TrimMemory();
            }
            else
            {
                Visibility = Visibility.Visible;
                Native.KeepOnTop(hwnd);
                UpdateEq();
            }
            if (backdrop != null) backdrop.Visible = S.Blur && !hide;
        }

        async void PollNotifications()
        {
            if (!S.Notifications) return;
            foreach (var t in await notes.Poll()) Enqueue(t);
        }

        void CreateTray()
        {
            tray = new WF.NotifyIcon { Text = "Dynamic Island", Visible = true };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); }
            catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Settings", null, (s, e) => OpenSettings());
            menu.Items.Add("Quit", null, (s, e) => Quit());
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) OpenSettings(); };
        }

        public void OpenSettings()
        {
            if (settingsWin == null)
            {
                settingsWin = new SettingsWindow(this, S);
                settingsWin.Closed += (s, e) => settingsWin = null;
                settingsWin.Show();
            }
            settingsWin.Activate();
        }

        public void Quit()
        {
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }
    }

    class BatteryView : StackPanel
    {
        static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));
        static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
        static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
        readonly TextBlock text, bolt;
        readonly Border fill;
        readonly double inner;

        public BatteryView(double s)
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            bolt = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10 * s, Foreground = Green, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3 * s, 0) };
            text = new TextBlock { FontSize = 12 * s, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6 * s, 0) };
            inner = 20 * s - 2;
            fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2 * s) };
            var frame = new Border { Width = 23 * s, Height = 12 * s, CornerRadius = new CornerRadius(3.5 * s), BorderBrush = Dim, BorderThickness = new Thickness(1), Padding = new Thickness(1.5 * s), Child = fill, VerticalAlignment = VerticalAlignment.Center };
            var nub = new Border { Width = 1.8 * s, Height = 4.5 * s, CornerRadius = new CornerRadius(0, s, s, 0), Background = Dim, Margin = new Thickness(1, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Children.Add(bolt); Children.Add(text); Children.Add(frame); Children.Add(nub);
        }

        public void Update(int pct, bool plugged)
        {
            text.Text = pct + "%";
            bolt.Visibility = plugged ? Visibility.Visible : Visibility.Collapsed;
            fill.Width = Math.Max(1.5, inner * pct / 100.0);
            fill.Background = plugged ? Green : pct <= 20 ? Red : Brushes.White;
        }
    }

    class SettingsWindow : Window
    {
        readonly FrameworkElement root;
        readonly IslandWindow island;
        readonly Settings S;

        public SettingsWindow(IslandWindow island, Settings s)
        {
            this.island = island;
            S = s;
            Title = "Dynamic Island Settings";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(Assembly.GetExecutingAssembly().Location)); } catch { }

            root = (FrameworkElement)XamlReader.Parse(Program.Resource("Settings.xaml"));
            Content = root;

            bool startup = false;
            try { startup = Settings.Startup; } catch (Exception ex) { Program.Log(ex); }
            Toggle("Startup", startup, v => { try { Settings.Startup = v; } catch (Exception ex) { Program.Log(ex); } });
            Toggle("HoverExpand", s.HoverExpand, v => s.HoverExpand = v);
            Toggle("HideFullscreen", s.HideFullscreen, v => s.HideFullscreen = v);
            Toggle("Blur", s.Blur, v => s.Blur = v);
            Toggle("Notifications", s.Notifications, v => s.Notifications = v);
            Toggle("MediaToasts", s.MediaToasts, v => s.MediaToasts = v);
            Slide("Tint", "TintLabel", "Glass tint", s.Tint * 100, "%", v => s.Tint = v / 100);
            Slide("Offset", "OffsetLabel", "Distance from top", s.Offset, " px", v => s.Offset = v);
            ((FrameworkElement)root.FindName("NotifHint")).Visibility = island.NotificationsAllowed ? Visibility.Collapsed : Visibility.Visible;
            ((FrameworkElement)root.FindName("Version")).SetValue(TextBlock.TextProperty, "Version " + Assembly.GetExecutingAssembly().GetName().Version.ToString(2));

            ((Button)root.FindName("CloseBtn")).Click += (a, b) => Close();
            ((Button)root.FindName("QuitBtn")).Click += (a, b) => { Close(); island.Quit(); };
            root.MouseLeftButtonDown += (a, b) => { if (b.ButtonState == MouseButtonState.Pressed) DragMove(); };
            KeyDown += (a, b) => { if (b.Key == Key.Escape) Close(); };
        }

        void Changed() { S.Save(); island.ApplySettings(); }

        void Toggle(string name, bool value, Action<bool> set)
        {
            var cb = (CheckBox)root.FindName(name);
            cb.IsChecked = value;
            cb.Click += (a, b) => { set(cb.IsChecked == true); Changed(); };
        }

        void Slide(string name, string labelName, string label, double value, string unit, Action<double> set)
        {
            var sl = (Slider)root.FindName(name);
            var lb = (TextBlock)root.FindName(labelName);
            sl.Value = Math.Round(value);
            lb.Text = label + "  ·  " + sl.Value + unit;
            sl.ValueChanged += (a, b) =>
            {
                double v = Math.Round(sl.Value);
                lb.Text = label + "  ·  " + v + unit;
                set(v);
                Changed();
            };
        }
    }
}
