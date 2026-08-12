using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Forms;
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

public class TodoWindow : Window, IComponentConnector, IStyleConnector
{
	private readonly TodoViewModel _viewModel;

	private nint _hwnd;

	private int _lastDragPhysX;

	private int _lastDragPhysY;

	private bool _inNativeMove;

	private int _resizeHit;

	private Point _dragStart;

	private Rect _dragStartRect;

	private bool _suppressPositionSave;

	private bool _isInitializing = true;

	private const int ResizeBorder = 6;

	private bool _isEditing;

	private bool _userClosing;

	private int _savedExStyle;

	private DispatcherTimer? _snapTimer;

	private DispatcherTimer? _buttonHideTimer;

	private bool _contextMenuOpen;

	private ContextMenu? _openContextMenu;

	private double _savedCollapsedHeight;

	private const double CollapsedHeight = 52.0;

	private DispatcherTimer? _hoverCollapseWatcher;

	private TodoItem? _draggingTodo;

	private FrameworkElement? _dragSource;

	private Point _todoDragStartPoint;

	private bool _todoDragActive;

	private Border? _todoDragGhost;

	private DateTime _lastTodoClickTime;

	private TodoItem? _lastClickedTodo;

	private ListCollectionView? _todoListView;

	private readonly DispatcherTimer _refreshDebounce = new DispatcherTimer
	{
		Interval = TimeSpan.FromMilliseconds(40.0)
	};

	private double _lastScrollOffset;

	private DesktopDragHost _dragHost;

	private TodoItem? _reminderTargetTodo;

	private DateTime? _reminderDate;

	private bool _reminderTimeSync;

	private TodoItem? _dateTargetTodo;

	private string? _customDateTarget;

	private bool _datePickerForReminder;

	private int _pickerYear;

	private int _pickerMonth;

	private DispatcherTimer? _displayChangeTimer;

	private DateTime _displayTransitionUntil = DateTime.MinValue;

	private double _preTransitionLeft = double.NaN;

	private double _preTransitionTop = double.NaN;

	private static readonly bool _logEnabled = Environment.GetEnvironmentVariable("DESKTOOL_NOLOG") != "1";

	internal Grid RootGrid;

	internal StackPanel TitleButtonsPanel;

	internal TextBlock PlaceholderText;

	internal System.Windows.Controls.TextBox TodoInputBox;

	internal System.Windows.Controls.TextBox TodoSearchBox;

	internal TextBlock TodoSearchPlaceholder;

	internal TextBlock TxtCustomDateFrom;

	internal TextBlock TxtCustomDateTo;

	internal ScrollViewer TodoScrollViewer;

	internal ItemsControl TodoItemsControl;

	internal Border DatePickerOverlay;

	internal Border DatePickerPanel;

	internal StackPanel DatePickerContent;

	internal System.Windows.Controls.Button BtnPrevMonth;

	internal TextBlock TxtMonthYear;

	internal System.Windows.Controls.Button BtnNextMonth;

	internal UniformGrid DayGrid;

	internal Border ReminderPickerOverlay;

	internal TextBlock TxtReminderDate;

	internal System.Windows.Controls.TextBox TxtReminderTime;

	internal System.Windows.Controls.ListBox LstReminderHour;

	internal System.Windows.Controls.ListBox LstReminderMinute;

	internal Canvas DragOverlay;

	private bool _contentLoaded;

	public TodoViewModel ViewModel => _viewModel;

	internal bool IsDesktopChild { get; private set; }

