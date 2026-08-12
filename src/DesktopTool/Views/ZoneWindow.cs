using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopTool.Models;
using DesktopTool.Services;
using DesktopTool.ViewModels;
using Microsoft.Win32;

namespace DesktopTool.Views;

public class ZoneWindow : Window, IComponentConnector, IStyleConnector
{
	private readonly ZoneViewModel _viewModel;

	private readonly IconZone _zone;

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

	private DateTime _lastIconClick = DateTime.MinValue;

	private ZoneIcon? _lastClickedIcon;

	private Point _iconDragStartPoint;

	private Point _iconSourcePos;

	private ZoneIcon? _draggingIcon;

	private bool _iconDragActive;

	private Border? _dragGhost;

	private bool _isEditingTitle;

	private bool _isEditingTab;

	private string _tabOriginalName = string.Empty;

	private IconGroup? _draggingTab;

	private Point _tabDragStartPoint;

	private bool _tabDragActive;

	private int _tabSourceIndex;

	private Border? _tabDragGhost;

	private DispatcherTimer? _tabAutoScrollTimer;

	private double _tabAutoScrollDelta;

	private const double TabAutoScrollZone = 28.0;

	private const double TabAutoScrollStep = 24.0;

	private DateTime _lastTabClickTime;

	private IconGroup? _lastClickedTabGroup;

	private int _savedExStyle;

	private DispatcherTimer? _oleReRegTimer;

	private int _oleReRegAttempt;

	private static readonly int[] OleReRegDelaysMs = new int[7] { 200, 700, 1500, 3000, 6000, 10000, 15000 };

	private DispatcherTimer? _snapTimer;

	private DispatcherTimer? _buttonHideTimer;

	private bool _contextMenuOpen;

	private ContextMenu? _openContextMenu;

	private double _savedCollapsedHeight;

	private const double CollapsedHeight = 52.0;

	private DispatcherTimer? _hoverCollapseWatcher;

	private FileSystemWatcher? _desktopWatcher;

	private bool _suppressDesktopWatcher;

	private readonly HashSet<string> _pendingRemoval = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private DispatcherTimer? _watcherDebounceTimer;

	private DateTime _suppressTitleEditUntil = DateTime.MinValue;

	private bool _oleDropCancelledForDesktop;

	private string? _dragTempFilePath;

	private string? _dragTempDir;

	private string? _dragTempDesktopTarget;

	private string[]? _cachedDragFiles;

	private bool _oleHandledDrop;

	private DispatcherTimer? _externalDragZTimer;

	private bool _externalDragElevated;

	private string[]? _dragPreviewFiles;

	private bool _dragPreviewAllDesktop;

	private DispatcherTimer _olePollTimer;

	private int _olePollAttempt;

	private int _oleStableCount;

	private const int MaxOlePollLoops = 25;

	private const int OleStableThreshold = 3;

	private static readonly DateTime _oleProcStart = Process.GetCurrentProcess().StartTime;

	private bool _d3FirstReRegLogged;

	private bool _d3FirstHitLogged;

	private bool _d3FirstDragLogged;

	private int _wndprocCenterX;

	private int _wndprocCenterY;

	private bool _wndprocCenterValid;

	private static bool _uipiDiagLogged;

	private static readonly bool _logEnabled = Environment.GetEnvironmentVariable("DESKTOOL_NOLOG") != "1";

	private static readonly bool _winMsgLogEnabled = Environment.GetEnvironmentVariable("DESKTOOL_WINLOG") == "1";

	private static readonly string _debugLogPath = Path.Combine(App.GetAppDataDir(), "debug_wndproc.log");

	private bool _searchActive;

	private const double NarrowListWidthThreshold = 300.0;

	private DesktopDragHost _dragHost;

	private static readonly string[] AcceptedDragFormats = new string[5] { "Shell IDList Array", "FileNameW", "FileName", "FileGroupDescriptorW", "DragContext" };

	private const string RunAsAdminDisabledTip = "微软商店版(MSIX)受系统沙箱限制，无法以管理员身份运行。请使用官网的独立安装版或绿色版。";

	private DispatcherTimer? _displayChangeTimer;

	private DateTime _displayTransitionUntil = DateTime.MinValue;

	private double _preTransitionX = double.NaN;

	private double _preTransitionY = double.NaN;

	internal Grid RootGrid;

	internal Canvas DragOverlay;

	internal Border LoadingOverlay;

	internal Border LoadingSpinner;

	internal BeginStoryboard SpinnerStoryboard;

	internal TextBlock LoadingText;

	internal TextBlock LoadingDetail;

	internal System.Windows.Controls.TextBox TitleTextBox;

	internal StackPanel TitleButtonsPanel;

	internal System.Windows.Controls.TextBox ZoneSearchBox;

	internal System.Windows.Controls.Button MoreButton;

	internal Popup MorePopup;

	internal System.Windows.Controls.Button BtnScrollLeft;

	internal ScrollViewer TabScrollViewer;

	internal ItemsControl TabsItemsControl;

	internal System.Windows.Controls.Button BtnScrollRight;

	internal ItemsControl IconsItemsControl;

	internal ItemsControl ListItemsControl;

	private bool _contentLoaded;

	internal bool IsDesktopChild { get; private set; }

	public string ZoneId => _zone.Id;

