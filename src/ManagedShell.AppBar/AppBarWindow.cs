﻿using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace ManagedShell.AppBar
{
    public class AppBarWindow : Window, INotifyPropertyChanged
    {
        protected readonly AppBarManager _appBarManager;
        protected readonly ExplorerHelper _explorerHelper;
        protected readonly FullScreenHelper _fullScreenHelper;

        public AppBarScreen Screen;
        protected bool ProcessScreenChanges = true;

        private double _dpiScale = 1.0;
        public double DpiScale
        {
            get
            {
                return _dpiScale;
            }
            set
            {
                if (_dpiScale != value)
                {
                    _dpiScale = value;
                    OnPropertyChanged();
                }
            }
        }

        // Window properties
        private WindowInteropHelper helper;
        private bool IsRaising;
        public IntPtr Handle;
        public bool AllowClose;
        public bool IsClosing;
        public bool IsOpening = true;
        protected double DesiredHeight;
        protected double DesiredWidth;
        private bool EnableBlur;

        // AppBar properties
        private int AppBarMessageId = -1;
        private NativeMethods.Rect _lastAppBarRect;

        private AppBarEdge _appBarEdge;
        public AppBarEdge AppBarEdge
        {
            get
            {
                return _appBarEdge;
            }
            set
            {
                _appBarEdge = value;
                OnPropertyChanged();
                OnPropertyChanged("Orientation");
            }
        }
        private AppBarMode _appBarMode;
        public AppBarMode AppBarMode
        {
            get
            {
                return _appBarMode;
            }
            set
            {
                _appBarMode = value;
                OnPropertyChanged();
            }
        }
        private FrameworkElement _autoHideElement;
        public FrameworkElement AutoHideElement
        {
            get
            {
                return _autoHideElement;
            }
            set
            {
                _autoHideElement = value;
                OnPropertyChanged();
            }
        }
        public bool AllowAutoHide
        {
            get => ShouldAllowAutoHide();
        }
        protected internal bool RequiresScreenEdge;
        protected double AutoHideShowMargin = 2;
        protected double AutoHideDelayMs = 400;
        protected double AutoHideShowDelayMs = 0;
        protected double AutoHideAnimationMs = 300;
        protected double AutoHideShowAnimationMs = 150;

        private bool _isDragWithin;
        private bool _isMouseWithin;
        private bool _isContextMenuOpen;
        private DispatcherTimer _peekAutoHideTimer;

        public Orientation Orientation
        {
            get => (AppBarEdge == AppBarEdge.Left || AppBarEdge == AppBarEdge.Right) ? Orientation.Vertical : Orientation.Horizontal;
        }

        public AppBarWindow(AppBarManager appBarManager, ExplorerHelper explorerHelper, FullScreenHelper fullScreenHelper, AppBarScreen screen, AppBarEdge edge, AppBarMode mode, double size)
        {
            _explorerHelper = explorerHelper;
            _fullScreenHelper = fullScreenHelper;
            _appBarManager = appBarManager;

            Closing += OnClosing;
            SourceInitialized += OnSourceInitialized;

            PreviewDragEnter += AppBarWindow_PreviewDragEnter;
            PreviewDragLeave += AppBarWindow_PreviewDragLeave;
            PreviewDrop += AppBarWindow_PreviewDrop;
            MouseEnter += AppBarWindow_MouseEnter;
            MouseLeave += AppBarWindow_MouseLeave;
            ContextMenuOpening += AppBarWindow_ContextMenuOpening;
            ContextMenuClosing += AppBarWindow_ContextMenuClosing;
            PropertyChanged += AppBarWindow_PropertyChanged;

            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Title = "";
            Topmost = true;
            UseLayoutRounding = true;
            WindowStyle = WindowStyle.None;

            Screen = screen;
            AppBarEdge = edge;
            AppBarMode = mode;

            if (Orientation == Orientation.Vertical)
            {
                DesiredWidth = size;
            }
            else
            {
                DesiredHeight = size;
            }
        }

        private void AppBarWindow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (IsOpening)
            {
                return;
            }

            if (e.PropertyName == "AllowAutoHide")
            {
                if (AllowAutoHide)
                {
                    AnimateAutoHide(true);
                }
                else
                {
                    AnimateAutoHide(false);
                }
            }
            else if (e.PropertyName == "AppBarMode")
            {
                if (AppBarMode == AppBarMode.Normal)
                {
                    RegisterAppBar();
                }
                else
                {
                    UnregisterAppBar();
                }

                if (AppBarMode == AppBarMode.AutoHide)
                {
                    _appBarManager.RegisterAutoHideBar(this);
                    OnPropertyChanged("AllowAutoHide");
                }
                else
                {
                    _appBarManager.UnregisterAutoHideBar(this);
                    AnimateAutoHide(false, true);
                }
            }
        }

        private void AnimateAutoHide(bool isHiding, bool immediate = false)
        {
            if (AutoHideElement == null)
            {
                return;
            }

            if (isHiding && AppBarMode != AppBarMode.AutoHide)
            {
                return;
            }

            double animTo = 0;

            if (isHiding)
            {
                animTo = Orientation == Orientation.Horizontal ? DesiredHeight : DesiredWidth;
                animTo -= AutoHideShowMargin;

                if (AppBarEdge == AppBarEdge.Top || AppBarEdge == ((FlowDirection == FlowDirection.LeftToRight) ? AppBarEdge.Left : AppBarEdge.Right))
                {
                    animTo *= -1;
                }
            }

            var animation = new DoubleAnimation(animTo, TimeSpan.FromMilliseconds(isHiding ? AutoHideAnimationMs : AutoHideShowAnimationMs).Duration());
            animation.BeginTime = TimeSpan.FromMilliseconds(immediate ? 0 : isHiding ? AutoHideDelayMs : AutoHideShowDelayMs);
            animation.EasingFunction = new SineEase();

            Storyboard.SetTarget(animation, AutoHideElement);
            Storyboard.SetTargetProperty(animation, new PropertyPath($"RenderTransform.(TranslateTransform.{(Orientation == Orientation.Horizontal ? 'Y' : 'X')})"));

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            animation.CurrentStateInvalidated += (object sender, EventArgs e) => {
                if (((AnimationClock)sender).CurrentState == ClockState.Active)
                {
                    OnAutoHideAnimationBegin(isHiding);
                }
            };

            storyboard.Completed += (object sender, EventArgs e) => {
                OnAutoHideAnimationComplete(isHiding);
            };

            storyboard.Begin(AutoHideElement);
        }

        protected void PeekDuringAutoHide(int msToPeek = 1000)
        {
            if (AppBarMode != AppBarMode.AutoHide)
            {
                return;
            }

            _peekAutoHideTimer?.Stop();

            AnimateAutoHide(false, true);

            _peekAutoHideTimer = new DispatcherTimer();
            _peekAutoHideTimer.Interval = TimeSpan.FromMilliseconds(msToPeek);
            _peekAutoHideTimer.Tick += (object sender, EventArgs e) =>
            {
                _peekAutoHideTimer?.Stop();
                if (AllowAutoHide)
                {
                    AnimateAutoHide(true, true);
                }
            };
            _peekAutoHideTimer.Start();
        }

        #region Events
        protected virtual void OnSourceInitialized(object sender, EventArgs e)
        {
            // set up helper and get handle
            helper = new WindowInteropHelper(this);
            Handle = helper.Handle;

            // set up window procedure
            HwndSource source = HwndSource.FromHwnd(Handle);
            source.AddHook(WndProc);

            // set initial DPI. We do it here so that we get the correct value when DPI has changed since initial user logon to the system.
            if (Screen.Primary)
            {
                DpiHelper.DpiScale = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice.M11;
            }

            // use system DPI initially; when we set position we will get WM_DPICHANGED and set it correctly
            DpiScale = DpiHelper.DpiScale;

            SetPosition();

            if (EnvironmentHelper.IsAppRunningAsShell)
            {
                // set position again, on a delay, in case one display has a different DPI. for some reason the system overrides us if we don't wait
                DelaySetPosition();
            }

            if (AppBarMode == AppBarMode.Normal)
            {
                RegisterAppBar();
            }
            else if (AppBarMode == AppBarMode.AutoHide)
            {
                _appBarManager.RegisterAutoHideBar(this);
            }

            // hide from alt-tab etc
            WindowHelper.HideWindowFromTasks(Handle);

            // register for full-screen notifications
            _fullScreenHelper.FullScreenApps.CollectionChanged += FullScreenApps_CollectionChanged;

            IsOpening = false;
            OnPropertyChanged("AllowAutoHide");
        }

        protected virtual void OnAutoHideAnimationBegin(bool isHiding)
        {
            if (isHiding && EnableBlur && Handle != IntPtr.Zero && AllowsTransparency && AllowAutoHide)
            {
                // Disable blur if enabled and hiding
                WindowHelper.SetWindowBlur(Handle, false);
            }
        }

        protected virtual void OnAutoHideAnimationComplete(bool isHiding)
        {
            if (!isHiding && EnableBlur && Handle != IntPtr.Zero && AllowsTransparency && !AllowAutoHide)
            {
                // Re-enable blur if enabled and showing
                WindowHelper.SetWindowBlur(Handle, true);
            }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            IsClosing = true;

            CustomClosing();

            if (AllowClose)
            {
                UnregisterAppBar();
                _appBarManager.UnregisterAutoHideBar(this);

                // unregister full-screen notifications
                _fullScreenHelper.FullScreenApps.CollectionChanged -= FullScreenApps_CollectionChanged;
            }
            else
            {
                IsClosing = false;
                e.Cancel = true;
            }
        }

        private void FullScreenApps_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            bool found = false;

            foreach (FullScreenApp app in _fullScreenHelper.FullScreenApps)
            {
                if (app.screen.DeviceName == Screen.DeviceName || app.screen.IsVirtualScreen)
                {
                    // we need to not be on top now
                    found = true;
                    break;
                }
            }

            if (found && Topmost)
            {
                setFullScreenMode(true);
            }
            else if (!found && !Topmost)
            {
                setFullScreenMode(false);
            }
        }

        private void AppBarWindow_ContextMenuClosing(object sender, ContextMenuEventArgs e)
        {
            SetAutoHideStateVar(ref _isContextMenuOpen, false);
        }

        private void AppBarWindow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // ContextMenuOpening fires even if the element has no context menu defined, so we must check
            if (HasContextMenu(e.OriginalSource as FrameworkElement))
            {
                SetAutoHideStateVar(ref _isContextMenuOpen, true);
            }
        }

        private void AppBarWindow_PreviewDragEnter(object sender, DragEventArgs e)
        {
            SetAutoHideStateVar(ref _isDragWithin, true);
        }

        private void AppBarWindow_PreviewDragLeave(object sender, DragEventArgs e)
        {
            SetAutoHideStateVar(ref _isDragWithin, false);
        }

        private void AppBarWindow_PreviewDrop(object sender, DragEventArgs e)
        {
            SetAutoHideStateVar(ref _isDragWithin, false);
        }

        private void AppBarWindow_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            SetAutoHideStateVar(ref _isMouseWithin, true);
        }

        private void AppBarWindow_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            SetAutoHideStateVar(ref _isMouseWithin, false);
        }

        protected virtual IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == AppBarMessageId && AppBarMessageId != -1)
            {
                switch ((NativeMethods.AppBarNotifications)wParam.ToInt32())
                {
                    case NativeMethods.AppBarNotifications.PosChanged:
                        if (Orientation == Orientation.Vertical)
                        {
                            _appBarManager.ABSetPos(this, DesiredWidth * DpiScale, ActualHeight * DpiScale, AppBarEdge);
                        }
                        else
                        {
                            _appBarManager.ABSetPos(this, ActualWidth * DpiScale, DesiredHeight * DpiScale, AppBarEdge);
                        }
                        break;

                    case NativeMethods.AppBarNotifications.WindowArrange:
                        if ((int)lParam != 0) // before
                        {
                            Visibility = Visibility.Collapsed;
                        }
                        else // after
                        {
                            Visibility = Visibility.Visible;
                        }

                        break;
                }
                handled = true;
            }
            else if (msg == (int)NativeMethods.WM.ACTIVATE && AppBarMode == AppBarMode.Normal && !EnvironmentHelper.IsAppRunningAsShell && !AllowClose)
            {
                _appBarManager.AppBarActivate(hwnd);
            }
            else if (msg == (int)NativeMethods.WM.WINDOWPOSCHANGING)
            {
                // Extract the WINDOWPOS structure corresponding to this message
                NativeMethods.WINDOWPOS wndPos = NativeMethods.WINDOWPOS.FromMessage(lParam);

                // Determine if the z-order is changing (absence of SWP_NOZORDER flag)
                // If we are intentionally trying to become topmost, make it so
                if (IsRaising && (wndPos.flags & NativeMethods.SetWindowPosFlags.SWP_NOZORDER) == 0)
                {
                    // Sometimes Windows thinks we shouldn't go topmost, so poke here to make it happen.
                    wndPos.hwndInsertAfter = (IntPtr)NativeMethods.WindowZOrder.HWND_TOPMOST;
                    wndPos.UpdateMessage(lParam);
                }
            }
            else if (msg == (int)NativeMethods.WM.WINDOWPOSCHANGED && AppBarMode == AppBarMode.Normal && !EnvironmentHelper.IsAppRunningAsShell && !AllowClose)
            {
                _appBarManager.AppBarWindowPosChanged(hwnd);
            }
            else if (msg == (int)NativeMethods.WM.DPICHANGED)
            {
                DpiScale = (wParam.ToInt32() & 0xFFFF) / 96d;

                if (Screen.Primary)
                {
                    DpiHelper.DpiScale = DpiScale;
                }

                // if we are opening, we're getting this message as a result of positioning
                // if we are an AppBar, that code will fix our position, so skip in that case to prevent infinite resizing.
                if (!IsOpening || AppBarMode != AppBarMode.Normal)
                {
                    ProcessScreenChange(ScreenSetupReason.DpiChange);
                }
            }
            else if (msg == (int)NativeMethods.WM.DISPLAYCHANGE)
            {
                ProcessScreenChange(ScreenSetupReason.DisplayChange);
                handled = true;
            }
            else if (msg == (int)NativeMethods.WM.DEVICECHANGE && (int)wParam == 0x0007)
            {
                ProcessScreenChange(ScreenSetupReason.DeviceChange);
                handled = true;
            }
            else if (msg == (int)NativeMethods.WM.DWMCOMPOSITIONCHANGED)
            {
                ProcessScreenChange(ScreenSetupReason.DwmChange);
                handled = true;
            }
            
            return IntPtr.Zero;
        }
        #endregion

        #region Helpers
        private void DelaySetPosition()
        {
            // delay changing things when we are shell. it seems that explorer AppBars do this too.
            // if we don't, the system moves things to bad places
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.1) };
            timer.Start();
            timer.Tick += (sender1, args) =>
            {
                SetPosition();
                timer.Stop();
            };
        }

        public void SetScreenPosition()
        {
            // set our position if running as shell, otherwise let AppBar do the work
            if (EnvironmentHelper.IsAppRunningAsShell || AppBarMode != AppBarMode.Normal)
            {
                DelaySetPosition();
            }
            else if (AppBarMode == AppBarMode.Normal)
            {
                if (Orientation == Orientation.Vertical)
                {
                    _appBarManager.ABSetPos(this, DesiredWidth * DpiScale, Screen.Bounds.Height, AppBarEdge);
                }
                else
                {
                    _appBarManager.ABSetPos(this, Screen.Bounds.Width, DesiredHeight * DpiScale, AppBarEdge);
                }
            }
        }

        internal void SetAppBarPosition(NativeMethods.Rect rect)
        {
            int swp = (int)NativeMethods.SetWindowPosFlags.SWP_NOZORDER | (int)NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE;

            if (rect.Width < 0 || rect.Height < 0)
            {
                swp |= (int)NativeMethods.SetWindowPosFlags.SWP_NOSIZE;
            }

            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, swp);
        }

        private void SetAutoHideStateVar(ref bool varToSet, bool newValue)
        {
            bool currentAutoHide = AllowAutoHide;
            varToSet = newValue;

            if (AllowAutoHide != currentAutoHide)
            {
                OnPropertyChanged("AllowAutoHide");
            }
        }

        private void ProcessScreenChange(ScreenSetupReason reason)
        {
            // process screen changes if we are on the primary display and the designated window
            // (or any display in the case of a DPI change, since only the changed display receives that message and not all windows receive it reliably)
            // suppress this if we are shutting down (which can trigger this method on multi-dpi setups due to window movements)
            if (((Screen.Primary && ProcessScreenChanges) || reason == ScreenSetupReason.DpiChange) && !AllowClose)
            {
                SetScreenProperties(reason);
            }
        }

        private void setFullScreenMode(bool entering)
        {
            if (entering)
            {
                ShellLogger.Debug($"AppBarWindow: {Name} on {Screen.DeviceName} conceding to full-screen app");

                Topmost = false;
                WindowHelper.ShowWindowBottomMost(Handle);
            }
            else
            {
                ShellLogger.Debug($"AppBarWindow: {Name} on {Screen.DeviceName} returning to normal state");

                IsRaising = true;
                Topmost = true;
                WindowHelper.ShowWindowTopMost(Handle);
                IsRaising = false;
            }
        }

        private bool HasContextMenu(FrameworkElement fe)
        {
            if (fe == null)
            {
                return false;
            }

            if (fe.ContextMenu != null)
            {
                return true;
            }
            else
            {
                var parent = VisualTreeHelper.GetParent(fe) as FrameworkElement;
                return HasContextMenu(parent);
            }
        }

        protected void SetBlur(bool enable)
        {
            if (EnableBlur != enable && Handle != IntPtr.Zero && AllowsTransparency)
            {
                EnableBlur = enable;

                if (enable && AppBarMode == AppBarMode.AutoHide && AllowAutoHide)
                {
                    // If we're auto-hidden, don't actually enable blur right now.
                    return;
                }

                WindowHelper.SetWindowBlur(Handle, enable);
            }
        }

        protected void RegisterAppBar()
        {
            if (AppBarMode != AppBarMode.Normal || _appBarManager.AppBars.Contains(this))
            {
                return;
            }

            if (Orientation == Orientation.Vertical)
            {
                AppBarMessageId = _appBarManager.RegisterBar(this, DesiredWidth * DpiScale, ActualHeight * DpiScale, AppBarEdge);
            }
            else
            {
                AppBarMessageId = _appBarManager.RegisterBar(this, ActualWidth * DpiScale, DesiredHeight * DpiScale, AppBarEdge);
            }
        }

        protected void UnregisterAppBar()
        {
            if (!_appBarManager.AppBars.Contains(this))
            {
                return;
            }

            if (Orientation == Orientation.Vertical)
            {
                _appBarManager.RegisterBar(this, DesiredWidth * DpiScale, ActualHeight * DpiScale);
            }
            else
            {
                _appBarManager.RegisterBar(this, ActualWidth * DpiScale, DesiredHeight * DpiScale);
            }
        }
        #endregion

        #region Virtual methods
        public virtual void AfterAppBarPos(bool isSameCoords, NativeMethods.Rect rect)
        {
            _lastAppBarRect = rect;
            if (!isSameCoords)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.1) };
                timer.Tick += (sender1, args) =>
                {
                    // set position again, since WPF may have overridden the original change from AppBarHelper
                    SetAppBarPosition(_lastAppBarRect);

                    timer.Stop();
                };
                timer.Start();
            }
        }

        protected virtual bool ShouldAllowAutoHide()
        {
            return AppBarMode == AppBarMode.AutoHide && !_isMouseWithin && !_isContextMenuOpen && !_isDragWithin && (_peekAutoHideTimer == null || !_peekAutoHideTimer.IsEnabled);
        }

        protected virtual void CustomClosing() { }

        protected virtual void SetScreenProperties(ScreenSetupReason reason)
        {
            _fullScreenHelper.NotifyScreensChanged();

            if (Screen.Primary && reason != ScreenSetupReason.DpiChange)
            {
                Screen = AppBarScreen.FromPrimaryScreen();
            }
            SetScreenPosition();
        }

        public virtual void SetPosition()
        {
            double edgeOffset = 0;
            int left;
            int top;
            int height;
            int width;

            if (!RequiresScreenEdge)
            {
                edgeOffset = _appBarManager.GetAppBarEdgeWindowsHeight(AppBarEdge, Screen);
            }

            if (Orientation == Orientation.Vertical)
            {
                top = Screen.Bounds.Top;
                height = Screen.Bounds.Height;
                width = Convert.ToInt32(DesiredWidth * DpiScale);

                if (AppBarEdge == AppBarEdge.Left)
                {
                    left = Screen.Bounds.Left + Convert.ToInt32(edgeOffset * DpiScale);
                }
                else
                {
                    left = Screen.Bounds.Right - width - Convert.ToInt32(edgeOffset * DpiScale);
                }
            }
            else
            {
                left = Screen.Bounds.Left;
                width = Screen.Bounds.Width;
                height = Convert.ToInt32(DesiredHeight * DpiScale);

                if (AppBarEdge == AppBarEdge.Top)
                {
                    top = Screen.Bounds.Top + Convert.ToInt32(edgeOffset * DpiScale);
                }
                else
                {
                    top = Screen.Bounds.Bottom - height - Convert.ToInt32(edgeOffset * DpiScale);
                }
            }

            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, left, top, width, height, (int)NativeMethods.SetWindowPosFlags.SWP_NOZORDER | (int)NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE);


            if (EnvironmentHelper.IsAppRunningAsShell)
            {
                _appBarManager.SetWorkArea(Screen);
            }
        }
        #endregion

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion
    }
}