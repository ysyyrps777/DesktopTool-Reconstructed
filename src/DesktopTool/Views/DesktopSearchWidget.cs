using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTool.Models;
using DesktopTool.Services;
using DesktopTool.ViewModels;
using Microsoft.Win32;

namespace DesktopTool.Views;

public class DesktopSearchWidget : Window, IComponentConnector
{
	private readonly MainWindow _owner;

	private readonly AppSettings _settings;

	private nint _hwnd;

	private int _lastDragPhysX;

	private int _lastDragPhysY;

	private bool _isClosing;

	private bool _isDesktopChild;

	private readonly int _pid = Process.GetCurrentProcess().Id;

	private readonly NativeMethods.WinEventDelegate _fgProc;

	private nint _fgHook;

	private bool _isEditing;

	private bool _dwmWoken;

	private bool _displayHooked;

	private DispatcherTimer? _displayChangeTimer;

	private Point _dragStartPoint;

	private bool _dragCandidate;

	private bool _dragging;

	private DesktopDragHost _dragHost;

	private bool _suppressPositionSave;

	internal Border RootBorder;

	internal Border BgLayer;

	internal Border DragGrip;

	internal Button SearchButton;

	internal TextBox SearchTextBox;

	internal TextBlock Placeholder;

	private bool _contentLoaded;

	public DesktopSearchWidget(MainWindow owner, AppSettings settings)
	{
		InitializeComponent();
		_owner = owner;
		_settings = settings;
		_fgProc = OnForegroundChanged;
		base.DataContext = new DesktopSearchWidgetViewModel(owner.ViewModel);
		base.Loaded += OnLoaded;
	}

	private int BlurTintAlpha()
	{
		return (int)Math.Max(0.0, Math.Min(255.0, (1.0 - _settings.BackgroundOpacity) * 255.0));
	}

