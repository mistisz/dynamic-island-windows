// Media session + Windows notification mirroring via WinRT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland
{
    static class WinRT
    {
        // The in-box compiler can't see the WinRT GetAwaiter extensions, so bridge IAsyncOperation by hand.
        public static Task<R> Await<R>(IAsyncOperation<R> op)
        {
            var t = new TaskCompletionSource<R>();
            op.Completed = (o, s) =>
            {
                if (s == AsyncStatus.Completed) t.TrySetResult(o.GetResults());
                else t.TrySetException(o.ErrorCode ?? new OperationCanceledException());
            };
            return t.Task;
        }

        public static async Task<ImageSource> LoadImage(IRandomAccessStreamReference reference, int decodeWidth)
        {
            using (var s = await Await(reference.OpenReadAsync()))
            {
                uint n = (uint)s.Size;
                if (n == 0 || n > 8 * 1024 * 1024) return null;
                var reader = new DataReader(s);
                await Await(reader.LoadAsync(n));
                var bytes = new byte[n];
                reader.ReadBytes(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = decodeWidth; // keep art small in memory
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
        }
    }

    class ToastInfo
    {
        public string App, Title, Body, Glyph;
        public ImageSource Image;
        public Color GlyphColor = Colors.White;
        public double Seconds = 4.5;
    }

    class MediaService
    {
        public event Action Changed;
        public bool HasSession, Playing;
        public string Title = "", Artist = "";
        public ImageSource Art;

        double position, duration;
        DateTimeOffset stamp;
        GlobalSystemMediaTransportControlsSessionManager mgr;
        GlobalSystemMediaTransportControlsSession session;
        readonly Dispatcher ui;

        public MediaService(Dispatcher ui) { this.ui = ui; }

        public async void Start()
        {
            try
            {
                mgr = await WinRT.Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                mgr.CurrentSessionChanged += (s, e) => ui.BeginInvoke(new Action(Attach));
                Attach();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        void Attach()
        {
            if (session != null)
            {
                session.MediaPropertiesChanged -= OnProps;
                session.PlaybackInfoChanged -= OnPlayback;
                session.TimelinePropertiesChanged -= OnTimeline;
            }
            session = mgr.GetCurrentSession();
            HasSession = session != null;
            if (session != null)
            {
                session.MediaPropertiesChanged += OnProps;
                session.PlaybackInfoChanged += OnPlayback;
                session.TimelinePropertiesChanged += OnTimeline;
                ReadPlayback();
                ReadTimeline();
                RefreshProps();
            }
            else
            {
                Playing = false; Title = Artist = ""; Art = null;
                Raise();
            }
        }

        void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) { ui.BeginInvoke(new Action(RefreshProps)); }
        void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) { ui.BeginInvoke(new Action(() => { ReadPlayback(); Raise(); })); }
        void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) { ui.BeginInvoke(new Action(ReadTimeline)); }

        void ReadPlayback()
        {
            try { Playing = session != null && session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch (Exception ex) { Program.Log(ex); }
        }

        void ReadTimeline()
        {
            try
            {
                if (session == null) return;
                var t = session.GetTimelineProperties();
                position = t.Position.TotalSeconds;
                duration = (t.EndTime - t.StartTime).TotalSeconds;
                stamp = t.LastUpdatedTime;
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        async void RefreshProps()
        {
            var s = session;
            if (s == null) return;
            try
            {
                var p = await WinRT.Await(s.TryGetMediaPropertiesAsync());
                if (s != session || p == null) return;
                ImageSource art = null;
                if (p.Thumbnail != null)
                {
                    try { art = await WinRT.LoadImage(p.Thumbnail, 160); }
                    catch (Exception ex) { Program.Log(ex); }
                }
                if (s != session) return;
                Title = p.Title ?? ""; Artist = p.Artist ?? ""; Art = art;
                Raise();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        void Raise() { if (Changed != null) Changed(); }

        // 0..1, extrapolated between the sparse timeline updates apps send.
        public double Progress
        {
            get
            {
                if (duration <= 0) return 0;
                double pos = position + (Playing ? (DateTimeOffset.Now - stamp).TotalSeconds : 0);
                return Math.Max(0, Math.Min(1, pos / duration));
            }
        }
        public double Duration { get { return duration; } }

#pragma warning disable 4014 // fire-and-forget: the session reports the new state via events
        public void PlayPause() { if (session != null) session.TryTogglePlayPauseAsync(); }
        public void Next() { if (session != null) session.TrySkipNextAsync(); }
        public void Previous() { if (session != null) session.TrySkipPreviousAsync(); }
#pragma warning restore 4014
    }

    class NotificationService
    {
        UserNotificationListener listener;
        HashSet<uint> seen;
        bool busy;
        public bool Allowed;

        public async Task Start()
        {
            try
            {
                listener = UserNotificationListener.Current;
                Allowed = await WinRT.Await(listener.RequestAccessAsync()) == UserNotificationListenerAccessStatus.Allowed;
            }
            catch (Exception ex) { Allowed = false; Program.Log(ex); }
        }

        // shortcut: polls every 2s because NotificationChanged needs package identity; switch to the event if packaged as MSIX.
        public async Task<List<ToastInfo>> Poll()
        {
            var result = new List<ToastInfo>();
            if (!Allowed || busy) return result;
            busy = true;
            try
            {
                var list = await WinRT.Await(listener.GetNotificationsAsync(NotificationKinds.Toast));
                var ids = new HashSet<uint>(list.Select(n => n.Id));
                if (seen != null)
                    foreach (var n in list)
                        if (!seen.Contains(n.Id))
                        {
                            var t = await Describe(n);
                            if (t != null) result.Add(t);
                        }
                seen = ids; // first poll only seeds: don't replay what's already in Action Center
            }
            catch (Exception ex) { Program.Log(ex); }
            finally { busy = false; }
            return result;
        }

        static async Task<ToastInfo> Describe(UserNotification n)
        {
            var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding == null) return null;
            var texts = binding.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (texts.Count == 0) return null;
            var info = new ToastInfo { Title = texts[0], Body = string.Join(" ", texts.Skip(1)), Glyph = "", App = "" };
            try
            {
                info.App = n.AppInfo.DisplayInfo.DisplayName;
                info.Image = await WinRT.LoadImage(n.AppInfo.DisplayInfo.GetLogo(new Size(64, 64)), 88);
            }
            catch { } // some Win32 apps expose no display info / logo; glyph fallback is fine
            return info;
        }
    }
}
