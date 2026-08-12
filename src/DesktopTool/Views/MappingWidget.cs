using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTool.Models;
using DesktopTool.Services;
using DesktopTool.ViewModels;
using Microsoft.Win32;

namespace DesktopTool.Views;

public class MappingWidget : System.Windows.Controls.UserControl, IComponentConnector, IStyleConnector
{
	private readonly MappingWindowViewModel _vm;

	private readonly MappingWindow _owner;

	private IWidgetHost _host;

	private bool _inNativeMove;

	private bool _suppressPositionSave;

	private bool _isInitializing = true;

	private bool _isEditingTitle;

	private bool _isEditingTab;

	private string _tabOriginalName = "";

	private WidgetDragAlignController? _align;

	private const int ResizeBorder = 6;

	private int _savedExStyle;

	private DispatcherTimer? _buttonHideTimer;

	private bool _contextMenuOpen;

	private ContextMenu? _openContextMenu;

	private MappingFileItem? _contextFileItem;

	private DispatcherTimer? _autoRefreshTimer;

	private int _currentRefreshIntervalMs = 4000;

	private int _noChangeCount;

	private const int FastRefreshMs = 4000;

	private const int SlowRefreshMs = 15000;

	private const int NoChangeThreshold = 3;

	private double _savedCollapsedHeight;

	private const double CollapsedHeight = 52.0;

	private bool _isClosing;

	private MappingGroup? _mappingDraggingTab;

	private Point _mappingTabDragStartPoint;

	private bool _mappingTabDragActive;

	private int _mappingTabSourceIndex;

	private Border? _mappingTabDragGhost;

	private DateTime _lastMappingTabClickTime;

	private MappingGroup? _lastClickedMappingGroup;

	private Point _fileDragStartPoint;

	private MappingFileItem? _draggingFileItem;

	private bool _fileDragActive;

	private bool _isSelfDragOut;

	private DispatcherTimer? _hoverCollapseWatcher;

	private DateTime _suppressTitleEditUntil = DateTime.MinValue;

	private DispatcherTimer? _displayChangeTimer;

	private DateTime _displayTransitionUntil = DateTime.MinValue;

	private double _preTransitionLeft = double.NaN;

	private double _preTransitionTop = double.NaN;

	private Point _dragStart;

	private Rect _dragStartRect;

	private bool _dragResizeActive;

	private bool _oleEffectiveTargetIsDesktop;

	private bool _loadedPartsInitialized;

	private DispatcherTimer? _oleHeartbeatTimer;

	private int _oleStableCount;

	private const int OleStableThreshold = 3;

	private const double NarrowListWidthThreshold = 300.0;

	private bool _searchActive;

	private DateTime _lastFileClick = DateTime.MinValue;

	private MappingFileItem? _lastClickedFile;

	private const string RunAsAdminDisabledTip = "微软商店版(MSIX)受系统沙箱限制，无法以管理员身份运行。请使用官网的独立安装版或绿色版。";

	private DesktopDragHost _dragHost;

	internal Grid RootGrid;

	internal Canvas DragOverlay;

	internal System.Windows.Controls.TextBox TitleTextBox;

	internal StackPanel TitleButtonsPanel;

	internal System.Windows.Controls.TextBox MappingSearchBox;

	internal System.Windows.Controls.Button MoreButton;

	internal Popup MorePopup;

	internal System.Windows.Controls.Button BtnScrollLeft;

	internal ScrollViewer TabScrollViewer;

	internal ItemsControl TabsItemsControl;

	internal System.Windows.Controls.Button BtnScrollRight;

	internal ItemsControl FilesItemsControl;

	internal ItemsControl ListItemsControl;

	private bool _contentLoaded;

