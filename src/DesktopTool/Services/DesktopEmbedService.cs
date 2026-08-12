using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DesktopTool.Services;

internal static class DesktopEmbedService
{
	private static nint _desktopOwner = IntPtr.Zero;

	private static readonly object _lock = new object();

	internal static bool EnableDesktopEmbedding = true;

	private static int _totalEmbedded;

	private static readonly HashSet<nint> _registeredWindows = new HashSet<nint>();

	private static bool _workerWNotFoundConfirmed;

	private static nint _progmanOwner = IntPtr.Zero;

	private static nint _lastFolderViewHwnd = IntPtr.Zero;

	private static bool _workerWLookupDeferred;

	private static DateTime _desktopOwnerValidatedAt;

	private static readonly DateTime _embedServiceStartTime = DateTime.UtcNow;

	private const int ColdBootGraceSeconds = 30;

	private const int ExplorerInitThresholdSeconds = 10;

	private const int SystemColdBootSeconds = 120;

	private static int _totalReinforceCalls;

	private static int _totalParentRestorations;

	private static DateTime _lastResetDesktopOwnerCacheAt = DateTime.MinValue;

	private static DateTime _lastTaskbarRecreatedAt = DateTime.MinValue;

	private static DateTime _lastRecoveryExecutedAt = DateTime.MinValue;

	public static volatile bool IsAppExiting;

	private static readonly Dictionary<nint, DateTime> _lastWakeAt = new Dictionary<nint, DateTime>();

	private static readonly ConcurrentDictionary<nint, DateTime> _lastReinforceFcTime = new ConcurrentDictionary<nint, DateTime>();

	internal static volatile bool IsAnyDragActive = false;

	private static readonly ConcurrentDictionary<nint, bool> _activeOleDropWindows = new ConcurrentDictionary<nint, bool>();

	private static DispatcherTimer _externalDragTimer;

	private static bool _isExternalDragActive;

	private static readonly string LogPath = Path.Combine(App.GetAppDataDir(), "debug_embed.log");

	public static bool IsWorkerWOwner { get; private set; }

	public static nint DesktopOwner
	{
		get
		{
			nint o = _desktopOwner;
			if (o == IntPtr.Zero || IsWindow(o))
			{
				return o;
			}
			try
			{
				lock (_lock)
				{
					if (_desktopOwner != IntPtr.Zero && !IsWindow(_desktopOwner))
					{
						Log($"[自愈] ⚠ DesktopOwner 死句柄 0x{_desktopOwner:X8}（Explorer 已重建 WorkerW/Progman）→ 清缓存重解析");
						_desktopOwner = IntPtr.Zero;
						_desktopOwnerValidatedAt = DateTime.MinValue;
						IsWorkerWOwner = false;
						_workerWNotFoundConfirmed = false;
					}
				}
				return EnsureDesktopOwner() ? _desktopOwner : IntPtr.Zero;
			}
			catch
			{
				return IntPtr.Zero;
			}
		}
	}

	public static bool IsRecoveryMode { get; set; }

	public static bool IsExcludeFromPeekWorking { get; private set; } = true;

	public static bool IsDegradedPath => !IsWorkerWOwner;

	public static bool NeedsCloakRecovery { get; private set; }

	public static bool IsExternalFileDragActive => _isExternalDragActive;

	public static event Action? DesktopOwnerReapplied;

	public static event Action ExternalDragActiveChanged;

	public static (double Left, double Top) ConvertScreenToDesktopRelative(double screenX, double screenY)
	{
		nint owner = _desktopOwner;
		if (owner == IntPtr.Zero)
		{
			return (Left: screenX, Top: screenY);
		}
		double dpiScale = 1.0;
		try
		{
			using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
			dpiScale = (double)g.DpiX / 96.0;
		}
		catch
		{
		}
		NativeMethods.POINT pt = new NativeMethods.POINT
		{
			X = (int)(screenX * dpiScale),
			Y = (int)(screenY * dpiScale)
		};
		NativeMethods.ScreenToClient(owner, ref pt);
		return (Left: (double)pt.X / dpiScale, Top: (double)pt.Y / dpiScale);
	}

	public static nint GetOwnerHwnd(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		try
		{
			return NativeMethods.GetWindowLongPtr(hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32());
		}
		catch
		{
			return IntPtr.Zero;
		}
	}

	public static (int Left, int Top) GetOwnerOrigin(nint hwnd)
	{
		try
		{
			nint owner = GetOwnerHwnd(hwnd);
			if (owner == IntPtr.Zero)
			{
				return (Left: 0, Top: 0);
			}
			StringBuilder sb = new StringBuilder(64);
			if (NativeMethods.GetClassName(owner, sb, sb.Capacity) > 0 && sb.ToString() == "Progman")
			{
				return (Left: 0, Top: 0);
			}
			if (NativeMethods.GetWindowRect(owner, out var r))
			{
				return (Left: r.Left, Top: r.Top);
			}
		}
		catch
		{
		}
		return (Left: 0, Top: 0);
	}

	public static bool IsProgmanOwned(nint hwnd)
	{
		nint owner = GetOwnerHwnd(hwnd);
		if (owner == IntPtr.Zero)
		{
			return false;
		}
		StringBuilder sb = new StringBuilder(64);
		if (NativeMethods.GetClassName(owner, sb, sb.Capacity) > 0)
		{
			return sb.ToString() == "Progman";
		}
		return false;
	}