	public ZoneWindow(IconZone zone, MainViewModel mainVm)
	{
		InitializeComponent();
		OleDiag("[OLE][D3] 时间线: ZoneWindow.ctor 进程" + OleElapsed() + " ZoneId=" + zone?.Id);
		TitleTextBox.Focusable = false;
		_zone = zone;
		_viewModel = new ZoneViewModel(zone, mainVm);
		base.DataContext = _viewModel;
		AddRenameMenuItem();
		base.Left = (_zone.PosPhysical ? WindowGeometryPersistence.ToDip(_zone.X) : _zone.X);
		base.Top = (_zone.PosPhysical ? WindowGeometryPersistence.ToDip(_zone.Y) : _zone.Y);
		base.Width = (_zone.PosPhysical ? WindowGeometryPersistence.ToDip(_zone.Width) : _zone.Width);
		double zoneInitH = (_zone.PosPhysical ? WindowGeometryPersistence.ToDip(_zone.Height) : _zone.Height);
		base.Height = ((zoneInitH > 57.0) ? zoneInitH : 420.0);
		_savedCollapsedHeight = base.Height;
		base.LocationChanged += OnLocationChanged;
		base.SizeChanged += OnSizeChanged;
		_viewModel.CollapsedChanged += OnCollapsedChanged;
		if (_viewModel.IsCollapsed)
		{
			ApplyCollapsedSize();
		}
		EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnContextMenuOpened));
		EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler(OnContextMenuClosed));
		_viewModel.Groups.CollectionChanged += OnGroupsChanged;
		base.PreviewKeyDown += delegate(object s, System.Windows.Input.KeyEventArgs e)
		{
			if (e.Key == Key.Escape && _iconDragActive)
			{
				CancelIconDrag();
			}
		};
		SetupDesktopWatcher();
		base.PreviewDragEnter += delegate(object s, System.Windows.DragEventArgs e)
		{
			if (_hwnd != IntPtr.Zero && !_isEditingTitle && !_isEditingTab)
			{
				if (!_d3FirstDragLogged)
				{
					_d3FirstDragLogged = true;
					OleDiag("[OLE][D3] 时间线: 首次拖入尝试(PreviewDragEnter) 进程" + OleElapsed());
				}
				RemoveNoActivateStyle();
				DesktopEmbedService.NotifyOleDragEnter(_hwnd);
				try
				{
					string[] formats = e.Data.GetFormats();
					OleDiag($"[OLE][Zone] PreviewDragEnter hwnd=0x{_hwnd:X8} 格式=[{string.Join(",", formats)}] 可接收={IsDragDataAcceptable(e)}");
				}
				catch (Exception ex)
				{
					OleDiag("[OLE][Zone] PreviewDragEnter 取格式异常: " + ex.Message);
				}
			}
			if (_viewModel.IsCollapsed)
			{
				_viewModel.IsHoverExpanded = true;
				RestoreExpandedSize();
				EnsureCollapseWatcher();
			}
		};
		base.PreviewDragOver += delegate
		{
			if (_viewModel.IsCollapsed)
			{
				_viewModel.IsHoverExpanded = true;
				RestoreExpandedSize();
				EnsureCollapseWatcher();
			}
		};
		base.PreviewDragLeave += delegate
		{
			if (_hwnd != IntPtr.Zero && !_isEditingTitle && !_isEditingTab)
			{
				RestoreNoActivateStyle();
				DesktopEmbedService.NotifyOleDragLeave(_hwnd);
			}
			ScheduleDragCollapse();
		};
		base.PreviewDrop += delegate
		{
			if (_hwnd != IntPtr.Zero && !_isEditingTitle && !_isEditingTab)
			{
				RestoreNoActivateStyle();
				DesktopEmbedService.NotifyOleDragLeave(_hwnd);
			}
			ScheduleDragCollapse();
		};
		base.Loaded += OnLoaded;
		base.Deactivated += delegate
		{
			CommitTitleEdit();
		};
		base.Closed += delegate
		{
			DesktopEmbedService.DesktopOwnerReapplied -= OnDesktopOwnerReapplied;
			DesktopEmbedService.ExternalDragActiveChanged -= OnExternalDragActiveChanged;
			DesktopEmbedService.UnregisterDesktopWindow(_hwnd);
			SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
			if (_hwnd != IntPtr.Zero)
			{
				WindowSnapService.Unregister(_hwnd);
			}
			WidgetAlignGlowService.Unregister(_hwnd);
			WidgetZOrderManager.Unregister(_hwnd);
			_snapTimer?.Stop();
			_oleReRegTimer?.Stop();
			_oleReRegTimer = null;
			_hoverCollapseWatcher?.Stop();
			_hoverCollapseWatcher = null;
			if (!DesktopEmbedService.IsAppExiting)
			{
				_viewModel.SaveState();
			}
		};
		base.Dispatcher.ShutdownStarted += delegate
		{
			DestroyDragGhost();
		};
		base.StateChanged += delegate
		{
			if (base.WindowState != WindowState.Normal)
			{
				base.WindowState = WindowState.Normal;
			}
		};
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
		_hwnd = source?.Handle ?? IntPtr.Zero;
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		OleDiag($"[OLE][D3] 时间线: OnLoaded hwnd=0x{_hwnd:X8} 进程{OleElapsed()}");
		WidgetZOrderManager.Register(_hwnd, topLayer: true);
		DesktopEmbedService.DesktopOwnerReapplied += OnDesktopOwnerReapplied;
		DesktopEmbedService.ExternalDragActiveChanged += OnExternalDragActiveChanged;
		DesktopEmbedService.StartExternalDragWatcher();
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
		_savedExStyle = NativeMethods.GetWindowLong(_hwnd, -20);
		DesktopWidgetInitializer.Register(_hwnd, source, DesktopWidgetInitializer.Default);
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
		PlaceAboveDesktop();
		source?.AddHook(WndProc);
		WindowSnapService.Register(_hwnd, () => NativeDragHelper.GetDipRect(_hwnd));
		WidgetAlignGlowService.Register(_hwnd, this);
		EnsureCollapseWatcher();
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
		TabScrollViewer.ScrollChanged += delegate
		{
			UpdateTabScrollButtons();
		};
		_viewModel.Groups.CollectionChanged += delegate
		{
			base.Dispatcher.BeginInvoke(new Action(UpdateTabScrollButtons), DispatcherPriority.Render);
		};
		base.Dispatcher.BeginInvoke(new Action(PushToBottom), DispatcherPriority.Loaded);
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			if (!(SystemParameters.VirtualScreenWidth < 100.0) && !(SystemParameters.VirtualScreenHeight < 100.0))
			{
				double width = ((_zone.Width > 100.0) ? WindowGeometryPersistence.ToDip(_zone.Width) : base.Width);
				double height = ((_zone.Height > 100.0) ? WindowGeometryPersistence.ToDip(_zone.Height) : base.Height);
				(double, double) tuple = WindowGeometryPersistence.ClampToVisibleScreen(base.Left, base.Top, width, height);
				if (tuple.Item1 != base.Left || tuple.Item2 != base.Top)
				{
					(base.Left, base.Top) = tuple;
					_viewModel.WindowLeft = WindowGeometryPersistence.ToPhysical(tuple.Item1);
					_viewModel.WindowTop = WindowGeometryPersistence.ToPhysical(tuple.Item2);
				}
			}
		}, DispatcherPriority.Background);
		SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
		EnsureOleDropRegistrationRobust();
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			PlaceAboveDesktop();
		});
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			_isInitializing = false;
		});
	}

	private static string OleElapsed()
	{
		return $"{(DateTime.Now - _oleProcStart).TotalSeconds:F2}s";
	}

	private void EnsureOleDropRegistrationRobust()
	{
		try
		{
			ReRegisterOleDropTarget();
			ProbeWindowFromPoint();
			base.Dispatcher.BeginInvoke(DispatcherPriority.SystemIdle, (Action)delegate
			{
				ReRegisterOleDropTarget();
			});
			_oleReRegAttempt = 0;
			_oleReRegTimer?.Stop();
			_oleReRegTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(OleReRegDelaysMs[0])
			};
			_oleReRegTimer.Tick += delegate
			{
				ReRegisterOleDropTarget();
				_oleReRegAttempt++;
				if (_oleReRegAttempt >= OleReRegDelaysMs.Length)
				{
					_oleReRegTimer?.Stop();
					_oleReRegTimer = null;
					StartOlePollIfNeeded();
				}
				else if (_oleReRegTimer != null)
				{
					_oleReRegTimer.Interval = TimeSpan.FromMilliseconds(OleReRegDelaysMs[_oleReRegAttempt] - OleReRegDelaysMs[_oleReRegAttempt - 1]);
				}
			};
			_oleReRegTimer.Start();
		}
		catch
		{
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

	private void StartOlePollIfNeeded()
	{
		if (_olePollTimer != null || _hwnd == IntPtr.Zero || DesktopEmbedService.IsAppExiting)
		{
			return;
		}
		OleDiag($"[OLE] 启动自愈轮询（初始2s，50s后转5s心跳；稳定即停；当前就绪={IsOleDropReady()}）");
		_olePollAttempt = 0;
		bool switched = false;
		_olePollTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(2000.0)
		};
		_olePollTimer.Tick += delegate
		{
			ReRegisterOleDropTarget();
			ProbeWindowFromPoint();
			_olePollAttempt++;
			if (!switched && _olePollAttempt >= 25)
			{
				switched = true;
				OleDiag($"[OLE] 达50s上限，转长心跳(5s)（稳定即停；就绪={IsOleDropReady()}）");
				_olePollTimer.Interval = TimeSpan.FromMilliseconds(5000.0);
			}
			if (switched && _oleStableCount >= 3)
			{
				_olePollTimer.Stop();
				_olePollTimer = null;
				OleDiag("[OLE] OLE 已稳定，停止心跳（保留按需重注册：桌面所有者重建/嵌入保护位移将触发 force 重建）");
			}
		};
		_olePollTimer.Start();
	}

	private void ReRegisterOleDropTarget(bool force = false)
	{
		try
		{
			if (_hwnd == IntPtr.Zero || DesktopEmbedService.IsAppExiting || DesktopEmbedService.IsOleDragActive(_hwnd) || DesktopEmbedService.IsExternalFileDragActive)
			{
				return;
			}
			nint owner = DesktopEmbedService.DesktopOwner;
			bool ownerValid = owner != IntPtr.Zero && NativeMethods.IsWindow(owner);
			PlaceAboveDesktop();
			if (force)
			{
				try
				{
					NativeMethods.RevokeDragDrop(_hwnd);
				}
				catch
				{
				}
			}
			base.AllowDrop = false;
			base.AllowDrop = true;
			ApplyUipiDropFilter();
			_oleStableCount++;
			if (!_d3FirstReRegLogged)
			{
				_d3FirstReRegLogged = true;
				OleDiag($"[OLE][D3] 时间线: 首次OLE重注册(AllowDrop切换) 进程{OleElapsed()} force={force}");
			}
			nint oleTarget = IntPtr.Zero;
			try
			{
				oleTarget = NativeMethods.GetProp(_hwnd, "OleDropTargetInterface");
			}
			catch
			{
			}
			OleDiag($"[OLE] 重注册 hwnd=0x{_hwnd:X8} owner=0x{owner:X8} 有效={ownerValid} force={force} 连续稳定={_oleStableCount} OLE目标={((oleTarget != IntPtr.Zero) ? $"已注册(0x{oleTarget:X})" : "★未注册(RegisterDragDrop静默失败)")}");
		}
		catch (Exception ex)
		{
			_oleStableCount = 0;
			OleDiag("[OLE] ⚠ 重注册失败: " + ex.Message);
		}
	}

	private void ApplyUipiDropFilter()
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		try
		{
			bool a = NativeMethods.ChangeWindowMessageFilterEx(_hwnd, 563u, 1u, IntPtr.Zero);
			bool b = NativeMethods.ChangeWindowMessageFilterEx(_hwnd, 74u, 1u, IntPtr.Zero);
			bool c = NativeMethods.ChangeWindowMessageFilterEx(_hwnd, 73u, 1u, IntPtr.Zero);
			if (_uipiDiagLogged)
			{
				return;
			}
			_uipiDiagLogged = true;
			bool elevated = false;
			try
			{
				using WindowsIdentity id = WindowsIdentity.GetCurrent();
				elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
			}
			catch
			{
			}
			OleDiag($"[OLE][UIPI] ★ 拖放消息放行 DROPFILES={a} COPYDATA={b} COPYGLOBALDATA={c} 进程提升(管理员)={elevated} hwnd=0x{_hwnd:X8}");
		}
		catch (Exception ex)
		{
			OleDiag("[OLE][UIPI] ⚠ 放行失败: " + ex.Message);
		}
	}

	private void OnDesktopOwnerReapplied()
	{
		if (_hwnd != IntPtr.Zero && !DesktopEmbedService.IsAppExiting)
		{
			_oleStableCount = 0;
			ReRegisterOleDropTarget(force: true);
			StartOlePollIfNeeded();
			OleDiag($"[OLE] 桌面所有者重建→按新 owner 重新钉锚 hwnd=0x{_hwnd:X8}");
		}
	}

	private void OnExternalDragActiveChanged()
	{
		if (_hwnd == IntPtr.Zero || DesktopEmbedService.IsAppExiting || DesktopEmbedService.IsAnyDragActive)
		{
			return;
		}
		if (DesktopEmbedService.IsExternalFileDragActive)
		{
			_oleHandledDrop = false;
			_cachedDragFiles = DesktopSelectionHelper.GetDesktopSelectedPathsCached().ToArray();
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(33, 3);
			defaultInterpolatedStringHandler.AppendLiteral("[外部拖拽] 缓存桌面选中项 ");
			string[]? cachedDragFiles = _cachedDragFiles;
			defaultInterpolatedStringHandler.AppendFormatted((cachedDragFiles != null) ? cachedDragFiles.Length : 0);
			defaultInterpolatedStringHandler.AppendLiteral(" 个用于兜底 来源=");
			defaultInterpolatedStringHandler.AppendFormatted(DesktopSelectionHelper.LastDiag);
			defaultInterpolatedStringHandler.AppendLiteral(" hwnd=0x");
			defaultInterpolatedStringHandler.AppendFormatted(_hwnd, "X8");
			OleDiag(defaultInterpolatedStringHandler.ToStringAndClear());
			if (_externalDragZTimer == null)
			{
				_externalDragZTimer = new DispatcherTimer
				{
					Interval = TimeSpan.FromMilliseconds(80.0)
				};
				_externalDragZTimer.Tick += ExternalDragZTick;
			}
			_externalDragZTimer.Start();
			ExternalDragZTick(null, null);
		}
		else
		{
			_externalDragZTimer?.Stop();
			ExternalDragEndReset();
		}
	}

	private void ExternalDragZTick(object? sender, EventArgs e)
	{
		if (!DesktopEmbedService.IsExternalFileDragActive)
		{
			_externalDragZTimer?.Stop();
			ExternalDragEndReset();
			return;
		}
		bool over = IsCursorOverWindow() && IsZoneVisibleAtCursor();
		if (over && !_externalDragElevated)
		{
			NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, 19u);
			NativeMethods.SetWindowLongPtr(_hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32(), IntPtr.Zero);
			RemoveNoActivateStyle();
			_externalDragElevated = true;
			OleDiag($"[外部拖拽][V2.22.0] 光标进入收纳区→抬升为 OLE 落点 hwnd=0x{_hwnd:X8}");
		}
		else if (!over && _externalDragElevated)
		{
			nint owner = DesktopEmbedService.DesktopOwner;
			if (owner != IntPtr.Zero && NativeMethods.IsWindow(owner))
			{
				NativeMethods.SetWindowLongPtr(_hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32(), owner);
			}
			PlaceAboveDesktop(forceBand: true);
			RestoreNoActivateStyle();
			_externalDragElevated = false;
			OleDiag($"[外部拖拽][V2.22.0] 光标离开收纳区→回落桌面带(不遮挡前台) hwnd=0x{_hwnd:X8}");
		}
	}

	private bool IsZoneVisibleAtCursor()
	{
		if (!NativeMethods.GetCursorPos(out var pt))
		{
			return false;
		}
		nint hit = NativeMethods.WindowFromPoint(pt);
		if (hit == IntPtr.Zero)
		{
			return false;
		}
		StringBuilder sb = new StringBuilder(256);
		for (int i = 0; i < 16; i++)
		{
			bool hitSelf = hit == _hwnd;
			nint w = hit;
			while (!hitSelf && w != IntPtr.Zero)
			{
				if (w == _hwnd)
				{
					hitSelf = true;
					break;
				}
				w = NativeMethods.GetParent(w);
			}
			if (hitSelf)
			{
				return true;
			}
			sb.Clear();
			NativeMethods.GetClassName(hit, sb, 256);
			switch (sb.ToString())
			{
			case "SysDragImage":
			case "SysShadow":
			case "OleDdeWndClass":
			case "DragWindow":
			case "Tooltips_class32":
				break;
			default:
				return false;
			}
			hit = NativeMethods.GetWindow(hit, 2u);
			if (hit == IntPtr.Zero)
			{
				break;
			}
		}
		return false;
	}

	private void ExternalDragEndReset()
	{
		TryManualDropFallback();
		if (_externalDragElevated)
		{
			nint owner = DesktopEmbedService.DesktopOwner;
			if (owner != IntPtr.Zero && NativeMethods.IsWindow(owner))
			{
				NativeMethods.SetWindowLongPtr(_hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32(), owner);
				OleDiag($"[外部拖拽] 收纳区挂回 Progman 所有权 0x{owner:X8} hwnd=0x{_hwnd:X8}");
			}
			PlaceAboveDesktop(forceBand: true);
			RestoreNoActivateStyle();
			_externalDragElevated = false;
			OleDiag($"[外部拖拽][V2.22.0] 外部拖拽结束→收纳区回落桌面所有者并恢复 NOACTIVATE hwnd=0x{_hwnd:X8}");
		}
	}

	private void TryManualDropFallback()
	{
		try
		{
			NativeMethods.POINT pt;
			NativeMethods.RECT rect;
			if (_oleHandledDrop)
			{
				OleDiag("[兜底] OLE 已成功投递，跳过手动落盘");
			}
			else if (_cachedDragFiles == null || _cachedDragFiles.Length == 0)
			{
				OleDiag("[兜底] 无缓存拖拽文件，跳过手动落盘");
			}
			else if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd) && NativeMethods.GetCursorPos(out pt) && NativeMethods.GetWindowRect(_hwnd, out rect))
			{
				bool overWindow = pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom;
				bool hitSelf = overWindow && IsZoneVisibleAtCursor();
				OleDiag($"[兜底] 拖拽结束 光标=({pt.X},{pt.Y}) 窗口=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) 命中={overWindow} 命中自身={hitSelf} 缓存文件数={_cachedDragFiles.Length}");
				if (hitSelf && AreAllInDesktopRoot(_cachedDragFiles))
				{
					OleDiag($"[兜底] ★ 触发手动落盘收纳：{_cachedDragFiles.Length} 个文件 hwnd=0x{_hwnd:X8}");
					string[] files = _cachedDragFiles;
					_cachedDragFiles = null;
					ProcessDroppedFiles(files);
				}
			}
		}
		catch (Exception ex)
		{
			OleDiag("[兜底] 异常: " + ex.Message);
		}
	}

	private bool IsOleDropReady()
	{
		if (_hwnd == IntPtr.Zero || DesktopEmbedService.IsAppExiting)
		{
			return true;
		}
		if (!NativeMethods.IsWindow(_hwnd))
		{
			return false;
		}
		nint owner = DesktopEmbedService.DesktopOwner;
		if (owner == IntPtr.Zero || !NativeMethods.IsWindow(owner))
		{
			return false;
		}
		nint ownerHwnd = DesktopEmbedService.GetOwnerHwnd(_hwnd);
		nint expectedOwner = DesktopEmbedService.GetGwlpOwnerForHwnd(_hwnd);
		return ownerHwnd == expectedOwner;
	}

	private void ProbeWindowFromPoint()
	{
		try
		{
			if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var wr))
			{
				return;
			}
			int cx = wr.Left + (wr.Right - wr.Left) / 2;
			int cy = wr.Top + (wr.Bottom - wr.Top) / 2;
			int exStyle = NativeMethods.GetWindowLong(_hwnd, -20);
			bool transparent = (exStyle & 0x20) != 0;
			nint hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT(cx, cy));
			bool hitSelf = hit == _hwnd;
			nint walk = hit;
			for (int i = 0; i < 6; i++)
			{
				if (walk == IntPtr.Zero)
				{
					break;
				}
				if (hitSelf)
				{
					break;
				}
				if (walk == _hwnd)
				{
					hitSelf = true;
					break;
				}
				walk = NativeMethods.GetParent(walk);
			}
			string hitClass = "n/a";
			if (!hitSelf && hit != IntPtr.Zero)
			{
				StringBuilder sb = new StringBuilder(256);
				NativeMethods.GetClassName(hit, sb, 256);
				hitClass = sb.ToString();
			}
			int vcx = _wndprocCenterX;
			int vcy = _wndprocCenterY;
			bool vValid = _wndprocCenterValid;
			nint vHit = (vValid ? NativeMethods.WindowFromPoint(new NativeMethods.POINT(vcx, vcy)) : IntPtr.Zero);
			bool vHitSelf = vValid && vHit == _hwnd;
			string vHitClass = "n/a";
			if (vValid && !vHitSelf && vHit != IntPtr.Zero)
			{
				StringBuilder sb2 = new StringBuilder(256);
				NativeMethods.GetClassName(vHit, sb2, 256);
				vHitClass = sb2.ToString();
			}
			OleDiag($"[OLE][D2] 命中探测 L={wr.Left} T={wr.Top} R={wr.Right} B={wr.Bottom} 物理中心=({cx},{cy}) hit=0x{hit:X8} 命中本窗={hitSelf} 命中类={hitClass} 视觉中心=({vcx},{vcy})有效={vValid} vHit=0x{vHit:X8} 命中本窗={vHitSelf} v类={vHitClass} EXSTYLE=0x{exStyle:X} TRANSPARENT={transparent} 就绪={IsOleDropReady()} 稳定计数={_oleStableCount}");
			if (hitSelf && !_d3FirstHitLogged)
			{
				_d3FirstHitLogged = true;
				OleDiag("[OLE][D3] 时间线: 首次WindowFromPoint命中本窗 进程" + OleElapsed());
			}
		}
		catch (Exception ex)
		{
			OleDiag("[OLE][D2] 探测异常: " + ex.Message);
		}
	}

	private void PushToBottom()
	{
		if (_hwnd != IntPtr.Zero)
		{
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			AppPanelTracker.PlaceDesktopWindow(_hwnd, band);
		}
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

	private void PlaceAboveDesktop(bool forceBand = false)
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		nint owner = DesktopEmbedService.DesktopOwner;
		if (owner != IntPtr.Zero)
		{
			nint insertAfter = ((!forceBand && (DesktopEmbedService.IsAnyDragActive || DesktopEmbedService.IsExternalFileDragActive)) ? NativeMethods.HWND_TOP : owner);
			if (NativeMethods.GetWindow(_hwnd, 2u) != insertAfter)
			{
				NativeMethods.SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0, 16403u);
				DebugDebugDebugDebugDebugLog($"[PlaceAboveDesktop] 窗口 0x{_hwnd:X8} 已抬到 {((insertAfter == owner) ? ("桌面所有者 0x" + ((IntPtr)owner).ToString("X8")) : "HWND_TOP(外部拖拽中)")} 之上（拖放可命中）");
			}
		}
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
		LogWndProc(msg, wParam, lParam);
		if (msg == 532)
		{
			WidgetAlignGlowService.OnSizing(_hwnd, wParam, lParam);
		}
		if (msg == 562)
		{
			WidgetAlignGlowService.OnExitSizeMove(_hwnd);
		}
		switch (msg)
		{
		case 2:
			DebugDebugDebugDebugDebugLog("[WndProc] ⚠ WM_DESTROY 已拦截 — 窗口受 Win+D 保护");
			handled = true;
			return IntPtr.Zero;
		case 16:
			if (DesktopEmbedService.ShouldCancelUserClose(hwnd))
			{
				DebugDebugDebugDebugDebugLog("[WndProc] ⚠ WM_CLOSE 已拦截（拒绝经任务栏关闭挂件），改重锚定到桌面");
				DesktopEmbedService.HandleTaskbarRecreated();
				handled = true;
			}
			return IntPtr.Zero;
		case 24:
			if (wParam == IntPtr.Zero)
			{
				handled = true;
				DesktopEmbedService.ReinforceProtectionLightweight(hwnd);
				return IntPtr.Zero;
			}
			break;
		}
		if (msg == 274 && (((IntPtr)wParam).ToInt32() & 0xFFF0) == 61472)
		{
			DebugDebugDebugDebugDebugLog($"[WndProc] ✅ SC_MINIMIZE已拦截 hwnd=0x{hwnd:X8}");
			handled = true;
			return IntPtr.Zero;
		}
		if (msg == 5 && ((IntPtr)wParam).ToInt32() == 1)
		{
			DebugDebugDebugDebugDebugLog($"[WndProc] ✅ WM_SIZE(MINIMIZED)已拦截 hwnd=0x{hwnd:X8}");
			NativeMethods.ShowWindow(hwnd, 4);
			handled = true;
			return IntPtr.Zero;
		}
		if (msg == 274 && (((IntPtr)wParam).ToInt32() & 0xFFF0) == 61472)
		{
			DebugDebugDebugDebugDebugLog($"[WndProc] ✅ SC_MINIMIZE已拦截 hwnd=0x{hwnd:X8}");
			handled = true;
			return IntPtr.Zero;
		}
		if (msg == 70)
		{
			NativeMethods.WINDOWPOS wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
			if ((wp.flags & 0x80) != 0)
			{
				DebugDebugDebugDebugDebugLog($"[WndProc] ✅ SWP_HIDEWINDOW已拦截 hwnd=0x{hwnd:X8} flags=0x{wp.flags:X} → 强制ShowWindow");
				wp.flags &= 4294967167u;
				wp.flags |= 19u;
				Marshal.StructureToPtr(wp, lParam, fDeleteOld: false);
				handled = true;
				NativeMethods.ShowWindow(hwnd, 4);
				DesktopEmbedService.ReinforceProtectionLightweight(hwnd);
				return IntPtr.Zero;
			}
		}
		if (msg == 71)
		{
			NativeMethods.WINDOWPOS wp2 = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
			_wndprocCenterX = wp2.x + wp2.cx / 2;
			_wndprocCenterY = wp2.y + wp2.cy / 2;
			_wndprocCenterValid = true;
			if ((wp2.flags & 0x80) != 0)
			{
				DebugDebugDebugDebugDebugLog($"[WndProc] ⚠ WM_WINDOWPOSCHANGED含HIDE标志 hwnd=0x{hwnd:X8} → 强制恢复");
				NativeMethods.ShowWindow(hwnd, 4);
				DesktopEmbedService.ReinforceProtectionLightweight(hwnd);
				handled = true;
				return IntPtr.Zero;
			}
		}
		if (msg == 28 && wParam == IntPtr.Zero)
		{
			base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				if (!base.IsVisible || base.WindowState != WindowState.Normal)
				{
					DebugDebugDebugDebugDebugLog($"[WndProc] WM_ACTIVATEAPP 后窗口异常 IsVisible={base.IsVisible} WindowState={base.WindowState}，强制恢复");
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
			});
		}
		if (NativeMethods.WM_DESKTOOL_WIND != 0 && msg == NativeMethods.WM_DESKTOOL_WIND)
		{
			DebugDebugDebugDebugDebugLog("[WndProc] 收到恢复消息 → UI线程窗口恢复");
			ExecuteWindowRecovery(hwnd);
		}
		if (msg == 562)
		{
			handled = false;
			base.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate
			{
				if (_hwnd != IntPtr.Zero)
				{
					NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 311u);
				}
			});
		}
		if (msg == 561)
		{
			CommitTitleEdit();
		}
		if (msg == 33)
		{
			handled = true;
			return (_isEditingTitle || _isEditingTab) ? 1 : 3;
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
			NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, 83u);
			DesktopEmbedService.ReapplyProtection(hwnd);
			nint handle = hwnd;
			base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				NativeMethods.SetWindowPos(handle, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0, 19u);
			});
			DebugDebugDebugDebugDebugLog($"[ExecuteWindowRecovery] 窗口 0x{hwnd:X8} 恢复完成");
		}
		catch (Exception ex)
		{
			DebugDebugDebugDebugDebugLog("[ExecuteWindowRecovery] 异常: " + ex.Message);
		}
	}

	private static void LogWndProc(int msg, nint wParam, nint lParam)
	{
		if (_logEnabled && _winMsgLogEnabled)
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
				2 => "WM_DESTROY", 
				131 => "WM_NCCALCSIZE", 
				133 => "WM_NCPAINT", 
				15 => "WM_PAINT", 
				36 => "WM_GETMINMAXINFO", 
				40 => "WM_WINDOWPOSCHANGED_POST", 
				_ => null, 
			};
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
			if (msgName != null)
			{
				DebugDebugDebugDebugDebugLog($"[WndProc] {msgName}(0x{msg:X4}) {detail}");
			}
		}
	}

	private static string LogWindowPos(nint lParam, string phase)
	{
		try
		{
			NativeMethods.WINDOWPOS wp = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
			string cloakInfo = "";
			if ((wp.flags & 0x80) != 0)
			{
				cloakInfo = " HIDE";
			}
			if ((wp.flags & 0x40) != 0)
			{
				cloakInfo = " SHOW";
			}
			return $"{phase} x={wp.x} y={wp.y} cx={wp.cx} cy={wp.cy} flags=0x{wp.flags:X}{cloakInfo}";
		}
		catch
		{
			return phase + " parse_err";
		}
	}

	private static void DebugDebugDebugDebugDebugLog(string message)
	{
		if (_logEnabled)
		{
			LogHelper.Append(_debugLogPath, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
		}
	}

	private void EnterTitleEditMode()
	{
		if (!_isEditingTitle && _hwnd != IntPtr.Zero)
		{
			_isEditingTitle = true;
			TitleTextBox.Focusable = true;
			ActivateWindowForEdit();
		}
	}

	private void LeaveTitleEditMode()
	{
		if (_isEditingTitle)
		{
			_isEditingTitle = false;
			TitleTextBox.Focusable = false;
			DeactivateWindowAfterEdit();
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

	private void EnterTabEditMode()
	{
		if (!_isEditingTab && _hwnd != IntPtr.Zero)
		{
			_isEditingTab = true;
			ActivateWindowForEdit();
		}
	}

	private void LeaveTabEditMode()
	{
		if (_isEditingTab)
		{
			_isEditingTab = false;
			DeactivateWindowAfterEdit();
		}
	}

	private void ActivateWindowForEdit()
	{
		NativeMethods.SetWindowLong(_hwnd, -20, _savedExStyle);
		nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
		NativeMethods.SetWindowPos(_hwnd, band, 0, 0, 0, 0, 16483u);
		NativeMethods.SetForegroundWindow(_hwnd);
	}

	private void DeactivateWindowAfterEdit()
	{
		if (_hwnd != IntPtr.Zero)
		{
			NativeMethods.SetWindowLong(_hwnd, -20, _savedExStyle | 0x8000000);
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			NativeMethods.SetWindowPos(_hwnd, band, 0, 0, 0, 0, 16403u);
			PushToBottom();
		}
	}

	private void ZoneSearch_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (!_searchActive)
		{
			_searchActive = true;
			ActivateWindowForEdit();
		}
	}

	private void ZoneSearch_GotFocus(object sender, RoutedEventArgs e)
	{
		if (!_searchActive)
		{
			_searchActive = true;
			ActivateWindowForEdit();
		}
	}

	private void ZoneSearch_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
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

	private static bool IsOwnEditableTextBox(DependencyObject? obj)
	{
		if (!(obj is System.Windows.Controls.TextBox))
		{
			return false;
		}
		for (DependencyObject d = obj; d != null; d = VisualTreeHelper.GetParent(d))
		{
			if (d is ZoneWindow)
			{
				return true;
			}
		}
		return false;
	}

	private void RemoveNoActivateStyle()
	{
		try
		{
			if (_hwnd != IntPtr.Zero)
			{
				int exStyle = NativeMethods.GetWindowLong(_hwnd, -20);
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
					NativeMethods.SetWindowLong(_hwnd, -20, exStyle);
					NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 55u);
				}
			}
		}
		catch
		{
		}
	}

	private void RestoreNoActivateStyle()
	{
		try
		{
			if (_hwnd != IntPtr.Zero)
			{
				int exStyle = NativeMethods.GetWindowLong(_hwnd, -20);
				bool changed = false;
				if ((exStyle & 0x8000000) == 0)
				{
					exStyle |= 0x8000000;
					changed = true;
				}
				if (changed)
				{
					NativeMethods.SetWindowLong(_hwnd, -20, exStyle);
					NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 55u);
				}
			}
		}
		catch
		{
		}
	}

	private void OnLocationChanged(object? sender, EventArgs e)
	{
		if (!DesktopEmbedService.IsAppExiting && !_isInitializing && !(DateTime.Now < _displayTransitionUntil) && !_viewModel.IsLocked && !_suppressPositionSave)
		{
			double cl = base.Left;
			double ct = base.Top;
			ClampPositionToOwner(ref cl, ref ct);
			_viewModel.WindowLeft = WindowGeometryPersistence.ToPhysical(cl);
			_viewModel.WindowTop = WindowGeometryPersistence.ToPhysical(ct);
		}
	}

	private void ClampPositionToOwner(ref double left, ref double top)
	{
		(int, int) ownerOrigin = DesktopEmbedService.GetOwnerOrigin(_hwnd);
		double dpi = GetMonitorDpi();
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
				try
				{
					nint hMonitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT
					{
						X = 0,
						Y = 0
					}, 1u);
					NativeMethods.MONITORINFO mi2 = new NativeMethods.MONITORINFO
					{
						cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
					};
					if (NativeMethods.GetMonitorInfo(hMonitor, ref mi2))
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
					return;
				}
				catch
				{
					return;
				}
			}
		}
		try
		{
			nint mon2 = NativeMethods.MonitorFromWindow(_hwnd, 2u);
			if (mon2 == IntPtr.Zero)
			{
				return;
			}
			NativeMethods.MONITORINFO mi3 = new NativeMethods.MONITORINFO
			{
				cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
			};
			if (NativeMethods.GetMonitorInfo(mon2, ref mi3))
			{
				double workLeft3 = (double)mi3.rcWork.Left / dpi;
				double num6 = (double)mi3.rcWork.Top / dpi;
				double num7 = (double)mi3.rcWork.Right / dpi;
				double workBottom3 = (double)mi3.rcWork.Bottom / dpi;
				double margin3 = 40.0;
				double minL3 = workLeft3 - (base.Width - margin3);
				double maxL3 = num7 - margin3;
				double minT3 = num6 - (base.Height - margin3);
				double maxT3 = workBottom3 - margin3;
				if (minL3 <= maxL3)
				{
					left = Math.Max(minL3, Math.Min(maxL3, left));
				}
				if (minT3 <= maxT3)
				{
					top = Math.Max(minT3, Math.Min(maxT3, top));
				}
			}
		}
		catch
		{
		}
	}

	private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
	{
		if (DesktopEmbedService.IsAppExiting || _viewModel.IsSyncingIcons)
		{
			return;
		}
		if (!_suppressPositionSave)
		{
			_viewModel.ZoneWidth = WindowGeometryPersistence.ToPhysical(base.Width);
		}
		if (!_viewModel.IsCollapsed && !_suppressPositionSave)
		{
			if (Math.Abs(base.Height - 52.0) > 1.0)
			{
				_savedCollapsedHeight = base.Height;
				_viewModel.ZoneHeight = WindowGeometryPersistence.ToPhysical(base.Height);
			}
		}
		else if (!_suppressPositionSave && _viewModel.IsCollapsed && _viewModel.IsHoverExpanded && base.Height > 53.0)
		{
			_savedCollapsedHeight = base.Height;
			_viewModel.ZoneHeight = WindowGeometryPersistence.ToPhysical(base.Height);
		}
		_viewModel.IsNarrow = base.ActualWidth < 300.0;
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

	private void ApplyCollapsedSize()
	{
		_savedCollapsedHeight = base.Height;
		base.MaxHeight = 52.0;
		base.Height = 52.0;
		base.ResizeMode = ResizeMode.CanResize;
	}

	private void RestoreExpandedSize()
	{
		base.ResizeMode = ResizeMode.CanResize;
		base.MaxHeight = double.PositiveInfinity;
		double persistedH = (_zone.PosPhysical ? WindowGeometryPersistence.ToDip(_zone.Height) : _zone.Height);
		double target = ((_savedCollapsedHeight > 57.0) ? _savedCollapsedHeight : persistedH);
		base.Height = ((target > 57.0) ? target : 420.0);
	}

	private void ScheduleDragCollapse()
	{
		EnsureCollapseWatcher();
	}

	private void EnsureCollapseWatcher()
	{
		if (base.Dispatcher.HasShutdownStarted)
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
		if (_contextMenuOpen)
		{
			ContextMenu? openContextMenu = _openContextMenu;
			if (openContextMenu != null && openContextMenu.IsOpen)
			{
				return;
			}
		}
		if (_viewModel.IsCollapsed && (_viewModel.IsHoverExpanded || !(Math.Abs(base.Height - 52.0) < 1.0)) && !_isEditingTitle && !_isEditingTab && (!base.IsKeyboardFocusWithin || !(Keyboard.FocusedElement is System.Windows.Controls.TextBox)) && !IsCursorOverWindow())
		{
			_viewModel.IsHoverExpanded = false;
			base.MaxHeight = 52.0;
			base.Height = 52.0;
			base.ResizeMode = ResizeMode.CanResize;
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
		nint hwnd = _hwnd;
		if (hwnd == IntPtr.Zero)
		{
			try
			{
				hwnd = new WindowInteropHelper(this).Handle;
			}
			catch
			{
			}
		}
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
		if (!base.Dispatcher.HasShutdownStarted && _viewModel.IsCollapsed && _viewModel.IsHoverExpanded)
		{
			ScheduleDragCollapse();
		}
	}

	private void TitleTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (TitleTextBox.IsReadOnly || _viewModel.IsLocked || DateTime.Now < _suppressTitleEditUntil)
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
					LeaveTabEditMode();
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
		if (sender is System.Windows.Controls.TextBox tb && string.IsNullOrWhiteSpace(tb.Text))
		{
			tb.Text = _viewModel.Name;
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
			TitleTextBox.Text = _zone.Name;
			Keyboard.ClearFocus();
			e.Handled = true;
		}
	}

	private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		WinDHookService.NotifyProtectedWindowInteraction();
		if (!e.Handled)
		{
			CommitTitleEdit();
			if (!_viewModel.IsLocked && (!(e.OriginalSource is DependencyObject child) || FindVisualParent<System.Windows.Controls.Button>(child) == null))
			{
				StartNativeDrag();
				e.Handled = true;
			}
		}
	}

	private void Header_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		WinDHookService.NotifyProtectedWindowInteraction();
		if (FindVisualParent<System.Windows.Controls.TextBox>(e.OriginalSource as DependencyObject) == null)
		{
			CommitTitleEdit();
		}
		if ((!(e.OriginalSource is DependencyObject child) || (FindVisualParent<System.Windows.Controls.Button>(child) == null && FindVisualParent<System.Windows.Controls.TextBox>(child) == null)) && !_viewModel.IsLocked)
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
		nint insertAfter = ((DesktopEmbedService.IsAnyDragActive || DesktopEmbedService.IsExternalFileDragActive) ? NativeMethods.HWND_TOP : ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM));
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
		_suppressTitleEditUntil = DateTime.Now + TimeSpan.FromMilliseconds(300.0);
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
			try
			{
				double fx = base.Left;
				double fy = base.Top;
				ClampPositionToOwner(ref fx, ref fy);
				_viewModel.WindowLeft = WindowGeometryPersistence.ToPhysical(fx);
				_viewModel.WindowTop = WindowGeometryPersistence.ToPhysical(fy);
				base.Left = fx;
				base.Top = fy;
				_viewModel.ZoneWidth = WindowGeometryPersistence.ToPhysical(base.Width);
				if ((!_viewModel.IsCollapsed || _viewModel.IsHoverExpanded) && Math.Abs(base.Height - 52.0) > 1.0)
				{
					_savedCollapsedHeight = base.Height;
					_viewModel.ZoneHeight = WindowGeometryPersistence.ToPhysical(base.Height);
				}
				return;
			}
			catch
			{
				return;
			}
		}
		bool isChild = false;
		if (isChild)
		{
			NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, _lastDragPhysX, _lastDragPhysY, 0, 0, 21u);
		}
		else
		{
			try
			{
				double releaseDpi = GetMonitorDpi();
				int endX = _lastDragPhysX;
				int endY = _lastDragPhysY;
				SyncDragLogical(endX, endY, releaseDpi);
			}
			catch
			{
			}
			WindowSnapService.TryDockAlign(_hwnd, this);
		}
		if (isChild)
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
			if (isChild)
			{
				NativeMethods.GetWindowRect(_hwnd, out var wr);
				double releaseDpi2 = GetMonitorDpi();
				double screenDipX = (double)wr.Left / releaseDpi2;
				double screenDipY = (double)wr.Top / releaseDpi2;
				double parentRelX = (double)(wr.Left - parentLeft) / releaseDpi2;
				double parentRelY = (double)(wr.Top - parentTop) / releaseDpi2;
				bool prevSuppress2 = _suppressPositionSave;
				_suppressPositionSave = true;
				base.Left = parentRelX;
				base.Top = parentRelY;
				_suppressPositionSave = prevSuppress2;
				_viewModel.WindowLeft = WindowGeometryPersistence.ToPhysical(screenDipX);
				_viewModel.WindowTop = WindowGeometryPersistence.ToPhysical(screenDipY);
			}
			else
			{
				double fx2 = base.Left;
				double fy2 = base.Top;
				ClampPositionToOwner(ref fx2, ref fy2);
				_viewModel.WindowLeft = WindowGeometryPersistence.ToPhysical(fx2);
				_viewModel.WindowTop = WindowGeometryPersistence.ToPhysical(fy2);
				base.Left = fx2;
				base.Top = fy2;
			}
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
		double dragDpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
		if (dragDpi <= 0.0)
		{
			dragDpi = 1.0;
		}
		_suppressPositionSave = true;
		DragDebugDebugDebugDebugLog($"[拖拽开始] hwnd=0x{_hwnd:X8} isRealChild={isRealChild} owner=0x{owner:X8} parentLeft={parentLeft} parentTop={parentTop} startRect=({startRect.Left},{startRect.Top}) startCursor=({startCursor.X},{startCursor.Y}) dpi={dragDpi:F2}");
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
				DragDebugDebugDebugDebugLog($"[拖拽中#{moveCount}] cursor=({lpPoint.X},{lpPoint.Y}) target=({num},{num2}) actual=({lpRect.Left},{lpRect.Top})");
			}
		};
		onUp = delegate
		{
			Mouse.Capture(null);
			base.MouseMove -= onMove;
			base.MouseLeftButtonUp -= onUp;
			base.LostMouseCapture -= OnDragLostCapture;
			_inNativeMove = false;
			_suppressTitleEditUntil = DateTime.Now + TimeSpan.FromMilliseconds(300.0);
			_suppressPositionSave = false;
			try
			{
				double num = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
				if (num <= 0.0)
				{
					num = 1.0;
				}
				NativeMethods.GetWindowRect(_hwnd, out var lpRect);
				int physX = (isRealChild ? (lpRect.Left - parentLeft) : lpRect.Left);
				int physY = (isRealChild ? (lpRect.Top - parentTop) : lpRect.Top);
				SyncDragLogical(physX, physY, num);
			}
			catch
			{
			}
			WindowSnapService.TryDockAlign(_hwnd, this);
			PullToTop();
			try
			{
				NativeMethods.GetWindowRect(_hwnd, out var lpRect2);
				double num2 = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
				if (num2 <= 0.0)
				{
					num2 = 1.0;
				}
				double num3 = (double)(isRealChild ? (lpRect2.Left - parentLeft) : lpRect2.Left) / num2;
				double num4 = (double)(isRealChild ? (lpRect2.Top - parentTop) : lpRect2.Top) / num2;
				base.Left = num3;
				base.Top = num4;
				_viewModel.WindowLeft = WindowGeometryPersistence.ToPhysical(num3);
				_viewModel.WindowTop = WindowGeometryPersistence.ToPhysical(num4);
			}
			catch
			{
			}
			NativeMethods.GetWindowRect(_hwnd, out var lpRect3);
			DragDebugDebugDebugDebugLog($"[拖拽结束] finalRect=({lpRect3.Left},{lpRect3.Top} {lpRect3.Width}x{lpRect3.Height}) savedLeft={_viewModel.WindowLeft:F1} savedTop={_viewModel.WindowTop:F1}");
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

	private static void DragDebugDebugDebugDebugLog(string msg)
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

	protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
	{
		base.OnMouseMove(e);
		if (!_iconDragActive && !_tabDragActive)
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
		WinDHookService.NotifyProtectedWindowInteraction();
		if (_draggingIcon != null || _draggingTab != null || e.Handled)
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

	private Point GetCursorPosScreen()
	{
		NativeMethods.GetCursorPos(out var pt);
		return new Point(pt.X, pt.Y);
	}

	private bool IsDragDataAcceptable(System.Windows.DragEventArgs e)
	{
		string[] acceptedDragFormats = AcceptedDragFormats;
		foreach (string fmt in acceptedDragFormats)
		{
			if (e.Data.GetDataPresent(fmt))
			{
				return true;
			}
		}
		return false;
	}

	private static bool AreAllInDesktopRoot(string[]? files)
	{
		if (files == null || files.Length == 0)
		{
			return true;
		}
		HashSet<string> desktopRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		Environment.SpecialFolder[] array = new Environment.SpecialFolder[3]
		{
			Environment.SpecialFolder.DesktopDirectory,
			Environment.SpecialFolder.Desktop,
			Environment.SpecialFolder.CommonDesktopDirectory
		};
		for (int i = 0; i < array.Length; i++)
		{
			string p = Environment.GetFolderPath(array[i]);
			if (!string.IsNullOrEmpty(p))
			{
				desktopRoots.Add(p);
			}
		}
		foreach (string f in files)
		{
			if (string.IsNullOrEmpty(f))
			{
				continue;
			}
			try
			{
				string dir = Path.GetDirectoryName(Path.GetFullPath(f));
				if (string.IsNullOrEmpty(dir) || !desktopRoots.Contains(dir))
				{
					return false;
				}
			}
			catch
			{
				return false;
			}
		}
		return true;
	}

	private void IconsArea_DragEnter(object sender, System.Windows.DragEventArgs e)
	{
		if (App.IsInternalDrag && !e.Data.GetDataPresent("DesktopToolInternalDrag"))
		{
			App.IsInternalDrag = false;
		}
		if (App.IsInternalDrag && e.Data.GetDataPresent("DesktopToolInternalDrag"))
		{
			e.Effects = System.Windows.DragDropEffects.Move;
			e.Handled = true;
			return;
		}
		try
		{
			_dragPreviewFiles = (IsDragDataAcceptable(e) ? ExtractDroppedFiles(e) : null);
			_dragPreviewAllDesktop = _dragPreviewFiles != null && _dragPreviewFiles.Length != 0 && AreAllInDesktopRoot(_dragPreviewFiles);
		}
		catch
		{
			_dragPreviewFiles = null;
			_dragPreviewAllDesktop = false;
		}
		e.Effects = (IsDragDataAcceptable(e) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None);
		e.Handled = true;
	}

	private void IconsArea_DragOver(object sender, System.Windows.DragEventArgs e)
	{
		if (App.IsInternalDrag && !e.Data.GetDataPresent("DesktopToolInternalDrag"))
		{
			App.IsInternalDrag = false;
		}
		if (App.IsInternalDrag && e.Data.GetDataPresent("DesktopToolInternalDrag"))
		{
			e.Effects = System.Windows.DragDropEffects.Move;
			e.Handled = true;
		}
		else
		{
			e.Effects = (IsDragDataAcceptable(e) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None);
			e.Handled = true;
		}
	}

	private void IconsArea_GiveFeedback(object sender, System.Windows.GiveFeedbackEventArgs e)
	{
		e.UseDefaultCursors = true;
		e.Handled = true;
	}

	private void IconsArea_Drop(object sender, System.Windows.DragEventArgs e)
	{
		if (App.IsInternalDrag && e.Data.GetDataPresent("DesktopToolInternalDrag"))
		{
			e.Effects = System.Windows.DragDropEffects.None;
			e.Handled = true;
			return;
		}
		_oleHandledDrop = true;
		_cachedDragFiles = null;
		e.Effects = System.Windows.DragDropEffects.None;
		e.Handled = true;
		if (!IsDragDataAcceptable(e))
		{
			return;
		}
		string[] files = ExtractDroppedFiles(e);
		if (files == null || files.Length == 0 || !AreAllInDesktopRoot(files))
		{
			return;
		}
		try
		{
			ProcessDroppedFiles(files);
		}
		catch (Exception ex)
		{
			try
			{
				ShowLoading(show: false);
				System.Windows.MessageBox.Show("无法添加文件：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
			catch
			{
			}
		}
	}

	private static string[]? ExtractDroppedFiles(System.Windows.DragEventArgs e)
	{
		if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
		{
			return e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
		}
		if (e.Data.GetDataPresent("Shell IDList Array"))
		{
			try
			{
				if (e.Data.GetData("Shell IDList Array") is MemoryStream ms && ms.Length > 0)
				{
					return ParseShellIdList(ms);
				}
			}
			catch
			{
			}
		}
		if (e.Data.GetDataPresent("FileNameW"))
		{
			try
			{
				if (e.Data.GetData("FileNameW") is string str && !string.IsNullOrEmpty(str) && (File.Exists(str) || Directory.Exists(str)))
				{
					return new string[1] { str };
				}
			}
			catch
			{
			}
		}
		if (e.Data.GetDataPresent("FileName"))
		{
			try
			{
				if (e.Data.GetData("FileName") is string str2 && !string.IsNullOrEmpty(str2) && (File.Exists(str2) || Directory.Exists(str2)))
				{
					return new string[1] { str2 };
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private static string[]? ParseShellIdList(MemoryStream ms)
	{
		try
		{
			ms.Position = 0L;
			using BinaryReader reader = new BinaryReader(ms);
			if (reader.ReadUInt32() == 0)
			{
				return null;
			}
			List<string> paths = new List<string>();
			byte[] bytes = ms.ToArray();
			foreach (Match item in Regex.Matches(Encoding.Unicode.GetString(bytes), "[A-Z]:\\\\[^\\0]+"))
			{
				string path = item.Value.TrimEnd('\0');
				if ((File.Exists(path) || Directory.Exists(path)) && !paths.Contains<string>(path, StringComparer.OrdinalIgnoreCase))
				{
					paths.Add(path);
				}
			}
			return (paths.Count > 0) ? paths.ToArray() : null;
		}
		catch
		{
			return null;
		}
	}

	private async void ProcessDroppedFiles(string[] files)
	{
		try
		{
			await ProcessDroppedFilesCore(files);
		}
		catch (Exception ex)
		{
			ShowLoading(show: false);
			System.Windows.MessageBox.Show("收纳文件时出错：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private async Task ProcessDroppedFilesCore(string[] files)
	{
		_suppressDesktopWatcher = true;
		try
		{
			await ProcessDroppedFilesCoreInner(files);
		}
		finally
		{
			base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				_suppressDesktopWatcher = false;
			});
		}
	}

	private async Task ProcessDroppedFilesCoreInner(string[] files)
	{
		bool isPhysical = DesktopIconService.UserSelectedMode == "Physical";
		string[] array;
		if (isPhysical)
		{
			List<(string, long)> largeItems = new List<(string, long)>();
			array = files;
			foreach (string file in array)
			{
				if (string.IsNullOrEmpty(file))
				{
					continue;
				}
				try
				{
					string full = Path.GetFullPath(file);
					if (File.Exists(full) || Directory.Exists(full))
					{
						long sizeMB = DesktopIconService.GetItemSize(full) / 1048576;
						if (sizeMB > 100)
						{
							largeItems.Add((Path.GetFileName(file), sizeMB));
						}
					}
				}
				catch
				{
				}
			}
			if (largeItems.Count > 0 && System.Windows.MessageBox.Show((largeItems.Count == 1) ? ($"当前文件夹「{largeItems[0].Item1}」大小约 {largeItems[0].Item2}MB，超过 100MB。\n\n" + "物理收纳模式会将文件夹移动到隐藏目录，大文件移动可能需要较长时间，且存在数据安全风险。\n\n建议：\n  • 切换到「属性隐藏收纳模式」仅隐藏图标而不移动文件\n  • 或仅将文件夹的快捷方式拖入收纳区\n\n是否继续移动收纳该文件夹？") : ("以下文件夹超过 100MB：\n" + string.Join("\n", largeItems.Select<(string, long), string>(((string Name, long SizeMB) x) => $"  • {x.Name}（约 {x.SizeMB}MB）")) + "\n\n物理收纳模式会将文件夹移动到隐藏目录，大文件移动可能需要较长时间，且存在数据安全风险。\n\n建议切换到「属性隐藏收纳模式」或仅拖入快捷方式。\n\n是否继续移动收纳这些文件夹？"), "大文件警告", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
			{
				return;
			}
		}
		List<string> duplicateNames = new List<string>();
		List<(string Name, string Reason)> preCheckFailed = new List<(string, string)>();
		if (!AreAllInDesktopRoot(files))
		{
			System.Windows.MessageBox.Show("本收纳区仅能收纳桌面根目录的文件，不支持其他路径文件。", "无法收纳", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			ShowDropResults(duplicateNames, preCheckFailed);
			return;
		}
		List<(string FilePath, string DisplayName, BitmapSource Icon, bool IsDir)> items = new List<(string, string, BitmapSource, bool)>();
		array = files;
		foreach (string file2 in array)
		{
			if (string.IsNullOrEmpty(file2))
			{
				continue;
			}
			string fileName = Path.GetFileName(file2);
			if (_viewModel.Icons.Any((ZoneIcon zoneIcon) => string.Equals(zoneIcon.FilePath, file2, StringComparison.OrdinalIgnoreCase)))
			{
				duplicateNames.Add(fileName);
				continue;
			}
			try
			{
				string full2 = Path.GetFullPath(file2);
				if (!File.Exists(full2) && !Directory.Exists(full2))
				{
					preCheckFailed.Add((fileName, "文件不存在"));
					continue;
				}
				bool isDir = Directory.Exists(file2);
				string name = (isDir ? Path.GetFileName(file2) : Path.GetFileNameWithoutExtension(file2));
				BitmapSource img = IconHelper.GetFileIconSafe(file2, large: false, _viewModel.IconImageSize);
				IconHelper.LogIconDiagnostic("[DROP-P1] " + fileName + " deskIcon=" + ((img == null) ? "NULL" : $"{img.PixelWidth}x{img.PixelHeight}"));
				items.Add((file2, name, img, isDir));
			}
			catch (Exception ex)
			{
				preCheckFailed.Add((fileName, ex.Message));
			}
		}
		if (items.Count == 0)
		{
			ShowDropResults(duplicateNames, preCheckFailed);
			return;
		}
		ShowLoading(show: true, (items.Count == 1) ? ("正在收纳「" + items[0].DisplayName + "」...") : $"正在收纳 {items.Count} 个文件...");
		Dispatcher dispatch = base.Dispatcher;
		List<(string, string, BitmapSource, string, string, string)> obj2 = await Task.Run(delegate
		{
			List<(string, string, BitmapSource, string, string, string)> list = new List<(string, string, BitmapSource, string, string, string)>();
			foreach (var item in items)
			{
				try
				{
					dispatch.InvokeAsync(delegate
					{
						try
						{
							LoadingDetail.Text = "正在处理：" + item.DisplayName;
						}
						catch
						{
						}
					});
					var (text, item2) = DesktopIconService.HideFile(item.FilePath);
					if (text != null)
					{
						list.Add((item.FilePath, item.DisplayName, item.Icon, text, item2, null));
					}
					else
					{
						string item3 = DesktopIconService.LastMoveError ?? "移动文件失败";
						list.Add((item.FilePath, item.DisplayName, item.Icon, null, null, item3));
					}
				}
				catch (Exception ex3)
				{
					list.Add((item.FilePath, item.DisplayName, item.Icon, null, null, GetFriendlyError(ex3)));
				}
			}
			return list;
		});
		List<(string, string)> failedNames = new List<(string, string)>();
		failedNames.AddRange(preCheckFailed);
		foreach (var r in obj2)
		{
			if (r.Item4 != null)
			{
				try
				{
					ZoneIcon icon = new ZoneIcon
					{
						FilePath = r.Item1,
						DisplayName = r.Item2,
						HiddenMethod = r.Item4,
						HiddenPath = r.Item5,
						IconImage = r.Item3
					};
					string rIconDesc = ((r.Item3 == null) ? "NULL" : $"{r.Item3.PixelWidth}x{r.Item3.PixelHeight}");
					string hiddenDesc = "n/a";
					if (!string.IsNullOrEmpty(r.Item5) && File.Exists(r.Item5))
					{
						BitmapSource hiddenImg = IconHelper.GetFileIconSafe(r.Item5, large: false, _viewModel.IconImageSize);
						hiddenDesc = ((hiddenImg == null) ? "NULL" : $"{hiddenImg.PixelWidth}x{hiddenImg.PixelHeight}");
					}
					IconHelper.LogIconDiagnostic($"[DROP-P4] {Path.GetFileName(r.Item1)} r.Icon={rIconDesc} hiddenPath={r.Item5 ?? "null"} hiddenIcon={hiddenDesc}");
					_viewModel.CommitIcon(icon);
				}
				catch (Exception ex2)
				{
					failedNames.Add((r.Item2, ex2.Message));
				}
			}
			else
			{
				failedNames.Add((r.Item2, r.Item6 ?? "未知错误"));
			}
		}
		ShowLoading(show: false);
		ShowDropResults(duplicateNames, failedNames);
		DesktopIconService.RefreshDesktop();
		if (!isPhysical)
		{
			DesktopIconService.ForceDesktopViewRefresh();
		}
	}

	private void ShowLoading(bool show, string? text = null)
	{
		if (show)
		{
			LoadingText.Text = text ?? "正在收纳文件...";
			LoadingDetail.Text = "";
			LoadingOverlay.Visibility = Visibility.Visible;
		}
		else
		{
			LoadingOverlay.Visibility = Visibility.Collapsed;
		}
	}

	private void ShowDropResults(List<string> duplicateNames, List<(string Name, string Reason)> failedNames)
	{
		if (duplicateNames.Count <= 0 && failedNames.Count <= 0)
		{
			return;
		}
		List<string> parts = new List<string>();
		if (duplicateNames.Count > 0)
		{
			parts.Add((duplicateNames.Count == 1) ? ("已存在同名文件：" + duplicateNames[0]) : ("以下文件已存在于收纳区，无法重复添加：\n" + string.Join("\n", duplicateNames)));
		}
		if (failedNames.Count > 0)
		{
			IEnumerable<string> lines = failedNames.Select(((string Name, string Reason) f) => "• " + f.Name + "\n  原因：" + f.Reason);
			string hint = ((failedNames.Count <= 3) ? string.Join("\n", lines) : (string.Join("\n", lines.Take(3)) + $"\n• ... 等 {failedNames.Count - 3} 个文件"));
			parts.Add("以下文件未能添加到收纳区：\n" + hint);
		}
		System.Windows.MessageBox.Show(string.Join("\n\n", parts), "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
	}

	private static string GetFriendlyError(Exception ex)
	{
		if (ex is UnauthorizedAccessException)
		{
			return "无访问权限，请检查文件是否受保护";
		}
		if (ex is FileNotFoundException)
		{
			return "文件不存在或已被移动";
		}
		if (ex is DirectoryNotFoundException)
		{
			return "目录不存在或已被移除";
		}
		return TranslateCommonErrors(ex.Message);
	}

	private static string TranslateCommonErrors(string message)
	{
		if (string.IsNullOrEmpty(message))
		{
			return "未知错误";
		}
		string lower = message.ToLowerInvariant();
		if (lower.Contains("being used by another process") || lower.Contains("used by another process"))
		{
			return "文件正被其他程序占用（如 Word、PPT 等已打开该文件），请关闭相关应用后重试";
		}
		if (lower.Contains("access to the path") && (lower.Contains("denied") || lower.Contains("is denied")))
		{
			return "没有权限访问此路径，请检查文件权限设置";
		}
		if (lower.Contains("sharing violation") || lower.Contains("in use"))
		{
			return "文件正被其他程序使用，请关闭占用该文件的程序后重试";
		}
		if (lower.Contains("path too long"))
		{
			return "文件路径过长，系统无法处理";
		}
		if (lower.Contains("not enough space") || lower.Contains("disk is full"))
		{
			return "磁盘空间不足，无法完成操作";
		}
		if (lower.Contains("could not find a part of the path") || lower.Contains("could not find file"))
		{
			return "目标路径不存在，可能已被删除或移动";
		}
		if (lower.Contains("device is not ready"))
		{
			return "存储设备未就绪，请检查磁盘连接";
		}
		return message;
	}

	private static long GetFolderSize(string folderPath)
	{
		try
		{
			long size = 0L;
			foreach (FileInfo file in new DirectoryInfo(folderPath).EnumerateFiles("*", SearchOption.AllDirectories))
			{
				try
				{
					size += file.Length;
				}
				catch
				{
				}
			}
			return size;
		}
		catch
		{
			return 0L;
		}
	}

	private void Icon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		CommitTitleEdit();
		if (sender is FrameworkElement { DataContext: ZoneIcon icon })
		{
			_iconDragStartPoint = e.GetPosition(this);
			_draggingIcon = icon;
			_iconDragActive = false;
		}
	}

	private void Icon_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (_draggingIcon == null || e.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}
		Point pos = e.GetPosition(this);
		double dx = pos.X - _iconDragStartPoint.X;
		double dy = pos.Y - _iconDragStartPoint.Y;
		if (!_iconDragActive)
		{
			if (Math.Abs(dx) < 6.0 && Math.Abs(dy) < 6.0)
			{
				return;
			}
			_iconDragActive = true;
			if (sender is FrameworkElement src)
			{
				CreateDragGhost(src, _draggingIcon);
				src.CaptureMouse();
			}
		}
		Point screenPt = PointToScreen(pos);
		if (!InZoneBounds(screenPt))
		{
			ZoneIcon icon = _draggingIcon;
			if (sender is FrameworkElement fe)
			{
				fe.ReleaseMouseCapture();
			}
			_draggingIcon = null;
			_iconDragActive = false;
			DestroyDragGhost();
			DragIconOutToExternal(icon);
		}
		else if (_dragGhost != null)
		{
			Canvas.SetLeft(_dragGhost, _iconSourcePos.X + dx);
			Canvas.SetTop(_dragGhost, _iconSourcePos.Y + dy);
		}
	}

	private void Icon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (sender is FrameworkElement fe)
		{
			fe.ReleaseMouseCapture();
		}
		if (!_iconDragActive)
		{
			ZoneIcon clickedIcon = _draggingIcon;
			DateTime now = DateTime.Now;
			bool num = clickedIcon != null && clickedIcon == _lastClickedIcon && (now - _lastIconClick).TotalMilliseconds < (double)SystemInformation.DoubleClickTime;
			_draggingIcon = null;
			_iconDragActive = false;
			DestroyDragGhost();
			if (num)
			{
				_lastClickedIcon = null;
				_lastIconClick = DateTime.MinValue;
				LaunchIcon(clickedIcon);
			}
			else
			{
				_lastClickedIcon = clickedIcon;
				_lastIconClick = now;
			}
			return;
		}
		if (_draggingIcon == null)
		{
			_draggingIcon = null;
			_iconDragActive = false;
			DestroyDragGhost();
			return;
		}
		ZoneIcon icon = _draggingIcon;
		Point mousePos = e.GetPosition(this);
		_draggingIcon = null;
		_iconDragActive = false;
		DestroyDragGhost();
		IconGroup targetGroup = FindTabGroupUnderPoint(mousePos);
		if (targetGroup != null)
		{
			IconGroup activeGroup = _viewModel.Groups.ElementAtOrDefault(_viewModel.ActiveGroupIndex);
			if (targetGroup != activeGroup)
			{
				_viewModel.MoveIconToGroup(icon, targetGroup);
				return;
			}
		}
		int nearestIdx = FindNearestInsertIndex(mousePos, icon);
		if (nearestIdx >= 0)
		{
			InsertIconAt(icon, nearestIdx);
		}
	}

	private void DragIconOutToExternal(ZoneIcon icon)
	{
		string path = icon.ResolvedPath;
		if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
		{
			return;
		}
		_oleDropCancelledForDesktop = false;
		_dragTempFilePath = null;
		_dragTempDir = null;
		_dragTempDesktopTarget = null;
		CleanupStaleTempDirs();
		bool isAttributeMode = icon.HiddenMethod == "Attributes";
		string expectedDesktopTarget = GetExpectedDesktopDropPath(path);
		string dropPath = path;
		if (TryCreateDragTempFile(path, isAttributeMode, expectedDesktopTarget, out _dragTempFilePath, out _dragTempDir) && _dragTempFilePath != null)
		{
			dropPath = _dragTempFilePath;
			_dragTempDesktopTarget = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileName(_dragTempFilePath));
		}
		try
		{
			RemoveNoActivateStyle();
			System.Windows.DataObject dataObj = new System.Windows.DataObject();
			StringCollection files = new StringCollection { dropPath };
			dataObj.SetFileDropList(files);
			dataObj.SetData("DesktopToolInternalDrag", path);
			dataObj.SetData("FileNameW", dropPath);
			QueryContinueDrag += OnZoneOleQueryContinueDrag_DropOnly;
			App.IsInternalDrag = true;
			DragDrop.DoDragDrop(this, dataObj, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move | System.Windows.DragDropEffects.Link);
			App.IsInternalDrag = false;
			QueryContinueDrag -= OnZoneOleQueryContinueDrag_DropOnly;
			DispatchDropTarget(icon);
		}
		catch (Exception)
		{
		}
		finally
		{
			App.IsInternalDrag = false;
			RestoreNoActivateStyle();
			ScheduleCleanupDragTempFiles();
		}
	}

	private void OnZoneOleQueryContinueDrag_DropOnly(object sender, System.Windows.QueryContinueDragEventArgs e)
	{
		NativeMethods.GetCursorPos(out var pt);
		Point screenPt = new Point(pt.X, pt.Y);
		if (InZoneBounds(screenPt))
		{
			return;
		}
		bool hittingDesktop = IsDesktopWindowUnderMouse(screenPt);
		bool overRealApp = false;
		DragDebugDebugDebugDebugLog($"[ZoneWindow][DIAG] QCD action={e.Action} hittingDesktop={hittingDesktop} winAt={DescribeWindowAt(screenPt)}");
		if (hittingDesktop)
		{
			for (nint cur = NativeMethods.WindowFromPoint(pt); cur != IntPtr.Zero; cur = NativeMethods.GetParent(cur))
			{
				if (LooksLikeRealAppWindow(cur))
				{
					overRealApp = true;
					break;
				}
			}
		}
		if (e.Action == System.Windows.DragAction.Drop && hittingDesktop && !overRealApp)
		{
			e.Action = System.Windows.DragAction.Cancel;
			_oleDropCancelledForDesktop = true;
		}
	}

	private static bool LooksLikeRealAppWindow(nint hWnd)
	{
		if (hWnd == IntPtr.Zero)
		{
			return false;
		}
		StringBuilder cls = new StringBuilder(256);
		NativeMethods.GetClassName(hWnd, cls, 256);
		string className = cls.ToString();
		if (className.StartsWith("Ole") || className.Contains("DragDrop") || className.Contains("DropTarget"))
		{
			return false;
		}
		switch (className)
		{
		case "Progman":
		case "WorkerW":
		case "SHELLDLL_DefView":
		case "SysListView32":
			return false;
		default:
		{
			if (IsKnownAppWindowClass(className))
			{
				return true;
			}
			StringBuilder title = new StringBuilder(256);
			NativeMethods.GetWindowTextW(hWnd, title, 256);
			if (title.Length == 0)
			{
				return false;
			}
			return true;
		}
		}
	}

	private static bool IsDesktopOrOleWindowClass(string className)
	{
		if (string.IsNullOrEmpty(className))
		{
			return false;
		}
		switch (className)
		{
		case "Progman":
		case "WorkerW":
		case "SHELLDLL_DefView":
		case "SysListView32":
		case "FolderView":
			return true;
		default:
			if (className.StartsWith("Ole") || className.Contains("DragDrop") || className.Contains("DropTarget") || className == "DropTargetHelper")
			{
				return true;
			}
			return false;
		}
	}

	private static bool IsKnownAppWindowClass(string className)
	{
		if (string.IsNullOrEmpty(className))
		{
			return false;
		}
		string[] array = new string[20]
		{
			"WeChatMainWndForPC", "WeChat", "ChatWnd", "TXGuiFoundation", "QQChatWnd", "ContainerFrame", "QQLite", "DingTalk", "StandardFrame_DingTalk", "Feishu",
			"Lark", "Chrome_WidgetWin_1", "Chrome_WidgetWin_0", "Chrome_RenderWidgetHostHWND", "MozillaWindowClass", "IEFrame", "CabinetWClass", "OperationStuckWindow", "Chrome_", "Qt"
		};
		foreach (string k in array)
		{
			if (k.EndsWith("*"))
			{
				if (className.StartsWith(k.TrimEnd('*')))
				{
					return true;
				}
			}
			else if (className == k)
			{
				return true;
			}
		}
		return false;
	}

	private static bool TryCreateDragTempFile(string sourcePath, bool forceCopy, string? expectedDesktopTarget, out string? tempFilePath, out string? tempDir)
	{
		tempFilePath = null;
		tempDir = null;
		try
		{
			string baseDir = Path.Combine(Path.GetTempPath(), "DesktopTool_Drag");
			Directory.CreateDirectory(baseDir);
			tempDir = Path.Combine(baseDir, $"{Environment.TickCount64:X}");
			Directory.CreateDirectory(tempDir);
			string fileName = Path.GetFileName(sourcePath);
			if (!string.IsNullOrEmpty(expectedDesktopTarget) && (File.Exists(expectedDesktopTarget) || Directory.Exists(expectedDesktopTarget)))
			{
				fileName = GetUniqueFileName(Path.GetDirectoryName(expectedDesktopTarget) ?? tempDir, fileName);
			}
			tempFilePath = Path.Combine(tempDir, fileName);
			if (Directory.Exists(sourcePath))
			{
				try
				{
					Directory.Delete(tempFilePath, recursive: true);
				}
				catch
				{
				}
				CopyDirectory(sourcePath, tempFilePath);
				return true;
			}
			try
			{
				File.Delete(tempFilePath);
			}
			catch
			{
			}
			if (!forceCopy && NativeMethods.CreateHardLink(tempFilePath, sourcePath, IntPtr.Zero))
			{
				return true;
			}
			File.Copy(sourcePath, tempFilePath, overwrite: true);
			return true;
		}
		catch (Exception)
		{
			if (tempFilePath != null)
			{
				try
				{
					File.Delete(tempFilePath);
				}
				catch
				{
				}
				tempFilePath = null;
			}
			if (tempDir != null)
			{
				try
				{
					Directory.Delete(tempDir, recursive: true);
				}
				catch
				{
				}
				tempDir = null;
			}
			return false;
		}
	}

	private static string? GetExpectedDesktopDropPath(string sourcePath)
	{
		try
		{
			string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			if (string.IsNullOrEmpty(desktopDir))
			{
				return null;
			}
			return Path.Combine(desktopDir, Path.GetFileName(sourcePath));
		}
		catch
		{
			return null;
		}
	}

	private static string GetUniqueFileName(string dir, string fileName)
	{
		if (!File.Exists(Path.Combine(dir, fileName)) && !Directory.Exists(Path.Combine(dir, fileName)))
		{
			return fileName;
		}
		string baseName = Path.GetFileNameWithoutExtension(fileName);
		string ext = Path.GetExtension(fileName);
		int n = 1;
		string candidate;
		do
		{
			candidate = $"{baseName} ({n}){ext}";
			n++;
		}
		while (File.Exists(Path.Combine(dir, candidate)) || Directory.Exists(Path.Combine(dir, candidate)));
		return candidate;
	}

	private static string DescribeWindowAt(Point screenPt)
	{
		try
		{
			nint h = NativeMethods.WindowFromPoint(new NativeMethods.POINT((int)screenPt.X, (int)screenPt.Y));
			h = SkipOwnProcessWindows(h);
			if (h == IntPtr.Zero)
			{
				return "null";
			}
			StringBuilder cls = new StringBuilder(256);
			NativeMethods.GetClassName(h, cls, 256);
			StringBuilder title = new StringBuilder(256);
			NativeMethods.GetWindowTextW(h, title, 256);
			uint pid = 0u;
			NativeMethods.GetWindowThreadProcessId(h, out pid);
			return $"cls={cls};title={((title.Length == 0) ? "<empty>" : title.ToString())};pid={pid}";
		}
		catch (Exception ex)
		{
			return "err=" + ex.Message;
		}
	}

	private void ScheduleCleanupDragTempFiles()
	{
		string tempFile = _dragTempFilePath;
		string tempDir = _dragTempDir;
		_dragTempFilePath = null;
		_dragTempDir = null;
		if (tempFile == null && tempDir == null)
		{
			return;
		}
		Task.Delay(3000).ContinueWith(delegate
		{
			if (tempFile != null)
			{
				try
				{
					if (File.Exists(tempFile))
					{
						File.Delete(tempFile);
					}
				}
				catch
				{
				}
			}
			if (tempDir != null)
			{
				try
				{
					if (Directory.Exists(tempDir))
					{
						Directory.Delete(tempDir, recursive: true);
					}
				}
				catch
				{
				}
			}
		}, TaskScheduler.Default);
	}

	private static void CleanupStaleTempDirs()
	{
		try
		{
			string baseDir = Path.Combine(Path.GetTempPath(), "DesktopTool_Drag");
			if (!Directory.Exists(baseDir))
			{
				return;
			}
			DateTime cutoff = DateTime.UtcNow.AddMinutes(-30.0);
			string[] directories = Directory.GetDirectories(baseDir);
			foreach (string dir in directories)
			{
				try
				{
					if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
					{
						Directory.Delete(dir, recursive: true);
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	private void DispatchDropTarget(ZoneIcon icon)
	{
		NativeMethods.GetCursorPos(out var pt);
		Point screenPt = new Point(pt.X, pt.Y);
		if (InZoneBounds(screenPt))
		{
			return;
		}
		ZoneWindow targetZone = FindZoneWindowAtPoint(screenPt, this);
		if (targetZone != null)
		{
			MoveIconToOtherZone(icon, targetZone);
			return;
		}
		bool isDeskUnderMouse = IsDesktopWindowUnderMouse(screenPt);
		DragDebugDebugDebugDebugLog($"[ZoneWindow][DIAG] DispatchDropTarget oleCancelled={_oleDropCancelledForDesktop} isDeskUnderMouse={isDeskUnderMouse} hidden={icon.HiddenMethod} \"{icon.DisplayName}\" winAt={DescribeWindowAt(screenPt)}");
		if (_oleDropCancelledForDesktop || isDeskUnderMouse)
		{
			if (_oleDropCancelledForDesktop)
			{
				RestoreIconToDesktop(icon);
			}
			else if (icon.HiddenMethod == "Attributes")
			{
				RestoreIconToDesktop(icon);
			}
			else
			{
				CleanupAfterOleDesktopDrop(icon);
			}
		}
	}

	private static ZoneWindow? FindZoneWindowAtPoint(Point screenPt, ZoneWindow excludeSelf)
	{
		if (!(System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel mainVm))
		{
			return null;
		}
		foreach (ZoneWindow zone in mainVm.OpenZoneWindows)
		{
			if (zone == excludeSelf || !zone.IsVisible)
			{
				continue;
			}
			try
			{
				if (new Rect(zone.PointToScreen(new Point(0.0, 0.0)), zone.PointToScreen(new Point(zone.ActualWidth, zone.ActualHeight))).Contains(screenPt))
				{
					return zone;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private void MoveIconToOtherZone(ZoneIcon icon, ZoneWindow targetZone)
	{
		_suppressDesktopWatcher = true;
		try
		{
			IconGroup group = _viewModel.Groups.ElementAtOrDefault(_viewModel.ActiveGroupIndex);
			if (group != null && group.Icons.Contains(icon))
			{
				group.Icons.Remove(icon);
				_viewModel.SyncActiveIcons();
				_viewModel.UpdateGroupActiveState();
				_zone.NotifyTotalIconCountChanged();
			}
		}
		finally
		{
			base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				_suppressDesktopWatcher = false;
			});
		}
		targetZone.Dispatcher.Invoke(delegate
		{
			targetZone.AddIconFromOtherZone(icon);
		});
		RefreshAllContent();
		(System.Windows.Application.Current?.MainWindow?.DataContext as MainViewModel)?.SaveZonesNow();
	}

	public void AddIconFromOtherZone(ZoneIcon icon)
	{
		if (_viewModel.Groups.ElementAtOrDefault(_viewModel.ActiveGroupIndex) != null)
		{
			_viewModel.CommitIcon(icon);
			RefreshAllContent();
			_viewModel.UpdateGroupActiveState();
			_zone.NotifyTotalIconCountChanged();
		}
	}

	private void Icon_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (!_iconDragActive && _draggingIcon != null)
		{
			if (sender is FrameworkElement fe)
			{
				fe.ReleaseMouseCapture();
			}
			_draggingIcon = null;
		}
	}

	private bool InZoneBounds(Point screenPt)
	{
		return new Rect(PointToScreen(new Point(0.0, 0.0)), PointToScreen(new Point(base.ActualWidth, base.ActualHeight))).Contains(screenPt);
	}

	public static bool IsDesktopWindowUnderMouse(Point screenPt)
	{
		nint hWnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT((int)screenPt.X, (int)screenPt.Y));
		if (hWnd == IntPtr.Zero)
		{
			return false;
		}
		hWnd = SkipOwnProcessWindows(hWnd);
		StringBuilder clsSb = new StringBuilder(256);
		NativeMethods.GetClassName(hWnd, clsSb, 256);
		if (IsKnownAppWindowClass(clsSb.ToString()))
		{
			return false;
		}
		StringBuilder sb = new StringBuilder(256);
		for (int i = 0; i < 8; i++)
		{
			sb.Clear();
			NativeMethods.GetClassName(hWnd, sb, 256);
			switch (sb.ToString())
			{
			case "Progman":
			case "WorkerW":
			case "SHELLDLL_DefView":
				return true;
			}
			hWnd = NativeMethods.GetParent(hWnd);
			if (hWnd == IntPtr.Zero)
			{
				break;
			}
		}
		return false;
	}

	private static nint SkipOwnProcessWindows(nint hWnd)
	{
		uint ownPid = (uint)Environment.ProcessId;
		for (int i = 0; i < 10; i++)
		{
			NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
			if (pid != ownPid)
			{
				break;
			}
			hWnd = NativeMethods.GetWindow(hWnd, 2u);
			if (hWnd == IntPtr.Zero)
			{
				break;
			}
		}
		return hWnd;
	}

	private static bool IsDesktopByListViewBounds(Point screenPt)
	{
		try
		{
			nint hDesktop = FindDesktopListView();
			if (hDesktop == IntPtr.Zero)
			{
				return false;
			}
			if (!NativeMethods.GetWindowRect(hDesktop, out var rect))
			{
				return false;
			}
			return screenPt.X >= (double)rect.Left && screenPt.X <= (double)rect.Right && screenPt.Y >= (double)rect.Top && screenPt.Y <= (double)rect.Bottom;
		}
		catch
		{
			return false;
		}
	}

	private static nint FindDesktopListView()
	{
		nint hProgman = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
		if (hProgman != IntPtr.Zero)
		{
			nint hDefView = NativeMethods.FindWindowEx(hProgman, IntPtr.Zero, "SHELLDLL_DefView", null);
			if (hDefView != IntPtr.Zero)
			{
				nint hListView = NativeMethods.FindWindowEx(hDefView, IntPtr.Zero, "SysListView32", "FolderView");
				if (hListView != IntPtr.Zero)
				{
					return hListView;
				}
			}
		}
		for (nint hWorkerW = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "WorkerW", null); hWorkerW != IntPtr.Zero; hWorkerW = NativeMethods.FindWindowEx(IntPtr.Zero, hWorkerW, "WorkerW", null))
		{
			nint hDefView2 = NativeMethods.FindWindowEx(hWorkerW, IntPtr.Zero, "SHELLDLL_DefView", null);
			if (hDefView2 != IntPtr.Zero)
			{
				nint hListView2 = NativeMethods.FindWindowEx(hDefView2, IntPtr.Zero, "SysListView32", "FolderView");
				if (hListView2 != IntPtr.Zero)
				{
					return hListView2;
				}
			}
		}
		return IntPtr.Zero;
	}

	private void RestoreIconToDesktop(ZoneIcon icon)
	{
		_suppressDesktopWatcher = true;
		try
		{
			if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath))
			{
				if (DesktopIconService.RestoreFromHidden(icon.FilePath, icon.HiddenPath))
				{
					string actualPath = DesktopIconService.LastRestoredActualPath;
					if (!string.IsNullOrEmpty(actualPath) && !string.Equals(actualPath, icon.FilePath, StringComparison.OrdinalIgnoreCase))
					{
						icon.FilePath = actualPath;
					}
					DesktopIconService.RefreshDesktop(icon.FilePath);
				}
			}
			else if (icon.HiddenMethod == "Attributes")
			{
				DesktopIconService.TryAttributeRestore(icon.FilePath);
				if (icon.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(icon.FilePath))
				{
					try
					{
						File.SetLastWriteTime(icon.FilePath, DateTime.Now);
					}
					catch
					{
					}
					try
					{
						IconHelper.RepairWhiteIcon(icon.FilePath);
					}
					catch
					{
					}
				}
				DesktopIconService.NotifyItemUnhidden(icon.FilePath);
				if (!string.IsNullOrEmpty(_dragTempDesktopTarget) && !string.Equals(_dragTempDesktopTarget, icon.FilePath, StringComparison.OrdinalIgnoreCase))
				{
					string targetToDelete = _dragTempDesktopTarget;
					Task.Delay(800).ContinueWith(delegate
					{
						for (int i = 0; i < 8; i++)
						{
							try
							{
								if (Directory.Exists(targetToDelete))
								{
									Directory.Delete(targetToDelete, recursive: true);
									break;
								}
								if (File.Exists(targetToDelete))
								{
									File.Delete(targetToDelete);
									break;
								}
							}
							catch (Exception)
							{
							}
							Task.Delay(400).Wait();
						}
					}, TaskScheduler.Default);
				}
			}
			_viewModel.RemoveIconFromZone(icon);
		}
		catch (Exception)
		{
		}
		finally
		{
			base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				_suppressDesktopWatcher = false;
			});
		}
	}

	private void CleanupAfterOleDesktopDrop(ZoneIcon icon)
	{
		_suppressDesktopWatcher = true;
		try
		{
			string desktopTarget = ((!string.IsNullOrEmpty(_dragTempDesktopTarget)) ? _dragTempDesktopTarget : icon.FilePath);
			if (string.IsNullOrEmpty(desktopTarget) || (!File.Exists(desktopTarget) && !Directory.Exists(desktopTarget)))
			{
				RestoreIconToDesktop(icon);
				return;
			}
			string hiddenOrigin = ((icon.HiddenMethod == "Physical") ? icon.HiddenPath : icon.FilePath);
			if (!string.IsNullOrEmpty(hiddenOrigin) && (File.Exists(hiddenOrigin) || Directory.Exists(hiddenOrigin)))
			{
				try
				{
					if (Directory.Exists(hiddenOrigin))
					{
						Directory.Delete(hiddenOrigin, recursive: true);
					}
					else
					{
						File.Delete(hiddenOrigin);
					}
				}
				catch (Exception)
				{
				}
			}
			_viewModel.RemoveIconFromZone(icon);
			DesktopIconService.RefreshDesktop(desktopTarget);
		}
		catch (Exception)
		{
		}
		finally
		{
			base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				_suppressDesktopWatcher = false;
			});
		}
	}

	private static Border? FindAncestorIconBorder(DependencyObject child)
	{
		while (child != null)
		{
			if (child is Border border && border.DataContext is ZoneIcon)
			{
				return border;
			}
			child = VisualTreeHelper.GetParent(child);
		}
		return null;
	}

	private void SetupDesktopWatcher()
	{
		try
		{
			string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
			if (!string.IsNullOrEmpty(desktopPath) && Directory.Exists(desktopPath))
			{
				_desktopWatcher = new FileSystemWatcher(desktopPath)
				{
					NotifyFilter = (NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite),
					IncludeSubdirectories = true,
					EnableRaisingEvents = true
				};
				_desktopWatcher.Deleted += OnDesktopFileChanged;
				_desktopWatcher.Renamed += OnDesktopFileChanged;
			}
		}
		catch (Exception)
		{
		}
	}

	private void OnDesktopFileChanged(object sender, FileSystemEventArgs e)
	{
		if (!_suppressDesktopWatcher)
		{
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				OnDesktopFileChangedUI(e);
			});
		}
	}

	private void OnDesktopFileChangedUI(FileSystemEventArgs e)
	{
		if (_suppressDesktopWatcher)
		{
			return;
		}
		try
		{
			if (e is RenamedEventArgs renamed)
			{
				string path = _desktopWatcher?.Path ?? Path.GetDirectoryName(e.FullPath) ?? string.Empty;
				string newFull = e.FullPath;
				string oldFull = Path.Combine(path, renamed.OldName);
				if (IsTrackedPath(newFull))
				{
					_pendingRemoval.Remove(newFull);
				}
				if (!IsTrackedPath(newFull) && IsTrackedPath(oldFull))
				{
					_pendingRemoval.Add(oldFull);
				}
			}
			else if (e.ChangeType == WatcherChangeTypes.Deleted && IsTrackedPath(e.FullPath))
			{
				_pendingRemoval.Add(e.FullPath);
			}
		}
		catch (Exception)
		{
		}
		ScheduleWatcherCheck();
	}

	private void ScheduleWatcherCheck()
	{
		if (_watcherDebounceTimer == null)
		{
			_watcherDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, base.Dispatcher)
			{
				Interval = TimeSpan.FromMilliseconds(1200.0)
			};
			_watcherDebounceTimer.Tick += delegate
			{
				_watcherDebounceTimer.Stop();
				CheckAndRemoveMissingIcons();
			};
		}
		_watcherDebounceTimer.Stop();
		_watcherDebounceTimer.Start();
	}

	private bool IsTrackedPath(string? path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return false;
		}
		foreach (IconGroup group in _viewModel.Groups)
		{
			foreach (ZoneIcon icon in group.Icons)
			{
				if (string.Equals(icon.FilePath, path, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
		}
		return false;
	}

	private void CheckAndRemoveMissingIcons()
	{
		if (_suppressDesktopWatcher)
		{
			return;
		}
		try
		{
			foreach (IconGroup item in _viewModel.Groups.ToList())
			{
				foreach (ZoneIcon icon in item.Icons.ToList())
				{
					if (string.IsNullOrEmpty(icon.FilePath))
					{
						continue;
					}
					if (File.Exists(icon.FilePath) || Directory.Exists(icon.FilePath))
					{
						if (icon.HiddenMethod == "Attributes" && !DesktopIconService.IsFullyAttributeHidden(icon.FilePath))
						{
							DesktopIconService.TryAttributeHide(icon.FilePath);
						}
						_pendingRemoval.Remove(icon.FilePath);
					}
					else if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath) && (File.Exists(icon.HiddenPath) || Directory.Exists(icon.HiddenPath)))
					{
						_pendingRemoval.Remove(icon.FilePath);
					}
					else if (_pendingRemoval.Contains(icon.FilePath))
					{
						_pendingRemoval.Remove(icon.FilePath);
						if (icon.HiddenMethod == "Attributes")
						{
							DesktopIconService.TryAttributeRestore(icon.FilePath);
						}
						_viewModel.RemoveIconFromZone(icon);
					}
					else
					{
						_pendingRemoval.Add(icon.FilePath);
						ScheduleWatcherCheck();
					}
				}
			}
		}
		catch (Exception)
		{
		}
	}

	protected override void OnClosed(EventArgs e)
	{
		_olePollTimer?.Stop();
		_olePollTimer = null;
		_desktopWatcher?.Dispose();
		_desktopWatcher = null;
		base.OnClosed(e);
	}

	private void ReorderIcon(ZoneIcon dragged, ZoneIcon target)
	{
		IconGroup activeGroup = _viewModel.Groups.ElementAtOrDefault(_viewModel.ActiveGroupIndex);
		if (activeGroup == null)
		{
			return;
		}
		int srcIdx = activeGroup.Icons.IndexOf(dragged);
		int dstIdx = activeGroup.Icons.IndexOf(target);
		if (srcIdx < 0 || dstIdx < 0 || srcIdx == dstIdx)
		{
			return;
		}
		activeGroup.Icons.RemoveAt(srcIdx);
		if (srcIdx < dstIdx)
		{
			dstIdx--;
		}
		activeGroup.Icons.Insert(dstIdx, dragged);
		int uiSrc = _viewModel.Icons.IndexOf(dragged);
		int uiDst = _viewModel.Icons.IndexOf(target);
		if (uiSrc >= 0 && uiDst >= 0)
		{
			if (uiSrc < uiDst)
			{
				uiDst--;
			}
			_viewModel.Icons.Move(uiSrc, uiDst);
		}
		_viewModel.SaveState();
	}

	private void MoveIconToEnd(ZoneIcon dragged)
	{
		IconGroup activeGroup = _viewModel.Groups.ElementAtOrDefault(_viewModel.ActiveGroupIndex);
		if (activeGroup == null)
		{
			return;
		}
		int srcIdx = activeGroup.Icons.IndexOf(dragged);
		if (srcIdx >= 0)
		{
			activeGroup.Icons.RemoveAt(srcIdx);
			activeGroup.Icons.Add(dragged);
			int uiSrc = _viewModel.Icons.IndexOf(dragged);
			if (uiSrc >= 0)
			{
				_viewModel.Icons.Move(uiSrc, _viewModel.Icons.Count - 1);
			}
			_viewModel.SaveState();
		}
	}

	private int FindNearestInsertIndex(Point mousePos, ZoneIcon dragged)
	{
		IconGroup activeGroup = _viewModel.Groups.ElementAtOrDefault(_viewModel.ActiveGroupIndex);
		if (activeGroup == null || activeGroup.Icons.Count <= 1)
		{
			return 0;
		}
		ItemsControl itemsControl = (_viewModel.IsListView ? ListItemsControl : IconsItemsControl);
		if (itemsControl == null)
		{
			return activeGroup.Icons.Count - 1;
		}
		List<(Point, int)> positions = new List<(Point, int)>();
		for (int i = 0; i < activeGroup.Icons.Count; i++)
		{
			if (activeGroup.Icons[i] == dragged)
			{
				continue;
			}
			DependencyObject container = itemsControl.ItemContainerGenerator.ContainerFromIndex(i);
			if (container != null && VisualTreeHelper.GetChildrenCount(container) >= 1 && VisualTreeHelper.GetChild(container, 0) is Border border)
			{
				try
				{
					Point topLeft = border.TranslatePoint(new Point(0.0, 0.0), this);
					double cx = topLeft.X + border.ActualWidth / 2.0;
					double cy = topLeft.Y + border.ActualHeight / 2.0;
					positions.Add((new Point(cx, cy), i));
				}
				catch
				{
				}
			}
		}
		if (positions.Count == 0)
		{
			return activeGroup.Icons.Count - 1;
		}
		(Point, int) nearest = positions.OrderBy<(Point, int), double>(delegate((Point Center, int SourceIndex) p)
		{
			double num = p.Center.X - mousePos.X;
			double num2 = p.Center.Y - mousePos.Y;
			return num * num + num2 * num2;
		}).First();
		int nearestIdx = nearest.Item2;
		bool insertAfter = ((!_viewModel.IsListView) ? (mousePos.Y > nearest.Item1.Y || (Math.Abs(mousePos.Y - nearest.Item1.Y) < 10.0 && mousePos.X > nearest.Item1.X)) : (mousePos.Y > nearest.Item1.Y));
		int insertIdx = (insertAfter ? (nearestIdx + 1) : nearestIdx);
		return Math.Max(0, Math.Min(insertIdx, activeGroup.Icons.Count));
	}

	private void InsertIconAt(ZoneIcon dragged, int insertIdx)
	{
		IconGroup activeGroup = _viewModel.Groups.ElementAtOrDefault(_viewModel.ActiveGroupIndex);
		if (activeGroup == null)
		{
			return;
		}
		int srcIdx = activeGroup.Icons.IndexOf(dragged);
		if (srcIdx >= 0)
		{
			activeGroup.Icons.RemoveAt(srcIdx);
			if (srcIdx < insertIdx)
			{
				insertIdx--;
			}
			insertIdx = Math.Max(0, Math.Min(insertIdx, activeGroup.Icons.Count));
			activeGroup.Icons.Insert(insertIdx, dragged);
			int uiSrc = _viewModel.Icons.IndexOf(dragged);
			if (uiSrc >= 0)
			{
				_viewModel.Icons.Move(uiSrc, Math.Min(insertIdx, _viewModel.Icons.Count - 1));
			}
			_viewModel.SaveState();
		}
	}

	private void CreateDragGhost(FrameworkElement source, ZoneIcon icon)
	{
		DestroyDragGhost();
		_iconSourcePos = source.TranslatePoint(new Point(0.0, 0.0), DragOverlay);
		Image ghostImage = new Image
		{
			Source = icon.IconImage,
			Width = _viewModel.IconImageSize,
			Height = _viewModel.IconImageSize,
			Stretch = Stretch.Uniform,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center
		};
		RenderOptions.SetBitmapScalingMode(ghostImage, BitmapScalingMode.HighQuality);
		_dragGhost = new Border
		{
			Width = source.ActualWidth,
			Height = source.ActualHeight,
			Background = new SolidColorBrush(Color.FromArgb(80, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			CornerRadius = new CornerRadius(4.0),
			Opacity = 0.85,
			IsHitTestVisible = false,
			Child = new StackPanel
			{
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = System.Windows.HorizontalAlignment.Center
			}
		};
		StackPanel obj = (StackPanel)_dragGhost.Child;
		obj.Children.Add(ghostImage);
		obj.Children.Add(new TextBlock
		{
			Text = icon.DisplayName,
			Foreground = Brushes.White,
			FontSize = _viewModel.IconFontSize,
			TextAlignment = TextAlignment.Center,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
		});
		Canvas.SetLeft(_dragGhost, _iconSourcePos.X);
		Canvas.SetTop(_dragGhost, _iconSourcePos.Y);
		DragOverlay.Children.Add(_dragGhost);
	}

	private void DestroyDragGhost()
	{
		if (_dragGhost != null)
		{
			DragOverlay.Children.Remove(_dragGhost);
			_dragGhost = null;
		}
	}

	private void CancelIconDrag()
	{
		if (_iconDragActive)
		{
			_draggingIcon = null;
			_iconDragActive = false;
			DestroyDragGhost();
		}
	}

	private async void RemoveIcon(ZoneIcon icon)
	{
		if (icon.HiddenPath != null)
		{
			List<(string FileName, string ProcessName)> locks;
			try
			{
				locks = DesktopIconService.CheckFileLocks(icon.HiddenPath);
			}
			catch
			{
				locks = new List<(string, string)>();
			}
			if (locks.Count > 0)
			{
				string name = icon.DisplayName ?? Path.GetFileName(icon.FilePath);
				IEnumerable<string> lockDetails = from l in locks.Take(5)
					select $"• {l.FileName} — 被 {l.ProcessName} 占用";
				System.Windows.MessageBox.Show("「" + name + "」中的文件正在被其他应用编辑中，强制移动可能导致数据丢失：\n\n" + string.Join("\n", lockDetails) + ((locks.Count > 5) ? $"\n... 等共 {locks.Count} 个文件" : "") + "\n\n请保存并关闭编辑后再试。", "文件编辑中", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			string fileName = icon.DisplayName ?? Path.GetFileName(icon.FilePath);
			if (Directory.Exists(icon.HiddenPath) || (File.Exists(icon.HiddenPath) && new FileInfo(icon.HiddenPath).Length > 10000000))
			{
				bool restoreSuccess = false;
				await ProgressWindow.RunWithProgressAsync(this, "正在将「" + fileName + "」移回桌面...", async delegate(Action<string?> report)
				{
					await Task.Run(delegate
					{
						report(fileName);
						restoreSuccess = DesktopIconService.RestoreFromHidden(icon.FilePath, icon.HiddenPath);
					});
				});
				if (!restoreSuccess)
				{
					string reason = DesktopIconService.LastRestoreFailReason ?? "未知原因";
					System.Windows.MessageBox.Show($"「{fileName}」还原到桌面失败，该图标将保留在收纳区。\n\n原因：{reason}\n\n可能原因：\n• 文件正在被其他程序占用\n• 隐藏目录被清理或损坏", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
					return;
				}
				icon.HiddenPath = null;
				DesktopIconService.SyncIconFilePath(icon, "ZoneWindow.RemoveIcon-Large");
			}
			else
			{
				if (!DesktopIconService.RestoreFromHidden(icon.FilePath, icon.HiddenPath))
				{
					string reason2 = DesktopIconService.LastRestoreFailReason ?? "未知原因";
					System.Windows.MessageBox.Show($"「{fileName}」还原到桌面失败，该图标将保留在收纳区。\n\n原因：{reason2}\n\n可能原因：\n• 文件正在被其他程序占用\n• 隐藏目录被清理或损坏", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
					return;
				}
				icon.HiddenPath = null;
				string actualPath = DesktopIconService.LastRestoredActualPath;
				if (!string.IsNullOrEmpty(actualPath) && !string.Equals(actualPath, icon.FilePath, StringComparison.OrdinalIgnoreCase))
				{
					icon.FilePath = actualPath;
				}
			}
		}
		try
		{
			DesktopIconService.RefreshDesktop(icon.FilePath);
			DesktopIconService.NotifyItemUnhidden(icon.FilePath);
		}
		catch
		{
		}
		_viewModel.DeleteIconCommand.Execute(icon);
	}

	private void LaunchIcon(ZoneIcon icon)
	{
		string path = icon?.ResolvedPath;
		LogHelper.Append(_debugLogPath, $"[ZoneWindow][LaunchIcon] 入口 icon={icon?.DisplayName} path={path} mode={icon?.HiddenMethod} hwnd=Zero{Environment.NewLine}");
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		try
		{
			nint hwnd = IntPtr.Zero;
			string sp = path;
			Task.Run(delegate
			{
				try
				{
					LogHelper.Append(_debugLogPath, $"[ZoneWindow][LaunchIcon] 进入后台线程 path={sp} isDir={Directory.Exists(sp)}{Environment.NewLine}");
					if (Directory.Exists(sp))
					{
						LogHelper.Append(_debugLogPath, "[ZoneWindow] 打开文件夹（ShellOpenAsync 路径）：" + sp + Environment.NewLine);
						ShellOpenAsync("open", sp, hwnd);
					}
					else
					{
						ShellOpenAsync("open", sp, hwnd);
					}
				}
				catch (Exception ex2)
				{
					LogHelper.Append(_debugLogPath, "[ZoneWindow] ShellOpenAsync 异常：" + ex2.Message + Environment.NewLine);
				}
			});
		}
		catch (Exception ex)
		{
			LogHelper.Append(_debugLogPath, $"[ZoneWindow] 打开图标异常：{path} - {ex.Message}{Environment.NewLine}");
		}
	}

	private void ShellOpenAsync(string verb, string path, nint hwnd, string? parameters = null)
	{
		NativeMethods.ShellExecuteOpenAsync(path, hwnd, verb, parameters);
	}

	private void Icon_ToolTipOpening(object sender, ToolTipEventArgs e)
	{
		if (!(sender is FrameworkElement { DataContext: ZoneIcon { ResolvedPath: var path } icon } fe))
		{
			return;
		}
		string type = "未知";
		string size = "-";
		string modified = "-";
		try
		{
			if (File.Exists(path))
			{
				FileInfo fileInfo = new FileInfo(path);
				type = GetFileTypeString(fileInfo.Extension);
				size = FormatFileSize(fileInfo.Length);
				modified = fileInfo.LastWriteTime.ToString("yyyy/M/d HH:mm");
			}
			else if (Directory.Exists(path))
			{
				DirectoryInfo di = new DirectoryInfo(path);
				type = "文件夹";
				try
				{
					size = di.GetFileSystemInfos().Length + " 个项目";
				}
				catch
				{
				}
				modified = di.LastWriteTime.ToString("yyyy/M/d HH:mm");
			}
		}
		catch
		{
		}
		fe.ToolTip = BuildToolTipContent(icon.DisplayName, type, size, modified);
	}

	private static FrameworkElement BuildToolTipContent(string fileName, string type, string size, string modified)
	{
		StackPanel sp = new StackPanel();
		sp.Children.Add(new TextBlock
		{
			Text = fileName,
			Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
			FontSize = 12.0,
			FontWeight = FontWeights.Bold
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
		if (type == "文件夹")
		{
			sp.Children.Add(new TextBlock
			{
				Text = "\ud83d\udca1 建议收纳文件夹的快捷方式，来确保文件安全、提升速度",
				Foreground = new SolidColorBrush(Color.FromRgb(180, 120, 40)),
				FontSize = 10.0,
				Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
				FontStyle = FontStyles.Italic
			});
		}
		return new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(240, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			CornerRadius = new CornerRadius(4.0),
			Padding = new Thickness(10.0, 8.0, 10.0, 8.0),
			Child = sp
		};
	}

	private static string GetFileTypeString(string ext)
	{
		switch (ext.ToLower())
		{
		case ".txt":
			return "文本文档";
		case ".doc":
		case ".docx":
			return "Word 文档";
		case ".xls":
		case ".xlsx":
			return "Excel 表格";
		case ".ppt":
		case ".pptx":
			return "PowerPoint 演示";
		case ".pdf":
			return "PDF 文档";
		case ".zip":
		case ".rar":
		case ".7z":
			return "压缩文件";
		case ".exe":
			return "应用程序";
		case ".lnk":
			return "快捷方式";
		case ".png":
		case ".jpg":
		case ".gif":
		case ".bmp":
		case ".webp":
		case ".jpeg":
			return "图片文件";
		case ".mp4":
		case ".mkv":
		case ".mov":
		case ".avi":
		case ".wmv":
			return "视频文件";
		case ".mp3":
		case ".aac":
		case ".wav":
		case ".flac":
			return "音频文件";
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

	private static string FormatFileSize(long bytes)
	{
		if (bytes >= 1024)
		{
			if (bytes < 1048576)
			{
				return $"{(double)bytes / 1024.0:F2} KB";
			}
			return $"{(double)bytes / 1048576.0:F2} MB";
		}
		return $"{bytes} B";
	}

	private static ZoneIcon? GetIconFromContextMenu(object sender)
	{
		DependencyObject current = sender as DependencyObject;
		while (current != null)
		{
			if (current is FrameworkElement { DataContext: ZoneIcon icon })
			{
				return icon;
			}
			if (current is ContextMenu { PlacementTarget: FrameworkElement { DataContext: ZoneIcon cicon } })
			{
				return cicon;
			}
			current = VisualTreeHelper.GetParent(current);
			if (current is MenuItem)
			{
				DependencyObject logicalParent = LogicalTreeHelper.GetParent(current);
				if (logicalParent is ContextMenu { PlacementTarget: FrameworkElement { DataContext: ZoneIcon licon } })
				{
					return licon;
				}
				current = logicalParent;
			}
		}
		return null;
	}

	private void ZoneIconContextMenu_Opening(object? sender, ContextMenuEventArgs e)
	{
		ContextMenu menu = (sender as ContextMenu) ?? (sender as FrameworkElement)?.ContextMenu;
		if (menu != null)
		{
			bool canRun = NativeMethods.CanRunAsAdmin(GetIconFromContextMenu(sender)?.ResolvedPath);
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
		ZoneIcon icon = GetIconFromContextMenu(sender);
		if (icon != null)
		{
			string path = icon.ResolvedPath;
			if (NativeMethods.CanRunAsAdmin(path))
			{
				NativeMethods.ShellExecuteOpenAsync(path, IntPtr.Zero, "runas");
			}
		}
	}

	private void ContextMenu_Open(object sender, RoutedEventArgs e)
	{
		ZoneIcon icon = GetIconFromContextMenu(sender);
		if (icon != null)
		{
			LaunchIcon(icon);
		}
	}

	private void ContextMenu_OpenFolder(object sender, RoutedEventArgs e)
	{
		ZoneIcon icon = GetIconFromContextMenu(sender);
		if (icon == null)
		{
			return;
		}
		try
		{
			string path = icon.ResolvedPath;
			if (string.IsNullOrEmpty(path))
			{
				return;
			}
			string openPath = path;
			if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
			{
				string target = ResolveShortcutTarget(path);
				if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
				{
					openPath = target;
				}
			}
			if (File.Exists(openPath) || Directory.Exists(openPath))
			{
				ShellOpenAsync("open", "explorer.exe", IntPtr.Zero, "/select,\"" + openPath + "\"");
			}
		}
		catch
		{
		}
	}

	private static string? ResolveShortcutTarget(string lnkPath)
	{
		try
		{
			if (!lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(lnkPath))
			{
				return null;
			}
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

	private void ContextMenu_Properties(object sender, RoutedEventArgs e)
	{
		ZoneIcon icon = GetIconFromContextMenu(sender);
		if (icon == null)
		{
			return;
		}
		try
		{
			string path = icon.ResolvedPath;
			if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
			{
				ShellOpenAsync("properties", path, IntPtr.Zero);
			}
		}
		catch
		{
		}
	}

	private void ContextMenu_Rename(object sender, RoutedEventArgs e)
	{
		ZoneIcon icon = GetIconFromContextMenu(sender);
		if (icon == null)
		{
			return;
		}
		string resolvedPath = icon.ResolvedPath;
		if (string.IsNullOrEmpty(resolvedPath) || (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath)))
		{
			return;
		}
		string dir = Path.GetDirectoryName(resolvedPath);
		bool num = Directory.Exists(resolvedPath);
		string ext = (num ? "" : Path.GetExtension(resolvedPath));
		string defaultName = (num ? Path.GetFileName(resolvedPath) : Path.GetFileNameWithoutExtension(icon.DisplayName ?? Path.GetFileName(resolvedPath)));
		InputDialog dialog = new InputDialog("请输入新文件名：", "重命名", defaultName)
		{
			Owner = this
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
		if (string.Equals(resolvedPath, newFullPath, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		if (File.Exists(newFullPath) || Directory.Exists(newFullPath))
		{
			System.Windows.MessageBox.Show("已存在同名文件，无法完成重命名。", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string oldHiddenPath = (icon.IsHidden ? icon.HiddenPath : null);
		try
		{
			if (File.Exists(resolvedPath))
			{
				File.Move(resolvedPath, newFullPath);
			}
			else
			{
				if (!Directory.Exists(resolvedPath))
				{
					return;
				}
				Directory.Move(resolvedPath, newFullPath);
			}
			if (icon.HiddenMethod == "Physical")
			{
				icon.HiddenPath = newFullPath;
			}
			icon.FilePath = Path.Combine(Path.GetDirectoryName(icon.FilePath) ?? Path.GetDirectoryName(resolvedPath) ?? "", newName + ext);
			icon.DisplayName = (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? newName : (newName + ext));
			if (oldHiddenPath != null)
			{
				DesktopIconService.UpdateManifestEntry(oldHiddenPath, newFullPath);
			}
			DesktopIconService.NotifyDesktopChange(newFullPath);
			_viewModel.SaveState();
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("重命名失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void ContextMenu_Remove(object sender, RoutedEventArgs e)
	{
		ZoneIcon icon = GetIconFromContextMenu(sender);
		if (icon != null)
		{
			RemoveIcon(icon);
		}
	}

	private void ContextMenu_Copy(object sender, RoutedEventArgs e)
	{
		ZoneIcon icon = GetIconFromContextMenu(sender);
		if (icon == null)
		{
			return;
		}
		try
		{
			string path = icon.ResolvedPath;
			if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
			{
				return;
			}
			if (!string.Equals(icon.HiddenMethod ?? "", "Physical", StringComparison.OrdinalIgnoreCase))
			{
				string cachePath = CopyToClipboardCacheWithClearedAttributes(path);
				if (!string.IsNullOrEmpty(cachePath))
				{
					System.Windows.Clipboard.SetFileDropList(new StringCollection { cachePath });
					return;
				}
			}
			System.Windows.Clipboard.SetFileDropList(new StringCollection { path });
		}
		catch (Exception)
		{
		}
	}

	private static string? CopyToClipboardCacheWithClearedAttributes(string source)
	{
		try
		{
			string cacheDir = Path.Combine(App.GetAppDataDir(), "ClipboardCache");
			try
			{
				if (Directory.Exists(cacheDir))
				{
					Directory.Delete(cacheDir, recursive: true);
				}
			}
			catch
			{
			}
			Directory.CreateDirectory(cacheDir);
			string dest = Path.Combine(cacheDir, Path.GetFileName(source));
			if (File.Exists(source))
			{
				File.Copy(source, dest, overwrite: true);
			}
			else
			{
				if (!Directory.Exists(source))
				{
					return null;
				}
				CopyDirectory(source, dest);
			}
			ClearHiddenSystemAttributes(dest);
			return dest;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static void CopyDirectory(string src, string dst)
	{
		Directory.CreateDirectory(dst);
		string[] files = Directory.GetFiles(src);
		foreach (string f in files)
		{
			File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
		}
		files = Directory.GetDirectories(src);
		foreach (string sub in files)
		{
			CopyDirectory(sub, Path.Combine(dst, Path.GetFileName(sub)));
		}
	}

	private static void ClearHiddenSystemAttributes(string path)
	{
		try
		{
			if (File.Exists(path) || Directory.Exists(path))
			{
				FileAttributes attrs = File.GetAttributes(path);
				FileAttributes cleared = attrs & ~(FileAttributes.Hidden | FileAttributes.System);
				if (cleared != attrs)
				{
					File.SetAttributes(path, cleared);
				}
			}
		}
		catch
		{
		}
		try
		{
			if (Directory.Exists(path))
			{
				string[] files = Directory.GetFiles(path);
				for (int i = 0; i < files.Length; i++)
				{
					ClearHiddenSystemAttributes(files[i]);
				}
				files = Directory.GetDirectories(path);
				for (int i = 0; i < files.Length; i++)
				{
					ClearHiddenSystemAttributes(files[i]);
				}
			}
		}
		catch
		{
		}
	}

	private void ContextMenu_Recycle(object sender, RoutedEventArgs e)
	{
		ZoneIcon icon = GetIconFromContextMenu(sender);
		if (icon == null)
		{
			return;
		}
		try
		{
			string fileName = icon.DisplayName ?? Path.GetFileName(icon.FilePath);
			string fileType = ((icon.FilePath != null && (File.Exists(icon.FilePath) || icon.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))) ? "文件" : "文件夹");
			if (System.Windows.MessageBox.Show($"确定要将{fileType}「{fileName}」移入回收站吗？\n\n移入后可从回收站还原。", "移入回收站", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
			{
				return;
			}
			if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath))
			{
				if (!DesktopIconService.RestoreFromHidden(icon.FilePath, icon.HiddenPath))
				{
					System.Windows.MessageBox.Show("无法还原文件到桌面：" + (DesktopIconService.LastRestoreFailReason ?? "未知错误"), "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
					return;
				}
				DesktopIconService.SyncIconFilePath(icon, "ZoneWindow.Recycle");
			}
			else if (icon.HiddenMethod == "Attributes")
			{
				DesktopIconService.TryAttributeRestore(icon.FilePath);
			}
			string desktopPath = icon.FilePath;
			if (string.IsNullOrEmpty(desktopPath) || (!File.Exists(desktopPath) && !Directory.Exists(desktopPath)))
			{
				System.Windows.MessageBox.Show("文件不存在，可能已被移动或删除。", "错误", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			SendToRecycleBin(desktopPath);
			icon.HiddenMethod = null;
			icon.HiddenPath = null;
			_viewModel.DeleteIconCommand.Execute(icon);
		}
		catch (Exception)
		{
		}
	}

	private static void SendToRecycleBin(string path)
	{
		NativeMethods.SHFILEOPSTRUCT fileOp = new NativeMethods.SHFILEOPSTRUCT
		{
			hwnd = IntPtr.Zero,
			wFunc = 3u,
			pFrom = path + "\0\0",
			fFlags = 16464
		};
		int result = NativeMethods.SHFileOperation(ref fileOp);
		if (result != 0)
		{
			throw new InvalidOperationException($"SHFileOperation returned error code: {result}");
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
		if (sender is Border { DataContext: IconGroup group } border)
		{
			_tabDragStartPoint = e.GetPosition(this);
			_draggingTab = group;
			_tabSourceIndex = _viewModel.Groups.IndexOf(group);
			_tabDragActive = false;
			border.CaptureMouse();
		}
	}

	private void TabItem_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (_draggingTab == null || e.LeftButton != MouseButtonState.Pressed)
		{
			if (_draggingTab != null)
			{
				_ = e.LeftButton;
				_ = 1;
			}
			return;
		}
		Point pos = e.GetPosition(this);
		double dx = pos.X - _tabDragStartPoint.X;
		double dy = pos.Y - _tabDragStartPoint.Y;
		if (!_tabDragActive)
		{
			if (Math.Abs(dx) < 6.0 && Math.Abs(dy) < 6.0)
			{
				return;
			}
			_tabDragActive = true;
			if (sender is FrameworkElement src)
			{
				CreateTabDragGhost(src);
			}
		}
		if (_tabDragGhost != null)
		{
			Canvas.SetLeft(_tabDragGhost, pos.X - _tabDragGhost.ActualWidth / 2.0);
			Canvas.SetTop(_tabDragGhost, pos.Y - _tabDragGhost.ActualHeight / 2.0);
		}
		UpdateTabAutoScroll(pos);
	}

	private void UpdateTabAutoScroll(Point mousePosRelativeToWindow)
	{
		if (TabScrollViewer == null || TabScrollViewer.ScrollableWidth <= 1.0)
		{
			StopTabAutoScroll();
			return;
		}
		try
		{
			double leftEdge = TabScrollViewer.TransformToAncestor(this).Transform(new Point(0.0, 0.0)).X;
			double rightEdge = leftEdge + TabScrollViewer.ViewportWidth;
			if (mousePosRelativeToWindow.X <= leftEdge + 28.0)
			{
				_tabAutoScrollDelta = -24.0;
				EnsureTabAutoScrollTimer();
			}
			else if (mousePosRelativeToWindow.X >= rightEdge - 28.0)
			{
				_tabAutoScrollDelta = 24.0;
				EnsureTabAutoScrollTimer();
			}
			else
			{
				StopTabAutoScroll();
			}
		}
		catch
		{
			StopTabAutoScroll();
		}
	}

	private void EnsureTabAutoScrollTimer()
	{
		if (_tabAutoScrollTimer != null)
		{
			return;
		}
		_tabAutoScrollTimer = new DispatcherTimer(DispatcherPriority.Background, base.Dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(30.0)
		};
		_tabAutoScrollTimer.Tick += delegate
		{
			if (TabScrollViewer == null)
			{
				StopTabAutoScroll();
			}
			else
			{
				double val = TabScrollViewer.HorizontalOffset + _tabAutoScrollDelta;
				val = Math.Max(0.0, Math.Min(TabScrollViewer.ScrollableWidth, val));
				TabScrollViewer.ScrollToHorizontalOffset(val);
			}
		};
		_tabAutoScrollTimer.Start();
	}

	private void StopTabAutoScroll()
	{
		_tabAutoScrollDelta = 0.0;
		if (_tabAutoScrollTimer != null)
		{
			_tabAutoScrollTimer.Stop();
			_tabAutoScrollTimer = null;
		}
	}

	private void TabItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (sender is FrameworkElement fe)
		{
			fe.ReleaseMouseCapture();
		}
		IconGroup group = _draggingTab;
		_draggingTab = null;
		if (_tabDragActive && group != null)
		{
			_tabDragActive = false;
			DestroyTabDragGhost();
			StopTabAutoScroll();
			Point mousePos = e.GetPosition(this);
			bool moved = false;
			IconGroup targetGroup = null;
			HitTestResult hitResult = VisualTreeHelper.HitTest(this, mousePos);
			if (hitResult != null)
			{
				Border targetBorder = FindAncestorTabBorder(hitResult.VisualHit);
				if (targetBorder != null && targetBorder.DataContext is IconGroup tg)
				{
					targetGroup = tg;
				}
			}
			if (targetGroup == null)
			{
				targetGroup = FindTabGroupUnderPoint(mousePos);
			}
			if (targetGroup != null && targetGroup != group)
			{
				int targetIndex = _viewModel.Groups.IndexOf(targetGroup);
				if (targetIndex >= 0 && _tabSourceIndex >= 0)
				{
					_viewModel.MoveGroup(_tabSourceIndex, targetIndex);
					moved = true;
				}
			}
			if (moved || _tabSourceIndex < 0)
			{
				return;
			}
			if (IsMouseInTabLeftZone(mousePos))
			{
				if (_tabSourceIndex != 0)
				{
					_viewModel.MoveGroup(_tabSourceIndex, 0);
				}
			}
			else if (IsMouseBeyondLastTab(mousePos) || IsMouseInTabRightZone(mousePos))
			{
				int last = _viewModel.Groups.Count - 1;
				if (_tabSourceIndex != last)
				{
					_viewModel.MoveGroup(_tabSourceIndex, last);
				}
			}
			return;
		}
		_tabDragActive = false;
		if (!(sender is Border { DataContext: IconGroup g } border))
		{
			return;
		}
		DateTime now = DateTime.Now;
		if (g == _lastClickedTabGroup && (now - _lastTabClickTime).TotalMilliseconds < (double)SystemInformation.DoubleClickTime)
		{
			System.Windows.Controls.TextBox tb = FindVisualChild(border);
			if (tb != null)
			{
				_lastTabClickTime = DateTime.MinValue;
				_lastClickedTabGroup = null;
				if (_isEditingTitle)
				{
					LeaveTitleEditMode();
					TitleTextBox.IsReadOnly = true;
				}
				_tabOriginalName = g.Name;
				EnterTabEditMode();
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
		_lastTabClickTime = now;
		_lastClickedTabGroup = g;
		_viewModel.SwitchGroupCommand.Execute(g);
	}

	private static Border? FindAncestorTabBorder(DependencyObject child)
	{
		while (child != null)
		{
			if (child is Border border && border.DataContext is IconGroup)
			{
				return border;
			}
			child = VisualTreeHelper.GetParent(child);
		}
		return null;
	}

	private IconGroup? FindTabGroupUnderPoint(Point mousePosRelativeToWindow)
	{
		if (TabsItemsControl == null)
		{
			return null;
		}
		for (int i = 0; i < _viewModel.Groups.Count; i++)
		{
			if (!(TabsItemsControl.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement { IsVisible: not false } container))
			{
				continue;
			}
			try
			{
				Point topLeft = container.TranslatePoint(new Point(0.0, 0.0), this);
				if (new Rect(topLeft, new Size(container.ActualWidth, container.ActualHeight)).Contains(mousePosRelativeToWindow))
				{
					return _viewModel.Groups[i];
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private bool IsMouseBeyondLastTab(Point mousePosRelativeToWindow)
	{
		if (TabScrollViewer.Content is ItemsControl itemsControl && itemsControl.Items.Count > 0)
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

	private bool IsMouseInTabLeftZone(Point mousePosRelativeToWindow)
	{
		if (TabScrollViewer == null || TabScrollViewer.ScrollableWidth <= 1.0)
		{
			return false;
		}
		try
		{
			double leftEdge = TabScrollViewer.TransformToAncestor(this).Transform(new Point(0.0, 0.0)).X;
			double topEdge = TabScrollViewer.TransformToAncestor(this).Transform(new Point(0.0, 0.0)).Y;
			double bottomEdge = TabScrollViewer.TransformToAncestor(this).Transform(new Point(0.0, TabScrollViewer.ViewportHeight)).Y;
			return mousePosRelativeToWindow.X <= leftEdge + 28.0 && mousePosRelativeToWindow.Y >= topEdge - 10.0 && mousePosRelativeToWindow.Y <= bottomEdge + 10.0;
		}
		catch
		{
			return false;
		}
	}

	private bool IsMouseInTabRightZone(Point mousePosRelativeToWindow)
	{
		if (TabScrollViewer == null || TabScrollViewer.ScrollableWidth <= 1.0)
		{
			return false;
		}
		try
		{
			Point topLeft = TabScrollViewer.TransformToAncestor(this).Transform(new Point(0.0, 0.0));
			double rightEdge = topLeft.X + TabScrollViewer.ViewportWidth;
			return mousePosRelativeToWindow.X >= rightEdge - 28.0 && mousePosRelativeToWindow.Y >= topLeft.Y - 10.0 && mousePosRelativeToWindow.Y <= topLeft.Y + TabScrollViewer.ViewportHeight + 10.0;
		}
		catch
		{
			return false;
		}
	}

	private void CreateTabDragGhost(FrameworkElement source)
	{
		DestroyTabDragGhost();
		Point pos = source.TranslatePoint(new Point(0.0, 0.0), DragOverlay);
		string label = FindVisualChild(source)?.Text ?? "分组";
		_tabDragGhost = new Border
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
		Canvas.SetLeft(_tabDragGhost, pos.X);
		Canvas.SetTop(_tabDragGhost, pos.Y);
		DragOverlay.Children.Add(_tabDragGhost);
	}

	private void DestroyTabDragGhost()
	{
		if (_tabDragGhost != null)
		{
			DragOverlay.Children.Remove(_tabDragGhost);
			_tabDragGhost = null;
		}
	}

	private void TabItem_Click(object sender, MouseButtonEventArgs e)
	{
	}

	private static System.Windows.Controls.TextBox? FindVisualChild(DependencyObject parent)
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(parent, i);
			if (child is System.Windows.Controls.TextBox tb)
			{
				return tb;
			}
			System.Windows.Controls.TextBox found = FindVisualChild(child);
			if (found != null)
			{
				return found;
			}
		}
		return null;
	}

	private void TabName_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (!_isEditingTab && sender is System.Windows.Controls.TextBox { IsReadOnly: false })
		{
			EnterTabEditMode();
		}
	}

	private void TabName_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (sender is System.Windows.Controls.TextBox tb)
		{
			if (string.IsNullOrWhiteSpace(tb.Text))
			{
				tb.Text = _tabOriginalName;
				if (tb.DataContext is IconGroup g)
				{
					g.Name = _tabOriginalName;
				}
			}
			tb.IsReadOnly = true;
		}
		LeaveTabEditMode();
		_viewModel.SaveState();
	}

	private void TabName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (!(sender is System.Windows.Controls.TextBox tb))
		{
			return;
		}
		if (e.Key == Key.Return)
		{
			tb.IsReadOnly = true;
			Keyboard.ClearFocus();
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			tb.Text = _tabOriginalName;
			if (tb.DataContext is IconGroup g)
			{
				g.Name = _tabOriginalName;
			}
			tb.IsReadOnly = true;
			Keyboard.ClearFocus();
			e.Handled = true;
		}
	}

	private void ContextMenu_RenameGroup(object sender, RoutedEventArgs e)
	{
		if (!(sender is MenuItem) || !(((MenuItem)sender).Parent is ContextMenu { PlacementTarget: FrameworkElement { DataContext: IconGroup group } fe }))
		{
			return;
		}
		System.Windows.Controls.TextBox tb = FindVisualChild(fe);
		if (tb != null)
		{
			if (_isEditingTitle)
			{
				LeaveTitleEditMode();
				TitleTextBox.IsReadOnly = true;
			}
			_tabOriginalName = group.Name;
			EnterTabEditMode();
			tb.IsReadOnly = false;
			tb.Focus();
			tb.Select(tb.Text.Length, 0);
		}
	}

	private void ContextMenu_DeleteGroup(object sender, RoutedEventArgs e)
	{
		if (sender is MenuItem && ((MenuItem)sender).Parent is ContextMenu { PlacementTarget: FrameworkElement { DataContext: IconGroup group } })
		{
			_viewModel.DeleteGroupCommand.Execute(group);
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

	private void ResetPosition_Click(object sender, RoutedEventArgs e)
	{
		e.Handled = true;
		ResetToScreenCenter();
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

	/// <summary>
	/// Adds the rename action to the decompiled popup without replacing the
	/// original embedded BAML.  This keeps the recovered project buildable while
	/// making the new action visible beside the existing zone options.
	/// </summary>
	private void AddRenameMenuItem()
	{
		if (!(MorePopup?.Child is Border border) || !(border.Child is StackPanel panel))
		{
			return;
		}

		System.Windows.Controls.Button renameButton = new System.Windows.Controls.Button
		{
			Content = "✎  更改名称",
			ToolTip = "更改当前收纳区名称",
			Style = TryFindResource("ZoneMenuButtonStyle") as Style
		};
		renameButton.Click += RenameZone_Click;

		// Place it directly below the lock action.
		panel.Children.Insert(Math.Min(1, panel.Children.Count), renameButton);
	}

	private void RenameZone_Click(object sender, RoutedEventArgs e)
	{
		e.Handled = true;
		MorePopup.IsOpen = false;
		ContextMenuAutoCloser.Detach(MorePopup);

		base.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)delegate
		{
			InputDialog dialog = new InputDialog("请输入新的收纳区名称：", "更改收纳区名称", _viewModel.Name)
			{
				Owner = this
			};

			if (dialog.ShowDialog() == true)
			{
				string newName = dialog.ResponseText.Trim();
				if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, _viewModel.Name, StringComparison.Ordinal))
				{
					_viewModel.Name = newName;
				}
			}
		});
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
			_buttonHideTimer = null;
			EnsureCollapseWatcher();
			if ((!_viewModel.IsCollapsed || !_viewModel.IsHoverExpanded || !IsCursorOverWindow()) && !_contextMenuOpen)
			{
				TitleButtonsPanel.Opacity = 0.0;
				if ((!base.IsKeyboardFocusWithin || !(Keyboard.FocusedElement is System.Windows.Controls.TextBox)) && _viewModel.IsCollapsed)
				{
					_viewModel.IsHoverExpanded = false;
					base.MaxHeight = 52.0;
					base.Height = 52.0;
					base.ResizeMode = ResizeMode.CanResize;
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
		if (!(sender is ContextMenu cm) || (_openContextMenu != null && cm != _openContextMenu))
		{
			return;
		}
		_openContextMenu = null;
		_contextMenuOpen = false;
		ContextMenuAutoCloser.Detach(cm);
		EnsureCollapseWatcher();
		if (!_viewModel.IsCollapsed || !IsCursorOverWindow())
		{
			_buttonHideTimer?.Stop();
			TitleButtonsPanel.Opacity = 0.0;
			if (_viewModel.IsCollapsed)
			{
				_viewModel.IsHoverExpanded = false;
				base.MaxHeight = 52.0;
				base.Height = 52.0;
				base.ResizeMode = ResizeMode.CanResize;
			}
		}
		PushToBottom();
	}

	public void ResetToScreenCenter()
	{
		RepositionWindow(360.0, 420.0, save: true);
	}

	private void RecoverToPrimaryScreen(bool save = false)
	{
		double w = ((base.Width > 100.0) ? base.Width : 360.0);
		double h = ((base.Height > 100.0) ? base.Height : 420.0);
		RepositionWindow(w, h, save);
	}

	private void RepositionWindow(double width, double height, bool save)
	{
		if (base.WindowState != WindowState.Normal)
		{
			base.WindowState = WindowState.Normal;
		}
		double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
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
		_viewModel.WindowLeft = WindowGeometryPersistence.ToPhysical(left);
		_viewModel.WindowTop = WindowGeometryPersistence.ToPhysical(top);
		_viewModel.ZoneWidth = WindowGeometryPersistence.ToPhysical(width);
		_viewModel.ZoneHeight = WindowGeometryPersistence.ToPhysical(height);
	}

	private void OnDisplaySettingsChanged(object? sender, EventArgs e)
	{
		if (DateTime.Now >= _displayTransitionUntil)
		{
			_preTransitionX = _viewModel.WindowLeft;
			_preTransitionY = _viewModel.WindowTop;
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
			if (!double.IsNaN(_preTransitionX) && !double.IsNaN(_preTransitionY))
			{
				double preLeft = WindowGeometryPersistence.ToDip(_preTransitionX);
				double preTop = WindowGeometryPersistence.ToDip(_preTransitionY);
				if (Math.Abs(base.Left - preLeft) > 1.0 || Math.Abs(base.Top - preTop) > 1.0)
				{
					_suppressPositionSave = true;
					try
					{
						base.Left = preLeft;
						base.Top = preTop;
						_viewModel.WindowLeft = _preTransitionX;
						_viewModel.WindowTop = _preTransitionY;
					}
					finally
					{
						_suppressPositionSave = false;
					}
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
		double left = WindowGeometryPersistence.ToDip(_zone.X);
		double checkTop = WindowGeometryPersistence.ToDip(_zone.Y);
		double checkWidth = ((_zone.Width > 100.0) ? WindowGeometryPersistence.ToDip(_zone.Width) : base.Width);
		double checkHeight = (_viewModel.IsCollapsed ? 52.0 : ((_zone.Height > 100.0) ? WindowGeometryPersistence.ToDip(_zone.Height) : base.Height));
		int minVisible = (_viewModel.IsCollapsed ? 20 : 100);
		if (!IsOnAnyScreen(left, checkTop, checkWidth, checkHeight, minVisible))
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

	private static bool IsOnAnyScreen(double left, double top, double width, double height, int minVisible = 100)
	{
		return DesktopEmbedService.IsOnAnyScreen(left, top, width, height, minVisible);
	}

	public void RefreshIconSizes()
	{
		_viewModel.RefreshIconSizes();
	}

	public void RefreshAllContent()
	{
		_viewModel.RefreshAllContent();
	}

	public void SetSuppressDesktopWatcher(bool suppress)
	{
		_suppressDesktopWatcher = suppress;
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
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/views/zonewindow.xaml", UriKind.Relative);
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
			LoadingOverlay = (Border)target;
			LoadingOverlay.MouseEnter += RootGrid_MouseEnter;
			LoadingOverlay.MouseLeave += RootGrid_MouseLeave;
			break;
		case 4:
			LoadingSpinner = (Border)target;
			break;
		case 5:
			SpinnerStoryboard = (BeginStoryboard)target;
			break;
		case 6:
			LoadingText = (TextBlock)target;
			break;
		case 7:
			LoadingDetail = (TextBlock)target;
			break;
		case 8:
			((Border)target).PreviewMouseLeftButtonDown += Header_PreviewMouseLeftButtonDown;
			break;
		case 9:
			((TextBlock)target).MouseLeftButtonDown += Handle_MouseLeftButtonDown;
			break;
		case 10:
			TitleTextBox = (System.Windows.Controls.TextBox)target;
			TitleTextBox.PreviewMouseLeftButtonDown += TitleTextBox_PreviewMouseLeftButtonDown;
			TitleTextBox.LostKeyboardFocus += TitleTextBox_LostKeyboardFocus;
			TitleTextBox.KeyDown += TitleTextBox_KeyDown;
			break;
		case 11:
			TitleButtonsPanel = (StackPanel)target;
			break;
		case 12:
			ZoneSearchBox = (System.Windows.Controls.TextBox)target;
			ZoneSearchBox.PreviewMouseLeftButtonDown += ZoneSearch_PreviewMouseLeftButtonDown;
			ZoneSearchBox.GotFocus += ZoneSearch_GotFocus;
			ZoneSearchBox.LostKeyboardFocus += ZoneSearch_LostKeyboardFocus;
			break;
		case 13:
			MoreButton = (System.Windows.Controls.Button)target;
			MoreButton.Click += MoreButton_Click;
			break;
		case 14:
			MorePopup = (Popup)target;
			MorePopup.Closed += MorePopup_Closed;
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
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 24:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 25:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 26:
			((System.Windows.Controls.Button)target).Click += MoreMenuItem_Click;
			break;
		case 27:
			BtnScrollLeft = (System.Windows.Controls.Button)target;
			BtnScrollLeft.Click += TabScrollLeft_Click;
			break;
		case 28:
			TabScrollViewer = (ScrollViewer)target;
			TabScrollViewer.PreviewMouseWheel += TabScrollViewer_PreviewMouseWheel;
			break;
		case 29:
			TabsItemsControl = (ItemsControl)target;
			break;
		case 34:
			BtnScrollRight = (System.Windows.Controls.Button)target;
			BtnScrollRight.Click += TabScrollRight_Click;
			break;
		case 35:
			((Border)target).DragEnter += IconsArea_DragEnter;
			((Border)target).DragOver += IconsArea_DragOver;
			((Border)target).Drop += IconsArea_Drop;
			((Border)target).GiveFeedback += IconsArea_GiveFeedback;
			break;
		case 36:
			IconsItemsControl = (ItemsControl)target;
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
		case 30:
			((Border)target).PreviewMouseLeftButtonDown += TabItem_PreviewMouseLeftButtonDown;
			((Border)target).PreviewMouseMove += TabItem_PreviewMouseMove;
			((Border)target).PreviewMouseLeftButtonUp += TabItem_PreviewMouseLeftButtonUp;
			break;
		case 31:
			((MenuItem)target).Click += ContextMenu_RenameGroup;
			break;
		case 32:
			((MenuItem)target).Click += ContextMenu_DeleteGroup;
			break;
		case 33:
			((System.Windows.Controls.TextBox)target).GotKeyboardFocus += TabName_GotKeyboardFocus;
			((System.Windows.Controls.TextBox)target).LostKeyboardFocus += TabName_LostKeyboardFocus;
			((System.Windows.Controls.TextBox)target).KeyDown += TabName_KeyDown;
			break;
		case 37:
			((Border)target).MouseLeftButtonDown += Icon_MouseLeftButtonDown;
			((Border)target).MouseMove += Icon_MouseMove;
			((Border)target).MouseLeftButtonUp += Icon_MouseLeftButtonUp;
			((Border)target).MouseLeave += Icon_MouseLeave;
			((Border)target).ToolTipOpening += Icon_ToolTipOpening;
			((Border)target).ContextMenuOpening += ZoneIconContextMenu_Opening;
			break;
		case 38:
			((MenuItem)target).Click += ContextMenu_Open;
			break;
		case 39:
			((MenuItem)target).Click += ContextMenu_RunAsAdmin;
			break;
		case 40:
			((MenuItem)target).Click += ContextMenu_OpenFolder;
			break;
		case 41:
			((MenuItem)target).Click += ContextMenu_Rename;
			break;
		case 42:
			((MenuItem)target).Click += ContextMenu_Properties;
			break;
		case 43:
			((MenuItem)target).Click += ContextMenu_Copy;
			break;
		case 44:
			((MenuItem)target).Click += ContextMenu_Remove;
			break;
		case 45:
			((MenuItem)target).Click += ContextMenu_Recycle;
			break;
		case 47:
			((Border)target).MouseLeftButtonDown += Icon_MouseLeftButtonDown;
			((Border)target).MouseMove += Icon_MouseMove;
			((Border)target).MouseLeftButtonUp += Icon_MouseLeftButtonUp;
			((Border)target).MouseLeave += Icon_MouseLeave;
			((Border)target).ToolTipOpening += Icon_ToolTipOpening;
			((Border)target).ContextMenuOpening += ZoneIconContextMenu_Opening;
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
			((MenuItem)target).Click += ContextMenu_Rename;
			break;
		case 52:
			((MenuItem)target).Click += ContextMenu_Properties;
			break;
		case 53:
			((MenuItem)target).Click += ContextMenu_Copy;
			break;
		case 54:
			((MenuItem)target).Click += ContextMenu_Remove;
			break;
		case 55:
			((MenuItem)target).Click += ContextMenu_Recycle;
			break;
		case 34:
		case 35:
		case 36:
		case 46:
			break;
		}
	}
}