	public void RefreshVisualStyle()
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		if (Environment.OSVersion.Version.Build < 22000)
		{
			if (_owner.ViewModel.IsGlassStyle)
			{
				GlassEffectHelper.EnableBlur(_hwnd, BlurTintAlpha());
			}
			else
			{
				GlassEffectHelper.DisableBlur(_hwnd);
			}
		}
		else if (_owner.ViewModel.IsGlassStyle)
		{
			GlassEffectHelper.EnableAcrylicWithTint(_hwnd);
			GlassEffectHelper.ApplyRoundCorners(_hwnd, (int)base.ActualWidth, (int)base.ActualHeight);
		}
		else
		{
			GlassEffectHelper.DisableBlur(_hwnd);
		}
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
		_hwnd = source?.Handle ?? IntPtr.Zero;
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		_isDesktopChild = false;
		source?.AddHook(WndProc);
		DesktopWidgetInitializer.Register(_hwnd, source, DesktopWidgetInitializer.Default);
		RefreshVisualStyle();
		_owner.ViewModel.PropertyChanged += delegate(object? s, PropertyChangedEventArgs ev)
		{
			if (ev.PropertyName == "IsGlassStyle")
			{
				RefreshVisualStyle();
			}
		};
		_dragHost = new DesktopDragHost(() => _hwnd, () => DesktopEmbedService.GetOwnerHwnd(_hwnd), () => base.MinWidth, () => base.MinHeight, this, BringToTop, delegate(bool s)
		{
			_suppressPositionSave = s;
		}, SetPosition, OnDragCommitted, delegate
		{
			WidgetAlignGlowService.UpdateAlignment(_hwnd, NativeDragHelper.GetDipRect(_hwnd), WindowSnapService.EdgeMask.All);
		}, delegate
		{
			WidgetAlignGlowService.ClearGlows();
		});
		InstallForegroundWatcher();
		PlaceAboveDesktop();
		base.ContentRendered += OnFirstContentRendered;
		base.Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)delegate
		{
			NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, 19u);
		});
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PlaceAboveDesktop));
		if (source != null)
		{
			source.CompositionTarget.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
		}
		base.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionAtDefault));
		Log($"[Widget] OnLoaded 完成 hwnd=0x{_hwnd:X8} IsDesktopChild={_isDesktopChild} IsVisible={base.IsVisible}");
	}

	internal void ForceTopMost()
	{
		if (_hwnd != IntPtr.Zero)
		{
			NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, 19u);
		}
	}

	private void BringToTop()
	{
		if (_hwnd != IntPtr.Zero)
		{
			AppPanelTracker.PlaceDesktopWindow(_hwnd, NativeMethods.HWND_TOP);
		}
	}

	private void OnFirstContentRendered(object sender, EventArgs e)
	{
		if (!_dwmWoken)
		{
			_dwmWoken = true;
			DesktopEmbedService.WakeDwmComposition(_hwnd);
			PlaceAboveDesktop();
			Log($"[Widget] ContentRendered→WakeDwmComposition+PlaceAboveDesktop hwnd=0x{_hwnd:X8} IsVisible={base.IsVisible} Opacity={base.Opacity:F2}");
			if (!_displayHooked)
			{
				_displayHooked = true;
				SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
			}
		}
	}

	private void OnDisplaySettingsChanged(object? sender, EventArgs e)
	{
		if (_displayChangeTimer == null)
		{
			_displayChangeTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(1500.0)
			};
			_displayChangeTimer.Tick += HandleDisplayChangeDebounced;
		}
		_displayChangeTimer.Stop();
		_displayChangeTimer.Start();
	}

	private void HandleDisplayChangeDebounced(object? sender, EventArgs e)
	{
		_displayChangeTimer?.Stop();
		if (_isClosing || _hwnd == IntPtr.Zero)
		{
			return;
		}
		try
		{
			double savedL = _settings.SearchWidgetLeft;
			double savedT = _settings.SearchWidgetTop;
			if (!(savedL >= 0.0) || !(savedT >= 0.0))
			{
				return;
			}
			double w = ((base.ActualWidth > 1.0) ? base.ActualWidth : 252.0);
			double h = ((base.ActualHeight > 1.0) ? base.ActualHeight : 52.0);
			if (DesktopEmbedService.IsOnAnyScreen(savedL, savedT, w, h))
			{
				if (Math.Abs(base.Left - savedL) > 1.0 || Math.Abs(base.Top - savedT) > 1.0)
				{
					base.Left = savedL;
					base.Top = savedT;
					Log($"[Widget] 显示变更恢复：搬回持久化位置 ({savedL:F0},{savedT:F0})");
				}
			}
			else
			{
				PositionAtDefault();
				Log("[Widget] 显示变更恢复：持久化位置离屏 → 回默认位置");
			}
		}
		catch
		{
		}
	}

	private void PositionAtDefault()
	{
		double w = ((base.ActualWidth > 1.0) ? base.ActualWidth : 252.0);
		double h = ((base.ActualHeight > 1.0) ? base.ActualHeight : 52.0);
		double screenW = SystemParameters.WorkArea.Width;
		double screenH = SystemParameters.WorkArea.Height;
		double left;
		double top;
		if (_settings.SearchWidgetLeft >= 0.0 && _settings.SearchWidgetTop >= 0.0)
		{
			left = _settings.SearchWidgetLeft;
			top = _settings.SearchWidgetTop;
		}
		else
		{
			left = (screenW - w) / 2.0;
			top = (screenH - h) / 2.0;
		}
		left = Math.Max(0.0, Math.Min(left, Math.Max(0.0, screenW - w)));
		top = Math.Max(0.0, Math.Min(top, Math.Max(0.0, screenH - h)));
		if (_isDesktopChild)
		{
			(left, top) = ConvertScreenToParentRelative(left, top);
		}
		base.Left = left;
		base.Top = top;
		Log($"[Widget] PositionAtDefault left={left:F0} top={top:F0} w={w:F0} h={h:F0} isChild={_isDesktopChild} restored={_settings.SearchWidgetLeft >= 0.0}");
	}

	private (double left, double top) ConvertScreenToParentRelative(double screenX, double screenY)
	{
		(int, int) ownerOrigin = DesktopEmbedService.GetOwnerOrigin(_hwnd);
		double dpiScale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
		nint hMonitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT
		{
			X = 0,
			Y = 0
		}, 1u);
		NativeMethods.MONITORINFO mi = new NativeMethods.MONITORINFO
		{
			cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
		};
		NativeMethods.GetMonitorInfo(hMonitor, ref mi);
		double num = (double)mi.rcMonitor.Left + screenX * dpiScale;
		double win32Y = (double)mi.rcMonitor.Top + screenY * dpiScale;
		double num2 = num - (double)ownerOrigin.Item1;
		return new ValueTuple<double, double>(item2: (win32Y - (double)ownerOrigin.Item2) / dpiScale, item1: num2 / dpiScale);
	}

	private static void Log(string message)
	{
		try
		{
			LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_desktopwidget.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	private void PlaceAboveDesktop()
	{
		if (_hwnd != IntPtr.Zero)
		{
			nint owner = DesktopEmbedService.GetOwnerHwnd(_hwnd);
			if (owner != IntPtr.Zero)
			{
				NativeMethods.SetWindowPos(_hwnd, owner, 0, 0, 0, 0, 19u);
			}
		}
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		_isClosing = true;
		SavePosition();
		base.OnClosing(e);
	}

	protected override void OnClosed(EventArgs e)
	{
		try
		{
			if (_displayHooked)
			{
				_displayHooked = false;
				SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
			}
			_displayChangeTimer?.Stop();
		}
		catch
		{
		}
		try
		{
			UninstallForegroundWatcher();
		}
		catch
		{
		}
		if (_hwnd != IntPtr.Zero)
		{
			try
			{
				DesktopEmbedService.UnregisterDesktopWindow(_hwnd);
			}
			catch
			{
			}
		}
		Log($"[Widget] OnClosed hwnd=0x{_hwnd:X8} IsDesktopChild={_isDesktopChild} 已注销");
		base.OnClosed(e);
	}

	private void RootBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (SearchTextBox.IsMouseOver)
		{
			if (!_isDesktopChild && !_isEditing)
			{
				_isEditing = true;
				ActivateWidgetForEdit();
			}
			SearchTextBox.Focus();
			_dragCandidate = false;
		}
		else
		{
			_dragCandidate = true;
			_dragging = false;
			_dragStartPoint = e.GetPosition(this);
		}
	}

	private void RootBorder_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (!_dragCandidate || _dragging)
		{
			return;
		}
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			_dragCandidate = false;
			return;
		}
		Point p = e.GetPosition(this);
		if (Math.Abs(p.X - _dragStartPoint.X) > 4.0 || Math.Abs(p.Y - _dragStartPoint.Y) > 4.0)
		{
			_dragCandidate = false;
			_dragging = true;
			StartNativeDrag();
		}
	}

	private void RootBorder_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		_dragCandidate = false;
		_dragging = false;
	}

	private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		Placeholder.Visibility = ((!string.IsNullOrEmpty(SearchTextBox.Text)) ? Visibility.Collapsed : Visibility.Visible);
	}

	private void SearchButton_Click(object sender, RoutedEventArgs e)
	{
		_owner.OpenSearchWindow(SearchTextBox.Text.Trim());
	}

	private void StartNativeDrag()
	{
		NativeDragHelper.Begin(_dragHost, 0);
	}

	private void SetPosition(int x, int y, int w, int h)
	{
		_lastDragPhysX = x;
		_lastDragPhysY = y;
		uint flags = (uint)(0x10 | (_isDesktopChild ? 4 : 0));
		if (w <= 0 || h <= 0)
		{
			flags |= 1;
		}
		NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, x, y, w, h, flags);
	}

	private void OnDragCommitted(int resizeHit)
	{
		try
		{
			if (_isDesktopChild)
			{
				NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, _lastDragPhysX, _lastDragPhysY, 0, 0, 21u);
			}
			double dpi = 1.0;
			try
			{
				nint mon = NativeMethods.MonitorFromWindow(_hwnd, 2u);
				if (mon != IntPtr.Zero)
				{
					NativeMethods.GetDpiForMonitor(mon, 0, out var dpiX, out var _);
					if (dpiX != 0)
					{
						dpi = (double)dpiX / 96.0;
					}
				}
			}
			catch
			{
			}
			if (_isDesktopChild)
			{
				NativeMethods.GetWindowRect(_hwnd, out var wr);
				base.Left = (double)wr.Left / dpi;
				base.Top = (double)wr.Top / dpi;
			}
			else
			{
				base.Left = (double)_lastDragPhysX / dpi;
				base.Top = (double)_lastDragPhysY / dpi;
			}
			if (!_suppressPositionSave)
			{
				SavePosition();
			}
		}
		catch
		{
		}
	}

	private void SavePosition()
	{
		try
		{
			double left = base.Left;
			double top = base.Top;
			if (_isDesktopChild)
			{
				(left, top) = ConvertParentToScreenRelative(left, top);
			}
			_settings.SearchWidgetLeft = left;
			_settings.SearchWidgetTop = top;
			_owner.SaveAppSettings();
		}
		catch
		{
		}
	}

	private (double left, double top) ConvertParentToScreenRelative(double parentX, double parentY)
	{
		(int, int) ownerOrigin = DesktopEmbedService.GetOwnerOrigin(_hwnd);
		return (left: parentX + (double)ownerOrigin.Item1, top: parentY + (double)ownerOrigin.Item2);
	}

	private void ActivateWidgetForEdit()
	{
		if (_hwnd != IntPtr.Zero)
		{
			int ex = NativeMethods.GetWindowLong(_hwnd, -20);
			if ((ex & 0x8000000) != 0)
			{
				ex &= -134217729;
				NativeMethods.SetWindowLong(_hwnd, -20, ex);
			}
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			NativeMethods.SetWindowPos(_hwnd, band, 0, 0, 0, 0, 99u);
			NativeMethods.SetForegroundWindow(_hwnd);
		}
	}

	private void DeactivateWidgetAfterEdit()
	{
		if (_hwnd != IntPtr.Zero)
		{
			int ex = NativeMethods.GetWindowLong(_hwnd, -20);
			if ((ex & 0x8000000) == 0)
			{
				ex |= 0x8000000;
				NativeMethods.SetWindowLong(_hwnd, -20, ex);
			}
			PlaceAboveDesktop();
		}
	}

	private void SearchTextBox_GotFocus(object sender, RoutedEventArgs e)
	{
		if (!_isDesktopChild && !_isEditing)
		{
			_isEditing = true;
			ActivateWidgetForEdit();
		}
	}

	private void SearchTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (!_isDesktopChild && _isEditing)
		{
			_isEditing = false;
			DeactivateWidgetAfterEdit();
		}
	}

	private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Return)
		{
			string q = SearchTextBox.Text.Trim();
			if (!string.IsNullOrEmpty(q))
			{
				_owner.OpenSearchWindow(q);
			}
		}
	}

	private void InstallForegroundWatcher()
	{
		if (_fgHook == IntPtr.Zero)
		{
			_fgHook = NativeMethods.SetWinEventHook(3u, 3u, IntPtr.Zero, _fgProc, 0u, 0u, 2u);
		}
	}

	private void UninstallForegroundWatcher()
	{
		if (_fgHook != IntPtr.Zero)
		{
			NativeMethods.UnhookWinEvent(_fgHook);
			_fgHook = IntPtr.Zero;
		}
	}

	private void OnForegroundChanged(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
	{
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		NativeMethods.GetWindowThreadProcessId(hwnd, out var fgPid);
		if (fgPid == _pid || fgPid == 0)
		{
			return;
		}
		base.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)delegate
		{
			if (SearchTextBox != null && SearchTextBox.IsKeyboardFocused)
			{
				Keyboard.ClearFocus();
			}
		});
	}

	private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		bool flag;
		if (_isDesktopChild)
		{
			switch (msg)
			{
			case 2:
				if (!_isClosing)
				{
					handled = true;
				}
				return IntPtr.Zero;
			case 16:
				return IntPtr.Zero;
			case 131:
				if (wParam != IntPtr.Zero)
				{
					handled = false;
					return IntPtr.Zero;
				}
				break;
			}
			switch (msg)
			{
			case 5:
			case 24:
			case 28:
			case 70:
			case 71:
			case 274:
				return IntPtr.Zero;
			case 33:
			case 256:
			case 260:
			case 513:
			case 515:
			case 516:
			case 519:
			case 520:
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				return IntPtr.Zero;
			}
			if (NativeMethods.WM_DESKTOOL_WIND != 0)
			{
				_ = msg;
				_ = NativeMethods.WM_DESKTOOL_WIND;
				return IntPtr.Zero;
			}
			return IntPtr.Zero;
		}
		if (DesktopEmbedService.IsRecoveryMode)
		{
			switch (msg)
			{
			case 5:
			case 24:
				return IntPtr.Zero;
			case 70:
				if ((Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam).flags & 0x80) != 0)
				{
					return IntPtr.Zero;
				}
				break;
			}
		}
		switch (msg)
		{
		case 2:
			if (!_isClosing)
			{
				handled = true;
			}
			return IntPtr.Zero;
		case 256:
		case 260:
		case 513:
		case 515:
		case 516:
		case 519:
		case 520:
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			WinDHookService.NotifyOwnWindowInteraction();
		}
		if (msg == 24 && wParam == IntPtr.Zero)
		{
			handled = true;
			DesktopEmbedService.ReinforceProtectionLightweight(hwnd);
			return IntPtr.Zero;
		}
		if (msg == 274 && (((IntPtr)wParam).ToInt32() & 0xFFF0) == 61472)
		{
			handled = true;
			return IntPtr.Zero;
		}
		if (msg == 5 && ((IntPtr)wParam).ToInt32() == 1)
		{
			NativeMethods.ShowWindow(hwnd, 4);
			handled = true;
			return IntPtr.Zero;
		}
		if (msg == 70)
		{
			NativeMethods.WINDOWPOS wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
			if ((wp.flags & 0x80) != 0)
			{
				wp.flags &= 4294967167u;
				wp.flags |= 19u;
				Marshal.StructureToPtr(wp, lParam, fDeleteOld: false);
				handled = true;
				NativeMethods.ShowWindow(hwnd, 4);
				DesktopEmbedService.ReinforceProtectionLightweight(hwnd);
				return IntPtr.Zero;
			}
		}
		if (msg == 71 && (Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam).flags & 0x80) != 0)
		{
			NativeMethods.ShowWindow(hwnd, 4);
			DesktopEmbedService.ReinforceProtectionLightweight(hwnd);
			handled = true;
			return IntPtr.Zero;
		}
		if (msg == 28 && wParam == IntPtr.Zero)
		{
			base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				if (!base.IsVisible || base.WindowState != WindowState.Normal)
				{
					if (base.WindowState != WindowState.Normal)
					{
						base.WindowState = WindowState.Normal;
					}
					if (!base.IsVisible)
					{
						Show();
					}
					NativeMethods.ShowWindow(_hwnd, 4);
				}
				if (SearchTextBox != null && SearchTextBox.IsKeyboardFocused)
				{
					Keyboard.ClearFocus();
				}
			});
		}
		if (NativeMethods.WM_DESKTOOL_WIND != 0 && msg == NativeMethods.WM_DESKTOOL_WIND)
		{
			try
			{
				if (base.WindowState != WindowState.Normal)
				{
					base.WindowState = WindowState.Normal;
				}
				if (!base.IsVisible)
				{
					Show();
				}
				NativeMethods.ShowWindow(hwnd, 5);
				DesktopEmbedService.ReapplyProtection(hwnd);
			}
			catch
			{
			}
			return IntPtr.Zero;
		}
		if (msg == 33)
		{
			handled = true;
			return 3;
		}
		return IntPtr.Zero;
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/views/desktopsearchwidget.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocater);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			RootBorder = (Border)target;
			RootBorder.PreviewMouseLeftButtonDown += RootBorder_PreviewMouseLeftButtonDown;
			RootBorder.PreviewMouseMove += RootBorder_PreviewMouseMove;
			RootBorder.PreviewMouseLeftButtonUp += RootBorder_PreviewMouseLeftButtonUp;
			break;
		case 2:
			BgLayer = (Border)target;
			break;
		case 3:
			DragGrip = (Border)target;
			break;
		case 4:
			SearchButton = (Button)target;
			SearchButton.Click += SearchButton_Click;
			break;
		case 5:
			SearchTextBox = (TextBox)target;
			SearchTextBox.TextChanged += SearchTextBox_TextChanged;
			SearchTextBox.KeyDown += SearchTextBox_KeyDown;
			SearchTextBox.GotFocus += SearchTextBox_GotFocus;
			SearchTextBox.LostKeyboardFocus += SearchTextBox_LostKeyboardFocus;
			break;
		case 6:
			Placeholder = (TextBlock)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