	public static (double Left, double Top) GetPrimaryCenterRelativeToOwner(double windowWidthDip, double windowHeightDip, double windowDpi = 0.0)
	{
		nint owner = _desktopOwner;
		if (owner == IntPtr.Zero)
		{
			return (Left: windowWidthDip, Top: windowHeightDip);
		}
		double dpiScale = ((windowDpi > 0.0) ? windowDpi : 1.0);
		if (dpiScale <= 0.0)
		{
			try
			{
				using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
				dpiScale = (double)g.DpiX / 96.0;
			}
			catch
			{
				dpiScale = 1.0;
			}
		}
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
		int waLeft = mi.rcWork.Left;
		int waTop = mi.rcWork.Top;
		int waRight = mi.rcWork.Right;
		int waBottom = mi.rcWork.Bottom;
		int num = (int)((double)waLeft + ((double)(waRight - waLeft) - windowWidthDip * dpiScale) / 2.0);
		int topPhys = (int)((double)waTop + ((double)(waBottom - waTop) - windowHeightDip * dpiScale) / 2.0);
		int ownerLeft = 0;
		int ownerTop = 0;
		StringBuilder sb = new StringBuilder(64);
		NativeMethods.RECT ownerRect;
		if (NativeMethods.GetClassName(owner, sb, sb.Capacity) > 0 && sb.ToString() == "Progman")
		{
			ownerLeft = 0;
			ownerTop = 0;
		}
		else if (NativeMethods.GetWindowRect(owner, out ownerRect))
		{
			ownerLeft = ownerRect.Left;
			ownerTop = ownerRect.Top;
		}
		int num2 = num - ownerLeft;
		int relY = topPhys - ownerTop;
		return (Left: (double)num2 / dpiScale, Top: (double)relY / dpiScale);
	}

	public static (double Left, double Top) GetPrimaryCenterRelativeToOwner(nint hwnd, double windowWidthDip, double windowHeightDip, double windowDpi = 0.0)
	{
		(int, int) ownerOrigin = GetOwnerOrigin(hwnd);
		double dpiScale = ((windowDpi > 0.0) ? windowDpi : 1.0);
		if (dpiScale <= 0.0)
		{
			try
			{
				using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
				dpiScale = (double)g.DpiX / 96.0;
			}
			catch
			{
				dpiScale = 1.0;
			}
		}
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
		int waLeft = mi.rcWork.Left;
		int waTop = mi.rcWork.Top;
		int waRight = mi.rcWork.Right;
		int waBottom = mi.rcWork.Bottom;
		int num = (int)((double)waLeft + ((double)(waRight - waLeft) - windowWidthDip * dpiScale) / 2.0);
		int topPhys = (int)((double)waTop + ((double)(waBottom - waTop) - windowHeightDip * dpiScale) / 2.0);
		int num2 = num - ownerOrigin.Item1;
		return new ValueTuple<double, double>(item2: (double)(topPhys - ownerOrigin.Item2) / dpiScale, item1: (double)num2 / dpiScale);
	}

	public static (double Left, double Top) GetPrimaryScreenCenter(double windowWidthDip, double windowHeightDip, double windowDpi = 0.0)
	{
		double dpiScale = ((windowDpi > 0.0) ? windowDpi : 1.0);
		if (dpiScale <= 0.0)
		{
			try
			{
				using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
				dpiScale = (double)g.DpiX / 96.0;
			}
			catch
			{
				dpiScale = 1.0;
			}
		}
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
		int waLeft = mi.rcWork.Left;
		int waTop = mi.rcWork.Top;
		int waRight = mi.rcWork.Right;
		int waBottom = mi.rcWork.Bottom;
		int num = (int)((double)waLeft + ((double)(waRight - waLeft) - windowWidthDip * dpiScale) / 2.0);
		return new ValueTuple<double, double>(item2: (double)(int)((double)waTop + ((double)(waBottom - waTop) - windowHeightDip * dpiScale) / 2.0) / dpiScale, item1: (double)num / dpiScale);
	}