	public MappingWidget(MappingWindowViewModel vm, MappingWindow owner)
	{
		InitializeComponent();
		_vm = vm;
		_owner = owner;
		base.DataContext = _vm;
		MorePopup.CustomPopupPlacementCallback = PlaceMorePopup;
		TitleTextBox.Focusable = false;
		base.SizeChanged += OnSizeChanged;
		_vm.CollapsedChanged += OnCollapsedChanged;
		if (_vm.IsCollapsed)
		{
			ApplyCollapsedSize();
		}
		EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnContextMenuOpened));
		EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler(OnContextMenuClosed));
		_vm.Groups.CollectionChanged += OnGroupsChanged;
		base.Loaded += delegate
		{
			TryInitializeLoadedParts();
		};
		base.PreviewKeyDown += delegate(object s, System.Windows.Input.KeyEventArgs e)
		{
			if (e.Key == Key.Escape && _fileDragActive)
			{
				CancelFileDrag();
			}
		};
		base.PreviewDragEnter += delegate
		{
			if (!_isEditingTitle && !_isEditingTab)
			{
				RemoveNoActivateForDrag();
			}
			if (_vm.IsCollapsed)
			{
				_vm.IsHoverExpanded = true;
				RestoreExpandedSize();
				EnsureCollapseWatcher();
			}
		};
		base.PreviewDragOver += delegate
		{
			if (_vm.IsCollapsed)
			{
				_vm.IsHoverExpanded = true;
				RestoreExpandedSize();
				EnsureCollapseWatcher();
			}
		};
		base.PreviewDragLeave += delegate
		{
			if (!_isEditingTitle && !_isEditingTab)
			{
				RestoreNoActivateAfterDrag();
			}
			ScheduleDragCollapse();
		};
		base.PreviewDrop += delegate
		{
			if (!_isEditingTitle && !_isEditingTab)
			{
				RestoreNoActivateAfterDrag();
			}
			ScheduleDragCollapse();
		};
	}

	internal void AttachHost(IWidgetHost host)
	{
		_host = host;
		if (base.IsLoaded)
		{
			TryInitializeLoadedParts();
		}
	}

	private void TryInitializeLoadedParts()
	{
		if (_loadedPartsInitialized || _host == null)
		{
			return;
		}
		_loadedPartsInitialized = true;
		_align = new WidgetDragAlignController(new WidgetDragAlignController.Config
		{
			Hwnd = _host.HostHwnd,
			GetLiveHwnd = () => _host.HostHwnd,
			GlowTarget = this,
			GetBoundsDip = () => NativeDragHelper.GetDipRect(_host.HostHwnd),
			SetBoundsDip = delegate(Rect r)
			{
				double monitorDpi = GetMonitorDpi();
				_host.SetBoundsScreen(r.X * monitorDpi, r.Y * monitorDpi, r.Width * monitorDpi, r.Height * monitorDpi);
			},
			ApplyDragPosition = delegate(int x, int y, int w, int h)
			{
				if (w <= 0 || h <= 0)
				{
					w = (int)Math.Round(_host.Width);
					h = (int)Math.Round(_host.Height);
				}
				_host.SetBounds(x, y, w, h);
			},
			MinWidth = () => base.MinWidth,
			MinHeight = () => base.MinHeight,
			IsLocked = () => _vm.IsLocked,
			Save = delegate(Rect r)
			{
				SaveBounds();
				double monitorDpi = GetMonitorDpi();
				_host.SetBoundsScreen(r.X * monitorDpi, r.Y * monitorDpi, r.Width * monitorDpi, r.Height * monitorDpi);
				_suppressTitleEditUntil = DateTime.Now + TimeSpan.FromMilliseconds(300.0);
			},
			ResizeGlowTick = delegate
			{
				UpdateResizeGlow();
			},
			ResizeGlowEnd = delegate
			{
				ClearResizeGlow();
			}
		}, base.Dispatcher);
		_dragHost = new DesktopDragHost(() => _host.HostHwnd, () => DesktopEmbedService.GetOwnerHwnd(_host.HostHwnd), () => base.MinWidth, () => base.MinHeight, this, delegate
		{
			if (_host is DesktopWidgetHost desktopWidgetHost)
			{
				desktopWidgetHost.IsDragging = true;
			}
			_host.BringToTop();
		}, delegate(bool v)
		{
			_suppressPositionSave = v;
		}, delegate(int x, int y, int w, int h)
		{
			if (w != 0 && h != 0)
			{
				_dragResizeActive = true;
			}
			_align.SetPosition(x, y, w, h);
		}, delegate(int r)
		{
			try
			{
				_align.OnDragCommitted(r);
			}
			catch (Exception ex)
			{
				try
				{
					LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_drag.log"), $"{DateTime.Now:HH:mm:ss.fff} [onCommitted-异常] hwnd=0x{_host.HostHwnd:X8} ex={ex.GetType().Name}:{ex.Message}{Environment.NewLine}");
				}
				catch
				{
				}
				throw;
			}
			finally
			{
				if (_host is DesktopWidgetHost desktopWidgetHost)
				{
					desktopWidgetHost.IsDragging = false;
				}
				_dragResizeActive = false;
			}
		}, delegate
		{
			_align.ResizeGlowTick();
		}, delegate
		{
			_align.ResizeGlowEnd();
		});
		WidgetZOrderManager.Register(_host.HostHwnd, topLayer: false);
		_vm.PropertyChanged += delegate(object? s, PropertyChangedEventArgs ev)
		{
			if (ev.PropertyName == "IsHoverExpanded")
			{
				WidgetZOrderManager.OnHoverExpandedChanged(_host.HostHwnd, _vm.IsHoverExpanded);
			}
		};
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			_isInitializing = false;
		}, DispatcherPriority.Background);
	}

	internal void Initialize()
	{
		nint hwnd = _host.HostHwnd;
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		if (Environment.OSVersion.Version.Build < 22000)
		{
			if (_vm.MainVm.IsGlassStyle)
			{
				GlassEffectHelper.EnableBlur(hwnd, BlurTintAlpha());
			}
			else
			{
				GlassEffectHelper.DisableBlur(hwnd);
			}
		}
		else if (_vm.MainVm.IsGlassStyle)
		{
			GlassEffectHelper.EnableAcrylicWithTint(hwnd);
		}
		_savedExStyle = NativeMethods.GetWindowLong(hwnd, -20);
		int exStyle = _savedExStyle | 0x8000000;
		NativeMethods.SetWindowLong(hwnd, -20, exStyle);
		int newStyle = NativeMethods.GetWindowLong(hwnd, -16) & -131073;
		NativeMethods.SetWindowLong(hwnd, -16, newStyle);
		NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 51u);
		_host.LocationChanged += delegate(object? s, EventArgs e)
		{
			OnLocationChanged(s, e);
		};
		_host.Deactivated += delegate
		{
			CommitTitleEdit();
		};
		_host.Closed += OnHostClosed;
		if (_vm.IsCollapsed)
		{
			ApplyCollapsedSize();
		}
		TabScrollViewer.ScrollChanged += delegate
		{
			UpdateTabScrollButtons();
		};
		_vm.Groups.CollectionChanged += delegate
		{
			base.Dispatcher.BeginInvoke(new Action(UpdateTabScrollButtons), DispatcherPriority.Render);
		};
		base.Dispatcher.BeginInvoke(new Action(UpdateTabScrollButtons), DispatcherPriority.Render);
		_vm.RefreshAllGroups();
		_autoRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, base.Dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(4000.0)
		};
		_autoRefreshTimer.Tick += delegate
		{
			if (!_isClosing && !base.IsMouseOver && !_contextMenuOpen)
			{
				_vm.RefreshFiles();
				if (_vm.LastRefreshHadChanges)
				{
					_noChangeCount = 0;
					_currentRefreshIntervalMs = 4000;
				}
				else
				{
					_noChangeCount++;
					if (_noChangeCount >= 3 && _currentRefreshIntervalMs != 15000)
					{
						_currentRefreshIntervalMs = 15000;
					}
				}
				if (_autoRefreshTimer != null && Math.Abs(_autoRefreshTimer.Interval.TotalMilliseconds - (double)_currentRefreshIntervalMs) > 100.0)
				{
					_autoRefreshTimer.Interval = TimeSpan.FromMilliseconds(_currentRefreshIntervalMs);
				}
			}
		};
		_autoRefreshTimer.Start();
		EnsureCollapseWatcher();
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			try
			{
				if (_host.HostHwnd != IntPtr.Zero)
				{
					base.AllowDrop = false;
					base.AllowDrop = true;
				}
			}
			catch
			{
			}
		});
		StartOleHeartbeat();
		SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
		DesktopEmbedService.DesktopOwnerReapplied += OnDesktopOwnerReapplied;
		ScheduleDisplayRecovery();
	}

	private void StartOleHeartbeat()
	{
		if (_oleHeartbeatTimer != null || _host == null || _host.HostHwnd == IntPtr.Zero)
		{
			return;
		}
		_oleHeartbeatTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(5000.0)
		};
		_oleHeartbeatTimer.Tick += delegate
		{
			ReRegisterOleDropTarget(force: false);
			if (_oleStableCount >= 3)
			{
				_oleHeartbeatTimer?.Stop();
				_oleHeartbeatTimer = null;
				OleDiag("[OLE][Map] OLE 已稳定，停止心跳（保留按需重注册）");
			}
		};
		_oleHeartbeatTimer.Start();
	}

	private void ReRegisterOleDropTarget(bool force)
	{
		try
		{
			nint hwnd = _host?.HostHwnd ?? IntPtr.Zero;
			if (hwnd == IntPtr.Zero || DesktopEmbedService.IsAppExiting || DesktopEmbedService.IsOleDragActive(hwnd))
			{
				return;
			}
			if (force)
			{
				try
				{
					NativeMethods.RevokeDragDrop(hwnd);
				}
				catch
				{
				}
			}
			base.AllowDrop = false;
			base.AllowDrop = true;
			_oleStableCount++;
			OleDiag($"[OLE][Map] 重注册 hwnd=0x{hwnd:X8} force={force} 连续稳定={_oleStableCount}");
		}
		catch (Exception ex)
		{
			_oleStableCount = 0;
			OleDiag("[OLE][Map] ⚠ 重注册失败: " + ex.Message);
		}
	}

	private void OnDesktopOwnerReapplied()
	{
		if (_host != null && _host.HostHwnd != IntPtr.Zero)
		{
			_oleStableCount = 0;
			ReRegisterOleDropTarget(force: true);
			StartOleHeartbeat();
			OleDiag($"[OLE][Map] 桌面所有者重建→重新钉锚 hwnd=0x{_host.HostHwnd:X8}");
		}
	}

	private static void OleDiag(string m)
	{
		try
		{
			LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_wndproc.log"), $"{DateTime.Now:HH:mm:ss.fff} {m}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	private void ScheduleDisplayRecovery()
	{
		if (_displayChangeTimer == null)
		{
			_displayChangeTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(1500.0)
			};
			_displayChangeTimer.Tick += HandleDisplayRecoveryDebounced;
		}
		_displayChangeTimer.Stop();
		_displayChangeTimer.Start();
	}

	private void OnDisplaySettingsChanged(object? sender, EventArgs e)
	{
		if (DateTime.Now >= _displayTransitionUntil)
		{
			_preTransitionLeft = _vm.WindowLeft;
			_preTransitionTop = _vm.WindowTop;
		}
		_displayTransitionUntil = DateTime.Now.AddSeconds(6.0);
		ScheduleDisplayRecovery();
	}

	private void HandleDisplayRecoveryDebounced(object? sender, EventArgs e)
	{
		_displayChangeTimer?.Stop();
		try
		{
			if (_host != null && _host.HostHwnd != IntPtr.Zero && DesktopEmbedService.DesktopOwner != IntPtr.Zero)
			{
				RestorePreTransitionPosition();
				NativeMethods.GetWindowRect(_host.HostHwnd, out var winRect);
				if (NativeMethods.MonitorFromRect(ref winRect, 0u) == IntPtr.Zero)
				{
					RecoverToPrimaryScreen();
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
	}

	private void RestorePreTransitionPosition()
	{
		if (double.IsNaN(_preTransitionLeft) || double.IsNaN(_preTransitionTop) || (Math.Abs(_host.Left - _preTransitionLeft) < 1.0 && Math.Abs(_host.Top - _preTransitionTop) < 1.0))
		{
			return;
		}
		_suppressPositionSave = true;
		try
		{
			_host.SetBounds(_preTransitionLeft, _preTransitionTop, _host.Width, _host.Height);
			_vm.WindowLeft = _preTransitionLeft;
			_vm.WindowTop = _preTransitionTop;
		}
		catch
		{
		}
		finally
		{
			_suppressPositionSave = false;
		}
	}

	private void RecoverToPrimaryScreen()
	{
		double w = ((_host.Width > 100.0) ? _host.Width : 360.0);
		double h = ((_host.Height > 100.0) ? _host.Height : 420.0);
		double left;
		double top;
		if (Environment.OSVersion.Version.Build >= 22000)
		{
			(left, top) = GetCenteredPosition(w, h);
		}
		else
		{
			(left, top) = DesktopEmbedService.GetPrimaryCenterRelativeToOwner(_host.HostHwnd, w, h);
		}
		_suppressPositionSave = true;
		try
		{
			_host.SetBounds(left, top, w, h);
		}
		finally
		{
			_suppressPositionSave = false;
		}
	}

	private (double left, double top) GetCenteredPosition(double w, double h)
	{
		return DesktopEmbedService.GetPrimaryCenterRelativeToOwner(w, h);
	}

	private void OnHostClosed(object? sender, EventArgs e)
	{
		WidgetZOrderManager.Unregister(_host.HostHwnd);
		Cleanup();
		if (!DesktopEmbedService.IsAppExiting)
		{
			_vm.SaveState();
		}
	}

	internal void Cleanup()
	{
		_isClosing = true;
		SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
		DesktopEmbedService.DesktopOwnerReapplied -= OnDesktopOwnerReapplied;
		_displayChangeTimer?.Stop();
		_align?.Dispose();
		_hoverCollapseWatcher?.Stop();
		_hoverCollapseWatcher = null;
		_autoRefreshTimer?.Stop();
		_autoRefreshTimer = null;
		_oleHeartbeatTimer?.Stop();
		_oleHeartbeatTimer = null;
		_buttonHideTimer?.Stop();
		_buttonHideTimer = null;
		_suppressPositionSave = true;
	}

	private void OnLocationChanged(object? sender, EventArgs e)
	{
		if (!_isInitializing && !(DateTime.Now < _displayTransitionUntil) && !_vm.IsLocked && !_suppressPositionSave)
		{
			_vm.WindowLeft = _host.Left;
			_vm.WindowTop = _host.Top;
			_vm.SaveState();
		}
	}

	private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
	{
		if (_host != null)
		{
			if (!_vm.IsCollapsed && !_suppressPositionSave)
			{
				_vm.WindowWidth = _host.Width;
				_vm.WindowHeight = _host.Height;
				_vm.SaveState();
			}
			_vm.IsNarrow = base.ActualWidth < 300.0;
			if (Environment.OSVersion.Version.Build >= 22000 && _host.HostHwnd != IntPtr.Zero && e.NewSize.Width > 0.0 && e.NewSize.Height > 0.0 && _vm.MainVm.IsGlassStyle)
			{
				GlassEffectHelper.ApplyRoundCorners(_host.HostHwnd, (int)e.NewSize.Width, (int)e.NewSize.Height);
			}
			if (!_inNativeMove && !_dragResizeActive && !_vm.IsCollapsed)
			{
				_align?.ScheduleSnapAlign();
			}
		}
	}

	private void OnCollapsedChanged(object? sender, EventArgs e)
	{
		if (_vm.IsCollapsed)
		{
			ApplyCollapsedSize();
		}
		else
		{
			RestoreExpandedSize();
		}
	}

	private void SetCollapsedVisualHeight()
	{
		base.MaxHeight = 52.0;
		base.Height = 52.0;
		InvalidateMeasure();
		InvalidateArrange();
	}

	private void SetExpandedVisualHeight(double h)
	{
		base.MaxHeight = double.PositiveInfinity;
		base.Height = double.NaN;
		InvalidateMeasure();
		InvalidateArrange();
	}

	private int CollapsedPhysicalHeight()
	{
		double scale = 1.0;
		try
		{
			scale = (double)NativeMethods.GetDpiForWindow(_host.HostHwnd) / 96.0;
		}
		catch
		{
		}
		return (int)Math.Round(52.0 * scale);
	}

	private void ApplyCollapsedSize()
	{
		if (_host != null)
		{
			_savedCollapsedHeight = _host.Height;
			SetCollapsedVisualHeight();
			_host.ResizeOnly(_host.Width, CollapsedPhysicalHeight());
		}
	}

	private void RestoreExpandedSize()
	{
		if (_host != null)
		{
			double h = ((_savedCollapsedHeight > 52.0) ? _savedCollapsedHeight : ((_vm.WindowHeight > 52.0) ? _vm.WindowHeight : 360.0));
			SetExpandedVisualHeight(h);
			_host.ResizeOnly(_host.Width, h);
		}
	}

	private void ScheduleDragCollapse()
	{
		EnsureCollapseWatcher();
	}

	private void EnsureCollapseWatcher()
	{
		if (_isClosing)
		{
			return;
		}
		if (_hoverCollapseWatcher != null)
		{
			if (!_hoverCollapseWatcher.IsEnabled)
			{
				_hoverCollapseWatcher.Start();
			}
		}
		else
		{
			_hoverCollapseWatcher = new DispatcherTimer(DispatcherPriority.Background, base.Dispatcher)
			{
				Interval = TimeSpan.FromMilliseconds(200.0)
			};
			_hoverCollapseWatcher.Tick += HoverCollapseTick;
			_hoverCollapseWatcher.Start();
		}
	}

	private void HoverCollapseTick(object? s, EventArgs args)
	{
		if (_isClosing || _host == null)
		{
			return;
		}
		if (_contextMenuOpen)
		{
			ContextMenu? openContextMenu = _openContextMenu;
			if (openContextMenu != null && openContextMenu.IsOpen)
			{
				return;
			}
		}
		if (_vm.IsCollapsed && (_vm.IsHoverExpanded || !(Math.Abs(base.Height - 52.0) < 1.0)) && !_isEditingTitle && !_isEditingTab && (!base.IsKeyboardFocusWithin || !(Keyboard.FocusedElement is System.Windows.Controls.TextBox)) && !IsCursorOverWindow())
		{
			_vm.IsHoverExpanded = false;
			SetCollapsedVisualHeight();
			_host.ResizeOnly(_host.Width, CollapsedPhysicalHeight());
			TitleButtonsPanel.Opacity = 0.0;
		}
	}

	private void StopHoverCollapseWatcher()
	{
		_hoverCollapseWatcher?.Stop();
		_hoverCollapseWatcher = null;
	}

	private bool IsCursorOverWindow()
	{
		if (_host == null)
		{
			return false;
		}
		nint hwnd = _host.HostHwnd;
		if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
		{
			return false;
		}
		if (!NativeMethods.GetCursorPos(out var pt))
		{
			return false;
		}
		if (!NativeMethods.GetWindowRect(hwnd, out var rect))
		{
			return false;
		}
		if (pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top)
		{
			return pt.Y <= rect.Bottom;
		}
		return false;
	}

	private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (!_isClosing && _vm.IsCollapsed && _vm.IsHoverExpanded)
		{
			ScheduleDragCollapse();
		}
	}

	private bool IsRealChildWindow()
	{
		try
		{
			return (NativeMethods.GetWindowLong(_host.HostHwnd, -16) & 0x40000000) != 0;
		}
		catch
		{
			return false;
		}
	}

	private void EnterTitleEditMode()
	{
		if (!_isEditingTitle && _host.HostHwnd != IntPtr.Zero)
		{
			_isEditingTitle = true;
			TitleTextBox.Focusable = true;
			_host.AllowMouseActivate = true;
			nint hwnd = _host.HostHwnd;
			NativeMethods.SetWindowLong(hwnd, -20, _savedExStyle);
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			AppPanelTracker.TraceZ("EnterTitleEditMode", hwnd, band);
			NativeMethods.SetWindowPos(hwnd, band, 0, 0, 0, 0, 99u);
			NativeMethods.SetForegroundWindow(hwnd);
		}
	}

	private void LeaveTitleEditMode()
	{
		if (_isEditingTitle)
		{
			_isEditingTitle = false;
			TitleTextBox.Focusable = false;
			DeactivateWindowAfterEdit();
			nint hwnd = _host.HostHwnd;
			if (hwnd != IntPtr.Zero)
			{
				nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
				AppPanelTracker.PlaceDesktopWindow(hwnd, band);
			}
		}
	}

	private void CommitTitleEdit()
	{
		if (_isEditingTitle)
		{
			Keyboard.ClearFocus();
			if (_isEditingTitle)
			{
				LeaveTitleEditMode();
			}
		}
	}

	private void ActivateForTabEdit()
	{
		if (_host.HostHwnd != IntPtr.Zero)
		{
			nint hwnd = _host.HostHwnd;
			_host.AllowMouseActivate = true;
			NativeMethods.SetWindowLong(hwnd, -20, _savedExStyle);
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			AppPanelTracker.TraceZ("ActivateForTabEdit", hwnd, band);
			NativeMethods.SetWindowPos(hwnd, band, 0, 0, 0, 0, 99u);
			NativeMethods.SetForegroundWindow(hwnd);
		}
	}

	private void DeactivateWindowAfterEdit()
	{
		if (_host.HostHwnd != IntPtr.Zero)
		{
			nint hostHwnd = _host.HostHwnd;
			NativeMethods.SetWindowLong(hostHwnd, -20, _savedExStyle | 0x8000000);
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			NativeMethods.SetWindowPos(hostHwnd, band, 0, 0, 0, 0, 19u);
			_host.AllowMouseActivate = false;
			PushToBottom();
		}
	}

	private void MappingSearch_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (!_searchActive)
		{
			_searchActive = true;
			ActivateForSearch();
		}
	}

	private void MappingSearch_GotFocus(object sender, RoutedEventArgs e)
	{
		if (!_searchActive)
		{
			_searchActive = true;
			ActivateForSearch();
		}
	}

	private void MappingSearch_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (!IsOwnEditableTextBox(e.NewFocus as DependencyObject) && _searchActive)
		{
			_searchActive = false;
			if (!_isEditingTitle && !_isEditingTab)
			{
				DeactivateWindowAfterEdit();
			}
		}
	}

	private void ActivateForSearch()
	{
		if (_host.HostHwnd != IntPtr.Zero)
		{
			nint hostHwnd = _host.HostHwnd;
			_host.AllowMouseActivate = true;
			NativeMethods.SetWindowLong(hostHwnd, -20, _savedExStyle);
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			NativeMethods.SetWindowPos(hostHwnd, band, 0, 0, 0, 0, 99u);
			NativeMethods.SetForegroundWindow(hostHwnd);
		}
	}

	private static bool IsOwnEditableTextBox(DependencyObject? obj)
	{
		if (!(obj is System.Windows.Controls.TextBox))
		{
			return false;
		}
		for (DependencyObject d = obj; d != null; d = VisualTreeHelper.GetParent(d))
		{
			if (d is MappingWidget)
			{
				return true;
			}
		}
		return false;
	}

	private void PushToBottom()
	{
		nint hwnd = _host.HostHwnd;
		if (hwnd != IntPtr.Zero)
		{
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			AppPanelTracker.PlaceDesktopWindow(hwnd, band);
		}
	}

	internal void ForceTopMost()
	{
		_host.ForceTopMost();
	}

	private void TitleTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (TitleTextBox.IsReadOnly || _vm.IsLocked || DateTime.Now < _suppressTitleEditUntil)
		{
			return;
		}
		if (!_isEditingTitle)
		{
			e.Handled = true;
			if (e.ClickCount == 2)
			{
				if (_isEditingTab)
				{
					TabNameCancelEdit();
					_isEditingTab = false;
				}
				EnterTitleEditMode();
				PositionTitleCaret(e);
			}
		}
		else
		{
			PositionTitleCaret(e);
		}
	}

	private void PositionTitleCaret(MouseButtonEventArgs e)
	{
		Point pos = e.GetPosition(TitleTextBox);
		int snapIndex = TitleTextBox.GetCharacterIndexFromPoint(pos, snapToText: true);
		int charIndex;
		if (snapIndex >= 0 && TitleTextBox.Text.Length > 0)
		{
			Rect rect = TitleTextBox.GetRectFromCharacterIndex(snapIndex);
			charIndex = ((pos.X > rect.Left + rect.Width / 2.0) ? (snapIndex + 1) : snapIndex);
		}
		else
		{
			charIndex = 0;
		}
		TitleTextBox.Focus();
		if (e.ClickCount > 1)
		{
			return;
		}
		System.Windows.Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			if (TitleTextBox.IsKeyboardFocusWithin)
			{
				TitleTextBox.Select(charIndex, 0);
			}
		});
	}

	private void TitleTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(TitleTextBox.Text))
		{
			TitleTextBox.Text = _vm.Name;
			_vm.Name = TitleTextBox.Text;
		}
		LeaveTitleEditMode();
	}

	private void TitleTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key == Key.Return)
		{
			Keyboard.ClearFocus();
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			TitleTextBox.Text = _vm.Name;
			Keyboard.ClearFocus();
			e.Handled = true;
		}
	}

	private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.OriginalSource is DependencyObject src)
		{
			System.Windows.Controls.TextBox editingBox = FindVisualParent<System.Windows.Controls.TextBox>(src);
			if (editingBox != null && !editingBox.IsReadOnly)
			{
				return;
			}
		}
		if (sender is Border { DataContext: MappingGroup group } border)
		{
			_mappingTabDragStartPoint = e.GetPosition(this);
			_mappingDraggingTab = group;
			_mappingTabSourceIndex = _vm.Groups.IndexOf(group);
			_mappingTabDragActive = false;
			border.CaptureMouse();
		}
	}

	private void TabItem_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (_mappingDraggingTab == null || e.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}
		Point pos = e.GetPosition(this);
		double dx = pos.X - _mappingTabDragStartPoint.X;
		double dy = pos.Y - _mappingTabDragStartPoint.Y;
		if (!_mappingTabDragActive)
		{
			if (Math.Abs(dx) < 6.0 && Math.Abs(dy) < 6.0)
			{
				return;
			}
			_mappingTabDragActive = true;
			if (sender is FrameworkElement fe)
			{
				CreateMappingTabDragGhost(fe);
			}
		}
		if (_mappingTabDragGhost != null)
		{
			Canvas.SetLeft(_mappingTabDragGhost, pos.X - _mappingTabDragGhost.ActualWidth / 2.0);
			Canvas.SetTop(_mappingTabDragGhost, pos.Y - _mappingTabDragGhost.ActualHeight / 2.0);
		}
	}

	private void TabItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (sender is FrameworkElement fe)
		{
			fe.ReleaseMouseCapture();
		}
		MappingGroup group = _mappingDraggingTab;
		_mappingDraggingTab = null;
		if (_mappingTabDragActive && group != null)
		{
			_mappingTabDragActive = false;
			DestroyMappingTabDragGhost();
			Point mousePos = e.GetPosition(this);
			bool moved = false;
			HitTestResult hitResult = VisualTreeHelper.HitTest(this, mousePos);
			if (hitResult != null)
			{
				Border targetBorder = FindMappingAncestorTabBorder(hitResult.VisualHit);
				if (targetBorder != null && targetBorder.DataContext is MappingGroup targetGroup && targetGroup != group)
				{
					int targetIndex = _vm.Groups.IndexOf(targetGroup);
					if (targetIndex >= 0 && _mappingTabSourceIndex >= 0)
					{
						_vm.MoveGroup(_mappingTabSourceIndex, targetIndex);
						moved = true;
					}
				}
			}
			if (!moved && _mappingTabSourceIndex >= 0 && _mappingTabSourceIndex != _vm.Groups.Count - 1 && IsMappingMouseBeyondLastTab(mousePos))
			{
				_vm.MoveGroup(_mappingTabSourceIndex, _vm.Groups.Count - 1);
			}
			return;
		}
		_mappingTabDragActive = false;
		if (!(sender is Border { DataContext: MappingGroup g } border))
		{
			return;
		}
		DateTime now = DateTime.Now;
		if (g == _lastClickedMappingGroup && (now - _lastMappingTabClickTime).TotalMilliseconds < (double)SystemInformation.DoubleClickTime)
		{
			System.Windows.Controls.TextBox tb = FindVisualChild<System.Windows.Controls.TextBox>(border);
			if (tb != null)
			{
				_lastMappingTabClickTime = DateTime.MinValue;
				_lastClickedMappingGroup = null;
				_tabOriginalName = g.Name;
				_isEditingTab = true;
				if (_isEditingTitle)
				{
					LeaveTitleEditMode();
				}
				ActivateForTabEdit();
				tb.IsReadOnly = false;
				tb.Focus();
				Point pos = e.GetPosition(tb);
				int snapIndex = tb.GetCharacterIndexFromPoint(pos, snapToText: true);
				int charIndex;
				if (snapIndex >= 0 && tb.Text.Length > 0)
				{
					Rect rect = tb.GetRectFromCharacterIndex(snapIndex);
					charIndex = ((pos.X > rect.Left + rect.Width / 2.0) ? (snapIndex + 1) : snapIndex);
				}
				else
				{
					charIndex = 0;
				}
				System.Windows.Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
				{
					if (tb.IsKeyboardFocusWithin)
					{
						tb.Select(charIndex, 0);
					}
				});
				e.Handled = true;
				return;
			}
		}
		_lastMappingTabClickTime = now;
		_lastClickedMappingGroup = g;
		_vm.SwitchGroupCommand.Execute(g);
	}

	private static Border? FindMappingAncestorTabBorder(DependencyObject child)
	{
		while (child != null)
		{
			if (child is Border border && border.DataContext is MappingGroup)
			{
				return border;
			}
			child = VisualTreeHelper.GetParent(child);
		}
		return null;
	}

	private bool IsMappingMouseBeyondLastTab(Point mousePosRelativeToWindow)
	{
		ItemsControl itemsControl = TabsItemsControl;
		if (itemsControl != null && itemsControl.Items.Count > 0)
		{
			int lastIndex = itemsControl.Items.Count - 1;
			if (itemsControl.ItemContainerGenerator.ContainerFromIndex(lastIndex) is FrameworkElement { IsVisible: not false } lastContainer)
			{
				try
				{
					Point rightEdge = lastContainer.TransformToAncestor(this).Transform(new Point(lastContainer.ActualWidth, 0.0));
					Point topEdge = lastContainer.TransformToAncestor(this).Transform(new Point(0.0, 0.0));
					Point bottomEdge = lastContainer.TransformToAncestor(this).Transform(new Point(0.0, lastContainer.ActualHeight));
					return mousePosRelativeToWindow.X > rightEdge.X && mousePosRelativeToWindow.Y >= topEdge.Y - 10.0 && mousePosRelativeToWindow.Y <= bottomEdge.Y + 10.0;
				}
				catch
				{
					return false;
				}
			}
		}
		return false;
	}

	private void CreateMappingTabDragGhost(FrameworkElement source)
	{
		DestroyMappingTabDragGhost();
		Point pos = source.TranslatePoint(new Point(0.0, 0.0), DragOverlay);
		string label = FindVisualChild<System.Windows.Controls.TextBox>(source)?.Text ?? "分组";
		_mappingTabDragGhost = new Border
		{
			Width = source.ActualWidth,
			Height = source.ActualHeight,
			Background = new SolidColorBrush(Color.FromArgb(140, 68, 68, 68)),
			CornerRadius = new CornerRadius(4.0),
			Opacity = 0.9,
			IsHitTestVisible = false,
			Child = new TextBlock
			{
				Text = label,
				Foreground = Brushes.White,
				FontSize = 12.0,
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
				Margin = new Thickness(8.0, 3.0, 8.0, 3.0)
			}
		};
		Canvas.SetLeft(_mappingTabDragGhost, pos.X);
		Canvas.SetTop(_mappingTabDragGhost, pos.Y);
		DragOverlay.Children.Add(_mappingTabDragGhost);
	}

	private void DestroyMappingTabDragGhost()
	{
		if (_mappingTabDragGhost != null)
		{
			DragOverlay.Children.Remove(_mappingTabDragGhost);
			_mappingTabDragGhost = null;
		}
	}

	private void TabName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 2 && sender is System.Windows.Controls.TextBox { DataContext: MappingGroup grp } tb)
		{
			_tabOriginalName = grp.Name;
			_isEditingTab = true;
			if (_isEditingTitle)
			{
				LeaveTitleEditMode();
			}
			ActivateForTabEdit();
			tb.IsReadOnly = false;
			tb.Focus();
			tb.Select(tb.Text.Length, 0);
			e.Handled = true;
		}
	}

	private void TabNameCancelEdit()
	{
		foreach (object item in (IEnumerable)TabsItemsControl.Items)
		{
			if (TabsItemsControl.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container)
			{
				System.Windows.Controls.TextBox tb = FindVisualChild<System.Windows.Controls.TextBox>(container);
				if (tb != null && !tb.IsReadOnly)
				{
					tb.IsReadOnly = true;
				}
			}
		}
	}

	private void TabName_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (sender is System.Windows.Controls.TextBox tb)
		{
			if (string.IsNullOrWhiteSpace(tb.Text))
			{
				tb.Text = _tabOriginalName;
			}
			tb.IsReadOnly = true;
		}
		_isEditingTab = false;
		DeactivateWindowAfterEdit();
		_vm.SaveState();
	}

	private void TabName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (sender is System.Windows.Controls.TextBox tb)
		{
			if (e.Key == Key.Return)
			{
				tb.IsReadOnly = true;
				Keyboard.ClearFocus();
				e.Handled = true;
			}
			else if (e.Key == Key.Escape)
			{
				tb.Text = _tabOriginalName;
				tb.IsReadOnly = true;
				Keyboard.ClearFocus();
				e.Handled = true;
			}
		}
	}

	private void AddGroup_Click(object sender, RoutedEventArgs e)
	{
		_vm.AddGroup();
	}

	private void UpdateTabScrollButtons()
	{
		if (TabScrollViewer != null)
		{
			bool overflow = TabScrollViewer.ScrollableWidth > 1.0;
			BtnScrollLeft.Visibility = ((!overflow) ? Visibility.Collapsed : Visibility.Visible);
			BtnScrollRight.Visibility = ((!overflow) ? Visibility.Collapsed : Visibility.Visible);
		}
	}

	private void TabScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (TabScrollViewer != null && !(TabScrollViewer.ScrollableWidth <= 0.0))
		{
			double delta = ((e.Delta > 0) ? (-60.0) : 60.0);
			double newOffset = TabScrollViewer.HorizontalOffset + delta;
			newOffset = Math.Max(0.0, Math.Min(TabScrollViewer.ScrollableWidth, newOffset));
			TabScrollViewer.ScrollToHorizontalOffset(newOffset);
			e.Handled = true;
		}
	}

	private void TabScrollLeft_Click(object sender, RoutedEventArgs e)
	{
		if (TabScrollViewer != null && TabScrollViewer.ScrollableWidth > 0.0)
		{
			TabScrollViewer.ScrollToHorizontalOffset(Math.Max(0.0, TabScrollViewer.HorizontalOffset - 80.0));
		}
	}

	private void TabScrollRight_Click(object sender, RoutedEventArgs e)
	{
		if (TabScrollViewer != null && TabScrollViewer.ScrollableWidth > 0.0)
		{
			TabScrollViewer.ScrollToHorizontalOffset(Math.Min(TabScrollViewer.ScrollableWidth, TabScrollViewer.HorizontalOffset + 80.0));
		}
	}

	private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		CommitTitleEdit();
		if (!_vm.IsLocked)
		{
			StartNativeDrag();
			e.Handled = true;
		}
	}

	private void TabItem_RightClick(object sender, MouseButtonEventArgs e)
	{
		e.Handled = true;
	}

	private void TabContext_Rename(object sender, RoutedEventArgs e)
	{
		if (!(sender is MenuItem { Parent: ContextMenu { PlacementTarget: Border { DataContext: MappingGroup grp } border } }))
		{
			return;
		}
		System.Windows.Controls.TextBox tb = FindVisualChild<System.Windows.Controls.TextBox>(border);
		if (tb != null)
		{
			_tabOriginalName = grp.Name;
			_isEditingTab = true;
			if (_isEditingTitle)
			{
				LeaveTitleEditMode();
			}
			ActivateForTabEdit();
			tb.IsReadOnly = false;
			tb.Focus();
			tb.Select(tb.Text.Length, 0);
		}
	}

	private void TabContext_Delete(object sender, RoutedEventArgs e)
	{
		if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: Border { DataContext: MappingGroup grp } } })
		{
			_vm.DeleteGroupCommand.Execute(grp);
		}
	}

	private void TabContext_SetPath(object sender, RoutedEventArgs e)
	{
		if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: Border { DataContext: MappingGroup grp } } })
		{
			_vm.SetGroupPathCommand.Execute(grp);
		}
	}

	private void TabContext_OpenPath(object sender, RoutedEventArgs e)
	{
		if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: Border { DataContext: MappingGroup grp } } })
		{
			OpenGroupPath(grp);
		}
	}

	public void OpenGroupPath(MappingGroup grp)
	{
		try
		{
			if (grp == null || string.IsNullOrEmpty(grp.FolderPath))
			{
				System.Windows.MessageBox.Show("分组「" + grp?.Name + "」尚未设置映射路径。\n请先在右键菜单中选择「设置映射路径」。", "打开路径", MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}
			if (!Directory.Exists(grp.FolderPath))
			{
				System.Windows.MessageBox.Show("映射路径不存在：\n" + grp.FolderPath + "\\n\n该目录可能已被移动或删除。\n请重新设置映射路径。", "打开路径", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			string fpG = grp.FolderPath;
			Task.Run(delegate
			{
				try
				{
					Process.Start("explorer.exe", "\"" + fpG + "\"");
				}
				catch
				{
				}
			});
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("打开路径失败：\n" + ex.Message, "打开路径", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void RemoveNoActivateForDrag()
	{
		try
		{
			if (_host.HostHwnd != IntPtr.Zero)
			{
				nint hwnd = _host.HostHwnd;
				int exStyle = NativeMethods.GetWindowLong(hwnd, -20);
				bool changed = false;
				if ((exStyle & 0x8000000) != 0)
				{
					exStyle &= -134217729;
					changed = true;
				}
				if ((exStyle & 0x80) != 0)
				{
					exStyle &= -129;
					changed = true;
				}
				if (changed)
				{
					NativeMethods.SetWindowLong(hwnd, -20, exStyle);
					NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 55u);
				}
			}
		}
		catch
		{
		}
	}

	private void RestoreNoActivateAfterDrag()
	{
		try
		{
			if (_host.HostHwnd != IntPtr.Zero)
			{
				nint hwnd = _host.HostHwnd;
				int exStyle = NativeMethods.GetWindowLong(hwnd, -20);
				bool changed = false;
				if ((exStyle & 0x8000000) == 0)
				{
					exStyle |= 0x8000000;
					changed = true;
				}
				if (changed)
				{
					NativeMethods.SetWindowLong(hwnd, -20, exStyle);
					NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 55u);
				}
			}
		}
		catch
		{
		}
	}

	private void FileItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		CommitTitleEdit();
		if (sender is FrameworkElement { DataContext: MappingFileItem file } fe)
		{
			_fileDragStartPoint = e.GetPosition(this);
			_draggingFileItem = file;
			_fileDragActive = false;
			fe.CaptureMouse();
			e.Handled = true;
		}
	}

	private void FileItem_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (_draggingFileItem == null || e.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}
		Point pos = e.GetPosition(this);
		double dx = pos.X - _fileDragStartPoint.X;
		double dy = pos.Y - _fileDragStartPoint.Y;
		if (!_fileDragActive && (!(Math.Abs(dx) < 6.0) || !(Math.Abs(dy) < 6.0)))
		{
			_fileDragActive = true;
			if (sender is FrameworkElement fe)
			{
				fe.ReleaseMouseCapture();
			}
			StartFileDragDrop(_draggingFileItem);
		}
	}

	private void FileItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (sender is FrameworkElement fe)
		{
			fe.ReleaseMouseCapture();
		}
		MappingFileItem file = _draggingFileItem;
		_draggingFileItem = null;
		if (_fileDragActive)
		{
			_fileDragActive = false;
			return;
		}
		_fileDragActive = false;
		if (file != null)
		{
			DateTime now = DateTime.Now;
			if (_lastClickedFile == file && (now - _lastFileClick).TotalMilliseconds < 400.0)
			{
				LaunchFile(file);
				_lastClickedFile = null;
			}
			else
			{
				_lastClickedFile = file;
				_lastFileClick = now;
			}
		}
	}

	private void CancelFileDrag()
	{
		if (_fileDragActive)
		{
			_draggingFileItem = null;
			_fileDragActive = false;
		}
	}

	private void OnMappingOleGiveFeedback(object sender, System.Windows.GiveFeedbackEventArgs e)
	{
		NativeMethods.GetCursorPos(out var pt);
		_oleEffectiveTargetIsDesktop = ZoneWindow.IsDesktopWindowUnderMouse(new Point(pt.X, pt.Y));
		e.UseDefaultCursors = true;
		e.Handled = true;
	}

	private void OnMappingOleQueryContinueDrag(object sender, System.Windows.QueryContinueDragEventArgs e)
	{
	}

	private void StartFileDragDrop(MappingFileItem file)
	{
		if (string.IsNullOrEmpty(file.FilePath))
		{
			return;
		}
		_oleEffectiveTargetIsDesktop = false;
		base.GiveFeedback += OnMappingOleGiveFeedback;
		base.QueryContinueDrag += OnMappingOleQueryContinueDrag;
		try
		{
			RemoveNoActivateForDrag();
			_isSelfDragOut = true;
			App.IsInternalDrag = true;
			System.Windows.DataObject dataObj = new System.Windows.DataObject();
			StringCollection files = new StringCollection { file.FilePath };
			dataObj.SetFileDropList(files);
			dataObj.SetData("DesktopToolInternalDrag", file.FilePath);
			DragDrop.DoDragDrop(this, dataObj, System.Windows.DragDropEffects.Copy);
		}
		catch (Exception)
		{
		}
		finally
		{
			base.QueryContinueDrag -= OnMappingOleQueryContinueDrag;
			base.GiveFeedback -= OnMappingOleGiveFeedback;
			_isSelfDragOut = false;
			App.IsInternalDrag = false;
			RestoreNoActivateAfterDrag();
			_draggingFileItem = null;
			_fileDragActive = false;
			_oleEffectiveTargetIsDesktop = false;
		}
	}

	private void FileItem_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (_vm.IsLocked)
		{
			StartNativeDrag();
			e.Handled = true;
			return;
		}
		if (sender is FrameworkElement fe)
		{
			_contextFileItem = fe.DataContext as MappingFileItem;
		}
		e.Handled = true;
	}

	private void LaunchFile(MappingFileItem file)
	{
		try
		{
			string fp = file.FilePath;
			Task.Run(delegate
			{
				try
				{
					if (Directory.Exists(fp))
					{
						NativeMethods.ShellExecuteOpenAsync(fp, IntPtr.Zero);
					}
					else
					{
						NativeMethods.ShellExecuteOpenAsync(fp, IntPtr.Zero);
					}
				}
				catch
				{
				}
			});
		}
		catch (Exception)
		{
		}
	}

	private void FileItem_ToolTipOpening(object sender, ToolTipEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: MappingFileItem file } fe)
		{
			string type = GetFileTypeString(file.Extension, file.IsDirectory);
			string size = FormatMappingFileSize(file);
			string modified = file.LastWriteTime.ToString("yyyy/M/d HH:mm");
			fe.ToolTip = BuildMappingToolTipContent(file.FileName, type, size, modified);
		}
	}

	private static FrameworkElement BuildMappingToolTipContent(string fileName, string type, string size, string modified)
	{
		StackPanel sp = new StackPanel();
		sp.Children.Add(new TextBlock
		{
			Text = fileName,
			Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
			FontSize = 12.0,
			FontWeight = FontWeights.Bold,
			TextWrapping = TextWrapping.Wrap,
			MaxWidth = 360.0
		});
		sp.Children.Add(new TextBlock
		{
			Text = "类型: " + type,
			Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
			FontSize = 11.0,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		});
		sp.Children.Add(new TextBlock
		{
			Text = "大小: " + size,
			Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
			FontSize = 11.0,
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
		});
		sp.Children.Add(new TextBlock
		{
			Text = "修改日期: " + modified,
			Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
			FontSize = 11.0,
			Margin = new Thickness(0.0, 1.0, 0.0, 0.0)
		});
		return new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(240, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			CornerRadius = new CornerRadius(4.0),
			Padding = new Thickness(10.0, 8.0, 10.0, 8.0),
			Child = sp
		};
	}

	private static string GetFileTypeString(string ext, bool isDirectory)
	{
		if (isDirectory)
		{
			return "文件夹";
		}
		switch (ext.ToLower())
		{
		case ".txt":
		case ".md":
		case ".log":
		case ".ini":
		case ".cfg":
		case ".json":
		case ".xml":
		case ".yaml":
		case ".yml":
			return "文本文档";
		case ".doc":
		case ".docx":
			return "Word 文档";
		case ".xls":
		case ".xlsx":
		case ".csv":
			return "Excel 表格";
		case ".ppt":
		case ".pptx":
			return "PowerPoint 演示";
		case ".pdf":
			return "PDF 文档";
		case ".zip":
		case ".rar":
		case ".7z":
		case ".tar":
		case ".gz":
			return "压缩文件";
		case ".exe":
		case ".msi":
		case ".bat":
		case ".cmd":
			return "应用程序";
		case ".lnk":
			return "快捷方式";
		case ".png":
		case ".jpg":
		case ".jpeg":
		case ".gif":
		case ".bmp":
		case ".webp":
		case ".svg":
		case ".ico":
			return "图片文件";
		case ".mp4":
		case ".mkv":
		case ".mov":
		case ".avi":
		case ".wmv":
		case ".flv":
		case ".webm":
			return "视频文件";
		case ".mp3":
		case ".aac":
		case ".wav":
		case ".flac":
		case ".ogg":
		case ".wma":
			return "音频文件";
		case ".dll":
		case ".sys":
		case ".ocx":
			return "系统文件";
		default:
		{
			object result;
			if (string.IsNullOrEmpty(ext))
			{
				result = "文件";
			}
			else
			{
				result = ext.Substring(1, ext.Length - 1).ToUpper() + " 文件";
			}
			return (string)result;
		}
		}
	}

	private static string FormatMappingFileSize(MappingFileItem file)
	{
		if (file.IsDirectory)
		{
			try
			{
				if (Directory.Exists(file.FilePath))
				{
					return new DirectoryInfo(file.FilePath).GetFileSystemInfos().Length + " 个项目";
				}
			}
			catch
			{
			}
			return "-";
		}
		long bytes = file.FileSize;
		if (bytes >= 1024)
		{
			if (bytes >= 1048576)
			{
				if (bytes >= 1073741824)
				{
					return $"{(double)bytes / 1073741824.0:F2} GB";
				}
				return $"{(double)bytes / 1048576.0:F2} MB";
			}
			return $"{(double)bytes / 1024.0:F2} KB";
		}
		return $"{bytes} B";
	}

	private MappingFileItem? GetFileItemFromContextMenu(object sender)
	{
		MappingFileItem file = GetFileItemFromContextMenuCore(sender);
		if (file == null)
		{
			file = _contextFileItem;
		}
		return file;
	}

	private static MappingFileItem? GetFileItemFromContextMenuCore(object sender)
	{
		DependencyObject current = sender as DependencyObject;
		while (current != null)
		{
			if (current is FrameworkElement { DataContext: MappingFileItem file })
			{
				return file;
			}
			if (current is ContextMenu { PlacementTarget: FrameworkElement { DataContext: MappingFileItem cfile } })
			{
				return cfile;
			}
			current = VisualTreeHelper.GetParent(current);
			if (current is MenuItem)
			{
				DependencyObject logicalParent = LogicalTreeHelper.GetParent(current);
				if (logicalParent is ContextMenu { PlacementTarget: FrameworkElement { DataContext: MappingFileItem lfile } })
				{
					return lfile;
				}
				current = logicalParent;
			}
		}
		return null;
	}

	private void MappingFileContextMenu_Opening(object? sender, ContextMenuEventArgs e)
	{
		ContextMenu menu = (sender as ContextMenu) ?? (sender as FrameworkElement)?.ContextMenu;
		if (menu != null)
		{
			bool canRun = NativeMethods.CanRunAsAdmin(GetFileItemFromContextMenu(sender)?.FilePath);
			SetupRunAsAdminMenu(menu, canRun);
		}
	}

	private static void SetupRunAsAdminMenu(ContextMenu menu, bool canRun)
	{
		foreach (object item in (IEnumerable)menu.Items)
		{
			if (item is MenuItem mi && mi.Header as string == "以管理员身份运行")
			{
				if (NativeMethods.IsRunningAsMsix())
				{
					mi.Visibility = Visibility.Visible;
					mi.IsEnabled = false;
					mi.ToolTip = "微软商店版(MSIX)受系统沙箱限制，无法以管理员身份运行。请使用官网的独立安装版或绿色版。";
				}
				else
				{
					mi.Visibility = ((!canRun) ? Visibility.Collapsed : Visibility.Visible);
					mi.IsEnabled = canRun;
					mi.ToolTip = null;
				}
			}
		}
	}

	private void ContextMenu_RunAsAdmin(object sender, RoutedEventArgs e)
	{
		if (NativeMethods.IsRunningAsMsix())
		{
			return;
		}
		MappingFileItem file = GetFileItemFromContextMenu(sender);
		if (file != null)
		{
			string path = file.FilePath;
			if (NativeMethods.CanRunAsAdmin(path))
			{
				NativeMethods.ShellExecuteOpenAsync(path, IntPtr.Zero, "runas");
			}
		}
	}

	private void ContextMenu_Open(object sender, RoutedEventArgs e)
	{
		MappingFileItem file = GetFileItemFromContextMenu(sender);
		if (file != null)
		{
			LaunchFile(file);
		}
	}

	private void ContextMenu_OpenFolder(object sender, RoutedEventArgs e)
	{
		MappingFileItem file = GetFileItemFromContextMenu(sender);
		if (file == null || (!File.Exists(file.FilePath) && !Directory.Exists(file.FilePath)))
		{
			return;
		}
		try
		{
			string openPath = file.FilePath;
			if (file.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(file.FilePath))
			{
				string target = ResolveMappingShortcutTarget(file.FilePath);
				if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
				{
					openPath = target;
				}
			}
			string fPath = openPath;
			Task.Run(delegate
			{
				try
				{
					NativeMethods.ShellExecuteOpenAsync("explorer.exe", IntPtr.Zero, "open", "/select,\"" + fPath + "\"");
				}
				catch
				{
				}
			});
		}
		catch (Exception)
		{
		}
	}

	private static string? ResolveMappingShortcutTarget(string lnkPath)
	{
		try
		{
			Type shellType = Type.GetTypeFromProgID("WScript.Shell");
			if (shellType == null)
			{
				return null;
			}
			dynamic shell = Activator.CreateInstance(shellType);
			try
			{
				dynamic shortcut = shell.CreateShortcut(lnkPath);
				try
				{
					string target = shortcut.TargetPath as string;
					if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
					{
						return target;
					}
					return null;
				}
				finally
				{
					if (shortcut != null)
					{
						Marshal.ReleaseComObject(shortcut);
					}
				}
			}
			finally
			{
				if (shell != null)
				{
					Marshal.ReleaseComObject(shell);
				}
			}
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void ContextMenu_RenameFile(object sender, RoutedEventArgs e)
	{
		MappingFileItem file = GetFileItemFromContextMenu(sender);
		if (file == null || string.IsNullOrEmpty(file.FilePath) || (!File.Exists(file.FilePath) && !Directory.Exists(file.FilePath)))
		{
			return;
		}
		string dir = Path.GetDirectoryName(file.FilePath);
		bool num = Directory.Exists(file.FilePath);
		string ext = (num ? "" : Path.GetExtension(file.FilePath));
		string defaultName = (num ? Path.GetFileName(file.FilePath) : Path.GetFileNameWithoutExtension(file.DisplayName ?? Path.GetFileName(file.FilePath)));
		InputDialog dialog = new InputDialog("请输入新文件名：", "重命名", defaultName)
		{
			Owner = System.Windows.Application.Current.MainWindow
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		string newName = dialog.ResponseText.Trim();
		if (string.IsNullOrEmpty(newName))
		{
			return;
		}
		string newFullPath = Path.Combine(dir ?? "", newName + ext);
		if (string.Equals(file.FilePath, newFullPath, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		if (File.Exists(newFullPath) || Directory.Exists(newFullPath))
		{
			System.Windows.MessageBox.Show("已存在同名文件，无法完成重命名。", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		try
		{
			if (File.Exists(file.FilePath))
			{
				File.Move(file.FilePath, newFullPath);
			}
			else
			{
				if (!Directory.Exists(file.FilePath))
				{
					return;
				}
				Directory.Move(file.FilePath, newFullPath);
			}
			_vm.RefreshFiles();
			DesktopIconService.NotifyDesktopChange(newFullPath);
			_vm.SaveState();
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("重命名失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void ContextMenu_Properties(object sender, RoutedEventArgs e)
	{
		MappingFileItem file = GetFileItemFromContextMenu(sender);
		if (file == null || string.IsNullOrEmpty(file.FilePath) || (!File.Exists(file.FilePath) && !Directory.Exists(file.FilePath)))
		{
			return;
		}
		try
		{
			nint hwnd = _host.HostHwnd;
			if (!NativeMethods.SHObjectProperties(hwnd, 2u, file.FilePath, null))
			{
				if (hwnd != IntPtr.Zero)
				{
					NativeMethods.ShellExecute(hwnd, "open", "explorer.exe", "/select,\"" + file.FilePath + "\"", null, 1);
					return;
				}
				Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file.FilePath + "\"")
				{
					UseShellExecute = true
				});
			}
		}
		catch (Exception)
		{
		}
	}

	private void ContextMenu_Copy(object sender, RoutedEventArgs e)
	{
		MappingFileItem file = GetFileItemFromContextMenu(sender);
		if (file == null || string.IsNullOrEmpty(file.FilePath) || (!File.Exists(file.FilePath) && !Directory.Exists(file.FilePath)))
		{
			return;
		}
		try
		{
			System.Windows.Clipboard.SetFileDropList(new StringCollection { file.FilePath });
		}
		catch (Exception)
		{
		}
	}

	private void ContextMenu_Recycle(object sender, RoutedEventArgs e)
	{
		MappingFileItem file = GetFileItemFromContextMenu(sender);
		if (file == null || string.IsNullOrEmpty(file.FilePath) || (!File.Exists(file.FilePath) && !Directory.Exists(file.FilePath)) || System.Windows.MessageBox.Show("确定要将文件「" + file.DisplayName + "」移入回收站吗？\n\n移入后可从回收站还原。", "移入回收站", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
		{
			return;
		}
		try
		{
			NativeMethods.SHFILEOPSTRUCT fileOp = new NativeMethods.SHFILEOPSTRUCT
			{
				hwnd = IntPtr.Zero,
				wFunc = 3u,
				pFrom = file.FilePath + "\0\0",
				fFlags = 16464
			};
			int ret = NativeMethods.SHFileOperation(ref fileOp);
			if (ret != 0)
			{
				System.Windows.MessageBox.Show($"移入回收站失败（错误码：{ret}）", "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
			else
			{
				if (_vm.ActiveGroup?.Files != null)
				{
					_vm.ActiveGroup.Files.Remove(file);
				}
				_vm.SaveState();
			}
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("移入回收站失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void IconsArea_DragEnter(object sender, System.Windows.DragEventArgs e)
	{
		if (App.IsInternalDrag && !e.Data.GetDataPresent("DesktopToolInternalDrag"))
		{
			App.IsInternalDrag = false;
		}
		if (_isSelfDragOut || (App.IsInternalDrag && e.Data.GetDataPresent("DesktopToolInternalDrag")) || !HasValidDropTarget() || !e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
		{
			e.Effects = System.Windows.DragDropEffects.None;
		}
		else
		{
			e.Effects = System.Windows.DragDropEffects.Copy;
		}
		e.Handled = true;
	}

	private void IconsArea_DragOver(object sender, System.Windows.DragEventArgs e)
	{
		if (App.IsInternalDrag && !e.Data.GetDataPresent("DesktopToolInternalDrag"))
		{
			App.IsInternalDrag = false;
		}
		if (_isSelfDragOut || (App.IsInternalDrag && e.Data.GetDataPresent("DesktopToolInternalDrag")) || !HasValidDropTarget() || !e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
		{
			e.Effects = System.Windows.DragDropEffects.None;
		}
		else
		{
			e.Effects = System.Windows.DragDropEffects.Copy;
		}
		e.Handled = true;
	}

	private void IconsArea_GiveFeedback(object sender, System.Windows.GiveFeedbackEventArgs e)
	{
		e.UseDefaultCursors = true;
		e.Handled = true;
	}

	private void IconsArea_Drop(object sender, System.Windows.DragEventArgs e)
	{
		e.Effects = System.Windows.DragDropEffects.None;
		e.Handled = true;
		if (!_isSelfDragOut && (!App.IsInternalDrag || !e.Data.GetDataPresent("DesktopToolInternalDrag")) && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length != 0)
		{
			ProcessMappingDrop(files);
		}
	}

	private bool HasValidDropTarget()
	{
		if (_vm.ActiveGroup != null && !string.IsNullOrEmpty(_vm.ActiveGroup.FolderPath))
		{
			return Directory.Exists(_vm.ActiveGroup.FolderPath);
		}
		return false;
	}

	private async void ProcessMappingDrop(string[] files)
	{
		string targetDir = _vm.ActiveGroup?.FolderPath;
		if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
		{
			System.Windows.MessageBox.Show("当前分组尚未设置映射路径。\n请先在右键菜单中选择「设置映射路径」。", "无法复制", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		int copied = 0;
		int skipped = 0;
		List<string> errors = new List<string>();
		foreach (string file in files)
		{
			try
			{
				string fullPath = Path.GetFullPath(file);
				string destPath = Path.Combine(targetDir, Path.GetFileName(file));
				bool overwrite = false;
				if (File.Exists(destPath) || Directory.Exists(destPath))
				{
					string type = (Directory.Exists(destPath) ? "文件夹" : "文件");
					switch (System.Windows.MessageBox.Show($"目标位置已存在同名{type}：\n\n{Path.GetFileName(file)}\n\n是否覆盖？\n\n点击「是」覆盖已有{type}\n点击「否」跳过此{type}\n点击「取消」停止所有复制", "确认覆盖", MessageBoxButton.YesNoCancel, MessageBoxImage.Question))
					{
					case MessageBoxResult.Cancel:
						skipped += files.Length - copied - skipped;
						goto end_IL_041f;
					case MessageBoxResult.No:
						skipped++;
						goto end_IL_00bb;
					default:
						overwrite = true;
						if (Directory.Exists(destPath))
						{
							await Task.Run(delegate
							{
								Directory.Delete(destPath, recursive: true);
							});
						}
						break;
					}
				}
				if (File.Exists(fullPath))
				{
					await Task.Run(delegate
					{
						File.Copy(fullPath, destPath, overwrite);
					});
					copied++;
				}
				else if (Directory.Exists(fullPath))
				{
					await Task.Run(delegate
					{
						CopyDirectoryRecursive(fullPath, destPath);
					});
					copied++;
				}
				end_IL_00bb:;
			}
			catch (Exception ex)
			{
				errors.Add(Path.GetFileName(file) + "：" + ex.Message);
			}
			continue;
			end_IL_041f:
			break;
		}
		_vm.RefreshFiles();
		if (errors.Count > 0)
		{
			System.Windows.MessageBox.Show(((copied > 0) ? $"成功复制 {copied} 个项目。" : "") + ((skipped > 0) ? (((copied > 0) ? "\n" : "") + $"跳过 {skipped} 个项目。") : "") + "\n\n" + ((errors.Count <= 3) ? string.Join("\n", from x in errors.Take(3)
				select "• " + x) : (string.Join("\n", from x in errors.Take(3)
				select "• " + x) + $"\n• ... 等 {errors.Count} 个失败项")), "复制结果", MessageBoxButton.OK, (copied > 0) ? MessageBoxImage.Asterisk : MessageBoxImage.Exclamation);
		}
	}

	private static void CopyDirectoryRecursive(string sourceDir, string destDir)
	{
		Directory.CreateDirectory(destDir);
		string[] files = Directory.GetFiles(sourceDir);
		foreach (string file in files)
		{
			string destFile = Path.Combine(destDir, Path.GetFileName(file));
			File.Copy(file, destFile, overwrite: false);
		}
		files = Directory.GetDirectories(sourceDir);
		foreach (string subDir in files)
		{
			string destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
			CopyDirectoryRecursive(subDir, destSubDir);
		}
	}

	private void RefreshButton_Click(object sender, RoutedEventArgs e)
	{
		_vm.RefreshFiles(force: true);
	}

	private CustomPopupPlacement[] PlaceMorePopup(Size popupSize, Size targetSize, Point offset)
	{
		try
		{
			Point screenTopLeft = MoreButton.PointToScreen(new Point(0.0, 0.0));
			Rect wa = SystemParameters.WorkArea;
			double btnBottom = screenTopLeft.Y + targetSize.Height;
			double spaceBelow = wa.Bottom - btnBottom;
			double spaceAbove = screenTopLeft.Y - wa.Top;
			if (spaceBelow >= popupSize.Height)
			{
				return new CustomPopupPlacement[1]
				{
					new CustomPopupPlacement(new Point(0.0, targetSize.Height), PopupPrimaryAxis.Vertical)
				};
			}
			if (spaceAbove >= popupSize.Height)
			{
				return new CustomPopupPlacement[1]
				{
					new CustomPopupPlacement(new Point(0.0, 0.0 - popupSize.Height), PopupPrimaryAxis.Vertical)
				};
			}
			if (spaceAbove >= spaceBelow)
			{
				return new CustomPopupPlacement[1]
				{
					new CustomPopupPlacement(new Point(0.0, 0.0 - popupSize.Height), PopupPrimaryAxis.Vertical)
				};
			}
			return new CustomPopupPlacement[1]
			{
				new CustomPopupPlacement(new Point(0.0, targetSize.Height), PopupPrimaryAxis.Vertical)
			};
		}
		catch
		{
			return new CustomPopupPlacement[1]
			{
				new CustomPopupPlacement(new Point(0.0, targetSize.Height), PopupPrimaryAxis.Vertical)
			};
		}
	}

	private void MoreButton_Click(object sender, RoutedEventArgs e)
	{
		MorePopup.IsOpen = !MorePopup.IsOpen;
		if (MorePopup.IsOpen)
		{
			ContextMenuAutoCloser.Attach(MorePopup);
			ForceTopMost();
		}
		else
		{
			ContextMenuAutoCloser.Detach(MorePopup);
		}
	}

	private void MoreMenuItem_Click(object sender, RoutedEventArgs e)
	{
		MorePopup.IsOpen = false;
		ContextMenuAutoCloser.Detach(MorePopup);
	}

	private void MorePopup_Closed(object sender, EventArgs e)
	{
		ContextMenuAutoCloser.Detach(MorePopup);
		PushToBottom();
	}

	private void Header_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		bool num = FindVisualParent<System.Windows.Controls.TextBox>(e.OriginalSource as DependencyObject) != null;
		if (!num)
		{
			CommitTitleEdit();
		}
		if (!num && !_vm.IsLocked && FindVisualParent<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) == null && FindVisualParent<System.Windows.Controls.TextBox>(e.OriginalSource as DependencyObject) == null)
		{
			StartNativeDrag();
			e.Handled = true;
		}
	}

	private void StartNativeDrag()
	{
		NativeDragHelper.Begin(_dragHost, 0);
	}

	private double GetMonitorDpi()
	{
		try
		{
			nint mon = NativeMethods.MonitorFromWindow(_host.HostHwnd, 2u);
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

	protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
	{
		base.OnMouseMove(e);
		if (_vm.IsLocked)
		{
			base.Cursor = System.Windows.Input.Cursors.Arrow;
			return;
		}
		Point pos = e.GetPosition(this);
		bool right = pos.X >= base.ActualWidth - 6.0;
		bool left = pos.X <= 6.0;
		bool bottom = pos.Y >= base.ActualHeight - 6.0;
		bool top = pos.Y <= 6.0;
		if ((right && bottom) || (left && top))
		{
			base.Cursor = System.Windows.Input.Cursors.SizeNWSE;
		}
		else if ((right && top) || (left && bottom))
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

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonDown(e);
		if (_vm.IsLocked || e.Handled)
		{
			return;
		}
		Point pos = e.GetPosition(this);
		bool right = pos.X >= base.ActualWidth - 6.0;
		bool left = pos.X <= 6.0;
		bool bottom = pos.Y >= base.ActualHeight - 6.0;
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

	private Point GetCursorPosScreen()
	{
		NativeMethods.GetCursorPos(out var pt);
		return new Point(pt.X, pt.Y);
	}

	private void UpdateResizeGlow()
	{
		Rect currentRect = NativeDragHelper.GetDipRect(_host.HostHwnd);
		WidgetAlignGlowService.UpdateAlignment(_host.HostHwnd, currentRect, WindowSnapService.EdgeMask.All);
	}

	private void ClearResizeGlow()
	{
		WidgetAlignGlowService.ClearGlows();
	}

	private void SaveBounds()
	{
		_vm.WindowLeft = _host.Left;
		_vm.WindowTop = _host.Top;
		_vm.WindowWidth = _host.Width;
		if ((!_vm.IsCollapsed || _vm.IsHoverExpanded) && Math.Abs(_host.Height - 52.0) > 1.0)
		{
			_vm.WindowHeight = _host.Height;
		}
		_vm.SaveState();
	}

	private void RootGrid_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
	{
		_buttonHideTimer?.Stop();
		TitleButtonsPanel.Opacity = 1.0;
		if (_vm.IsCollapsed)
		{
			_vm.IsHoverExpanded = true;
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
			EnsureCollapseWatcher();
			if ((!_vm.IsCollapsed || !_vm.IsHoverExpanded || !IsCursorOverWindow()) && !_contextMenuOpen)
			{
				TitleButtonsPanel.Opacity = 0.0;
				if ((!base.IsKeyboardFocusWithin || !(Keyboard.FocusedElement is System.Windows.Controls.TextBox)) && _vm.IsCollapsed)
				{
					_vm.IsHoverExpanded = false;
					SetCollapsedVisualHeight();
					_host.ResizeOnly(_host.Width, CollapsedPhysicalHeight());
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
			DependencyObject p = cm.PlacementTarget;
			bool belongsToSelf = false;
			while (p != null)
			{
				if (p == this)
				{
					belongsToSelf = true;
					break;
				}
				p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p);
			}
			if (!belongsToSelf)
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
		if (!(sender is ContextMenu cm) || (_openContextMenu != null && cm != _openContextMenu))
		{
			return;
		}
		_openContextMenu = null;
		_contextMenuOpen = false;
		ContextMenuAutoCloser.Detach(cm);
		_contextFileItem = null;
		if (_vm.IsCollapsed)
		{
			EnsureCollapseWatcher();
			if (!IsCursorOverWindow())
			{
				_buttonHideTimer?.Stop();
				TitleButtonsPanel.Opacity = 0.0;
				_vm.IsHoverExpanded = false;
				SetCollapsedVisualHeight();
				_host.ResizeOnly(_host.Width, CollapsedPhysicalHeight());
			}
		}
		PushToBottom();
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		DismissMappingZone();
	}

	private void DismissButton_Click(object sender, RoutedEventArgs e)
	{
		MorePopup.IsOpen = false;
		DismissMappingZone();
	}

	private void DismissMappingZone()
	{
		if (System.Windows.MessageBox.Show("确定要解散映射区「" + _vm.Name + "」吗？\n\n解散后可从主面板「映射区管理」中重新打开。", "解散映射区", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			_owner.CloseMappingZone();
		}
	}

	private static MappingWidget? GetWidgetRoot(DependencyObject? o)
	{
		while (o != null)
		{
			if (o is MappingWidget w)
			{
				return w;
			}
			o = VisualTreeHelper.GetParent(o);
		}
		return null;
	}

	private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
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

	private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
	{
		if (parent == null)
		{
			return null;
		}
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(parent, i);
			if (child is T result)
			{
				return result;
			}
			T found = FindVisualChild<T>(child);
			if (found != null)
			{
				return found;
			}
		}
		return null;
	}

	public void RefreshAll()
	{
		_vm.RefreshIconSizes();
		_vm.RefreshAllGroups();
	}

	private int BlurTintAlpha()
	{
		return (int)Math.Max(0.0, Math.Min(255.0, (1.0 - _vm.MainVm.Settings.BackgroundOpacity) * 255.0));
	}

	public void RefreshVisualStyle()
	{
		if (_host.HostHwnd == IntPtr.Zero)
		{
			return;
		}
		nint hwnd = _host.HostHwnd;
		if (Environment.OSVersion.Version.Build < 22000)
		{
			if (_vm.MainVm.IsGlassStyle)
			{
				GlassEffectHelper.EnableBlur(hwnd, BlurTintAlpha());
			}
			else
			{
				GlassEffectHelper.DisableBlur(hwnd);
			}
		}
		else if (_vm.MainVm.IsGlassStyle)
		{
			GlassEffectHelper.EnableAcrylicWithTint(hwnd);
			GlassEffectHelper.ApplyRoundCorners(hwnd, (int)base.ActualWidth, (int)base.ActualHeight);
		}
		else
		{
			GlassEffectHelper.DisableBlur(hwnd);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/views/mappingwidget.xaml", UriKind.Relative);
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
			DragOverlay = (Canvas)target;
			break;
		case 3:
			((Border)target).PreviewMouseLeftButtonDown += Header_PreviewMouseLeftButtonDown;
			break;
		case 4:
			((TextBlock)target).MouseLeftButtonDown += Handle_MouseLeftButtonDown;
			break;
		case 5:
			TitleTextBox = (System.Windows.Controls.TextBox)target;
			TitleTextBox.PreviewMouseLeftButtonDown += TitleTextBox_PreviewMouseLeftButtonDown;
			TitleTextBox.LostKeyboardFocus += TitleTextBox_LostKeyboardFocus;
			TitleTextBox.KeyDown += TitleTextBox_KeyDown;
			break;
		case 6:
			TitleButtonsPanel = (StackPanel)target;
			break;
		case 7:
			MappingSearchBox = (System.Windows.Controls.TextBox)target;
			MappingSearchBox.PreviewMouseLeftButtonDown += MappingSearch_PreviewMouseLeftButtonDown;
			MappingSearchBox.GotFocus += MappingSearch_GotFocus;
			MappingSearchBox.LostKeyboardFocus += MappingSearch_LostKeyboardFocus;
			break;
		case 8:
			MoreButton = (System.Windows.Controls.Button)target;
			MoreButton.Click += MoreButton_Click;
			break;
		case 9:
			MorePopup = (Popup)target;
			MorePopup.Closed += MorePopup_Closed;
			break;
		case 10:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 11:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 12:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 13:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 14:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 15:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 16:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 17:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 18:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 19:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 20:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 21:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 22:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 23:
			((System.Windows.Controls.Button)target).Click += DismissButton_Click;
			break;
		case 24:
			((System.Windows.Controls.Button)target).Click += CloseButton_Click;
			break;
		case 25:
			BtnScrollLeft = (System.Windows.Controls.Button)target;
			BtnScrollLeft.Click += TabScrollLeft_Click;
			break;
		case 26:
			TabScrollViewer = (ScrollViewer)target;
			TabScrollViewer.PreviewMouseWheel += TabScrollViewer_PreviewMouseWheel;
			break;
		case 27:
			TabsItemsControl = (ItemsControl)target;
			break;
		case 34:
			BtnScrollRight = (System.Windows.Controls.Button)target;
			BtnScrollRight.Click += TabScrollRight_Click;
			break;
		case 35:
			((System.Windows.Controls.Button)target).Click += AddGroup_Click;
			break;
		case 36:
			((ScrollViewer)target).DragEnter += IconsArea_DragEnter;
			((ScrollViewer)target).DragOver += IconsArea_DragOver;
			((ScrollViewer)target).Drop += IconsArea_Drop;
			((ScrollViewer)target).GiveFeedback += IconsArea_GiveFeedback;
			break;
		case 37:
			FilesItemsControl = (ItemsControl)target;
			break;
		case 46:
			ListItemsControl = (ItemsControl)target;
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
		case 28:
			((Border)target).PreviewMouseLeftButtonDown += TabItem_PreviewMouseLeftButtonDown;
			((Border)target).PreviewMouseMove += TabItem_PreviewMouseMove;
			((Border)target).PreviewMouseLeftButtonUp += TabItem_PreviewMouseLeftButtonUp;
			((Border)target).MouseRightButtonDown += TabItem_RightClick;
			break;
		case 29:
			((MenuItem)target).Click += TabContext_Rename;
			break;
		case 30:
			((MenuItem)target).Click += TabContext_Delete;
			break;
		case 31:
			((MenuItem)target).Click += TabContext_SetPath;
			break;
		case 32:
			((MenuItem)target).Click += TabContext_OpenPath;
			break;
		case 33:
			((System.Windows.Controls.TextBox)target).MouseLeftButtonDown += TabName_MouseLeftButtonDown;
			((System.Windows.Controls.TextBox)target).LostKeyboardFocus += TabName_LostKeyboardFocus;
			((System.Windows.Controls.TextBox)target).KeyDown += TabName_KeyDown;
			break;
		case 38:
			((Border)target).MouseLeftButtonDown += FileItem_MouseLeftButtonDown;
			((Border)target).MouseMove += FileItem_MouseMove;
			((Border)target).MouseLeftButtonUp += FileItem_MouseLeftButtonUp;
			((Border)target).MouseRightButtonDown += FileItem_MouseRightButtonDown;
			((Border)target).ToolTipOpening += FileItem_ToolTipOpening;
			((Border)target).ContextMenuOpening += MappingFileContextMenu_Opening;
			break;
		case 39:
			((MenuItem)target).Click += ContextMenu_Open;
			break;
		case 40:
			((MenuItem)target).Click += ContextMenu_RunAsAdmin;
			break;
		case 41:
			((MenuItem)target).Click += ContextMenu_OpenFolder;
			break;
		case 42:
			((MenuItem)target).Click += ContextMenu_RenameFile;
			break;
		case 43:
			((MenuItem)target).Click += ContextMenu_Properties;
			break;
		case 44:
			((MenuItem)target).Click += ContextMenu_Copy;
			break;
		case 45:
			((MenuItem)target).Click += ContextMenu_Recycle;
			break;
		case 47:
			((Border)target).MouseLeftButtonDown += FileItem_MouseLeftButtonDown;
			((Border)target).MouseMove += FileItem_MouseMove;
			((Border)target).MouseLeftButtonUp += FileItem_MouseLeftButtonUp;
			((Border)target).MouseRightButtonDown += FileItem_MouseRightButtonDown;
			((Border)target).ToolTipOpening += FileItem_ToolTipOpening;
			((Border)target).ContextMenuOpening += MappingFileContextMenu_Opening;
			break;
		case 48:
			((MenuItem)target).Click += ContextMenu_Open;
			break;
		case 49:
			((MenuItem)target).Click += ContextMenu_RunAsAdmin;
			break;
		case 50:
			((MenuItem)target).Click += ContextMenu_OpenFolder;
			break;
		case 51:
			((MenuItem)target).Click += ContextMenu_RenameFile;
			break;
		case 52:
			((MenuItem)target).Click += ContextMenu_Properties;
			break;
		case 53:
			((MenuItem)target).Click += ContextMenu_Copy;
			break;
		case 54:
			((MenuItem)target).Click += ContextMenu_Recycle;
			break;
		case 34:
		case 35:
		case 36:
		case 37:
		case 46:
			break;
		}
	}
}