	public TodoWindow(ObservableCollection<TodoItem> todoItems, MainViewModel mainVm)
	{
		InitializeComponent();
		_viewModel = new TodoViewModel(todoItems, mainVm);
		base.DataContext = _viewModel;
		ReminderPickerOverlay.IsVisibleChanged += delegate
		{
			if (_hwnd != IntPtr.Zero)
			{
				int windowLong = NativeMethods.GetWindowLong(_hwnd, -20);
				if (ReminderPickerOverlay.IsVisible)
				{
					NativeMethods.SetWindowLong(_hwnd, -20, windowLong & -134217729);
				}
				else
				{
					NativeMethods.SetWindowLong(_hwnd, -20, windowLong | 0x8000000);
				}
			}
		};
		mainVm.TodoResorted += delegate
		{
			_viewModel.RefreshSortView();
		};
		mainVm.TodoViewRefreshed += delegate
		{
			_viewModel.ApplyFilter();
		};
		SetupTodoGroupedView();
		base.Left = _viewModel.WindowLeft;
		base.Top = _viewModel.WindowTop;
		base.Width = _viewModel.TodoWidth;
		base.Height = _viewModel.TodoHeight;
		if (!DesktopEmbedService.IsOnAnyScreen(base.Left, base.Top, base.Width, base.Height))
		{
			(double Left, double Top) primaryScreenCenter = DesktopEmbedService.GetPrimaryScreenCenter(base.Width, base.Height);
			double cx = primaryScreenCenter.Left;
			double cy = primaryScreenCenter.Top;
			base.Left = cx;
			base.Top = cy;
			_viewModel.WindowLeft = cx;
			_viewModel.WindowTop = cy;
			_viewModel.SaveWindowState();
		}
		base.LocationChanged += delegate
		{
			if (!DesktopEmbedService.IsAppExiting && !_isInitializing && !(DateTime.Now < _displayTransitionUntil) && !_viewModel.IsLocked && !_suppressPositionSave)
			{
				double left = base.Left;
				double top = base.Top;
				ClampPositionToOwner(ref left, ref top);
				if (left != base.Left || top != base.Top)
				{
					DragLog($"[LocationChanged-自愈合] hwnd=0x{_hwnd:X8} 离屏({base.Left:F0},{base.Top:F0})→夹取({left:F0},{top:F0})");
					base.Left = left;
					base.Top = top;
				}
				_viewModel.WindowLeft = left;
				_viewModel.WindowTop = top;
			}
		};
		base.SizeChanged += OnSizeChanged;
		_viewModel.CollapsedChanged += OnCollapsedChanged;
		if (_viewModel.IsCollapsed)
		{
			ApplyCollapsedSize();
		}
		EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnContextMenuOpened));
		EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler(OnContextMenuClosed));
		base.Loaded += OnLoaded;
		base.Deactivated += TodoWindow_Deactivated;
		base.Closed += delegate
		{
			DebugLog("[Closed] TodoWindow 关闭 → SaveWindowState + 清理");
			DesktopEmbedService.UnregisterDesktopWindow(_hwnd);
			SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
			if (_hwnd != IntPtr.Zero)
			{
				WindowSnapService.Unregister(_hwnd);
			}
			WidgetAlignGlowService.Unregister(_hwnd);
			WidgetZOrderManager.Unregister(_hwnd);
			_snapTimer?.Stop();
			_hoverCollapseWatcher?.Stop();
			_hoverCollapseWatcher = null;
			if (!DesktopEmbedService.IsAppExiting)
			{
				_viewModel.SaveWindowState();
			}
		};
		base.StateChanged += delegate
		{
			if (base.WindowState != WindowState.Normal)
			{
				base.WindowState = WindowState.Normal;
			}
		};
	}

	private void SetupTodoGroupedView()
	{
		if (_viewModel?.FilteredTodos != null && FindName("TodoItemsControl") is ItemsControl itemsControl)
		{
			if (_todoListView == null)
			{
				_todoListView = new ListCollectionView(_viewModel.FilteredTodos);
				_todoListView.GroupDescriptions.Add(new PropertyGroupDescription("DateKey"));
				_viewModel.FilteredTodos.CollectionChanged -= OnFilteredTodosChanged;
				_viewModel.FilteredTodos.CollectionChanged += OnFilteredTodosChanged;
				_refreshDebounce.Tick -= RefreshDebounce_Tick;
				_refreshDebounce.Tick += RefreshDebounce_Tick;
			}
			itemsControl.ItemsSource = _todoListView;
		}
	}

	private void OnFilteredTodosChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		_lastScrollOffset = (FindName("TodoScrollViewer") as ScrollViewer)?.VerticalOffset ?? 0.0;
		_refreshDebounce.Stop();
		_refreshDebounce.Start();
	}

	private void RefreshDebounce_Tick(object? sender, EventArgs e)
	{
		_refreshDebounce.Stop();
		if (_todoListView != null)
		{
			ScrollViewer obj = FindName("TodoScrollViewer") as ScrollViewer;
			_todoListView.Refresh();
			obj?.ScrollToVerticalOffset(_lastScrollOffset);
		}
	}

	private TodoItem? FindTodoItemAtPoint(Point windowPos)
	{
		if (_todoListView == null)
		{
			return null;
		}
		if (!(FindName("TodoItemsControl") is ItemsControl ic))
		{
			return null;
		}
		TodoItem nearest = null;
		double bestDist = double.MaxValue;
		foreach (object item in (IEnumerable)_todoListView)
		{
			if (item is TodoItem todo && ic.ItemContainerGenerator.ContainerFromItem(todo) is Visual container)
			{
				Rect bounds = VisualTreeHelper.GetDescendantBounds(container);
				Point topLeft = container.TransformToAncestor(this).Transform(bounds.TopLeft);
				if (new Rect(topLeft, bounds.Size).Contains(windowPos))
				{
					return todo;
				}
				double d = Math.Abs(topLeft.Y + bounds.Height / 2.0 - windowPos.Y);
				if (d < bestDist)
				{
					bestDist = d;
					nearest = todo;
				}
			}
		}
		return nearest;
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		_hwnd = new WindowInteropHelper(this).Handle;
		HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
		WidgetZOrderManager.Register(_hwnd, topLayer: true);
		_viewModel.PropertyChanged += delegate(object? s, PropertyChangedEventArgs ev)
		{
			if (ev.PropertyName == "IsHoverExpanded")
			{
				WidgetZOrderManager.OnHoverExpandedChanged(_hwnd, _viewModel.IsHoverExpanded);
			}
		};
		IsDesktopChild = false;
		if (source != null)
		{
			source.CompositionTarget.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
		}
		_savedExStyle = NativeMethods.GetWindowLong(_hwnd, -20);
		DesktopWidgetInitializer.Register(_hwnd, source, DesktopWidgetInitializer.Default);
		if (Environment.OSVersion.Version.Build < 22000)
		{
			if (_viewModel.MainVm.IsGlassStyle)
			{
				GlassEffectHelper.EnableBlur(_hwnd, BlurTintAlpha());
			}
		}
		else if (_viewModel.MainVm.IsGlassStyle)
		{
			GlassEffectHelper.EnableAcrylicWithTint(_hwnd);
		}
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
		source?.AddHook(WndProc);
		WindowSnapService.Register(_hwnd, () => NativeDragHelper.GetDipRect(_hwnd));
		WidgetAlignGlowService.Register(_hwnd, this);
		_snapTimer = new DispatcherTimer(DispatcherPriority.Background, base.Dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(300.0)
		};
		_snapTimer.Tick += delegate
		{
			_snapTimer.Stop();
			if (_hwnd != IntPtr.Zero && !_inNativeMove)
			{
				WindowSnapService.LogDiagnose(_hwnd);
				WindowSnapService.TryDockAlign(_hwnd, this);
			}
			WidgetAlignGlowService.ClearGlows();
		};
		if (!IsDesktopChild)
		{
			base.Dispatcher.BeginInvoke(new Action(PushToBottom), DispatcherPriority.Loaded);
		}
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			if (!(SystemParameters.VirtualScreenWidth < 100.0) && !(SystemParameters.VirtualScreenHeight < 100.0))
			{
				double width = ((_viewModel.TodoWidth > 100.0) ? _viewModel.TodoWidth : base.Width);
				double height = ((_viewModel.TodoHeight > 100.0) ? _viewModel.TodoHeight : base.Height);
				if (!IsOnAnyScreen(base.Left, base.Top, width, height))
				{
					RecoverToPrimaryScreen();
				}
			}
		}, DispatcherPriority.Background);
		SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			_isInitializing = false;
		}, DispatcherPriority.Background);
	}

	public void PushToBottom()
	{
		_ = _hwnd;
		_ = IntPtr.Zero;
	}

	internal void BringToTop()
	{
		if (_hwnd != IntPtr.Zero)
		{
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			AppPanelTracker.PlaceDesktopWindow(_hwnd, band);
		}
	}

	internal void ForceTopMost()
	{
		if (_hwnd != IntPtr.Zero)
		{
			NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, 19u);
		}
	}

	private void PullToTop()
	{
		if (_hwnd != IntPtr.Zero)
		{
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			AppPanelTracker.PlaceDesktopWindow(_hwnd, band);
		}
	}

	private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (!e.Handled && !_viewModel.IsLocked && (!(e.OriginalSource is DependencyObject child) || FindVisualParent<System.Windows.Controls.Button>(child) == null))
		{
			StartNativeDrag();
			e.Handled = true;
		}
	}

	private void Header_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (!_viewModel.IsLocked && (!(e.OriginalSource is DependencyObject child) || FindVisualParent<System.Windows.Controls.Button>(child) == null))
		{
			StartNativeDrag();
			e.Handled = true;
		}
	}

	private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
	{
		while (child != null)
		{
			if (child is T r)
			{
				return r;
			}
			child = VisualTreeHelper.GetParent(child);
		}
		return null;
	}

	private void StartNativeDrag()
	{
		NativeDragHelper.Begin(_dragHost, 0);
	}

	private void SetPosition(int x, int y, int w, int h)
	{
		_lastDragPhysX = x;
		_lastDragPhysY = y;
		uint flags = 16u;
		if (w <= 0 || h <= 0)
		{
			flags |= 1;
		}
		nint insertAfter = (DesktopEmbedService.IsAnyDragActive ? NativeMethods.HWND_TOP : ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM));
		flags |= 0x4000;
		NativeMethods.SetWindowPos(_hwnd, insertAfter, x, y, w, h, flags);
	}

	private double GetMonitorDpi()
	{
		try
		{
			nint mon = NativeMethods.MonitorFromWindow(_hwnd, 2u);
			if (mon == IntPtr.Zero)
			{
				return 1.0;
			}
			NativeMethods.GetDpiForMonitor(mon, 0, out var dpiX, out var _);
			return (double)dpiX / 96.0;
		}
		catch
		{
			return 1.0;
		}
	}

	private void OnDragCommitted(int resizeHit)
	{
		bool isRealChild = false;
		var (parentLeft, parentTop) = DesktopEmbedService.GetOwnerOrigin(_hwnd);
		if (resizeHit != 0)
		{
			if (WindowSnapService.TryResizeSnap(currentBounds: new Rect(base.Left, base.Top, base.Width, base.Height), selfHwnd: _hwnd, draggedEdges: WindowSnapService.HitToEdgeMask(resizeHit), minWidth: base.MinWidth, minHeight: base.MinHeight, adjusted: out var snapped))
			{
				base.Left = snapped.X;
				base.Top = snapped.Y;
				base.Width = snapped.Width;
				base.Height = snapped.Height;
			}
			double fx = base.Left;
			double fy = base.Top;
			ClampPositionToOwner(ref fx, ref fy);
			_viewModel.WindowLeft = fx;
			_viewModel.WindowTop = fy;
			_viewModel.TodoWidth = base.Width;
			if (_viewModel.IsCollapsed && !_viewModel.IsHoverExpanded)
			{
				ApplyCollapsedSize();
			}
			else
			{
				_savedCollapsedHeight = base.Height;
				_viewModel.TodoHeight = base.Height;
			}
			_viewModel.SaveWindowState();
			return;
		}
		if (isRealChild)
		{
			NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, _lastDragPhysX, _lastDragPhysY, 0, 0, 21u);
		}
		else
		{
			try
			{
				int endX = _lastDragPhysX;
				int endY = _lastDragPhysY;
				SyncDragLogical(endX, endY, GetMonitorDpi());
			}
			catch
			{
			}
			WindowSnapService.TryDockAlign(_hwnd, this);
		}
		if (isRealChild)
		{
			double dpi = GetMonitorDpi();
			double prX = (double)_lastDragPhysX / dpi;
			double prY = (double)_lastDragPhysY / dpi;
			bool prevSuppress = _suppressPositionSave;
			_suppressPositionSave = true;
			base.Left = prX;
			base.Top = prY;
			_suppressPositionSave = prevSuppress;
			if (WindowSnapService.TryDockAlign(selfBounds: new Rect(prX, prY, base.Width, base.Height), selfHwnd: _hwnd, adjusted: out var adjDock))
			{
				int adjPhysX = (int)(adjDock.Left * dpi);
				int adjPhysY = (int)(adjDock.Top * dpi);
				NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, adjPhysX, adjPhysY, 0, 0, 21u);
				_suppressPositionSave = true;
				base.Left = adjDock.Left;
				base.Top = adjDock.Top;
				_suppressPositionSave = false;
			}
		}
		PullToTop();
		try
		{
			if (isRealChild)
			{
				NativeMethods.GetWindowRect(_hwnd, out var wr);
				double releaseDpi = GetMonitorDpi();
				double screenDipX = (double)wr.Left / releaseDpi;
				double screenDipY = (double)wr.Top / releaseDpi;
				double parentRelX = (double)(wr.Left - parentLeft) / releaseDpi;
				double parentRelY = (double)(wr.Top - parentTop) / releaseDpi;
				bool prevSuppress2 = _suppressPositionSave;
				_suppressPositionSave = true;
				base.Left = parentRelX;
				base.Top = parentRelY;
				_suppressPositionSave = prevSuppress2;
				_viewModel.WindowLeft = screenDipX;
				_viewModel.WindowTop = screenDipY;
			}
			else
			{
				double fx2 = base.Left;
				double fy2 = base.Top;
				ClampPositionToOwner(ref fx2, ref fy2);
				_viewModel.WindowLeft = fx2;
				_viewModel.WindowTop = fy2;
				base.Left = fx2;
				base.Top = fy2;
			}
			try
			{
				_viewModel.SaveWindowState();
			}
			catch
			{
			}
			DragLog($"[拖拽结束] hwnd=0x{_hwnd:X8} pos=({base.Left:F0},{base.Top:F0}) isRealChild={isRealChild}");
		}
		catch
		{
		}
	}

	private void ManualDragMove()
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		NativeMethods.GetCursorPos(out var startCursor);
		NativeMethods.GetWindowRect(_hwnd, out var startRect);
		nint owner = DesktopEmbedService.DesktopOwner;
		bool isRealChild = false;
		int parentLeft = 0;
		int parentTop = 0;
		if (isRealChild && owner != IntPtr.Zero)
		{
			NativeMethods.GetWindowRect(owner, out var pr);
			parentLeft = pr.Left;
			parentTop = pr.Top;
		}
		_suppressPositionSave = true;
		BringToTop();
		Mouse.Capture(this, CaptureMode.Element);
		System.Windows.Input.MouseEventHandler onMove = null;
		MouseButtonEventHandler onUp = null;
		int moveCount = 0;
		base.LostMouseCapture += OnDragLostCapture;
		onMove = delegate
		{
			NativeMethods.GetCursorPos(out var lpPoint);
			int num = startRect.Left + (lpPoint.X - startCursor.X);
			int num2 = startRect.Top + (lpPoint.Y - startCursor.Y);
			if (isRealChild)
			{
				int x = num - parentLeft;
				int y = num2 - parentTop;
				NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, x, y, 0, 0, 21u);
			}
			else
			{
				NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, num, num2, 0, 0, 21u);
			}
			if (++moveCount % 20 == 0)
			{
				NativeMethods.GetWindowRect(_hwnd, out var lpRect);
				DragLog($"[拖拽中#{moveCount}] cursor=({lpPoint.X},{lpPoint.Y}) target=({num},{num2}) actual=({lpRect.Left},{lpRect.Top})");
			}
		};
		onUp = delegate
		{
			Mouse.Capture(null);
			base.MouseMove -= onMove;
			base.MouseLeftButtonUp -= onUp;
			base.LostMouseCapture -= OnDragLostCapture;
			_inNativeMove = false;
			_suppressPositionSave = false;
			try
			{
				NativeMethods.GetWindowRect(_hwnd, out var lpRect);
				int physX = (isRealChild ? (lpRect.Left - parentLeft) : lpRect.Left);
				int physY = (isRealChild ? (lpRect.Top - parentTop) : lpRect.Top);
				SyncDragLogical(physX, physY);
			}
			catch
			{
			}
			WindowSnapService.TryDockAlign(_hwnd, this);
			PullToTop();
			try
			{
				NativeMethods.GetWindowRect(_hwnd, out var lpRect2);
				double num = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
				if (num <= 0.0)
				{
					num = 1.0;
				}
				double left = (double)(isRealChild ? (lpRect2.Left - parentLeft) : lpRect2.Left) / num;
				double top = (double)(isRealChild ? (lpRect2.Top - parentTop) : lpRect2.Top) / num;
				ClampPositionToOwner(ref left, ref top);
				base.Left = left;
				base.Top = top;
				_viewModel.WindowLeft = left;
				_viewModel.WindowTop = top;
				DragLog($"[拖拽结束-Manual] hwnd=0x{_hwnd:X8} finalRect=({lpRect2.Left},{lpRect2.Top},{lpRect2.Right},{lpRect2.Bottom}) savedLeft={left:F0} savedTop={top:F0} isRealChild={isRealChild}");
			}
			catch
			{
			}
		};
		base.MouseMove += onMove;
		base.MouseLeftButtonUp += onUp;
		void OnDragLostCapture(object sender, RoutedEventArgs args)
		{
			base.MouseMove -= onMove;
			base.MouseLeftButtonUp -= onUp;
			base.LostMouseCapture -= OnDragLostCapture;
			_suppressPositionSave = false;
		}
	}

	private static void DragLog(string msg)
	{
		try
		{
			LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_drag.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	private void SyncDragLogical(int physX, int physY, double? dpi = null)
	{
		double s = dpi ?? PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
		if (s <= 0.0)
		{
			s = 1.0;
		}
		base.Left = (double)physX / s;
		base.Top = (double)physY / s;
	}

	private void ClampPositionToOwner(ref double left, ref double top)
	{
		double dpi = GetMonitorDpi();
		(int, int) ownerOrigin = DesktopEmbedService.GetOwnerOrigin(_hwnd);
		if (ownerOrigin.Item1 != 0 || ownerOrigin.Item2 != 0)
		{
			try
			{
				double num = (double)ownerOrigin.Item1 + left * dpi;
				double screenTop = (double)ownerOrigin.Item2 + top * dpi;
				double screenCenterX = num + base.Width * dpi / 2.0;
				double screenCenterY = screenTop + base.Height * dpi / 2.0;
				nint mon = NativeMethods.MonitorFromPoint(new NativeMethods.POINT
				{
					X = (int)Math.Round(screenCenterX),
					Y = (int)Math.Round(screenCenterY)
				}, 2u);
				if (mon != IntPtr.Zero)
				{
					NativeMethods.MONITORINFO mi = new NativeMethods.MONITORINFO
					{
						cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
					};
					if (NativeMethods.GetMonitorInfo(mon, ref mi))
					{
						double margin = 40.0;
						double workLeft = (double)(mi.rcWork.Left - ownerOrigin.Item1) / dpi;
						double num2 = (double)(mi.rcWork.Top - ownerOrigin.Item2) / dpi;
						double num3 = (double)(mi.rcWork.Right - ownerOrigin.Item1) / dpi;
						double workBottom = (double)(mi.rcWork.Bottom - ownerOrigin.Item2) / dpi;
						double minL = workLeft - (base.Width - margin);
						double maxL = num3 - margin;
						double minT = num2 - (base.Height - margin);
						double maxT = workBottom - margin;
						if (minL <= maxL)
						{
							left = Math.Max(minL, Math.Min(maxL, left));
						}
						if (minT <= maxT)
						{
							top = Math.Max(minT, Math.Min(maxT, top));
						}
					}
				}
				return;
			}
			catch
			{
				return;
			}
		}
		try
		{
			nint mon2 = NativeMethods.MonitorFromWindow(_hwnd, 2u);
			if (mon2 == IntPtr.Zero)
			{
				return;
			}
			NativeMethods.MONITORINFO mi2 = new NativeMethods.MONITORINFO
			{
				cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
			};
			if (NativeMethods.GetMonitorInfo(mon2, ref mi2))
			{
				double margin2 = 40.0;
				double workLeft2 = (double)mi2.rcWork.Left / dpi;
				double num4 = (double)mi2.rcWork.Top / dpi;
				double num5 = (double)mi2.rcWork.Right / dpi;
				double workBottom2 = (double)mi2.rcWork.Bottom / dpi;
				double minL2 = workLeft2 - (base.Width - margin2);
				double maxL2 = num5 - margin2;
				double minT2 = num4 - (base.Height - margin2);
				double maxT2 = workBottom2 - margin2;
				if (minL2 <= maxL2)
				{
					left = Math.Max(minL2, Math.Min(maxL2, left));
				}
				if (minT2 <= maxT2)
				{
					top = Math.Max(minT2, Math.Min(maxT2, top));
				}
			}
		}
		catch
		{
		}
	}

	protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
	{
		base.OnMouseMove(e);
		if (!_todoDragActive && _draggingTodo == null)
		{
			Point pos = e.GetPosition(this);
			bool right = pos.X >= base.Width - 6.0;
			bool left = pos.X <= 6.0;
			bool bottom = pos.Y >= base.Height - 6.0;
			bool top = pos.Y <= 6.0;
			if (right && bottom)
			{
				base.Cursor = System.Windows.Input.Cursors.SizeNWSE;
			}
			else if (left && top)
			{
				base.Cursor = System.Windows.Input.Cursors.SizeNWSE;
			}
			else if (right && top)
			{
				base.Cursor = System.Windows.Input.Cursors.SizeNESW;
			}
			else if (left && bottom)
			{
				base.Cursor = System.Windows.Input.Cursors.SizeNESW;
			}
			else if (right || left)
			{
				base.Cursor = System.Windows.Input.Cursors.SizeWE;
			}
			else if (bottom || top)
			{
				base.Cursor = System.Windows.Input.Cursors.SizeNS;
			}
			else
			{
				base.Cursor = System.Windows.Input.Cursors.Arrow;
			}
		}
	}

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonDown(e);
		if (_draggingTodo != null || e.Handled)
		{
			return;
		}
		Point pos = e.GetPosition(this);
		bool right = pos.X >= base.Width - 6.0;
		bool left = pos.X <= 6.0;
		bool bottom = pos.Y >= base.Height - 6.0;
		bool top = pos.Y <= 6.0;
		int hit;
		if (right && bottom)
		{
			hit = 17;
		}
		else if (left && top)
		{
			hit = 13;
		}
		else if (right && top)
		{
			hit = 14;
		}
		else if (left && bottom)
		{
			hit = 16;
		}
		else if (right)
		{
			hit = 11;
		}
		else if (left)
		{
			hit = 10;
		}
		else if (bottom)
		{
			hit = 15;
		}
		else
		{
			if (!top)
			{
				return;
			}
			hit = 12;
		}
		NativeDragHelper.Begin(_dragHost, hit);
		e.Handled = true;
	}

	protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonUp(e);
	}

	private void ApplyManualResize()
	{
		Point p = GetCursorPosScreen();
		double dx = p.X - _dragStart.X;
		double dy = p.Y - _dragStart.Y;
		double nl = _dragStartRect.X;
		double nt = _dragStartRect.Y;
		double nw = _dragStartRect.Width;
		double nh = _dragStartRect.Height;
		switch (_resizeHit)
		{
		case 10:
			nl = _dragStartRect.Right + dx - _dragStartRect.Width;
			nw = _dragStartRect.Width - dx;
			break;
		case 11:
			nw = _dragStartRect.Width + dx;
			break;
		case 12:
			nt = _dragStartRect.Bottom + dy - _dragStartRect.Height;
			nh = _dragStartRect.Height - dy;
			break;
		case 15:
			nh = _dragStartRect.Height + dy;
			break;
		case 13:
			nl = _dragStartRect.Right + dx - _dragStartRect.Width;
			nw = _dragStartRect.Width - dx;
			nt = _dragStartRect.Bottom + dy - _dragStartRect.Height;
			nh = _dragStartRect.Height - dy;
			break;
		case 14:
			nw = _dragStartRect.Width + dx;
			nt = _dragStartRect.Bottom + dy - _dragStartRect.Height;
			nh = _dragStartRect.Height - dy;
			break;
		case 16:
			nl = _dragStartRect.Right + dx - _dragStartRect.Width;
			nw = _dragStartRect.Width - dx;
			nh = _dragStartRect.Height + dy;
			break;
		case 17:
			nw = _dragStartRect.Width + dx;
			nh = _dragStartRect.Height + dy;
			break;
		}
		if (nw < base.MinWidth)
		{
			nw = base.MinWidth;
			nl = _dragStartRect.Right - base.MinWidth;
		}
		if (nh < base.MinHeight)
		{
			nh = base.MinHeight;
			nt = _dragStartRect.Bottom - base.MinHeight;
		}
		base.Left = nl;
		base.Top = nt;
		base.Width = nw;
		base.Height = nh;
		base.Cursor = System.Windows.Input.Cursors.Arrow;
	}

	private static WindowSnapService.EdgeMask HitToEdgeMask(int resizeHit)
	{
		return resizeHit switch
		{
			10 => WindowSnapService.EdgeMask.Left, 
			11 => WindowSnapService.EdgeMask.Right, 
			12 => WindowSnapService.EdgeMask.Top, 
			15 => WindowSnapService.EdgeMask.Bottom, 
			13 => WindowSnapService.EdgeMask.Left | WindowSnapService.EdgeMask.Top, 
			14 => WindowSnapService.EdgeMask.Top | WindowSnapService.EdgeMask.Right, 
			16 => WindowSnapService.EdgeMask.Left | WindowSnapService.EdgeMask.Bottom, 
			17 => WindowSnapService.EdgeMask.Right | WindowSnapService.EdgeMask.Bottom, 
			_ => WindowSnapService.EdgeMask.None, 
		};
	}

	private Point GetCursorPosScreen()
	{
		NativeMethods.GetCursorPos(out var pt);
		return new Point(pt.X, pt.Y);
	}

	private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
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
		if (msg == 2)
		{
			if (_userClosing)
			{
				return IntPtr.Zero;
			}
			handled = true;
			return IntPtr.Zero;
		}
		LogWndProc(msg, wParam, lParam);
		if (msg == 532)
		{
			WidgetAlignGlowService.OnSizing(_hwnd, wParam, lParam);
		}
		if (msg == 24 && wParam == IntPtr.Zero)
		{
			handled = true;
			DesktopEmbedService.ReinforceProtectionLightweight(hwnd);
			return IntPtr.Zero;
		}
		if (msg == 5 && ((IntPtr)wParam).ToInt32() == 1)
		{
			NativeMethods.ShowWindow(hwnd, 4);
			handled = true;
			return IntPtr.Zero;
		}
		if (msg == 274 && (((IntPtr)wParam).ToInt32() & 0xFFF0) == 61472)
		{
			handled = true;
			return IntPtr.Zero;
		}
		if (msg == 70)
		{
			NativeMethods.WINDOWPOS wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
			if ((wp.flags & 0x80) != 0)
			{
				DebugLog($"[TodoWin] ✅ SWP_HIDEWINDOW已拦截 hwnd=0x{hwnd:X8} flags=0x{wp.flags:X} → ShowWindow");
				wp.flags &= 4294967167u;
				wp.flags |= 19u;
				Marshal.StructureToPtr(wp, lParam, fDeleteOld: false);
				handled = true;
				NativeMethods.ShowWindow(hwnd, 4);
				DesktopEmbedService.ReinforceProtectionLightweight(hwnd);
				return IntPtr.Zero;
			}
		}
		if (NativeMethods.WM_DESKTOOL_WIND != 0 && msg == NativeMethods.WM_DESKTOOL_WIND)
		{
			DebugLog("[TodoWin] 收到恢复消息 → UI线程窗口恢复");
			ExecuteWindowRecovery(hwnd);
		}
		if (msg == 562)
		{
			handled = false;
			WidgetAlignGlowService.OnExitSizeMove(_hwnd);
			base.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate
			{
				if (_hwnd != IntPtr.Zero)
				{
					NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 311u);
				}
			});
		}
		if (msg == 28)
		{
			_ = IntPtr.Zero;
		}
		if (msg == 33)
		{
			handled = true;
			return _isEditing ? 1 : 3;
		}
		return IntPtr.Zero;
	}

	private void ExecuteWindowRecovery(nint hwnd)
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
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			NativeMethods.SetWindowPos(hwnd, band, 0, 0, 0, 0, 83u);
			DesktopEmbedService.ReapplyProtection(hwnd);
			nint handle = hwnd;
			base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				NativeMethods.SetWindowPos(handle, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0, 19u);
			});
			DebugLog($"[ExecuteWindowRecovery] 窗口 0x{hwnd:X8} 恢复完成");
		}
		catch (Exception ex)
		{
			DebugLog("[ExecuteWindowRecovery] 异常: " + ex.Message);
		}
	}

	private void ActivateWindowForEdit()
	{
		if (_hwnd != IntPtr.Zero)
		{
			NativeMethods.SetWindowLong(_hwnd, -20, _savedExStyle);
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			NativeMethods.SetWindowPos(_hwnd, band, 0, 0, 0, 0, 99u);
			NativeMethods.SetForegroundWindow(_hwnd);
		}
	}

	private void DeactivateWindowAfterEdit()
	{
		if (_hwnd != IntPtr.Zero)
		{
			NativeMethods.SetWindowLong(_hwnd, -20, _savedExStyle | 0x8000000);
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			NativeMethods.SetWindowPos(_hwnd, band, 0, 0, 0, 0, 19u);
		}
	}

	private void TodoInput_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (!_isEditing)
		{
			_isEditing = true;
			ActivateWindowForEdit();
		}
	}

	private void TodoInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (!IsOwnEditableTextBox(e.NewFocus as DependencyObject))
		{
			if (_isEditing)
			{
				_isEditing = false;
				DeactivateWindowAfterEdit();
			}
			if (string.IsNullOrEmpty(TodoInputBox.Text))
			{
				PlaceholderText.Visibility = Visibility.Visible;
			}
		}
	}

	private void TodoInput_GotFocus(object sender, RoutedEventArgs e)
	{
		PlaceholderText.Visibility = Visibility.Collapsed;
	}

	private void TodoSearch_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (!_isEditing)
		{
			_isEditing = true;
			ActivateWindowForEdit();
		}
	}

	private void TodoSearch_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (sender is System.Windows.Controls.TextBox box && FindName("TodoSearchPlaceholder") is TextBlock placeholder)
		{
			placeholder.Visibility = ((!string.IsNullOrEmpty(box.Text)) ? Visibility.Collapsed : Visibility.Visible);
		}
	}

	private void TodoSearch_GotFocus(object sender, RoutedEventArgs e)
	{
		if (!_isEditing)
		{
			_isEditing = true;
			ActivateWindowForEdit();
		}
	}

	private void TodoSearch_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (!IsOwnEditableTextBox(e.NewFocus as DependencyObject) && _isEditing)
		{
			_isEditing = false;
			DeactivateWindowAfterEdit();
		}
	}

	private static bool IsOwnEditableTextBox(DependencyObject? obj)
	{
		System.Windows.Controls.TextBox tb = obj as System.Windows.Controls.TextBox;
		bool flag = tb != null;
		if (flag)
		{
			bool flag2;
			switch (tb.Name)
			{
			case "TodoInputBox":
			case "TodoSearchBox":
			case "TodoContentTextBox":
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			flag = flag2;
		}
		return flag;
	}

	private void TodoInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key != Key.Return && e.Key != Key.Return)
		{
			return;
		}
		if (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == ModifierKeys.Shift || Keyboard.Modifiers == ModifierKeys.Alt)
		{
			if (sender is System.Windows.Controls.TextBox { CaretIndex: var caret } box)
			{
				box.Text = box.Text.Insert(caret, "\r\n");
				box.CaretIndex = caret + 2;
				AdjustTodoInputHeight();
			}
			e.Handled = true;
		}
		else
		{
			_viewModel.AddTodoCommand.Execute(null);
			Keyboard.ClearFocus();
			e.Handled = true;
		}
	}

	private void TodoInput_TextChanged(object sender, TextChangedEventArgs e)
	{
		PlaceholderText.Visibility = ((!string.IsNullOrEmpty(TodoInputBox.Text)) ? Visibility.Collapsed : Visibility.Visible);
		AdjustTodoInputHeight();
	}

	private void AdjustTodoInputHeight()
	{
		if (TodoInputBox != null)
		{
			double lineHeight = TodoInputBox.FontSize * 1.4;
			int visibleLines = Math.Min(Math.Max(TodoInputBox.Text.Count((char c) => c == '\n') + 1, 1), 6);
			TodoInputBox.Height = (double)visibleLines * lineHeight + 16.0;
		}
	}

	private void AddTodoButton_Click(object sender, RoutedEventArgs e)
	{
		_viewModel.AddTodoCommand.Execute(null);
		Keyboard.ClearFocus();
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		_userClosing = true;
		Close();
	}

	private void ExportMenuButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { ContextMenu: not null } btn)
		{
			btn.ContextMenu.PlacementTarget = btn;
			btn.ContextMenu.IsOpen = true;
		}
	}

	private static TodoItem? GetTodoFromContextMenu(object sender)
	{
		if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: TodoItem todo } } })
		{
			return todo;
		}
		if (sender is MenuItem { Parent: MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: TodoItem ptodo } } } })
		{
			return ptodo;
		}
		return null;
	}

	private void ContextMenu_SetColor(object sender, RoutedEventArgs e)
	{
		TodoItem todo = GetTodoFromContextMenu(sender);
		if (todo != null)
		{
			string color = ((sender as MenuItem)?.Tag as string) ?? "";
			_viewModel.SetTodoColorCommand.Execute(new object[2] { todo, color });
		}
	}

	private void ContextMenu_EditTodo(object sender, RoutedEventArgs e)
	{
		TodoItem todo = GetTodoFromContextMenu(sender);
		if (todo == null)
		{
			return;
		}
		Border border = FindTodoItemBorder(todo);
		if (border != null)
		{
			Grid grid = FindVisualChildren<Grid>(border).FirstOrDefault();
			if (grid != null)
			{
				EnterTodoEditMode(grid);
			}
		}
	}

	private void ContextMenu_DeleteTodo(object sender, RoutedEventArgs e)
	{
		TodoItem todo = GetTodoFromContextMenu(sender);
		if (todo != null)
		{
			_viewModel.DeleteTodoCommand.Execute(todo);
		}
	}

	private void ContextMenu_SetReminder(object sender, RoutedEventArgs e)
	{
		TodoItem todo = GetTodoFromContextMenu(sender);
		if (todo != null)
		{
			_reminderTargetTodo = todo;
			DateTime baseTime = todo.ReminderTime ?? DateTime.Now.AddHours(1.0);
			_reminderDate = baseTime.Date;
			EnsureReminderTimeCombos();
			LstReminderHour.SelectedIndex = baseTime.Hour;
			LstReminderMinute.SelectedIndex = baseTime.Minute;
			UpdateReminderDateDisplay();
			ReminderPickerOverlay.Visibility = Visibility.Visible;
		}
	}

	private void EnsureReminderTimeCombos()
	{
		if (LstReminderHour.Items.Count == 0)
		{
			for (int h = 0; h < 24; h++)
			{
				LstReminderHour.Items.Add(h.ToString("D2"));
			}
		}
		if (LstReminderMinute.Items.Count == 0)
		{
			for (int m = 0; m < 60; m++)
			{
				LstReminderMinute.Items.Add(m.ToString("D2"));
			}
		}
	}

	private void TxtReminderTime_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_reminderTimeSync || !TryParseTime(TxtReminderTime.Text, out var hh, out var mm))
		{
			return;
		}
		_reminderTimeSync = true;
		try
		{
			hh = Math.Clamp(hh, 0, 23);
			mm = Math.Clamp(mm, 0, 59);
			if (LstReminderHour.SelectedIndex != hh)
			{
				LstReminderHour.SelectedIndex = hh;
			}
			if (LstReminderMinute.SelectedIndex != mm)
			{
				LstReminderMinute.SelectedIndex = mm;
			}
		}
		finally
		{
			_reminderTimeSync = false;
		}
	}

	private void LstReminderHour_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		SyncReminderTimeBoxFromLists();
	}

	private void LstReminderMinute_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		SyncReminderTimeBoxFromLists();
	}

	private void SyncReminderTimeBoxFromLists()
	{
		if (_reminderTimeSync)
		{
			return;
		}
		_reminderTimeSync = true;
		try
		{
			int hh = ((LstReminderHour.SelectedIndex >= 0) ? LstReminderHour.SelectedIndex : 0);
			int mm = ((LstReminderMinute.SelectedIndex >= 0) ? LstReminderMinute.SelectedIndex : 0);
			TxtReminderTime.Text = $"{hh:D2}:{mm:D2}";
		}
		finally
		{
			_reminderTimeSync = false;
		}
	}

	private static bool TryParseTime(string s, out int hh, out int mm)
	{
		hh = 0;
		mm = 0;
		if (string.IsNullOrWhiteSpace(s))
		{
			return false;
		}
		s = s.Replace("：", ":");
		string[] parts = s.Split(':');
		if (parts.Length == 2)
		{
			if (int.TryParse(parts[0], out hh))
			{
				return int.TryParse(parts[1], out mm);
			}
			return false;
		}
		if (parts.Length == 1 && s.Length >= 3 && s.Length <= 4 && int.TryParse(s, out var v))
		{
			hh = v / 100;
			mm = v % 100;
			if (hh >= 0 && hh <= 23 && mm >= 0)
			{
				return mm <= 59;
			}
			return false;
		}
		return false;
	}

	private void UpdateReminderDateDisplay()
	{
		TxtReminderDate.Text = _reminderDate?.ToString("yyyy-MM-dd") ?? "-";
		SyncReminderTimeBoxFromLists();
	}

	private void ReminderPicker_PickDate(object sender, RoutedEventArgs e)
	{
		OpenReminderDatePicker(_reminderDate ?? DateTime.Now);
	}

	private void OpenReminderDatePicker(DateTime initial)
	{
		_datePickerForReminder = true;
		_reminderDate = initial.Date;
		_pickerYear = initial.Year;
		_pickerMonth = initial.Month;
		BuildDayGrid();
		ReminderPickerOverlay.Visibility = Visibility.Collapsed;
		DatePickerOverlay.Visibility = Visibility.Visible;
	}

	private void ReminderPicker_Today(object sender, RoutedEventArgs e)
	{
		DateTime dt = DateTime.Today.AddHours(9.0);
		_reminderDate = dt.Date;
		EnsureReminderTimeCombos();
		LstReminderHour.SelectedIndex = dt.Hour;
		LstReminderMinute.SelectedIndex = dt.Minute;
		UpdateReminderDateDisplay();
	}

	private void ReminderPicker_Tomorrow(object sender, RoutedEventArgs e)
	{
		DateTime dt = DateTime.Today.AddDays(1.0).AddHours(9.0);
		_reminderDate = dt.Date;
		EnsureReminderTimeCombos();
		LstReminderHour.SelectedIndex = dt.Hour;
		LstReminderMinute.SelectedIndex = dt.Minute;
		UpdateReminderDateDisplay();
	}

	private void ReminderPicker_Clear(object sender, RoutedEventArgs e)
	{
		if (_reminderTargetTodo != null)
		{
			_reminderTargetTodo.ReminderEnabled = false;
			_reminderTargetTodo.ReminderTime = null;
			_reminderTargetTodo.LastRemindedAt = null;
			PersistTodo();
		}
		ReminderPickerOverlay.Visibility = Visibility.Collapsed;
	}

	private void ReminderPicker_OK(object sender, RoutedEventArgs e)
	{
		if (_reminderTargetTodo == null)
		{
			ReminderPickerOverlay.Visibility = Visibility.Collapsed;
			return;
		}
		if (!_reminderDate.HasValue)
		{
			_reminderDate = DateTime.Today;
		}
		int hh = LstReminderHour.SelectedIndex;
		int mm = LstReminderMinute.SelectedIndex;
		if (hh < 0 || hh > 23)
		{
			hh = 9;
		}
		if (mm < 0 || mm > 59)
		{
			mm = 0;
		}
		DateTime dt = _reminderDate.Value.Date.AddHours(hh).AddMinutes(mm);
		_reminderTargetTodo.ReminderEnabled = true;
		_reminderTargetTodo.ReminderTime = dt;
		_reminderTargetTodo.LastRemindedAt = null;
		PersistTodo();
		ReminderPickerOverlay.Visibility = Visibility.Collapsed;
	}

	private void ReminderPicker_Cancel(object sender, RoutedEventArgs e)
	{
		ReminderPickerOverlay.Visibility = Visibility.Collapsed;
	}

	private void ReminderOverlay_BackgroundClick(object sender, MouseButtonEventArgs e)
	{
		ReminderPickerOverlay.Visibility = Visibility.Collapsed;
	}

	private void TodoWindow_Deactivated(object? sender, EventArgs e)
	{
		if (ReminderPickerOverlay.Visibility == Visibility.Visible)
		{
			ReminderPickerOverlay.Visibility = Visibility.Collapsed;
		}
	}

	private void PersistTodo()
	{
		_viewModel.MainVm.SaveTodos();
		_viewModel.ApplyFilter();
	}

	private void EnterTodoEditMode(Grid grid)
	{
		FrameworkElement tb = grid.FindName("TodoContentTextBlock") as FrameworkElement;
		System.Windows.Controls.TextBox editBox = grid.FindName("TodoContentTextBox") as System.Windows.Controls.TextBox;
		if (tb != null && editBox != null)
		{
			tb.Measure(new Size(tb.ActualWidth, double.PositiveInfinity));
			double textBlockHeight = tb.DesiredSize.Height;
			editBox.MinHeight = Math.Max(24.0, textBlockHeight);
			tb.Visibility = Visibility.Collapsed;
			editBox.Visibility = Visibility.Visible;
			editBox.Focus();
			editBox.CaretIndex = editBox.Text.Length;
			if (!_isEditing)
			{
				_isEditing = true;
				ActivateWindowForEdit();
			}
		}
	}

	private Border? FindTodoItemBorder(TodoItem todo)
	{
		foreach (Border child in FindVisualChildren<Border>(this))
		{
			if (child.DataContext == todo)
			{
				return child;
			}
		}
		return null;
	}

	private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(parent, i);
			if (child is T t)
			{
				yield return t;
			}
			foreach (T item in FindVisualChildren<T>(child))
			{
				yield return item;
			}
		}
	}

	private void TodoItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.OriginalSource is DependencyObject src)
		{
			System.Windows.Controls.TextBox editingBox = FindVisualParent<System.Windows.Controls.TextBox>(src);
			if (editingBox != null && editingBox.Visibility == Visibility.Visible && editingBox.Name == "TodoContentTextBox")
			{
				return;
			}
		}
		if (sender is FrameworkElement { DataContext: TodoItem todo } fe)
		{
			_todoDragStartPoint = e.GetPosition(this);
			_draggingTodo = todo;
			_dragSource = fe;
			_todoDragActive = false;
		}
	}

	private void TodoItem_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (_draggingTodo == null || e.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}
		Point pos = e.GetPosition(this);
		double dx = pos.X - _todoDragStartPoint.X;
		double dy = pos.Y - _todoDragStartPoint.Y;
		if (!_todoDragActive)
		{
			if (Math.Abs(dx) < 6.0 && Math.Abs(dy) < 6.0)
			{
				return;
			}
			_todoDragActive = true;
			_dragSource?.CaptureMouse();
			if (_dragSource != null)
			{
				CreateTodoDragGhost(_dragSource);
			}
		}
		if (_todoDragGhost != null)
		{
			Canvas.SetLeft(_todoDragGhost, pos.X);
			Canvas.SetTop(_todoDragGhost, pos.Y - _todoDragGhost.ActualHeight / 2.0);
		}
	}

	private void TodoItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!_todoDragActive || _draggingTodo == null)
		{
			_dragSource?.ReleaseMouseCapture();
			DateTime now = DateTime.Now;
			TodoItem currentTodo = _draggingTodo;
			if (currentTodo != null && currentTodo == _lastClickedTodo && (now - _lastTodoClickTime).TotalMilliseconds < (double)SystemInformation.DoubleClickTime && _dragSource is Border border)
			{
				Grid contentGrid = FindTodoContentGrid(border);
				if (contentGrid != null)
				{
					_lastTodoClickTime = DateTime.MinValue;
					_lastClickedTodo = null;
					EnterTodoEditMode(contentGrid);
					_draggingTodo = null;
					_dragSource = null;
					_todoDragActive = false;
					DestroyTodoDragGhost();
					e.Handled = true;
					return;
				}
			}
			_lastTodoClickTime = now;
			_lastClickedTodo = currentTodo;
			_draggingTodo = null;
			_dragSource = null;
			_todoDragActive = false;
			DestroyTodoDragGhost();
			return;
		}
		_dragSource?.ReleaseMouseCapture();
		TodoItem dragged = _draggingTodo;
		_draggingTodo = null;
		_dragSource = null;
		_todoDragActive = false;
		DestroyTodoDragGhost();
		Point mousePos = e.GetPosition(this);
		HitTestResult hitResult = VisualTreeHelper.HitTest(this, mousePos);
		TodoItem target = null;
		if (hitResult != null && FindAncestorTodoBorder(hitResult.VisualHit)?.DataContext is TodoItem t1)
		{
			target = t1;
		}
		if (target == null)
		{
			target = FindTodoItemAtPoint(mousePos);
		}
		if (target != null && target != dragged)
		{
			if (target.DateRef.Date != dragged.DateRef.Date)
			{
				dragged.DueDate = target.DateRef.Date;
			}
			_viewModel.MoveTodo(dragged, target);
		}
	}

	private static Grid? FindTodoContentGrid(Border todoBorder)
	{
		foreach (Grid grid in FindVisualChildren<Grid>(todoBorder))
		{
			if (grid.FindName("TodoContentTextBox") != null)
			{
				return grid;
			}
		}
		return null;
	}

	private static Border? FindAncestorTodoBorder(DependencyObject child)
	{
		while (child != null)
		{
			if (child is Border border && border.DataContext is TodoItem)
			{
				return border;
			}
			child = VisualTreeHelper.GetParent(child);
		}
		return null;
	}

	private void CreateTodoDragGhost(FrameworkElement source)
	{
		DestroyTodoDragGhost();
		Point pos = source.TranslatePoint(new Point(0.0, 0.0), DragOverlay);
		_todoDragGhost = new Border
		{
			Width = source.ActualWidth,
			Height = source.ActualHeight,
			Background = new SolidColorBrush(Color.FromArgb(120, 74, 144, 217)),
			CornerRadius = new CornerRadius(6.0),
			Opacity = 0.9,
			IsHitTestVisible = false,
			Child = new TextBlock
			{
				Text = (_draggingTodo?.Content ?? ""),
				Foreground = Brushes.White,
				FontSize = _viewModel.TodoContentFontSize,
				TextWrapping = TextWrapping.Wrap,
				VerticalAlignment = VerticalAlignment.Center,
				Padding = new Thickness(8.0, 6.0, 8.0, 6.0)
			}
		};
		Canvas.SetLeft(_todoDragGhost, pos.X);
		Canvas.SetTop(_todoDragGhost, pos.Y);
		DragOverlay.Children.Add(_todoDragGhost);
	}

	private void DestroyTodoDragGhost()
	{
		if (_todoDragGhost != null)
		{
			DragOverlay.Children.Remove(_todoDragGhost);
			_todoDragGhost = null;
		}
	}

	private void TodoContent_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 2 && sender is Grid g)
		{
			e.Handled = true;
			EnterTodoEditMode(g);
		}
	}

	private void TodoCheckBox_Toggled(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.CheckBox { DataContext: TodoItem t } cb)
		{
			_viewModel.SetTodoCompleted(t, cb.IsChecked == true);
		}
	}

	private void TodoContentTextBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (!IsOwnEditableTextBox(e.NewFocus as DependencyObject) && _isEditing)
		{
			_isEditing = false;
			DeactivateWindowAfterEdit();
		}
		if (sender is System.Windows.Controls.TextBox editBox)
		{
			editBox.Visibility = Visibility.Collapsed;
			if ((editBox.Parent as Grid)?.FindName("TodoContentTextBlock") is FrameworkElement tb)
			{
				tb.Visibility = Visibility.Visible;
			}
		}
	}

	private void TodoContentTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			Keyboard.ClearFocus();
			e.Handled = true;
		}
		else
		{
			if (e.Key != Key.Return && e.Key != Key.Return)
			{
				return;
			}
			if (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == ModifierKeys.Shift || Keyboard.Modifiers == ModifierKeys.Alt)
			{
				if (sender is System.Windows.Controls.TextBox { CaretIndex: var caret } box)
				{
					box.Text = box.Text.Insert(caret, "\r\n");
					box.CaretIndex = caret + 2;
				}
				e.Handled = true;
			}
			else
			{
				Keyboard.ClearFocus();
				e.Handled = true;
			}
		}
	}

	private void CustomDateFrom_Click(object sender, RoutedEventArgs e)
	{
		_customDateTarget = "from";
		DateTime defaultDate = _viewModel.CustomDateFrom ?? DateTime.Today;
		_pickerYear = defaultDate.Year;
		_pickerMonth = defaultDate.Month;
		BuildDayGrid();
		DatePickerOverlay.Visibility = Visibility.Visible;
	}

	private void CustomDateTo_Click(object sender, RoutedEventArgs e)
	{
		_customDateTarget = "to";
		DateTime defaultDate = _viewModel.CustomDateTo ?? DateTime.Today;
		_pickerYear = defaultDate.Year;
		_pickerMonth = defaultDate.Month;
		BuildDayGrid();
		DatePickerOverlay.Visibility = Visibility.Visible;
	}

	private void ContextMenu_SetDate(object sender, RoutedEventArgs e)
	{
		TodoItem todo = GetTodoFromContextMenu(sender);
		DatePickerLog("[ContextMenu_SetDate] todo=" + ((todo != null) ? todo.Content : "NULL"));
		if (todo == null)
		{
			return;
		}
		TodoItem capturedTodo = todo;
		base.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)delegate
		{
			try
			{
				DatePickerLog("[ContextMenu_SetDate-Delayed] 准备打开内联日期选择器");
				ShowInlineDatePicker(capturedTodo);
			}
			catch (Exception ex)
			{
				DatePickerLog("[ContextMenu_SetDate-Delayed] ❌ 异常: " + ex.Message + "\n" + ex.StackTrace);
			}
		});
	}

	private void ShowInlineDatePicker(TodoItem todo)
	{
		_dateTargetTodo = todo;
		DateTime defaultDate = todo.DateRef;
		_pickerYear = defaultDate.Year;
		_pickerMonth = defaultDate.Month;
		DatePickerLog($"[ShowInlineDatePicker] target={defaultDate:yyyy-MM-dd}, year={_pickerYear}, month={_pickerMonth}, hasDueDate={todo.HasDueDate}");
		BuildDayGrid();
		DatePickerLog("[ShowInlineDatePicker] DayGrid 构建完成, 设置 Visibility=Visible");
		DatePickerOverlay.Visibility = Visibility.Visible;
		DatePickerLog("[ShowInlineDatePicker] Visibility 已设置");
	}

	private void HideInlineDatePicker()
	{
		DatePickerLog("[HideInlineDatePicker] 关闭");
		DatePickerOverlay.Visibility = Visibility.Collapsed;
		_dateTargetTodo = null;
		_customDateTarget = null;
		if (_datePickerForReminder)
		{
			_datePickerForReminder = false;
			ReminderPickerOverlay.Visibility = Visibility.Visible;
		}
	}

	private void BuildDayGrid()
	{
		DayGrid.Children.Clear();
		TxtMonthYear.Text = $"{_pickerYear}年{_pickerMonth}月";
		DateTime firstDay = new DateTime(_pickerYear, _pickerMonth, 1);
		int startDayOfWeek = (int)firstDay.DayOfWeek;
		int daysInMonth = DateTime.DaysInMonth(_pickerYear, _pickerMonth);
		DateTime today = DateTime.Today;
		DateTime currentDisplayDate = ((_datePickerForReminder && _reminderDate.HasValue) ? _reminderDate.Value : ((_customDateTarget == "from" && _viewModel.CustomDateFrom.HasValue) ? _viewModel.CustomDateFrom.Value : ((!(_customDateTarget == "to") || !_viewModel.CustomDateTo.HasValue) ? (_dateTargetTodo?.DateRef ?? today) : _viewModel.CustomDateTo.Value)));
		if (startDayOfWeek > 0)
		{
			DateTime prevMonth = firstDay.AddMonths(-1);
			int prevDays = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);
			for (int i = startDayOfWeek - 1; i >= 0; i--)
			{
				int day = prevDays - i;
				DayGrid.Children.Add(CreateDayButton(day, isCurrentMonth: false, isToday: false, isSelected: false, new DateTime(prevMonth.Year, prevMonth.Month, day)));
			}
		}
		for (int d = 1; d <= daysInMonth; d++)
		{
			DateTime date = new DateTime(_pickerYear, _pickerMonth, d);
			bool isToday = date == today;
			bool isSelected = date == currentDisplayDate.Date;
			DayGrid.Children.Add(CreateDayButton(d, isCurrentMonth: true, isToday, isSelected, date));
		}
		int remaining = 42 - DayGrid.Children.Count;
		DateTime nextMonth = firstDay.AddMonths(1);
		for (int j = 1; j <= remaining; j++)
		{
			DayGrid.Children.Add(CreateDayButton(j, isCurrentMonth: false, isToday: false, isSelected: false, new DateTime(nextMonth.Year, nextMonth.Month, j)));
		}
		DatePickerLog($"[BuildDayGrid] 共 {DayGrid.Children.Count} 个按钮");
	}

	private System.Windows.Controls.Button CreateDayButton(int day, bool isCurrentMonth, bool isToday, bool isSelected, DateTime date)
	{
		System.Windows.Controls.Button obj = new System.Windows.Controls.Button
		{
			Content = day.ToString(),
			Width = 28.0,
			Height = 28.0,
			FontSize = 12.0,
			Cursor = System.Windows.Input.Cursors.Hand,
			Tag = date,
			ToolTip = date.ToString("yyyy-MM-dd")
		};
		Color bg;
		Color fg;
		if (isSelected)
		{
			bg = Color.FromRgb(51, 153, byte.MaxValue);
			fg = Colors.White;
		}
		else if (isToday)
		{
			bg = Color.FromRgb(85, 85, 101);
			fg = Colors.White;
		}
		else if (isCurrentMonth)
		{
			bg = Color.FromRgb(58, 58, 68);
			fg = Colors.White;
		}
		else
		{
			bg = Color.FromRgb(42, 42, 50);
			fg = Color.FromRgb(102, 102, 119);
		}
		obj.Background = new SolidColorBrush(bg);
		obj.Foreground = new SolidColorBrush(fg);
		obj.BorderThickness = new Thickness(0.0);
		ControlTemplate template = new ControlTemplate(typeof(System.Windows.Controls.Button));
		FrameworkElementFactory borderFactory = new FrameworkElementFactory(typeof(Border));
		borderFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
		{
			RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
		});
		borderFactory.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
		{
			RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
		});
		borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4.0));
		borderFactory.SetValue(Border.PaddingProperty, new Thickness(2.0));
		FrameworkElementFactory contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
		contentPresenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
		contentPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		borderFactory.AppendChild(contentPresenter);
		template.VisualTree = borderFactory;
		Trigger hoverTrigger = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hoverTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(85, 85, 101))));
		template.Triggers.Add(hoverTrigger);
		obj.Template = template;
		obj.Click += DayButton_Click;
		return obj;
	}

	private void DayButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { Tag: var tag } && tag is DateTime selectedDate)
		{
			if (_datePickerForReminder)
			{
				_reminderDate = selectedDate;
				_datePickerForReminder = false;
				DatePickerOverlay.Visibility = Visibility.Collapsed;
				ReminderPickerOverlay.Visibility = Visibility.Visible;
				UpdateReminderDateDisplay();
				return;
			}
			if (_customDateTarget == "from")
			{
				_viewModel.CustomDateFrom = selectedDate;
				_viewModel.ApplyCustomDateRangeCommand.Execute(null);
				HideInlineDatePicker();
				return;
			}
			if (_customDateTarget == "to")
			{
				if (_viewModel.CustomDateFrom.HasValue && selectedDate < _viewModel.CustomDateFrom.Value)
				{
					_viewModel.CustomDateFrom = selectedDate;
				}
				_viewModel.CustomDateTo = selectedDate;
				_viewModel.ApplyCustomDateRangeCommand.Execute(null);
				HideInlineDatePicker();
				return;
			}
			if (_dateTargetTodo != null)
			{
				DatePickerLog($"[DayButton_Click] 选中 {selectedDate:yyyy-MM-dd}");
				_viewModel.SetTodoDateCommand.Execute(new object[2] { _dateTargetTodo, selectedDate });
			}
		}
		HideInlineDatePicker();
	}

	private void DatePicker_PrevMonth(object sender, RoutedEventArgs e)
	{
		if (_pickerMonth == 1)
		{
			_pickerMonth = 12;
			_pickerYear--;
		}
		else
		{
			_pickerMonth--;
		}
		BuildDayGrid();
	}

	private void DatePicker_NextMonth(object sender, RoutedEventArgs e)
	{
		if (_pickerMonth == 12)
		{
			_pickerMonth = 1;
			_pickerYear++;
		}
		else
		{
			_pickerMonth++;
		}
		BuildDayGrid();
	}

	private void DatePickerOverlay_BackgroundClick(object sender, MouseButtonEventArgs e)
	{
		HideInlineDatePicker();
	}

	private void DatePickerPanel_PreventClose(object sender, MouseButtonEventArgs e)
	{
		e.Handled = true;
	}

	private void DatePickerCloseButton_Click(object sender, RoutedEventArgs e)
	{
		HideInlineDatePicker();
	}

	private void OnCollapsedChanged(object? sender, EventArgs e)
	{
		if (_viewModel.IsCollapsed)
		{
			ApplyCollapsedSize();
		}
		else
		{
			RestoreExpandedSize();
		}
	}

	private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
	{
		if (DesktopEmbedService.IsAppExiting)
		{
			return;
		}
		if (!_viewModel.IsCollapsed && !_suppressPositionSave)
		{
			_viewModel.TodoWidth = base.Width;
			if (Math.Abs(base.Height - 52.0) > 1.0)
			{
				_viewModel.TodoHeight = base.Height;
			}
		}
		if (_hwnd != IntPtr.Zero && e.NewSize.Width > 0.0 && e.NewSize.Height > 0.0 && _viewModel.MainVm.IsGlassStyle)
		{
			GlassEffectHelper.ApplyRoundCorners(_hwnd, (int)e.NewSize.Width, (int)e.NewSize.Height);
		}
		if (!_inNativeMove && !_viewModel.IsCollapsed && _snapTimer != null)
		{
			_snapTimer.Stop();
			_snapTimer.Start();
		}
	}

	private void ApplyCollapsedSize()
	{
		_savedCollapsedHeight = base.Height;
		base.MaxHeight = 52.0;
		base.Height = 52.0;
		base.ResizeMode = ResizeMode.NoResize;
	}

	private void RestoreExpandedSize()
	{
		base.ResizeMode = ResizeMode.NoResize;
		base.MaxHeight = double.PositiveInfinity;
		base.Height = ((_savedCollapsedHeight > 52.0) ? _savedCollapsedHeight : 400.0);
	}

	private void ScheduleDragCollapse()
	{
		if (!_viewModel.IsCollapsed)
		{
			StopHoverCollapseWatcher();
			return;
		}
		StopHoverCollapseWatcher();
		_hoverCollapseWatcher = new DispatcherTimer(DispatcherPriority.Background, base.Dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(200.0)
		};
		_hoverCollapseWatcher.Tick += delegate
		{
			if (_contextMenuOpen)
			{
				ContextMenu? openContextMenu = _openContextMenu;
				if (openContextMenu != null && openContextMenu.IsOpen)
				{
					return;
				}
			}
			if (!_viewModel.IsCollapsed)
			{
				StopHoverCollapseWatcher();
			}
			else if (!_viewModel.IsHoverExpanded && Math.Abs(base.Height - 52.0) < 1.0)
			{
				StopHoverCollapseWatcher();
			}
			else if ((!base.IsKeyboardFocusWithin || !(Keyboard.FocusedElement is System.Windows.Controls.TextBox)) && !IsCursorOverWindow())
			{
				StopHoverCollapseWatcher();
				_viewModel.IsHoverExpanded = false;
				base.MaxHeight = 52.0;
				base.Height = 52.0;
				base.ResizeMode = ResizeMode.NoResize;
				TitleButtonsPanel.Opacity = 0.0;
			}
		};
		_hoverCollapseWatcher.Start();
	}

	private void StopHoverCollapseWatcher()
	{
		_hoverCollapseWatcher?.Stop();
		_hoverCollapseWatcher = null;
	}

	private bool IsCursorOverWindow()
	{
		if (PresentationSource.FromVisual(this) == null)
		{
			return false;
		}
		if (!NativeMethods.GetCursorPos(out var pt))
		{
			return false;
		}
		Point local = PointFromScreen(new Point(pt.X, pt.Y));
		if (local.X >= 0.0 && local.Y >= 0.0 && local.X <= base.ActualWidth)
		{
			return local.Y <= base.ActualHeight;
		}
		return false;
	}

	private void ResetPosition_Click(object sender, RoutedEventArgs e)
	{
		e.Handled = true;
		ResetToScreenCenter();
	}

	private void RootGrid_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
	{
		_buttonHideTimer?.Stop();
		TitleButtonsPanel.Opacity = 1.0;
		if (_viewModel.IsCollapsed)
		{
			_viewModel.IsHoverExpanded = true;
			RestoreExpandedSize();
			ScheduleDragCollapse();
		}
	}

	private void RootGrid_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
	{
		_buttonHideTimer?.Stop();
		_buttonHideTimer = new DispatcherTimer(DispatcherPriority.Background, base.Dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(300.0)
		};
		_buttonHideTimer.Tick += delegate
		{
			_buttonHideTimer.Stop();
			StopHoverCollapseWatcher();
			if (!_contextMenuOpen)
			{
				TitleButtonsPanel.Opacity = 0.0;
				if (base.IsKeyboardFocusWithin && Keyboard.FocusedElement is System.Windows.Controls.TextBox)
				{
					ScheduleDragCollapse();
				}
				else if (_viewModel.IsCollapsed)
				{
					_viewModel.IsHoverExpanded = false;
					base.MaxHeight = 52.0;
					base.Height = 52.0;
					base.ResizeMode = ResizeMode.NoResize;
				}
			}
		};
		_buttonHideTimer.Start();
	}

	private void OnContextMenuOpened(object sender, RoutedEventArgs e)
	{
		if (!(sender is ContextMenu cm))
		{
			return;
		}
		if (cm.PlacementTarget != null)
		{
			Window ownerWin = Window.GetWindow(cm.PlacementTarget);
			if (ownerWin != null && ownerWin != this)
			{
				return;
			}
		}
		_openContextMenu = cm;
		_contextMenuOpen = true;
		ContextMenuAutoCloser.Attach(cm);
		_buttonHideTimer?.Stop();
		TitleButtonsPanel.Opacity = 1.0;
		ForceTopMost();
	}

	private void OnContextMenuClosed(object sender, RoutedEventArgs e)
	{
		if (sender is ContextMenu cm && (_openContextMenu == null || cm == _openContextMenu))
		{
			_openContextMenu = null;
			_contextMenuOpen = false;
			ContextMenuAutoCloser.Detach(cm);
			if (_viewModel.IsCollapsed && !base.IsMouseOver)
			{
				_buttonHideTimer?.Stop();
				StopHoverCollapseWatcher();
				TitleButtonsPanel.Opacity = 0.0;
				_viewModel.IsHoverExpanded = false;
				base.MaxHeight = 52.0;
				base.Height = 52.0;
				base.ResizeMode = ResizeMode.NoResize;
			}
			PushToBottom();
		}
	}

	public void ResetToScreenCenter()
	{
		RepositionWindow(360.0, 500.0, save: true);
	}

	private void RecoverToPrimaryScreen(bool save = false)
	{
		double w = ((base.Width > 100.0) ? base.Width : 360.0);
		double h = ((base.Height > 100.0) ? base.Height : 500.0);
		RepositionWindow(w, h, save);
	}

	private void RepositionWindow(double width, double height, bool save)
	{
		if (base.WindowState != WindowState.Normal)
		{
			base.WindowState = WindowState.Normal;
		}
		double dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
		if (dpi <= 0.0)
		{
			dpi = 1.0;
		}
		var (left, top) = DesktopEmbedService.GetPrimaryCenterRelativeToOwner(_hwnd, width, height, dpi);
		if (!save)
		{
			_suppressPositionSave = true;
			try
			{
				base.Left = left;
				base.Top = top;
				base.Width = width;
				base.Height = height;
				return;
			}
			finally
			{
				_suppressPositionSave = false;
			}
		}
		base.Left = left;
		base.Top = top;
		base.Width = width;
		base.Height = height;
		_viewModel.WindowLeft = left;
		_viewModel.WindowTop = top;
		_viewModel.TodoWidth = width;
		_viewModel.TodoHeight = height;
	}

	private void OnDisplaySettingsChanged(object? sender, EventArgs e)
	{
		if (DateTime.Now >= _displayTransitionUntil)
		{
			_preTransitionLeft = _viewModel.WindowLeft;
			_preTransitionTop = _viewModel.WindowTop;
		}
		_displayTransitionUntil = DateTime.Now.AddSeconds(6.0);
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
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		if (_viewModel.IsLocked)
		{
			_displayTransitionUntil = DateTime.MinValue;
			return;
		}
		try
		{
			if (!double.IsNaN(_preTransitionLeft) && !double.IsNaN(_preTransitionTop) && (Math.Abs(base.Left - _preTransitionLeft) > 1.0 || Math.Abs(base.Top - _preTransitionTop) > 1.0))
			{
				_suppressPositionSave = true;
				try
				{
					base.Left = _preTransitionLeft;
					base.Top = _preTransitionTop;
					_viewModel.WindowLeft = _preTransitionLeft;
					_viewModel.WindowTop = _preTransitionTop;
				}
				finally
				{
					_suppressPositionSave = false;
				}
			}
		}
		catch
		{
		}
		finally
		{
			_displayTransitionUntil = DateTime.MinValue;
		}
		double windowLeft = _viewModel.WindowLeft;
		double checkTop = _viewModel.WindowTop;
		double checkW = ((_viewModel.TodoWidth > 100.0) ? _viewModel.TodoWidth : base.Width);
		double checkH = ((_viewModel.TodoHeight > 100.0) ? _viewModel.TodoHeight : base.Height);
		if (!IsOnAnyScreen(windowLeft, checkTop, checkW, checkH))
		{
			RecoverToPrimaryScreen();
		}
	}

	private static (double left, double top) GetCenteredPosition(double windowWidth, double windowHeight)
	{
		return DesktopEmbedService.GetPrimaryScreenCenter(windowWidth, windowHeight);
	}

	private (double left, double top) ConvertParentToScreenRelative(double parentX, double parentY)
	{
		nint owner = DesktopEmbedService.DesktopOwner;
		if (owner == IntPtr.Zero)
		{
			return (left: parentX, top: parentY);
		}
		double dpiScale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
		NativeMethods.GetWindowRect(owner, out var ownerRect);
		return (left: parentX + (double)ownerRect.Left / dpiScale, top: parentY + (double)ownerRect.Top / dpiScale);
	}

	private static bool IsOnAnyScreen(double left, double top, double width, double height)
	{
		return DesktopEmbedService.IsOnAnyScreen(left, top, width, height);
	}

	public void RefreshFontSizes()
	{
		_viewModel.RefreshFontSizes();
	}

	public void SaveWindowState()
	{
		_viewModel.SaveWindowState();
	}

	private static void DebugLog(string message)
	{
		if (_logEnabled)
		{
			LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_wndproc.log"), $"{DateTime.Now:HH:mm:ss.fff} [TodoWin] {message}{Environment.NewLine}");
		}
	}

	private static void DatePickerLog(string message)
	{
		LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_datepicker.log"), $"{DateTime.Now:HH:mm:ss.fff} [DatePicker] {message}{Environment.NewLine}");
	}

	private static void LogWndProc(int msg, nint wParam, nint lParam)
	{
		if (_logEnabled)
		{
			string msgName = msg switch
			{
				24 => "WM_SHOWWINDOW", 
				5 => "WM_SIZE", 
				274 => "WM_SYSCOMMAND", 
				70 => "WM_WINDOWPOSCHANGING", 
				71 => "WM_WINDOWPOSCHANGED", 
				28 => "WM_ACTIVATEAPP", 
				6 => "WM_ACTIVATE", 
				_ => null, 
			};
			if (msgName != null)
			{
				string detail = msg switch
				{
					24 => (wParam == IntPtr.Zero) ? "隐藏(HIDE)" : "显示(SHOW)", 
					5 => ((IntPtr)wParam).ToInt32() switch
					{
						0 => "RESTORED", 
						1 => "MINIMIZED", 
						2 => "MAXIMIZED", 
						_ => ((IntPtr)wParam).ToString(), 
					}, 
					274 => $"cmd=0x{((IntPtr)wParam).ToInt32():X4}", 
					28 => (wParam == IntPtr.Zero) ? "停用" : "激活", 
					70 => LogWindowPos(lParam, "CHANGING"), 
					71 => LogWindowPos(lParam, "CHANGED"), 
					_ => "", 
				};
				DebugLog($"[WndProc] {msgName}(0x{msg:X4}) {detail}");
			}
		}
	}

	private static string LogWindowPos(nint lParam, string phase)
	{
		try
		{
			NativeMethods.WINDOWPOS wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
			string extra = "";
			if ((wp.flags & 0x80) != 0)
			{
				extra = " HIDE";
			}
			if ((wp.flags & 0x40) != 0)
			{
				extra = " SHOW";
			}
			return $"{phase} x={wp.x} y={wp.y} cx={wp.cx} cy={wp.cy} flags=0x{wp.flags:X}{extra}";
		}
		catch
		{
			return phase + " parse_err";
		}
	}

	private int BlurTintAlpha()
	{
		return (int)Math.Max(0.0, Math.Min(255.0, (1.0 - _viewModel.MainVm.Settings.BackgroundOpacity) * 255.0));
	}

	public void RefreshVisualStyle()
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		if (Environment.OSVersion.Version.Build < 22000)
		{
			if (_viewModel.MainVm.IsGlassStyle)
			{
				GlassEffectHelper.EnableBlur(_hwnd, BlurTintAlpha());
			}
			else
			{
				GlassEffectHelper.DisableBlur(_hwnd);
			}
		}
		else if (_viewModel.MainVm.IsGlassStyle)
		{
			GlassEffectHelper.EnableAcrylicWithTint(_hwnd);
			GlassEffectHelper.ApplyRoundCorners(_hwnd, (int)base.ActualWidth, (int)base.ActualHeight);
		}
		else
		{
			GlassEffectHelper.DisableBlur(_hwnd);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/views/todowindow.xaml", UriKind.Relative);
			System.Windows.Application.LoadComponent(this, resourceLocater);
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
			RootGrid = (Grid)target;
			RootGrid.MouseEnter += RootGrid_MouseEnter;
			RootGrid.MouseLeave += RootGrid_MouseLeave;
			break;
		case 2:
			((Border)target).PreviewMouseLeftButtonDown += Header_PreviewMouseLeftButtonDown;
			break;
		case 3:
			((TextBlock)target).MouseLeftButtonDown += Handle_MouseLeftButtonDown;
			break;
		case 4:
			TitleButtonsPanel = (StackPanel)target;
			break;
		case 5:
			((System.Windows.Controls.Button)target).Click += ExportMenuButton_Click;
			break;
		case 6:
			((System.Windows.Controls.Button)target).Click += CloseButton_Click;
			break;
		case 7:
			PlaceholderText = (TextBlock)target;
			break;
		case 8:
			TodoInputBox = (System.Windows.Controls.TextBox)target;
			TodoInputBox.PreviewMouseLeftButtonDown += TodoInput_PreviewMouseLeftButtonDown;
			TodoInputBox.LostKeyboardFocus += TodoInput_LostKeyboardFocus;
			TodoInputBox.GotFocus += TodoInput_GotFocus;
			TodoInputBox.PreviewKeyDown += TodoInput_PreviewKeyDown;
			TodoInputBox.TextChanged += TodoInput_TextChanged;
			break;
		case 9:
			TodoSearchBox = (System.Windows.Controls.TextBox)target;
			TodoSearchBox.TextChanged += TodoSearch_TextChanged;
			TodoSearchBox.PreviewMouseLeftButtonDown += TodoSearch_PreviewMouseLeftButtonDown;
			TodoSearchBox.GotFocus += TodoSearch_GotFocus;
			TodoSearchBox.LostKeyboardFocus += TodoSearch_LostKeyboardFocus;
			break;
		case 10:
			TodoSearchPlaceholder = (TextBlock)target;
			break;
		case 11:
			TxtCustomDateFrom = (TextBlock)target;
			TxtCustomDateFrom.MouseLeftButtonDown += CustomDateFrom_Click;
			break;
		case 12:
			((System.Windows.Controls.Button)target).Click += CustomDateFrom_Click;
			break;
		case 13:
			TxtCustomDateTo = (TextBlock)target;
			TxtCustomDateTo.MouseLeftButtonDown += CustomDateTo_Click;
			break;
		case 14:
			((System.Windows.Controls.Button)target).Click += CustomDateTo_Click;
			break;
		case 15:
			TodoScrollViewer = (ScrollViewer)target;
			break;
		case 16:
			TodoItemsControl = (ItemsControl)target;
			break;
		case 33:
			DatePickerOverlay = (Border)target;
			DatePickerOverlay.MouseLeftButtonDown += DatePickerOverlay_BackgroundClick;
			break;
		case 34:
			DatePickerPanel = (Border)target;
			DatePickerPanel.MouseLeftButtonDown += DatePickerPanel_PreventClose;
			break;
		case 35:
			((System.Windows.Controls.Button)target).Click += DatePickerCloseButton_Click;
			break;
		case 36:
			DatePickerContent = (StackPanel)target;
			break;
		case 37:
			BtnPrevMonth = (System.Windows.Controls.Button)target;
			BtnPrevMonth.Click += DatePicker_PrevMonth;
			break;
		case 38:
			TxtMonthYear = (TextBlock)target;
			break;
		case 39:
			BtnNextMonth = (System.Windows.Controls.Button)target;
			BtnNextMonth.Click += DatePicker_NextMonth;
			break;
		case 40:
			DayGrid = (UniformGrid)target;
			break;
		case 41:
			ReminderPickerOverlay = (Border)target;
			ReminderPickerOverlay.MouseLeftButtonDown += ReminderOverlay_BackgroundClick;
			break;
		case 42:
			((Border)target).MouseLeftButtonDown += DatePickerPanel_PreventClose;
			break;
		case 43:
			TxtReminderDate = (TextBlock)target;
			break;
		case 44:
			((System.Windows.Controls.Button)target).Click += ReminderPicker_PickDate;
			break;
		case 45:
			TxtReminderTime = (System.Windows.Controls.TextBox)target;
			TxtReminderTime.TextChanged += TxtReminderTime_TextChanged;
			break;
		case 46:
			LstReminderHour = (System.Windows.Controls.ListBox)target;
			LstReminderHour.SelectionChanged += LstReminderHour_SelectionChanged;
			break;
		case 47:
			LstReminderMinute = (System.Windows.Controls.ListBox)target;
			LstReminderMinute.SelectionChanged += LstReminderMinute_SelectionChanged;
			break;
		case 48:
			((System.Windows.Controls.Button)target).Click += ReminderPicker_Today;
			break;
		case 49:
			((System.Windows.Controls.Button)target).Click += ReminderPicker_Tomorrow;
			break;
		case 50:
			((System.Windows.Controls.Button)target).Click += ReminderPicker_Clear;
			break;
		case 51:
			((System.Windows.Controls.Button)target).Click += ReminderPicker_OK;
			break;
		case 52:
			((System.Windows.Controls.Button)target).Click += ReminderPicker_Cancel;
			break;
		case 53:
			DragOverlay = (Canvas)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IStyleConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 17:
			((Border)target).PreviewMouseLeftButtonDown += TodoItem_PreviewMouseLeftButtonDown;
			((Border)target).PreviewMouseMove += TodoItem_PreviewMouseMove;
			((Border)target).PreviewMouseLeftButtonUp += TodoItem_PreviewMouseLeftButtonUp;
			break;
		case 18:
			((MenuItem)target).Click += ContextMenu_SetColor;
			break;
		case 19:
			((MenuItem)target).Click += ContextMenu_SetColor;
			break;
		case 20:
			((MenuItem)target).Click += ContextMenu_SetColor;
			break;
		case 21:
			((MenuItem)target).Click += ContextMenu_SetColor;
			break;
		case 22:
			((MenuItem)target).Click += ContextMenu_SetColor;
			break;
		case 23:
			((MenuItem)target).Click += ContextMenu_SetColor;
			break;
		case 24:
			((MenuItem)target).Click += ContextMenu_SetColor;
			break;
		case 25:
			((MenuItem)target).Click += ContextMenu_SetColor;
			break;
		case 26:
			((MenuItem)target).Click += ContextMenu_EditTodo;
			break;
		case 27:
			((MenuItem)target).Click += ContextMenu_SetDate;
			break;
		case 28:
			((MenuItem)target).Click += ContextMenu_SetReminder;
			break;
		case 29:
			((MenuItem)target).Click += ContextMenu_DeleteTodo;
			break;
		case 30:
			((System.Windows.Controls.CheckBox)target).Checked += TodoCheckBox_Toggled;
			((System.Windows.Controls.CheckBox)target).Unchecked += TodoCheckBox_Toggled;
			break;
		case 31:
			((Grid)target).MouseLeftButtonDown += TodoContent_MouseLeftButtonDown;
			break;
		case 32:
			((System.Windows.Controls.TextBox)target).LostKeyboardFocus += TodoContentTextBox_LostFocus;
			((System.Windows.Controls.TextBox)target).PreviewKeyDown += TodoContentTextBox_PreviewKeyDown;
			break;
		}
	}
}