	public static bool IsOnPrimaryScreen(double left, double top, double width, double height)
	{
		double dpiScale = 1.0;
		try
		{
			using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
			dpiScale = (double)g.DpiX / 96.0;
		}
		catch
		{
			dpiScale = 1.0;
		}
		nint hMonitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT
		{
			X = 0,
			Y = 0
		}, 1u);
		NativeMethods.MONITORINFO mi = new NativeMethods.MONITORINFO
		{
			cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
		};
		if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
		{
			return true;
		}
		double wl = mi.rcWork.Left;
		double wt = mi.rcWork.Top;
		double wr = mi.rcWork.Right;
		double wb = mi.rcWork.Bottom;
		double pl = left * dpiScale;
		double pt = top * dpiScale;
		double num = (left + width) * dpiScale;
		double pb = (top + height) * dpiScale;
		if (num > wl && pb > wt && pl < wr)
		{
			return pt < wb;
		}
		return false;
	}

	public static bool IsOnAnyScreen(double left, double top, double width, double height, int minVisible = 100)
	{
		double virtualScreenLeft = SystemParameters.VirtualScreenLeft;
		double dipTop = SystemParameters.VirtualScreenTop;
		double dipRight = virtualScreenLeft + SystemParameters.VirtualScreenWidth;
		double dipBottom = dipTop + SystemParameters.VirtualScreenHeight;
		double physLeft = NativeMethods.GetSystemMetrics(76);
		double physTop = NativeMethods.GetSystemMetrics(77);
		double physRight = physLeft + (double)NativeMethods.GetSystemMetrics(78);
		double physBottom = physTop + (double)NativeMethods.GetSystemMetrics(79);
		double virtualLeft = Math.Min(virtualScreenLeft, physLeft);
		double virtualTop = Math.Min(dipTop, physTop);
		double virtualRight = Math.Max(dipRight, physRight);
		double virtualBottom = Math.Max(dipBottom, physBottom);
		double visibleLeft = Math.Max(left, virtualLeft);
		double visibleTop = Math.Max(top, virtualTop);
		double num = Math.Min(left + width, virtualRight);
		double visibleBottom = Math.Min(top + height, virtualBottom);
		double num2 = num - visibleLeft;
		double visibleHeight = visibleBottom - visibleTop;
		if (num2 > (double)minVisible)
		{
			return visibleHeight > (double)minVisible;
		}
		return false;
	}

	internal static nint GetGwlpOwnerForHwnd(nint hwnd)
	{
		if ((NativeMethods.GetWindowLong(hwnd, -20) & 0x80000) != 0)
		{
			return GetProgmanOwner();
		}
		return _desktopOwner;
	}

	private static nint GetProgmanOwner()
	{
		if (_progmanOwner == IntPtr.Zero || !IsWindow(_progmanOwner))
		{
			_progmanOwner = FindProgman();
		}
		return _progmanOwner;
	}

	public static bool RegisterDesktopWindow(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return false;
		}
		if (!EnableDesktopEmbedding)
		{
			try
			{
				int excludeFromPeek = 1;
				int hr = NativeMethods.DwmSetWindowAttribute(hwnd, 12, ref excludeFromPeek, 4);
				Log($"[注册] ★ 样式模式：跳过桌面嵌入，仅设 DWMWA_EXCLUDED_FROM_PEEK hr=0x{hr:X8}");
			}
			catch (Exception ex)
			{
				Log("[注册] ⚠ DWMWA_EXCLUDED_FROM_PEEK 设置异常: " + ex.Message);
			}
			Log($"[注册] ✅ 窗口 0x{hwnd:X8} 已注册（样式免疫模式，无桌面嵌入）");
			lock (_lock)
			{
				_registeredWindows.Add(hwnd);
			}
			return true;
		}
		try
		{
			if (!EnsureDesktopOwner())
			{
				return false;
			}
			bool num = (NativeMethods.GetWindowLong(hwnd, -20) & 0x80000) != 0;
			if (num)
			{
				NeedsCloakRecovery = true;
				Log($"[注册] hwnd=0x{hwnd:X8} 分层 — 跳过SetParent（避免DWM合成失败/全透明），GWLP owner=Progman（稳定）+DWMWA_EXCLUDED_FROM_PEEK，NeedsCloakRecovery=true");
			}
			else
			{
				nint oldParent = NativeMethods.SetParent(hwnd, _desktopOwner);
				if (oldParent != IntPtr.Zero)
				{
					Log($"[注册] hwnd=0x{hwnd:X8} SetParent→0x{_desktopOwner:X8} oldParent=0x{oldParent:X8}");
				}
				else
				{
					Log($"[注册] hwnd=0x{hwnd:X8} SetParent→0x{_desktopOwner:X8}（首次设为子窗口）");
				}
			}
			nint gwlpOwner = GetGwlpOwnerForHwnd(hwnd);
			NativeMethods.SetWindowLongPtr(hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32(), gwlpOwner);
			int excludeFromPeek2 = 1;
			int hr2 = NativeMethods.DwmSetWindowAttribute(hwnd, 12, ref excludeFromPeek2, 4);
			Log($"[注册] hwnd=0x{hwnd:X8} DWMWA_EXCLUDED_FROM_PEEK=TRUE hr=0x{hr2:X8}");
			IsExcludeFromPeekWorking = false;
			nint insertAfter = (num ? NativeMethods.HWND_BOTTOM : NativeMethods.HWND_TOP);
			NativeMethods.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, 19u);
			NativeMethods.SetWindowLongPtr(hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32(), gwlpOwner);
			NativeMethods.ShowWindow(hwnd, 4);
			NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 55u);
			Log($"[注册] ★ DWM合成唤醒 ShowWindow+SetWindowPos(FRAMECHANGED) hwnd=0x{hwnd:X8}");
			if (num)
			{
				NativeMethods.InvalidateRect(hwnd, IntPtr.Zero, bErase: true);
				NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 119u);
				Log($"[注册] ★ V2.5.51 无 HIDEWINDOW DWM 唤醒（InvalidateRect+SHOWWINDOW+FRAMECHANGED）hwnd=0x{hwnd:X8}，保留透明通道");
				if (NativeMethods.GetWindowRect(hwnd, out var rc))
				{
					NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rc.Left + 1, rc.Top, 0, 0, 53u);
					NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rc.Left, rc.Top, 0, 0, 53u);
					Log($"[注册] 微移唤醒（1px→恢复）hwnd=0x{hwnd:X8}，强制 DWM 刷新层级关系");
				}
			}
			int count = Interlocked.Increment(ref _totalEmbedded);
			string strategy = (num ? "GWLP_HWNDPARENT" : "SetParent+GWLP_HWNDPARENT");
			StringBuilder sbCls = new StringBuilder(64);
			NativeMethods.GetClassName(gwlpOwner, sbCls, 64);
			string gwlpOwnerClass = sbCls.ToString();
			Log($"[注册] ✅ 窗口 0x{hwnd:X8} 已注册桌面保护（共 {count} 个），{strategy}→{gwlpOwnerClass}(owner)+DWMWA_EXCLUDED_FROM_PEEK");
			lock (_lock)
			{
				_registeredWindows.Add(hwnd);
			}
			WinDHookService.Register(hwnd);
			return true;
		}
		catch (Exception ex2)
		{
			Log("[注册] ❌ 失败: " + ex2.Message);
			return false;
		}
	}

	public static nint GetDesktopContainer()
	{
		try
		{
			if (EnsureDesktopOwner() && _desktopOwner != IntPtr.Zero)
			{
				return _desktopOwner;
			}
		}
		catch
		{
		}
		return FindProgman();
	}

	public static void ResetDesktopOwnerCache()
	{
		DateTime now = DateTime.UtcNow;
		lock (_lock)
		{
			if ((now - _lastResetDesktopOwnerCacheAt).TotalSeconds < 30.0)
			{
				Log($"[恢复] ResetDesktopOwnerCache 跳过（距上次仅 {(now - _lastResetDesktopOwnerCacheAt).TotalSeconds:F0}s < 30s 冷却）");
				return;
			}
			_lastResetDesktopOwnerCacheAt = now;
			_desktopOwner = IntPtr.Zero;
			_progmanOwner = IntPtr.Zero;
			_workerWNotFoundConfirmed = false;
			_workerWLookupDeferred = false;
			_desktopOwnerValidatedAt = DateTime.MinValue;
			IsWorkerWOwner = false;
			Log("[恢复] ★ V2.3.64 ResetDesktopOwnerCache — 已清除会话级缓存（锁屏解锁/Explorer 重启），下次将重新查找 Progman/WorkerW");
		}
	}

	public static int ReapplyAllProtectedWindows()
	{
		nint[] snapshot;
		lock (_lock)
		{
			snapshot = _registeredWindows.ToArray();
		}
		int restored = 0;
		nint[] array = snapshot;
		foreach (nint hwnd in array)
		{
			if (hwnd == IntPtr.Zero)
			{
				continue;
			}
			try
			{
				if (ReapplyProtection(hwnd))
				{
					restored++;
				}
			}
			catch
			{
			}
		}
		try
		{
			DesktopEmbedService.DesktopOwnerReapplied?.Invoke();
		}
		catch
		{
		}
		Log($"[恢复] ★ V2.3.64 ReapplyAllProtectedWindows — 已重应用 {snapshot.Length} 个受保护窗口的桌面保护（恢复所有者 {restored} 个）");
		return restored;
	}

	public static void HandleTaskbarRecreated()
	{
		DateTime now = DateTime.UtcNow;
		if ((now - _lastTaskbarRecreatedAt).TotalSeconds < 2.0)
		{
			return;
		}
		_lastTaskbarRecreatedAt = now;
		RecoverAfterTaskbarRecreated("0s");
		int[] array = new int[3] { 1, 3, 6 };
		foreach (int sec in array)
		{
			int captured = sec;
			Task.Delay(TimeSpan.FromSeconds(captured)).ContinueWith(delegate
			{
				Dispatcher dispatcher = Application.Current?.Dispatcher;
				if (dispatcher != null)
				{
					dispatcher.BeginInvoke((Action)delegate
					{
						RecoverAfterTaskbarRecreated($"{captured}s");
					}, DispatcherPriority.Background);
				}
				else
				{
					RecoverAfterTaskbarRecreated($"{captured}s");
				}
			}, TaskScheduler.Default);
		}
	}

	private static void RecoverAfterTaskbarRecreated(string phase)
	{
		DateTime now = DateTime.UtcNow;
		if ((now - _lastRecoveryExecutedAt).TotalSeconds < 28.0)
		{
			Log($"[TaskbarRecreated][{phase}] 跳过（距上次恢复仅 {(now - _lastRecoveryExecutedAt).TotalSeconds:F0}s < 28s 间隔）");
			return;
		}
		_lastRecoveryExecutedAt = now;
		try
		{
			ResetDesktopOwnerCache();
			int restored = ReapplyAllProtectedWindows();
			Log($"[TaskbarRecreated][{phase}] 已重建桌面保护（恢复所有者 {restored} 个）");
		}
		catch (Exception ex)
		{
			Log("[TaskbarRecreated][" + phase + "] ⚠ 恢复异常: " + ex.Message);
		}
	}

	public static bool IsOrphaned(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return false;
		}
		nint owner = NativeMethods.GetWindowLongPtr(hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32());
		if (owner == IntPtr.Zero)
		{
			return true;
		}
		if (!NativeMethods.IsWindow(owner))
		{
			return true;
		}
		if (_progmanOwner != IntPtr.Zero && _desktopOwner != IntPtr.Zero && owner != _progmanOwner && owner != _desktopOwner)
		{
			return true;
		}
		return false;
	}

	public static bool ShouldCancelUserClose(nint hwnd)
	{
		if (IsAppExiting)
		{
			return false;
		}
		return IsOrphaned(hwnd);
	}

	private static nint GetCurrentFolderViewHwnd(nint foregroundHwnd)
	{
		try
		{
			StringBuilder sb = new StringBuilder(64);
			NativeMethods.GetClassName(foregroundHwnd, sb, sb.Capacity);
			if (sb.ToString() == "SysListView32")
			{
				return foregroundHwnd;
			}
			nint w = TryResolveWorkerW(allowCreate: false);
			if (w != IntPtr.Zero)
			{
				nint def = NativeMethods.FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
				if (def != IntPtr.Zero)
				{
					nint lv = NativeMethods.FindWindowEx(def, IntPtr.Zero, "SysListView32", "FolderView");
					if (lv != IntPtr.Zero)
					{
						return lv;
					}
				}
			}
		}
		catch
		{
		}
		return IntPtr.Zero;
	}

	public static void ReestablishAfterDesktopRefresh(nint foregroundHwnd)
	{
		if (!EnableDesktopEmbedding || foregroundHwnd == IntPtr.Zero)
		{
			return;
		}
		nint current = GetCurrentFolderViewHwnd(foregroundHwnd);
		if (current == IntPtr.Zero)
		{
			return;
		}
		nint last;
		lock (_lock)
		{
			last = _lastFolderViewHwnd;
		}
		if (last == IntPtr.Zero)
		{
			lock (_lock)
			{
				_lastFolderViewHwnd = current;
				return;
			}
		}
		bool changed = current != last;
		bool destroyed = !IsWindow(last);
		if (IsAnyDragActive || IsExternalFileDragActive || IsMouseButtonDown())
		{
			Log($"[重建立] 拖拽进行中，跳过重建且不更新基准（保持 0x{last:X8}），避免闪烁");
		}
		else if (changed || destroyed)
		{
			lock (_lock)
			{
				_lastFolderViewHwnd = current;
			}
			Log($"[重建立] ★ 检测到桌面容器已重建(FolderView 新=0x{current:X8} 旧=0x{last:X8}，旧已销毁={destroyed})，立即重建嵌入并重新标记为已交互");
			try
			{
				ReapplyAllProtectedWindows();
			}
			catch (Exception ex)
			{
				Log("[重建立] ⚠ Reapply 异常: " + ex.Message);
			}
			Log("[重建立] ★ 已完成桌面刷新后恢复（Win+D 免疫 + 桌面交互恢复）");
		}
	}

	private static bool IsMouseButtonDown()
	{
		try
		{
			return (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0 || (NativeMethods.GetAsyncKeyState(2) & 0x8000) != 0;
		}
		catch
		{
			return false;
		}
	}

	public static void WakeDwmComposition(nint hwnd)
	{
		if (hwnd == IntPtr.Zero || !EnableDesktopEmbedding)
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		lock (_lock)
		{
			if (_lastWakeAt.TryGetValue(hwnd, out var last) && (now - last).TotalMilliseconds < 500.0)
			{
				return;
			}
			_lastWakeAt[hwnd] = now;
		}
		try
		{
			NativeMethods.ShowWindow(hwnd, 4);
			NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 55u);
			if (NativeMethods.GetWindowRect(hwnd, out var rc))
			{
				NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rc.Left + 1, rc.Top, 0, 0, 53u);
				NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rc.Left, rc.Top, 0, 0, 53u);
			}
			Log($"[合成唤醒] ★ 首帧后 WakeDwmComposition hwnd=0x{hwnd:X8}（SHOWNOACTIVATE+FRAMECHANGED+1px微移）");
		}
		catch (Exception ex)
		{
			Log("[合成唤醒] ❌ 失败: " + ex.Message);
		}
	}

	public static void UnregisterDesktopWindow(nint hwnd)
	{
		if (hwnd != IntPtr.Zero)
		{
			WinDHookService.Unregister(hwnd);
			lock (_lock)
			{
				_registeredWindows.Remove(hwnd);
			}
			Log($"[注销] 窗口 0x{hwnd:X8} 已移出受保护集合");
		}
	}

	public static bool ReapplyProtection(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return false;
		}
		if (!EnableDesktopEmbedding)
		{
			return false;
		}
		if (!EnsureDesktopOwner())
		{
			Log("[重保护] ❌ 重新初始化失败，跳过本次重应用");
			return false;
		}
		if (IsOleDragActive(hwnd))
		{
			return ReinforceProtectionOleSafe(hwnd);
		}
		if (IsExternalFileDragActive)
		{
			return true;
		}
		bool parentRestored = false;
		try
		{
			bool num = (NativeMethods.GetWindowLong(hwnd, -20) & 0x80000) != 0;
			if (!num)
			{
				NativeMethods.SetParent(hwnd, _desktopOwner);
			}
			nint gwlpOwner = GetGwlpOwnerForHwnd(hwnd);
			nint windowLongPtr = NativeMethods.GetWindowLongPtr(hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32());
			NativeMethods.SetWindowLongPtr(hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32(), gwlpOwner);
			if (windowLongPtr != gwlpOwner)
			{
				bool removed = NativeMethods.RemoveTaskbarButton(hwnd);
				Log($"[重保护] RemoveTaskbarButton hwnd=0x{hwnd:X8} → {(removed ? "已执行DeleteTab" : "⚠跳过/异常(COM不可用)")}");
				Log($"[重保护] hwnd=0x{hwnd:X8} 恢复所有者关系→0x{gwlpOwner:X8}");
				parentRestored = true;
				NativeMethods.SetWindowPos(hwnd, _desktopOwner, 0, 0, 0, 0, 19u);
			}
			if (num)
			{
				NativeMethods.ShowWindow(hwnd, 4);
				NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 55u);
			}
			int excludeFromPeek = 1;
			NativeMethods.DwmSetWindowAttribute(hwnd, 12, ref excludeFromPeek, 4);
			if (!IsAnyDragActive && !IsExternalFileDragActive && !IsMouseButtonDown())
			{
				NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 55u);
			}
		}
		catch
		{
		}
		return parentRestored;
	}

	internal static bool IsOleDragActive(nint hwnd)
	{
		return _activeOleDropWindows.ContainsKey(hwnd);
	}

	public static void StartExternalDragWatcher()
	{
		if (_externalDragTimer == null)
		{
			if (Application.Current?.Dispatcher == null)
			{
				_ = Dispatcher.CurrentDispatcher;
			}
			_externalDragTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(150.0)
			};
			_externalDragTimer.Tick += delegate
			{
				PollExternalDrag();
			};
			_externalDragTimer.Start();
			Log("[外部拖拽] ★ 已启动 SysDragImage 轮询(150ms)，用于跨进程检测文件拖拽");
		}
	}

	private static void PollExternalDrag()
	{
		if (IsAppExiting)
		{
			return;
		}
		try
		{
			bool present = NativeMethods.FindWindow("SysDragImage", null) != IntPtr.Zero;
			if (!_isExternalDragActive)
			{
				if (present)
				{
					_isExternalDragActive = true;
					Log("[外部拖拽] 开始 → IsExternalFileDragActive=True");
					DesktopEmbedService.ExternalDragActiveChanged?.Invoke();
				}
				return;
			}
			bool buttonDown = (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0 || (NativeMethods.GetAsyncKeyState(2) & 0x8000) != 0;
			if (!present && !buttonDown)
			{
				_isExternalDragActive = false;
				Log("[外部拖拽] 结束（SysDragImage 消失且鼠标键已释放）→ IsExternalFileDragActive=False");
				DesktopEmbedService.ExternalDragActiveChanged?.Invoke();
			}
		}
		catch
		{
		}
	}

	public static void NotifyOleDragEnter(nint hwnd)
	{
		if (hwnd != IntPtr.Zero)
		{
			_activeOleDropWindows[hwnd] = true;
		}
	}

	public static void NotifyOleDragLeave(nint hwnd)
	{
		if (hwnd != IntPtr.Zero)
		{
			_activeOleDropWindows.TryRemove(hwnd, out var _);
		}
	}

	public static bool ReinforceProtectionLightweight(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return false;
		}
		if (!EnableDesktopEmbedding)
		{
			return false;
		}
		if (IsAnyDragActive)
		{
			return false;
		}
		if (IsOleDragActive(hwnd))
		{
			return ReinforceProtectionOleSafe(hwnd);
		}
		if (IsExternalFileDragActive)
		{
			return false;
		}
		Interlocked.Increment(ref _totalReinforceCalls);
		bool restored = false;
		try
		{
			_desktopOwner = (EnsureDesktopOwner() ? _desktopOwner : IntPtr.Zero);
			if (_desktopOwner == IntPtr.Zero)
			{
				return false;
			}
			nint gwlpOwner = GetGwlpOwnerForHwnd(hwnd);
			nint currentOwner = NativeMethods.GetWindowLongPtr(hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32());
			if (currentOwner != gwlpOwner)
			{
				NativeMethods.SetWindowLongPtr(hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32(), gwlpOwner);
				restored = true;
				Interlocked.Increment(ref _totalParentRestorations);
				NativeMethods.SetWindowPos(hwnd, _desktopOwner, 0, 0, 0, 0, 19u);
				bool doFc = true;
				if (_lastReinforceFcTime.TryGetValue(hwnd, out var lastFc) && (DateTime.Now - lastFc).TotalMilliseconds < 2000.0)
				{
					doFc = false;
				}
				if (doFc)
				{
					_lastReinforceFcTime[hwnd] = DateTime.Now;
					NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 55u);
					Log($"[加固] hwnd=0x{hwnd:X8} GWLP_HWNDPARENT 被清除 {currentOwner:X8}→{gwlpOwner:X8} 已恢复 + Z序 + FRAMECHANGED");
				}
				else
				{
					Log($"[加固] hwnd=0x{hwnd:X8} GWLP_HWNDPARENT 被清除 {currentOwner:X8}→{gwlpOwner:X8} 已恢复 + Z序（FRAMECHANGED 跳过，距上次 {(DateTime.Now - lastFc).TotalMilliseconds:F0}ms < 2000ms）");
				}
			}
			if (IsDegradedPath || IsExcludeFromPeekWorking)
			{
				int excludeFromPeek = 1;
				NativeMethods.DwmSetWindowAttribute(hwnd, 12, ref excludeFromPeek, 4);
			}
		}
		catch (Exception ex)
		{
			Log("[加固] ⚠ 异常: " + ex.Message);
		}
		return restored;
	}

	private static bool ReinforceProtectionOleSafe(nint hwnd)
	{
		try
		{
			_desktopOwner = (EnsureDesktopOwner() ? _desktopOwner : IntPtr.Zero);
			if (_desktopOwner == IntPtr.Zero)
			{
				return false;
			}
			nint gwlpOwner = GetGwlpOwnerForHwnd(hwnd);
			nint currentOwner = NativeMethods.GetWindowLongPtr(hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32());
			if (currentOwner != gwlpOwner)
			{
				NativeMethods.SetWindowLongPtr(hwnd, ((IntPtr)NativeMethods.GWLP_HWNDPARENT).ToInt32(), gwlpOwner);
				Interlocked.Increment(ref _totalParentRestorations);
				Log($"[加固-静默] hwnd=0x{hwnd:X8} OLE拖放中：GWLP_HWNDPARENT {currentOwner:X8}→{gwlpOwner:X8} 已静默恢复（跳过 SetWindowPos，保护 OLE 拖放）");
				return true;
			}
		}
		catch (Exception ex)
		{
			Log("[加固-静默] ⚠ 异常: " + ex.Message);
		}
		return false;
	}

	public static string GetReinforceStats()
	{
		return $"加固调用={_totalReinforceCalls} GWLP_HWNDPARENT修复={_totalParentRestorations} 降级路径={IsDegradedPath}";
	}

	public static string GetDiagnosticInfo()
	{
		lock (_lock)
		{
			if (_desktopOwner == IntPtr.Zero)
			{
				return "DesktopOwner=未初始化";
			}
			uint pid = 0u;
			NativeMethods.GetWindowThreadProcessId(_desktopOwner, out pid);
			StringBuilder sb = new StringBuilder(64);
			NativeMethods.GetWindowTextW(_desktopOwner, sb, 64);
			string title = sb.ToString();
			sb.Clear();
			NativeMethods.GetClassName(_desktopOwner, sb, 64);
			string className = sb.ToString();
			return $"HWND=0x{_desktopOwner:X8} Class=\"{className}\" Title=\"{title}\" PID={pid} IsWorkerWOwner={IsWorkerWOwner} IsDegradedPath={IsDegradedPath} {GetReinforceStats()}";
		}
	}

	private static bool EnsureDesktopOwner()
	{
		lock (_lock)
		{
			if (_desktopOwner != IntPtr.Zero && IsWindow(_desktopOwner))
			{
				if (!IsWorkerWOwner || !((DateTime.UtcNow - _desktopOwnerValidatedAt).TotalMinutes > 30.0))
				{
					if (_workerWLookupDeferred && !IsWorkerWOwner)
					{
						if ((DateTime.UtcNow - _embedServiceStartTime).TotalSeconds >= 30.0 && !IsSystemColdBoot())
						{
							Log("[初始化] ★ V2.18.5 冷启动宽限期已过且系统已就绪，重新查找 WorkerW…");
							_desktopOwner = FindDesktopWorkerW();
							if (_desktopOwner != IntPtr.Zero)
							{
								IsWorkerWOwner = true;
								_workerWLookupDeferred = false;
								_desktopOwnerValidatedAt = DateTime.UtcNow;
								Log($"[初始化] ★ WorkerW 延迟查找成功 hwnd=0x{_desktopOwner:X8}");
								return true;
							}
							Log("[初始化] ★ WorkerW 延迟查找失败，确认本会话 WorkerW 不存在，继续使用 Progman");
							_workerWLookupDeferred = false;
							_workerWNotFoundConfirmed = true;
						}
						_desktopOwner = FindProgman();
						IsWorkerWOwner = false;
						_desktopOwnerValidatedAt = DateTime.UtcNow;
						return _desktopOwner != IntPtr.Zero;
					}
					return true;
				}
				Log("[初始化] ⚠ WorkerW 所有者已超过30分钟未重新验证，强制刷新…");
				_desktopOwner = IntPtr.Zero;
			}
			if (_workerWNotFoundConfirmed)
			{
				_desktopOwner = FindProgman();
				IsWorkerWOwner = false;
				Log($"[初始化] WorkerW 缓存命中（本会话已确认不存在），直接回退 Progman=0x{_desktopOwner:X8}");
				_desktopOwnerValidatedAt = DateTime.UtcNow;
				return _desktopOwner != IntPtr.Zero;
			}
			if (IsSystemColdBoot())
			{
				nint silent = FindWorkerWSilentOnly();
				if (silent != IntPtr.Zero)
				{
					_desktopOwner = silent;
					IsWorkerWOwner = true;
					_workerWLookupDeferred = false;
					_desktopOwnerValidatedAt = DateTime.UtcNow;
					Log($"[初始化] ★ V2.3.28 系统冷启动期静默命中 WorkerW=0x{silent:X8}（未发 0x052C）");
					return _desktopOwner != IntPtr.Zero;
				}
				_desktopOwner = FindProgman();
				IsWorkerWOwner = false;
				_workerWLookupDeferred = true;
				_desktopOwnerValidatedAt = DateTime.UtcNow;
				Log($"[初始化] ★ V2.3.28 系统冷启动期 → 跳过 0x052C，使用 Progman=0x{_desktopOwner:X8}（待系统就绪后重试）");
				return _desktopOwner != IntPtr.Zero;
			}
			_desktopOwner = FindDesktopWorkerW();
			IsWorkerWOwner = _desktopOwner != IntPtr.Zero;
			if (_desktopOwner == IntPtr.Zero)
			{
				_desktopOwner = FindProgman();
				IsWorkerWOwner = false;
				_workerWNotFoundConfirmed = true;
				Log("[初始化] ⚠ WorkerW 不存在，回退到 Progman — 已缓存，后续注册跳过重试（V1.5.8）");
			}
			bool ok = _desktopOwner != IntPtr.Zero;
			if (ok)
			{
				_desktopOwnerValidatedAt = DateTime.UtcNow;
			}
			Log($"[初始化] 桌面所有者 hwnd=0x{_desktopOwner:X8} isWorkerW={IsWorkerWOwner} success={ok}");
			return ok;
		}
	}

	private static bool IsExplorerInitialized()
	{
		try
		{
			Process[] explorerProcs = Process.GetProcessesByName("explorer");
			if (explorerProcs.Length == 0)
			{
				return false;
			}
			Process[] array = explorerProcs;
			foreach (Process proc in array)
			{
				try
				{
					if (!proc.HasExited && proc.StartTime < _embedServiceStartTime)
					{
						double runningSeconds = (DateTime.UtcNow - proc.StartTime.ToUniversalTime()).TotalSeconds;
						bool initialized = runningSeconds > 10.0;
						Log($"[检测] Explorer 已运行 {runningSeconds:F0}s（阈值={10}s）→ {(initialized ? "已初始化（热启动）" : "尚未初始化（冷启动）")}");
						return initialized;
					}
				}
				catch
				{
				}
			}
			return false;
		}
		catch (Exception ex)
		{
			Log("[检测] Explorer 初始化检测异常: " + ex.Message + "，保守返回 false");
			return false;
		}
	}

	private static bool IsSystemColdBoot()
	{
		try
		{
			return (double)Environment.TickCount64 / 1000.0 < 120.0;
		}
		catch
		{
			return true;
		}
	}

	private static nint FindDesktopWorkerW(bool allowCreate = true)
	{
		nint progman = NativeMethods.FindWindow("Progman", null);
		if (progman == IntPtr.Zero)
		{
			Log("[查找] Progman 未找到");
			return IntPtr.Zero;
		}
		nint silentFound = TryFindWorkerW();
		if (silentFound != IntPtr.Zero)
		{
			Log($"[查找] ✅ 静默枚举命中 WorkerW=0x{silentFound:X8}（无需 0x052C）");
			return silentFound;
		}
		if (!allowCreate)
		{
			Log("[查找] ⚠ 创建模式被禁止（冷启动/调用方要求）→ 跳过 0x052C，仅静默枚举（未找到 WorkerW）");
			return IntPtr.Zero;
		}
		NativeMethods.SendMessageTimeout(progman, 1324u, IntPtr.Zero, IntPtr.Zero, 0u, 1000u, out var _);
		Log("[查找] WorkerW 不存在，Progman 0x052C 已发送（兜底创建）");
		for (int retry = 0; retry < 10; retry++)
		{
			nint found = TryFindWorkerW();
			if (found != IntPtr.Zero)
			{
				return found;
			}
			if (retry < 9)
			{
				Log($"[查找] WorkerW 未就绪，第{retry + 1}/{10}次重试，等待{200}ms…");
				Thread.Sleep(200);
			}
		}
		Log($"[查找] ⚠ {10}次重试后仍未找到 WorkerW，回退到 Progman");
		return IntPtr.Zero;
	}

	private static nint TryFindWorkerW()
	{
		nint strategy1 = IntPtr.Zero;
		nint progman = NativeMethods.FindWindow("Progman", null);
		nint strategy2 = ((progman != IntPtr.Zero) ? NativeMethods.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null) : IntPtr.Zero);
		nint strategy3 = IntPtr.Zero;
		nint strategy4 = IntPtr.Zero;
		List<string> allWorkerW = new List<string>();
		NativeMethods.EnumWindows(delegate(nint hWnd, nint lParam)
		{
			StringBuilder stringBuilder = new StringBuilder(64);
			NativeMethods.GetClassName(hWnd, stringBuilder, 64);
			if (stringBuilder.ToString() != "WorkerW")
			{
				return true;
			}
			uint lpdwProcessId = 0u;
			NativeMethods.GetWindowThreadProcessId(hWnd, out lpdwProcessId);
			bool value = NativeMethods.IsWindowVisible(hWnd);
			int windowLong = NativeMethods.GetWindowLong(hWnd, -16);
			int windowLong2 = NativeMethods.GetWindowLong(hWnd, -20);
			nint num = NativeMethods.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
			nint num2 = IntPtr.Zero;
			string value2 = "";
			if (num != IntPtr.Zero)
			{
				num2 = NativeMethods.FindWindowEx(num, IntPtr.Zero, "SysListView32", "FolderView");
				if (num2 != IntPtr.Zero)
				{
					StringBuilder stringBuilder2 = new StringBuilder(64);
					NativeMethods.GetWindowTextW(num2, stringBuilder2, 64);
					value2 = stringBuilder2.ToString();
				}
			}
			allWorkerW.Add($"  0x{hWnd:X8} PID={lpdwProcessId} Visible={value} Style=0x{windowLong:X8} ExStyle=0x{windowLong2:X8} DefView=0x{num:X8} ListView=0x{num2:X8} ListViewTitle=\"{value2}\"");
			if (num != IntPtr.Zero && num2 != IntPtr.Zero)
			{
				strategy1 = hWnd;
				return false;
			}
			if (num != IntPtr.Zero && strategy3 == IntPtr.Zero)
			{
				strategy3 = hWnd;
			}
			if (strategy4 == IntPtr.Zero)
			{
				strategy4 = hWnd;
			}
			return true;
		}, IntPtr.Zero);
		Log($"[查找] 枚举到 {allWorkerW.Count} 个 WorkerW 窗口:");
		foreach (string item in allWorkerW)
		{
			Log(item);
		}
		if (strategy1 != IntPtr.Zero)
		{
			Log($"[查找] ✅ 策略1命中 — WorkerW=0x{strategy1:X8}");
			return strategy1;
		}
		if (strategy2 != IntPtr.Zero)
		{
			Log($"[查找] ✅ 策略0命中（Progman子窗口，FolderView未就绪） — WorkerW=0x{strategy2:X8}");
			return strategy2;
		}
		if (strategy3 != IntPtr.Zero)
		{
			Log($"[查找] ⚠ 策略2命中（无 SysListView32） — WorkerW=0x{strategy3:X8}");
			return strategy3;
		}
		Log("[查找] ⚠ 无可用桌面 WorkerW（策略1/0/2 均未命中），放弃『任意 WorkerW』兜底，返回 0 → 调用方回退 Progman");
		Log("[查找] ❌ 未找到任何 WorkerW 窗口");
		return IntPtr.Zero;
	}

	private static nint FindWorkerWSilentOnly()
	{
		if (NativeMethods.FindWindow("Progman", null) == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		return TryFindWorkerW();
	}

	private static nint TryResolveWorkerW(bool allowCreate = true)
	{
		nint silent = FindWorkerWSilentOnly();
		if (silent != IntPtr.Zero)
		{
			return silent;
		}
		if (allowCreate && IsExplorerInitialized())
		{
			Log("[查找] ★ V2.19.x Explorer 已初始化，发送 0x052C 兜底创建 WorkerW…");
			return FindDesktopWorkerW(allowCreate);
		}
		return IntPtr.Zero;
	}

	private static nint FindProgman()
	{
		nint progman = NativeMethods.FindWindow("Progman", null);
		Log($"[查找] 回退 Progman=0x{progman:X8}");
		return progman;
	}

	[DllImport("user32.dll")]
	private static extern bool IsWindow(nint hWnd);

	private static void Log(string message)
	{
		LogHelper.Append(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [Embed] {message}{Environment.NewLine}");
	}
}
