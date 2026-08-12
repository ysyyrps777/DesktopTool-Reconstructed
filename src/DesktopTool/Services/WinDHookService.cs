using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DesktopTool.Models;

namespace DesktopTool.Services;

internal static class WinDHookService
{
	private static readonly HashSet<nint> Protected = new HashSet<nint>();

	private static readonly object Lock = new object();

	private static Timer? _recoveryTimer;

	private static int _recovering;

	private static int _tickCount;

	private static long _lastLogTime;

	private static Timer? _gentleRecoveryTimer;

	private static Timer? _reshowTimer;

	private static int _reshowPending;

	private static long _lastGentleDiagLog;

	private static bool _lastForegroundWasDesktop;

	private static bool _winDZOrderImmune;

	private static long _lastImmuneFlagLoad;

	private static readonly HashSet<nint> _zBouncedUp = new HashSet<nint>();

	private static bool _winDLatched;

	private static long _winDSettleUntil;

	private const long WinDSettleTicks = 4000000L;

	private static nint _lastRealFgHwnd;

	private static nint _winDCandidateHwnd;

	private static int _realAppStableTicks;

	private static long _realAppSolidSince;

	private static int _ownFgStableTicks;

	private static int _gentleTickRunning;

	private static bool _lastWinDMode;

	private static nint _winEventHook;

	private static NativeMethods.WinEventDelegate? _winEventDelegate;

	private static nint _hideHook;

	private static NativeMethods.WinEventDelegate? _hideDelegate;

	private static nint _fgHook;

	private static NativeMethods.WinEventDelegate? _fgDelegate;

	private static readonly uint EVENT_SYSTEM_FOREGROUND = 3u;

	private static readonly Dictionary<nint, long> _lastRecoveryTick = new Dictionary<nint, long>();

	private const long RecoveryCooldownTicks = 5000000L;

	private static readonly Dictionary<nint, bool> _lastDesiredTop = new Dictionary<nint, bool>();

	private static readonly Dictionary<nint, bool> _intendedVisible = new Dictionary<nint, bool>();

	private static readonly object _intendedLock = new object();

	private static long _lastOwnWindowInteractionTick;

	private const long OwnInteractionGraceTicks = 12000000L;

	private static nint _desktopForegroundHwnd;

	private static readonly Dictionary<nint, long> _lastProgmanRecoveryTick = new Dictionary<nint, long>();

	private static int _totalProgmanRecoveries;

	private static bool _isProgmanDetection;

	private static long _toggleDesktopGlobalCooldownUntil;

	private static readonly TimeSpan ToggleDesktopGlobalCooldown = TimeSpan.FromSeconds(3.0);

	private static bool _detection6LastWasDesktop;

	private static long _detection6CooldownUntil;

	private static readonly TimeSpan Detection6ExtendedCooldown = TimeSpan.FromSeconds(15.0);

	private static bool _detection5LastWasIconic;

	private static long _detection5CooldownUntil;

	private static readonly TimeSpan Detection5ExtendedCooldown = TimeSpan.FromSeconds(15.0);

	private static bool _detection4LastOwnerIconic;

	private static long _detection4CooldownUntil;

	private static readonly TimeSpan Detection4ExtendedCooldown = TimeSpan.FromSeconds(15.0);

	private static long _dwmStabilizationUntil;

	private static long _lastFullDwmWakeupAt;

	private static int _consecutiveParentRestoreCycles;

	private static int _totalRecoveries;

	private static int _totalWinEventRecoveries;

	private static int _totalTimerRecoveries;

	private static Timer? _diagTimer;

	private static Timer? _reapplyTimer;

	private static Timer? _startupWarmupTimer;

	private static readonly int[] _warmupDelays = new int[3] { 1500, 4000, 8000 };

	private static int _warmupIndex;

	private static int _totalReapplyCycles;

	private static int _totalParentRestored;

	private static long _lastParentRestoreAt;

	private static long _lastFgRecoveryAt;

	private static long _lastProtectedInteractionTick;

	private static uint _lastPid;

	private static string _lastPidName = string.Empty;

	private static readonly int _selfPid = Process.GetCurrentProcess().Id;

	private static int _reapplyCyclePending;

	private static bool _reapplyCycleMigrationLogged;

	private static readonly bool _logEnabled = Environment.GetEnvironmentVariable("DESKTOOL_NOLOG") != "1";

	public static void SetIntendedVisible(nint h, bool visible)
	{
		if (h == IntPtr.Zero)
		{
			return;
		}
		lock (_intendedLock)
		{
			_intendedVisible[h] = visible;
		}
	}

	public static void NotifyOwnWindowInteraction()
	{
		_lastOwnWindowInteractionTick = DateTime.UtcNow.Ticks;
	}

	public static void KeepDesktopForeground()
	{
		try
		{
			nint hwnd = NativeMethods.GetShellWindow();
			if (hwnd == IntPtr.Zero)
			{
				hwnd = NativeMethods.GetDesktopWindow();
			}
			if (hwnd != IntPtr.Zero)
			{
				_desktopForegroundHwnd = hwnd;
				if (!NativeMethods.SetForegroundWindow(hwnd))
				{
					Log($"[WinDHook] ★ V2.4.50 KeepDesktopForeground 失败 hwnd=0x{hwnd:X8}，回退 V2.4.48/49 逻辑");
				}
			}
		}
		catch
		{
		}
	}

	public static void NotifyProtectedWindowInteraction()
	{
		Interlocked.Exchange(ref _lastProtectedInteractionTick, DateTime.UtcNow.Ticks);
	}

	public static void Register(nint hwnd)
	{
		lock (Lock)
		{
			if (!Protected.Contains(hwnd))
			{
				Protected.Add(hwnd);
			}
		}
		SetIntendedVisible(hwnd, visible: true);
		Log($"[服务] 注册受保护窗口 0x{hwnd:X8}（分层路径），共 {Protected.Count} 个");
	}

	public static void Unregister(nint hwnd)
	{
		lock (Lock)
		{
			if (Protected.Contains(hwnd))
			{
				Protected.Remove(hwnd);
			}
		}
		lock (Lock)
		{
			_lastDesiredTop.Remove(hwnd);
		}
		SetIntendedVisible(hwnd, visible: false);
		Log($"[服务] 注销窗口 0x{hwnd:X8}，剩余 {Protected.Count} 个");
		_dwmStabilizationUntil = DateTime.UtcNow.Ticks + 50000000;
		if (Protected.Count <= 0)
		{
			return;
		}
		try
		{
			nint[] remaining = GetProtected();
			nint[] array = remaining;
			foreach (nint h in array)
			{
				if (h != IntPtr.Zero && h != hwnd)
				{
					DesktopEmbedService.ReapplyProtection(h);
				}
			}
			Log($"[服务] 注销后已预防性刷新 {remaining.Length} 个剩余窗口的保护属性");
		}
		catch (Exception ex)
		{
			Log("[服务] 注销后刷新异常: " + ex.Message);
		}
	}

	private static nint[] GetProtected()
	{
		lock (Lock)
		{
			nint[] a = new nint[Protected.Count];
			Protected.CopyTo(a);
			return a;
		}
	}

	public static void Start()
	{
		NativeMethods.RegisterDesktopToolMessage();
		try
		{
			_winEventDelegate = OnWinEvent;
			_winEventHook = NativeMethods.SetWinEventHook(32791u, 32792u, IntPtr.Zero, _winEventDelegate, 0u, 0u, 0u);
			if (_winEventHook != IntPtr.Zero)
			{
				Log("[服务] SetWinEventHook 已启动（降级路径事件驱动重应用 GWLP_HWNDPARENT，无轮询）");
			}
			else
			{
				Log("[服务] ⚠ SetWinEventHook 启动失败（非致命）");
			}
			try
			{
				_fgDelegate = OnForegroundChanged;
				_fgHook = NativeMethods.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgDelegate, 0u, 0u, 0u);
				if (_fgHook != IntPtr.Zero)
				{
					Log("[服务] ★ V2.5.22 SetWinEventHook(前台) 已启动（PEEK失效系统事件驱动 Win+D 恢复，无轮询）");
				}
				else
				{
					Log("[服务] ⚠ SetWinEventHook(前台) 启动失败（非致命）");
				}
			}
			catch (Exception ex)
			{
				Log("[服务] ⚠ SetWinEventHook(前台) 异常: " + ex.Message + "（非致命）");
			}
			try
			{
				_hideDelegate = OnWinEvent;
				_hideHook = NativeMethods.SetWinEventHook(32772u, 32772u, IntPtr.Zero, _hideDelegate, 0u, 0u, 0u);
				if (_hideHook != IntPtr.Zero)
				{
					Log("[服务] ★ V2.5.54 SetWinEventHook(HIDE) 已启动（降级路径 SW_HIDE 兜底恢复，无轮询）");
				}
				else
				{
					Log("[服务] ⚠ SetWinEventHook(HIDE) 启动失败（非致命）");
				}
			}
			catch (Exception ex2)
			{
				Log("[服务] ⚠ SetWinEventHook(HIDE) 异常: " + ex2.Message + "（非致命）");
			}
		}
		catch (Exception ex3)
		{
			Log("[服务] ⚠ SetWinEventHook 异常: " + ex3.Message + "（非致命）");
		}
		if (Environment.OSVersion.Version.Build < 22000 || DesktopEmbedService.IsDegradedPath)
		{
			_gentleRecoveryTimer = new Timer(delegate
			{
				GentleCloakRecoveryTick();
			}, null, 200, 200);
		}
		else
		{
			Log("[服务] ★ V2.5.26 Win11(WorkerW 路径) 不启动轮询定时器（Win+D 恢复由前台事件驱动，无轮询）");
		}
		Version ver = Assembly.GetExecutingAssembly().GetName().Version;
		Log($"[服务] V{ver.Major}.{ver.Minor}.{ver.Build} 已启动 — ★ 官方API防护 + Win10温和恢复(前台桌面转移/多信号并集，仅PEEK失效系统，无ToggleDesktop)");
		_reapplyTimer = new Timer(delegate
		{
			ReapplyProtectionCycle();
		}, null, 15000, 15000);
		ScheduleStartupWarmup();
		_diagTimer = new Timer(delegate
		{
			DiagnoseProtectionStatus();
		}, null, 3000, -1);
	}

	public static void Stop()
	{
		_diagTimer?.Dispose();
		_diagTimer = null;
		_reapplyTimer?.Dispose();
		_reapplyTimer = null;
		_startupWarmupTimer?.Dispose();
		_startupWarmupTimer = null;
		_recoveryTimer?.Dispose();
		_recoveryTimer = null;
		_gentleRecoveryTimer?.Dispose();
		_gentleRecoveryTimer = null;
		_reshowTimer?.Dispose();
		_reshowTimer = null;
		_reshowPending = 0;
		_winDLatched = false;
		_winDSettleUntil = 0L;
		_lastRealFgHwnd = IntPtr.Zero;
		_winDCandidateHwnd = IntPtr.Zero;
		_realAppStableTicks = 0;
		_realAppSolidSince = 0L;
		_ownFgStableTicks = 0;
		Interlocked.Exchange(ref _gentleTickRunning, 0);
		_lastRealFgHwnd = GetFirstVisibleNormalWindow();
		lock (Lock)
		{
			_lastDesiredTop.Clear();
		}
		if (_winEventHook != IntPtr.Zero)
		{
			NativeMethods.UnhookWinEvent(_winEventHook);
			_winEventHook = IntPtr.Zero;
		}
		_winEventDelegate = null;
		if (_hideHook != IntPtr.Zero)
		{
			NativeMethods.UnhookWinEvent(_hideHook);
			_hideHook = IntPtr.Zero;
		}
		_hideDelegate = null;
		Log("[服务] 防护已停止");
	}

	private static void OnWinEvent(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
	{
		if (eventType != 32791 && eventType != 32792 && eventType != 32772)
		{
			return;
		}
		bool isProtected;
		lock (Lock)
		{
			isProtected = Protected.Contains(hwnd);
		}
		if (!isProtected || !DesktopEmbedService.EnableDesktopEmbedding)
		{
			return;
		}
		if (!DesktopEmbedService.NeedsCloakRecovery)
		{
			if (eventType == 32791)
			{
				Log($"[WinEvent] 诊断：窗口 0x{hwnd:X8} 被DWM斗篷 (非恢复路径，无需恢复) EventID={eventType}");
			}
			return;
		}
		if (eventType == 32772)
		{
			bool intended;
			lock (_intendedLock)
			{
				_intendedVisible.TryGetValue(hwnd, out intended);
			}
			if (!intended)
			{
				Log($"[WinEvent] HIDE 但 intended=false(用户已关闭)，跳过恢复 hwnd=0x{hwnd:X8}");
				return;
			}
			if (NativeMethods.IsWindowVisible(hwnd))
			{
				return;
			}
		}
		Interlocked.Increment(ref _totalWinEventRecoveries);
		long nowTicks = DateTime.UtcNow.Ticks;
		lock (Lock)
		{
			if (_lastRecoveryTick.TryGetValue(hwnd, out var last) && nowTicks - last < 5000000)
			{
				return;
			}
			_lastRecoveryTick[hwnd] = nowTicks;
		}
		try
		{
			DesktopEmbedService.ReinforceProtectionLightweight(hwnd);
			if (eventType == 32791 || eventType == 32772)
			{
				string tag = ((eventType == 32791) ? "CLOAKED" : "HIDE");
				Log($"[WinEvent] ★ V2.5.54 收到 {tag}(挂件被隐藏) hwnd=0x{hwnd:X8} → 温和恢复(无焦点/无闪烁)");
				NativeMethods.ShowWindow(hwnd, 4);
				NativeMethods.SetWindowPos(hwnd, DesktopBottomRef(), 0, 0, 0, 0, 19u);
				Log($"[WinEvent] ★ V2.5.54 已温和恢复显示并压回桌面层级 hwnd=0x{hwnd:X8}");
			}
			Log($"[WinEvent] ★ 降级路径事件驱动重应用：窗口 0x{hwnd:X8} EventID={eventType}");
		}
		catch (Exception ex)
		{
			Log("[WinEvent] ⚠ 重应用异常: " + ex.Message);
		}
	}

	private static void OnForegroundChanged(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
	{
		try
		{
			if (idObject != 0 || idChild != 0 || !DesktopEmbedService.EnableDesktopEmbedding)
			{
				return;
			}
			bool num = IsForegroundDesktop();
			bool fgIsRealApp = IsForegroundRealApp();
			if (!num && !fgIsRealApp)
			{
				return;
			}
			long nowTicks = DateTime.UtcNow.Ticks;
			if (fgIsRealApp)
			{
				if (nowTicks - Interlocked.Read(in _lastProtectedInteractionTick) < 5000000)
				{
					return;
				}
				nint[] realAppSnapshot;
				lock (Lock)
				{
					realAppSnapshot = new nint[Protected.Count];
					Protected.CopyTo(realAppSnapshot);
				}
				nint[] array = realAppSnapshot;
				foreach (nint h in array)
				{
					if (h != IntPtr.Zero && NativeMethods.IsWindowVisible(h))
					{
						NativeMethods.SetWindowPos(h, DesktopBottomRef(), 0, 0, 0, 0, 19u);
					}
				}
			}
			else
			{
				if (nowTicks - Interlocked.Read(in _lastProtectedInteractionTick) < 5000000 || nowTicks - _lastFgRecoveryAt < 3000000)
				{
					return;
				}
				_lastFgRecoveryAt = nowTicks;
				nint[] snapshot;
				lock (Lock)
				{
					snapshot = new nint[Protected.Count];
					Protected.CopyTo(snapshot);
				}
				nint[] array = snapshot;
				foreach (nint h2 in array)
				{
					if (h2 == IntPtr.Zero)
					{
						continue;
					}
					int cloaked = 0;
					int cloakHr = -1;
					bool isHiddenByWinD = false;
					bool dbgCloaked = false;
					bool dbgStyleVisible = false;
					bool dbgNativeVisible = false;
					bool dbgIntended = false;
					try
					{
						cloakHr = NativeMethods.DwmGetWindowAttribute(h2, 14, out cloaked, 4);
					}
					catch
					{
					}
					if (cloakHr == 0 && cloaked != 0)
					{
						isHiddenByWinD = true;
						dbgCloaked = true;
					}
					else
					{
						try
						{
							dbgStyleVisible = (NativeMethods.GetWindowLong(h2, -16) & 0x10000000) != 0;
							dbgNativeVisible = NativeMethods.IsWindowVisible(h2);
							if (dbgStyleVisible && !dbgNativeVisible)
							{
								isHiddenByWinD = true;
							}
							else
							{
								lock (_intendedLock)
								{
									_intendedVisible.TryGetValue(h2, out dbgIntended);
								}
								if (dbgIntended && !dbgNativeVisible)
								{
									isHiddenByWinD = true;
								}
							}
						}
						catch
						{
						}
					}
					Log($"[前台事件] 窗口 0x{h2:X8} cloaked={dbgCloaked} styleVisible={dbgStyleVisible} nativeVisible={dbgNativeVisible} intended={dbgIntended} → hiddenByWinD={isHiddenByWinD}");
					lock (_intendedLock)
					{
						_intendedVisible.TryGetValue(h2, out dbgIntended);
					}
					if (!dbgIntended)
					{
						Log($"[前台事件] 窗口 0x{h2:X8} intended={dbgIntended} → 跳过(用户已关闭)");
						continue;
					}
					DesktopEmbedService.ReinforceProtectionLightweight(h2);
					NativeMethods.ShowWindow(h2, 4);
					NativeMethods.SetWindowPos(h2, DesktopBottomRef(), 0, 0, 0, 0, 19u);
					Log($"[前台事件] 窗口 0x{h2:X8} cloaked={dbgCloaked} styleVisible={dbgStyleVisible} nativeVisible={dbgNativeVisible} intended={dbgIntended} → ★断言可见(重显+复锚定) detected={isHiddenByWinD}");
				}
				Log($"[前台事件] ★ V2.5.57 Win+D 前台事件分治恢复完成（真子窗口门控重显/分层顶层无条件重显，无轮询）受保护窗口数={snapshot.Length}");
				ScheduleDebouncedReshow();
			}
		}
		catch
		{
		}
	}

	private static void ScheduleDebouncedReshow()
	{
		if (Interlocked.Exchange(ref _reshowPending, 1) == 1)
		{
			return;
		}
		if (_reshowTimer == null)
		{
			_reshowTimer = new Timer(delegate
			{
				DebouncedReshow();
			}, null, 250, -1);
		}
		_reshowTimer.Change(250, -1);
	}

	private static void DebouncedReshow()
	{
		Interlocked.Exchange(ref _reshowPending, 0);
		try
		{
			if (!DesktopEmbedService.EnableDesktopEmbedding || !IsForegroundDesktop())
			{
				return;
			}
			nint[] snapshot;
			lock (Lock)
			{
				snapshot = new nint[Protected.Count];
				Protected.CopyTo(snapshot);
			}
			int n = 0;
			nint[] array = snapshot;
			foreach (nint h in array)
			{
				if (h != IntPtr.Zero)
				{
					bool intended;
					lock (_intendedLock)
					{
						_intendedVisible.TryGetValue(h, out intended);
					}
					if (intended)
					{
						DesktopEmbedService.ReinforceProtectionLightweight(h);
						NativeMethods.ShowWindow(h, 4);
						NativeMethods.SetWindowPos(h, DesktopBottomRef(), 0, 0, 0, 0, 19u);
						n++;
					}
				}
			}
			if (n > 0)
			{
				Log($"[兜底] ★ V2.5.56 滞后斗篷一次性重显完成 挂件数={n}");
			}
		}
		catch
		{
		}
	}

	private static void OnForegroundEvent(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
	{
		if (eventType != EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero || DesktopEmbedService.IsExcludeFromPeekWorking || !DesktopEmbedService.NeedsCloakRecovery)
		{
			return;
		}
		try
		{
			StringBuilder sb = new StringBuilder(64);
			NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
			string cls = sb.ToString();
			if (IsDesktopContext(hwnd))
			{
				DesktopEmbedService.ReestablishAfterDesktopRefresh(hwnd);
			}
			else
			{
				if (IsOwnProcessWindow(hwnd) || IsTransientForegroundWindow(hwnd) || IsShellContextTransientWindow(hwnd) || IsContextMenuOpen() || IsMenuModeActive())
				{
					return;
				}
				string proc = GetProcessNameCached(GetPidOf(hwnd));
				Log($"[前台事件] 应用(0x{hwnd:X8},{cls},进程={proc})成为前台 → 重新标记全部受保护窗口为已交互（Win10 免疫加固，不动桌面焦点）");
				nint appHwnd = hwnd;
				(Application.Current?.Dispatcher)?.BeginInvoke((Action)delegate
				{
					try
					{
						NativeMethods.SetFocus(appHwnd);
					}
					catch (Exception ex)
					{
						Log("[前台事件] 还回应用焦点异常: " + ex.Message);
					}
				}, DispatcherPriority.Input);
			}
		}
		catch
		{
		}
	}

	private static void OnMenuEvent(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
	{
		if (eventType != 6)
		{
			return;
		}
		try
		{
			nint owner = NativeMethods.GetWindow(hwnd, 4u);
			string ownerCls = string.Empty;
			if (owner != IntPtr.Zero)
			{
				StringBuilder sbOwner = new StringBuilder(64);
				NativeMethods.GetClassName(owner, sbOwner, sbOwner.Capacity);
				ownerCls = sbOwner.ToString();
			}
			if (owner != IntPtr.Zero && !(ownerCls == "Progman") && !(ownerCls == "WorkerW") && !(ownerCls == "SysListView32"))
			{
				return;
			}
			nint fg = NativeMethods.GetForegroundWindow();
			string fgCls = GetClassNameSafe(fg);
			nint focus = NativeMethods.GetFocus();
			string focusCls = GetClassNameSafe(focus);
			bool widgetIsFg = false;
			bool widgetIsFocus = false;
			nint[] array = GetProtected();
			foreach (nint intPtr in array)
			{
				if (intPtr == fg)
				{
					widgetIsFg = true;
				}
				if (intPtr == focus)
				{
					widgetIsFocus = true;
				}
			}
			Log($"[菜单诊断] ★ 桌面右键上下文菜单打开 hwnd=0x{hwnd:X8} ownerCls={ownerCls} 前台=0x{fg:X8}({fgCls}) 焦点=0x{focus:X8}({focusCls}) 挂件抢前台={widgetIsFg} 挂件抢焦点={widgetIsFocus}");
		}
		catch
		{
		}
	}

	private static string GetClassNameSafe(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return "NULL";
		}
		try
		{
			StringBuilder sb = new StringBuilder(64);
			NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
			return sb.ToString();
		}
		catch
		{
			return "?";
		}
	}

	private static bool IsOwnProcessWindow(nint hwnd)
	{
		try
		{
			uint pid = 0u;
			NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
			return pid == (uint)Environment.ProcessId;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsDesktopContext(nint hwnd)
	{
		try
		{
			StringBuilder sb = new StringBuilder(64);
			NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
			switch (sb.ToString())
			{
			case "Progman":
			case "WorkerW":
				return true;
			case "SysListView32":
				return true;
			case "#32768":
			{
				nint owner = NativeMethods.GetWindow(hwnd, 4u);
				if (owner == IntPtr.Zero)
				{
					return true;
				}
				StringBuilder sbOwner = new StringBuilder(64);
				NativeMethods.GetClassName(owner, sbOwner, sbOwner.Capacity);
				switch (sbOwner.ToString())
				{
				case "Progman":
				case "WorkerW":
				case "SysListView32":
					return true;
				}
				break;
			}
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsTransientForegroundWindow(nint hwnd)
	{
		try
		{
			StringBuilder sb = new StringBuilder(64);
			NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
			string cls = sb.ToString();
			if (cls == "#32768")
			{
				return true;
			}
			if (cls == "#32770")
			{
				return true;
			}
			if (NativeMethods.GetWindow(hwnd, 4u) != IntPtr.Zero)
			{
				return true;
			}
			if ((NativeMethods.GetWindowLong(hwnd, -20) & 0x80) != 0)
			{
				return true;
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsShellContextTransientWindow(nint hwnd)
	{
		try
		{
			uint pid = GetPidOf(hwnd);
			if (pid == 0)
			{
				return false;
			}
			if (!string.Equals(GetProcessNameCached(pid), "explorer", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			StringBuilder sb = new StringBuilder(64);
			NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
			switch (sb.ToString())
			{
			case "CabinetWClass":
			case "Progman":
			case "WorkerW":
				return false;
			default:
				return true;
			}
		}
		catch
		{
			return false;
		}
	}

	private static uint GetPidOf(nint hwnd)
	{
		try
		{
			uint pid = 0u;
			NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
			return pid;
		}
		catch
		{
			return 0u;
		}
	}

	private static string GetProcessNameCached(uint pid)
	{
		if (pid == _lastPid)
		{
			return _lastPidName;
		}
		string name = string.Empty;
		try
		{
			using Process p = Process.GetProcessById((int)pid);
			name = p?.ProcessName ?? string.Empty;
		}
		catch
		{
			name = string.Empty;
		}
		_lastPid = pid;
		_lastPidName = name;
		return name;
	}

	private static bool IsMenuModeActive()
	{
		try
		{
			NativeMethods.GUITHREADINFO gti = new NativeMethods.GUITHREADINFO
			{
				cbSize = Marshal.SizeOf(typeof(NativeMethods.GUITHREADINFO))
			};
			if (!NativeMethods.GetGUIThreadInfo(0u, ref gti))
			{
				return false;
			}
			return (gti.flags & 0x1C) != 0 || gti.hwndMenuOwner != IntPtr.Zero;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsRightMouseButtonDown()
	{
		try
		{
			return (NativeMethods.GetAsyncKeyState(2) & 0x8000) != 0;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsLeftMouseButtonDown()
	{
		try
		{
			return (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsDesktopRightClickActive()
	{
		if (!IsContextMenuOpen())
		{
			return IsRightMouseButtonDown();
		}
		return true;
	}

	public static bool IsContextMenuOpen()
	{
		try
		{
			bool found = false;
			NativeMethods.EnumWindows(delegate(nint hWnd, nint lParam)
			{
				if (found)
				{
					return true;
				}
				if (!NativeMethods.IsWindowVisible(hWnd))
				{
					return true;
				}
				StringBuilder stringBuilder = new StringBuilder(64);
				NativeMethods.GetClassName(hWnd, stringBuilder, stringBuilder.Capacity);
				if (stringBuilder.ToString() == "#32768")
				{
					found = true;
					return false;
				}
				return true;
			}, IntPtr.Zero);
			return found;
		}
		catch
		{
			return false;
		}
	}

	private static void GentleCloakRecoveryTick()
	{
		if (Interlocked.Exchange(ref _gentleTickRunning, 1) == 1)
		{
			return;
		}
		try
		{
			if (DesktopEmbedService.IsExcludeFromPeekWorking)
			{
				return;
			}
			if (Environment.OSVersion.Version.Build >= 22000 && !DesktopEmbedService.IsDegradedPath)
			{
				Timer? gentleRecoveryTimer = _gentleRecoveryTimer;
				_gentleRecoveryTimer = null;
				gentleRecoveryTimer?.Change(-1, -1);
			}
			else
			{
				if (!DesktopEmbedService.EnableDesktopEmbedding)
				{
					return;
				}
				nint[] snapshot;
				lock (Lock)
				{
					snapshot = new nint[Protected.Count];
					Protected.CopyTo(snapshot);
				}
				if (snapshot.Length == 0)
				{
					return;
				}
				bool fgIsDesktop = IsForegroundDesktop();
				nint currentFg = NativeMethods.GetForegroundWindow();
				bool desktopJustBecameForeground = fgIsDesktop && !_lastForegroundWasDesktop;
				bool desktopJustLeftForeground = !fgIsDesktop && _lastForegroundWasDesktop;
				_lastForegroundWasDesktop = fgIsDesktop;
				if (!fgIsDesktop && IsForegroundRealApp())
				{
					try
					{
						_lastRealFgHwnd = NativeMethods.GetForegroundWindow();
					}
					catch
					{
					}
				}
				if (desktopJustBecameForeground)
				{
					_winDCandidateHwnd = _lastRealFgHwnd;
					_winDSettleUntil = DateTime.UtcNow.Ticks + 4000000;
					_winDLatched = HasWinDEvidence();
				}
				bool fgRealApp = IsForegroundRealApp();
				bool fgIsOwnWindow = currentFg != IntPtr.Zero && IsOwnProcessWindow(currentFg);
				bool fgRealAppSolid = false;
				if (fgRealApp)
				{
					try
					{
						nint fgNow = NativeMethods.GetForegroundWindow();
						fgRealAppSolid = fgNow != IntPtr.Zero && NativeMethods.IsWindowVisible(fgNow) && !NativeMethods.IsIconic(fgNow);
					}
					catch
					{
					}
				}
				_realAppStableTicks = (fgRealAppSolid ? (_realAppStableTicks + 1) : 0);
				bool ownInteractionNow = DateTime.UtcNow.Ticks - _lastOwnWindowInteractionTick < 12000000;
				if (fgRealAppSolid)
				{
					if (_realAppSolidSince == 0L)
					{
						_realAppSolidSince = DateTime.UtcNow.Ticks;
					}
					long num = (DateTime.UtcNow.Ticks - _realAppSolidSince) / 10000;
					bool normalClear = !ownInteractionNow;
					bool forcedClear = num >= 800;
					if (normalClear || forcedClear)
					{
						_winDLatched = false;
						_winDCandidateHwnd = IntPtr.Zero;
					}
				}
				else
				{
					_realAppSolidSince = 0L;
				}
				_ownFgStableTicks = (fgIsOwnWindow ? (_ownFgStableTicks + 1) : 0);
				if (_winDLatched && _ownFgStableTicks >= 2)
				{
					_winDLatched = false;
					_winDCandidateHwnd = IntPtr.Zero;
					Log("[温和恢复] ★ V2.20.x(用户9) 本进程窗口稳定前台(≥400ms)=用户在使用主面板，非 Win+D → 清除闩锁，停止 TOPMOST 风暴");
				}
				if (!_winDLatched && !fgRealApp && !fgIsOwnWindow && HasWinDEvidence())
				{
					_winDLatched = true;
				}
				else if (!_winDLatched && !fgIsOwnWindow && _realAppStableTicks == 0 && desktopJustLeftForeground)
				{
					_winDLatched = true;
				}
				bool currentWinDMode = (fgIsDesktop && (!AreNormalWindowsVisible() || _winDLatched)) || _winDLatched;
				bool winDModeEnded = !currentWinDMode && _lastWinDMode;
				nint[] array = snapshot;
				foreach (nint hwnd in array)
				{
					if (hwnd == IntPtr.Zero)
					{
						continue;
					}
					if (!NativeMethods.IsWindow(hwnd))
					{
						lock (Lock)
						{
							Protected.Remove(hwnd);
							_lastDesiredTop.Remove(hwnd);
							_lastRecoveryTick.Remove(hwnd);
						}
						Log($"[温和恢复] ★ V2.5.89 剪除死句柄 hwnd=0x{hwnd:X8}（窗口已销毁未注销）");
						continue;
					}
					bool immune = WinDZOrderImmuneEnabled();
					if (immune)
					{
						TryZOrderImmuneBounce(hwnd);
					}
					int cloaked = -1;
					int cloakHr = -1;
					bool isVisible = true;
					bool isIconic = false;
					int showCmd = -1;
					try
					{
						cloakHr = NativeMethods.DwmGetWindowAttribute(hwnd, 14, out cloaked, 4);
						isVisible = NativeMethods.IsWindowVisible(hwnd);
						isIconic = NativeMethods.IsIconic(hwnd);
						NativeMethods.WINDOWPLACEMENT wp = default(NativeMethods.WINDOWPLACEMENT);
						wp.length = Marshal.SizeOf(wp);
						if (NativeMethods.GetWindowPlacement(hwnd, ref wp))
						{
							showCmd = wp.showCmd;
						}
					}
					catch
					{
						continue;
					}
					bool hidden = (cloakHr == 0 && cloaked != 0) || !isVisible || isIconic || showCmd == 2 || showCmd == 6 || showCmd == 0;
					if (!(hidden || desktopJustBecameForeground || currentWinDMode || desktopJustLeftForeground))
					{
						continue;
					}
					bool winDMode = currentWinDMode;
					bool realAppForeground = IsForegroundRealApp();
					bool ownInteraction = DateTime.UtcNow.Ticks - _lastOwnWindowInteractionTick < 12000000;
					bool effectiveRealApp = realAppForeground && !ownInteraction;
					bool? targetTop = null;
					if (hidden)
					{
						targetTop = winDMode || !effectiveRealApp || ownInteraction;
					}
					else if (winDMode && !effectiveRealApp)
					{
						targetTop = true;
					}
					else if (desktopJustLeftForeground && realAppForeground)
					{
						if (ownInteraction)
						{
							targetTop = true;
						}
						else
						{
							Log($"[温和恢复] ★ V2.4.49 延迟一拍：桌面→真实应用过渡(疑似点击挂件回退激活)，等 ownInteraction 生效避免闪烁 hwnd=0x{hwnd:X8}");
							targetTop = null;
						}
					}
					else if (realAppForeground)
					{
						targetTop = false;
					}
					else if (ownInteraction)
					{
						targetTop = true;
					}
					else if (fgIsDesktop)
					{
						targetTop = false;
					}
					bool hasTopmost = (NativeMethods.GetWindowLong(hwnd, -20) & 8) != 0;
					bool transitionGrace = desktopJustLeftForeground && realAppForeground && ownInteraction;
					bool num2 = !ownInteraction && targetTop != true && !transitionGrace && (!winDMode || effectiveRealApp);
					bool aboveFg = num2 && realAppForeground && IsWindowAbove(hwnd, currentFg);
					if (num2 && (winDModeEnded || hasTopmost || aboveFg))
					{
						try
						{
							NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, 16403u);
							NativeMethods.SetWindowPos(hwnd, DesktopBottomRef(), 0, 0, 0, 0, 16403u);
							long dropTicks = DateTime.UtcNow.Ticks;
							lock (Lock)
							{
								_lastDesiredTop[hwnd] = false;
								_lastRecoveryTick[hwnd] = dropTicks;
							}
							Log($"[温和恢复] ★ V2.20.x 强制落底(撤TOPMOST) hwnd=0x{hwnd:X8} winDModeEnded={winDModeEnded} hasTopmost={hasTopmost} aboveFg={aboveFg}");
						}
						catch (Exception ex)
						{
							Log("[温和恢复] ⚠ V2.20.x 强制落底异常: " + ex.Message);
						}
					}
					else
					{
						if (!targetTop.HasValue)
						{
							continue;
						}
						bool changed;
						lock (Lock)
						{
							changed = !_lastDesiredTop.TryGetValue(hwnd, out var lt) || lt != targetTop.Value;
						}
						if (!hidden && !changed && !winDMode && !ownInteraction)
						{
							continue;
						}
						long nowTicks = DateTime.UtcNow.Ticks;
						bool inCooldown;
						lock (Lock)
						{
							inCooldown = _lastRecoveryTick.TryGetValue(hwnd, out var last) && nowTicks - last < 5000000;
						}
						if (inCooldown)
						{
							if (targetTop == true)
							{
								nint topRef = (winDMode ? NativeMethods.HWND_TOPMOST : DesktopBottomRef());
								TraceZRef("targetTop", topRef);
								NativeMethods.SetWindowPos(hwnd, topRef, 0, 0, 0, 0, 16403u);
							}
							continue;
						}
						lock (Lock)
						{
							_lastRecoveryTick[hwnd] = nowTicks;
						}
						try
						{
							Interlocked.Increment(ref _totalTimerRecoveries);
							string trigger;
							if (targetTop == true)
							{
								if (desktopJustBecameForeground || hidden)
								{
									DesktopEmbedService.ReinforceProtectionLightweight(hwnd);
								}
								if (hidden)
								{
									NativeMethods.ShowWindow(hwnd, 4);
								}
								nint topRef2 = (winDMode ? NativeMethods.HWND_TOPMOST : DesktopBottomRef());
								TraceZRef("ownInteraction", topRef2);
								NativeMethods.SetWindowPos(hwnd, topRef2, 0, 0, 0, 0, 16403u);
								trigger = (winDMode ? "Win+D进行中→抬HWND_TOPMOST" : (effectiveRealApp ? "真实应用前台→保持HWND_TOP" : (ownInteraction ? "点击本程序挂件→保持HWND_TOP" : "本程序窗口前台→保持HWND_TOP")));
							}
							else
							{
								if (hidden)
								{
									NativeMethods.ShowWindow(hwnd, 4);
								}
								if (!immune)
								{
									NativeMethods.SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 16403u);
									NativeMethods.SetWindowPos(hwnd, DesktopBottomRef(), 0, 0, 0, 0, 16403u);
								}
								trigger = (realAppForeground ? "真实应用前台→落deskOwner层" : (hidden ? "窗口状态命中" : "松Win+D→落deskOwner层"));
							}
							lock (Lock)
							{
								_lastDesiredTop[hwnd] = targetTop.Value;
							}
							int cloakedAfter = -1;
							int hrAfter = NativeMethods.DwmGetWindowAttribute(hwnd, 14, out cloakedAfter, 4);
							bool visibleAfter = NativeMethods.IsWindowVisible(hwnd);
							Log($"[温和恢复] ★ V2.4.46 触发={trigger} hwnd=0x{hwnd:X8} 恢复前(CLOAKED={cloaked} Visible={isVisible}) → 恢复后(CLOAKED={cloakedAfter}(hr=0x{hrAfter:X8}) Visible={visibleAfter})");
						}
						catch (Exception ex2)
						{
							Log("[温和恢复] ⚠ 恢复异常: " + ex2.Message);
						}
					}
				}
				_lastWinDMode = currentWinDMode;
			}
		}
		catch (Exception ex3)
		{
			try
			{
				Log("[温和恢复] ⚠ GentleCloakRecoveryTick 异常(已吸收，不崩进程): " + ex3.GetType().Name + ": " + ex3.Message);
			}
			catch
			{
			}
		}
		finally
		{
			Interlocked.Exchange(ref _gentleTickRunning, 0);
		}
	}

	private static nint DesktopBottomRef()
	{
		nint deskOwner = DesktopEmbedService.DesktopOwner;
		if (deskOwner == IntPtr.Zero)
		{
			return NativeMethods.HWND_BOTTOM;
		}
		return deskOwner;
	}

	private static void TraceZRef(string branch, nint topRef)
	{
	}

	private static bool IsWindowAbove(nint a, nint b)
	{
		if (a == b || a == IntPtr.Zero || b == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			nint cur = NativeMethods.GetTopWindow(IntPtr.Zero);
			int guard = 0;
			while (cur != IntPtr.Zero && guard++ < 4096)
			{
				if (cur == a)
				{
					return true;
				}
				if (cur == b)
				{
					return false;
				}
				cur = NativeMethods.GetWindow(cur, 2u);
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool IsForegroundDesktop()
	{
		try
		{
			nint fg = NativeMethods.GetForegroundWindow();
			if (fg == IntPtr.Zero)
			{
				return false;
			}
			StringBuilder sb = new StringBuilder(64);
			NativeMethods.GetClassName(fg, sb, sb.Capacity);
			string cls = sb.ToString();
			return cls == "Progman" || cls == "WorkerW";
		}
		catch
		{
			return false;
		}
	}

	private static bool IsForegroundRealApp()
	{
		try
		{
			nint fg = NativeMethods.GetForegroundWindow();
			if (fg == IntPtr.Zero)
			{
				return false;
			}
			if (IsForegroundDesktop())
			{
				return false;
			}
			if (IsOwnProcessWindow(fg))
			{
				return false;
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static nint GetFirstVisibleNormalWindow()
	{
		nint found = IntPtr.Zero;
		try
		{
			NativeMethods.EnumWindows(delegate(nint hwnd, nint lParam)
			{
				NativeMethods.GetWindowThreadProcessId(hwnd, out var lpdwProcessId);
				if (lpdwProcessId == _selfPid)
				{
					return true;
				}
				StringBuilder stringBuilder = new StringBuilder(64);
				NativeMethods.GetClassName(hwnd, stringBuilder, stringBuilder.Capacity);
				switch (stringBuilder.ToString())
				{
				case "Progman":
				case "WorkerW":
				case "Shell_TrayWnd":
				case "Shell_SecondaryTrayWnd":
				case "TrayNotifyWnd":
				case "TrayClockWClass":
					return true;
				default:
					if (!NativeMethods.IsWindowVisible(hwnd))
					{
						return true;
					}
					if (NativeMethods.IsIconic(hwnd))
					{
						return true;
					}
					if ((NativeMethods.GetWindowLong(hwnd, -20) & 0x80) != 0)
					{
						return true;
					}
					if (NativeMethods.GetWindow(hwnd, 4u) != IntPtr.Zero)
					{
						return true;
					}
					found = hwnd;
					return false;
				}
			}, IntPtr.Zero);
		}
		catch
		{
		}
		return found;
	}

	private static bool HasWinDEvidence()
	{
		try
		{
			if (_winDCandidateHwnd != IntPtr.Zero && NativeMethods.IsWindow(_winDCandidateHwnd))
			{
				if (NativeMethods.IsIconic(_winDCandidateHwnd) || !NativeMethods.IsWindowVisible(_winDCandidateHwnd))
				{
					return true;
				}
				return false;
			}
		}
		catch
		{
		}
		return !AreNormalWindowsVisible();
	}

	private static bool AreNormalWindowsVisible()
	{
		bool found = false;
		try
		{
			NativeMethods.EnumWindows(delegate(nint hwnd, nint lParam)
			{
				NativeMethods.GetWindowThreadProcessId(hwnd, out var lpdwProcessId);
				if (lpdwProcessId == _selfPid)
				{
					return true;
				}
				StringBuilder stringBuilder = new StringBuilder(64);
				NativeMethods.GetClassName(hwnd, stringBuilder, stringBuilder.Capacity);
				switch (stringBuilder.ToString())
				{
				case "Progman":
				case "WorkerW":
				case "Shell_TrayWnd":
				case "Shell_SecondaryTrayWnd":
				case "TrayNotifyWnd":
				case "TrayClockWClass":
					return true;
				default:
					if (!NativeMethods.IsWindowVisible(hwnd))
					{
						return true;
					}
					if (NativeMethods.IsIconic(hwnd))
					{
						return true;
					}
					if ((NativeMethods.GetWindowLong(hwnd, -20) & 0x80) != 0)
					{
						return true;
					}
					if (NativeMethods.GetWindow(hwnd, 4u) != IntPtr.Zero)
					{
						return true;
					}
					found = true;
					return true;
				}
			}, IntPtr.Zero);
		}
		catch
		{
		}
		return found;
	}

	private static bool WinDZOrderImmuneEnabled()
	{
		long now = DateTime.UtcNow.Ticks;
		if (now - _lastImmuneFlagLoad > 30000000)
		{
			_lastImmuneFlagLoad = now;
			try
			{
				AppSettings s = DataService.LoadSettingsStatic();
				if (s != null)
				{
					_winDZOrderImmune = s.WinDZOrderImmune;
				}
			}
			catch
			{
			}
		}
		if (!_winDZOrderImmune && _zBouncedUp.Count > 0)
		{
			foreach (nint h in _zBouncedUp)
			{
				try
				{
					NativeMethods.SetWindowPos(h, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0, 19u);
				}
				catch
				{
				}
			}
			_zBouncedUp.Clear();
			Log("[Z序免疫-实验] 开关关闭 → 全部落回 HWND_BOTTOM");
		}
		return _winDZOrderImmune;
	}

	private static bool IsDesktopAboveWindow(nint hwnd)
	{
		try
		{
			nint cur = NativeMethods.GetTopWindow(IntPtr.Zero);
			int guard = 0;
			while (cur != IntPtr.Zero && guard++ < 1000)
			{
				if (cur == hwnd)
				{
					return false;
				}
				StringBuilder sb = new StringBuilder(64);
				NativeMethods.GetClassName(cur, sb, sb.Capacity);
				string cls = sb.ToString();
				if (cls == "WorkerW" || cls == "Progman")
				{
					return true;
				}
				cur = NativeMethods.GetWindow(cur, 2u);
			}
		}
		catch
		{
		}
		return false;
	}

	private static void TryZOrderImmuneBounce(nint hwnd)
	{
		try
		{
			if (IsDesktopAboveWindow(hwnd))
			{
				if (!_zBouncedUp.Contains(hwnd))
				{
					NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, 19u);
					_zBouncedUp.Add(hwnd);
					Log($"[Z序免疫-实验] 桌面层盖过窗体 → 抬回 HWND_TOP hwnd=0x{hwnd:X8}");
				}
			}
			else if (_zBouncedUp.Contains(hwnd))
			{
				NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0, 19u);
				_zBouncedUp.Remove(hwnd);
				Log($"[Z序免疫-实验] 桌面层已落下 → 落回 HWND_BOTTOM hwnd=0x{hwnd:X8}");
			}
		}
		catch
		{
		}
	}

	private static void RecoveryTimerTick()
	{
		_tickCount++;
	}

	private static void PerformRecovery(nint hwnd, bool bypassCooldown = false)
	{
		Log($"[恢复] ★ 恢复操作已禁用 (V2.19.0) — 窗口 0x{hwnd:X8} 异常但不会自动恢复");
	}

	private static void PerformProgmanRecovery(nint hwnd)
	{
		Log("[恢复] ★ ProgmanRecovery 已禁用 (V2.19.0)");
	}

	private static void ToggleDesktopViaShell()
	{
		Log("[ToggleDesktop] ★ 已禁用 (V2.19.0)");
	}

	private static void StartupDwmWakeup()
	{
	}

	private static void ReapplyProtectionCycle()
	{
		if (!DesktopEmbedService.EnableDesktopEmbedding || !DesktopEmbedService.NeedsCloakRecovery)
		{
			return;
		}
		if (Interlocked.CompareExchange(ref _reapplyCyclePending, 1, 0) != 0)
		{
			Log("[重保护周期] ⚠ 上一轮尚未完成（UI 线程忙），本轮跳过");
			return;
		}
		Dispatcher dispatcher = Application.Current?.Dispatcher;
		if (dispatcher == null || dispatcher.HasShutdownStarted)
		{
			Interlocked.Exchange(ref _reapplyCyclePending, 0);
			return;
		}
		if (!_reapplyCycleMigrationLogged)
		{
			_reapplyCycleMigrationLogged = true;
			Log("[重保护周期] ★ V2.7.43(用户9防死锁) 周期重应用已迁移至 UI 线程异步执行");
		}
		dispatcher.BeginInvoke((Action)delegate
		{
			try
			{
				Interlocked.Increment(ref _totalReapplyCycles);
				List<nint> list;
				lock (Lock)
				{
					list = new List<nint>(Protected);
				}
				foreach (nint current in list)
				{
					try
					{
						DesktopEmbedService.ReapplyProtection(current);
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
			finally
			{
				Interlocked.Exchange(ref _reapplyCyclePending, 0);
			}
		}, DispatcherPriority.Background);
	}

	private static void ScheduleStartupWarmup()
	{
		try
		{
			_warmupIndex = 0;
			_startupWarmupTimer?.Dispose();
			_startupWarmupTimer = new Timer(delegate
			{
				StartupWarmupTick();
			}, null, _warmupDelays[0], -1);
		}
		catch
		{
		}
	}

	private static void StartupWarmupTick()
	{
		try
		{
			ReapplyProtectionCycle();
		}
		catch
		{
		}
		finally
		{
			_warmupIndex++;
			if (_warmupIndex < _warmupDelays.Length)
			{
				int nextInterval = _warmupDelays[_warmupIndex] - _warmupDelays[_warmupIndex - 1];
				try
				{
					_startupWarmupTimer?.Change(nextInterval, -1);
				}
				catch
				{
					_startupWarmupTimer = null;
				}
			}
			else
			{
				_startupWarmupTimer?.Dispose();
				_startupWarmupTimer = null;
			}
		}
	}

	private static void PerformFullDwmWakeup(nint[] hwnds)
	{
	}

	private static void DiagnoseProtectionStatus()
	{
		try
		{
			StringBuilder sb = new StringBuilder();
			string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(35, 1, stringBuilder);
			handler.AppendLiteral("========== Win+D 防护诊断快照 ");
			handler.AppendFormatted(now);
			handler.AppendLiteral(" ==========");
			stringBuilder2.AppendLine(ref handler);
			sb.AppendLine();
			sb.AppendLine("【系统环境】");
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(6, 1, stringBuilder);
			handler.AppendLiteral("  OS: ");
			handler.AppendFormatted(Environment.OSVersion);
			stringBuilder3.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder4 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
			handler.AppendLiteral("  64位: ");
			handler.AppendFormatted(Environment.Is64BitOperatingSystem);
			stringBuilder4.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder5 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(8, 1, stringBuilder);
			handler.AppendLiteral("  .NET: ");
			handler.AppendFormatted(Environment.Version);
			stringBuilder5.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder6 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(8, 1, stringBuilder);
			handler.AppendLiteral("  进程位数: ");
			handler.AppendFormatted(Environment.Is64BitProcess ? "x64" : "x86");
			stringBuilder6.AppendLine(ref handler);
			sb.AppendLine();
			nint[] hwnds;
			lock (Lock)
			{
				hwnds = new nint[Protected.Count];
				Protected.CopyTo(hwnds);
			}
			stringBuilder = sb;
			StringBuilder stringBuilder7 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder);
			handler.AppendLiteral("【受保护窗口】共 ");
			handler.AppendFormatted(hwnds.Length);
			handler.AppendLiteral(" 个");
			stringBuilder7.AppendLine(ref handler);
			for (int i = 0; i < hwnds.Length; i++)
			{
				nint hwnd = hwnds[i];
				if (hwnd != IntPtr.Zero)
				{
					stringBuilder = sb;
					StringBuilder stringBuilder8 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(12, 2, stringBuilder);
					handler.AppendLiteral("  [");
					handler.AppendFormatted(i + 1);
					handler.AppendLiteral("] HWND=0x");
					handler.AppendFormatted(hwnd, "X8");
					stringBuilder8.AppendLine(ref handler);
					int cloaked = -1;
					NativeMethods.DwmGetWindowAttribute(hwnd, 14, out cloaked, 4);
					string cloakedDesc = cloaked switch
					{
						0 => "可见", 
						1 => "应用斗篷", 
						2 => "Shell斗篷(Win+D)", 
						_ => $"未知({cloaked})", 
					};
					stringBuilder = sb;
					StringBuilder stringBuilder9 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(23, 2, stringBuilder);
					handler.AppendLiteral("      DWMWA_CLOAKED=");
					handler.AppendFormatted(cloaked);
					handler.AppendLiteral(" (");
					handler.AppendFormatted(cloakedDesc);
					handler.AppendLiteral(")");
					stringBuilder9.AppendLine(ref handler);
					NativeMethods.WINDOWPLACEMENT wp = default(NativeMethods.WINDOWPLACEMENT);
					wp.length = Marshal.SizeOf(wp);
					if (NativeMethods.GetWindowPlacement(hwnd, ref wp))
					{
						string showCmdDesc = wp.showCmd switch
						{
							0 => "SW_HIDE(隐藏)", 
							1 => "SW_SHOWNORMAL", 
							2 => "SW_SHOWMINIMIZED", 
							3 => "SW_SHOWMAXIMIZED", 
							6 => "SW_MINIMIZE", 
							_ => $"其他({wp.showCmd})", 
						};
						stringBuilder = sb;
						StringBuilder stringBuilder10 = stringBuilder;
						handler = new StringBuilder.AppendInterpolatedStringHandler(59, 7, stringBuilder);
						handler.AppendLiteral("      GetWindowPlacement: showCmd=");
						handler.AppendFormatted(wp.showCmd);
						handler.AppendLiteral(" (");
						handler.AppendFormatted(showCmdDesc);
						handler.AppendLiteral(") ");
						handler.AppendLiteral("flags=");
						handler.AppendFormatted(wp.flags);
						handler.AppendLiteral(" rcNormal=(");
						handler.AppendFormatted(wp.rcNormalPosition.Left);
						handler.AppendLiteral(",");
						handler.AppendFormatted(wp.rcNormalPosition.Top);
						handler.AppendLiteral(" ");
						handler.AppendFormatted(wp.rcNormalPosition.Width);
						handler.AppendLiteral("x");
						handler.AppendFormatted(wp.rcNormalPosition.Height);
						handler.AppendLiteral(")");
						stringBuilder10.AppendLine(ref handler);
					}
					else
					{
						sb.AppendLine("      GetWindowPlacement: ❌ 失败");
					}
					int exStyle = NativeMethods.GetWindowLong(hwnd, -20);
					bool isLayered = (exStyle & 0x80000) != 0;
					bool isNoActivate = (exStyle & 0x8000000) != 0;
					bool isToolWindow = (exStyle & 0x80) != 0;
					stringBuilder = sb;
					StringBuilder stringBuilder11 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(48, 4, stringBuilder);
					handler.AppendLiteral("      WS_EX: 0x");
					handler.AppendFormatted(exStyle, "X8");
					handler.AppendLiteral(" LAYERED=");
					handler.AppendFormatted(isLayered);
					handler.AppendLiteral(" NOACTIVATE=");
					handler.AppendFormatted(isNoActivate);
					handler.AppendLiteral(" TOOLWINDOW=");
					handler.AppendFormatted(isToolWindow);
					stringBuilder11.AppendLine(ref handler);
					int style = NativeMethods.GetWindowLong(hwnd, -16);
					bool hasMinBox = (style & 0x20000) != 0;
					bool isVisible = (style & 0x10000000) != 0;
					stringBuilder = sb;
					StringBuilder stringBuilder12 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(36, 3, stringBuilder);
					handler.AppendLiteral("      WS:   0x");
					handler.AppendFormatted(style, "X8");
					handler.AppendLiteral(" MINIMIZEBOX=");
					handler.AppendFormatted(hasMinBox);
					handler.AppendLiteral(" VISIBLE=");
					handler.AppendFormatted(isVisible);
					stringBuilder12.AppendLine(ref handler);
					stringBuilder = sb;
					StringBuilder stringBuilder13 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(32, 2, stringBuilder);
					handler.AppendLiteral("      IsIconic=");
					handler.AppendFormatted(NativeMethods.IsIconic(hwnd));
					handler.AppendLiteral(" IsWindowVisible=");
					handler.AppendFormatted(NativeMethods.IsWindowVisible(hwnd));
					stringBuilder13.AppendLine(ref handler);
					int excludeFromPeek = -1;
					int hr = NativeMethods.DwmGetWindowAttribute(hwnd, 12, out excludeFromPeek, 4);
					string peekDesc = ((hr != 0) ? $"读取失败(hr=0x{hr:X8})" : ((excludeFromPeek == 1) ? "TRUE(1)" : $"FALSE({excludeFromPeek})"));
					stringBuilder = sb;
					StringBuilder stringBuilder14 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(31, 1, stringBuilder);
					handler.AppendLiteral("      DWMWA_EXCLUDED_FROM_PEEK=");
					handler.AppendFormatted(peekDesc);
					stringBuilder14.AppendLine(ref handler);
					sb.AppendLine();
				}
			}
			sb.AppendLine("【桌面嵌入 (DesktopEmbedService)】");
			stringBuilder = sb;
			StringBuilder stringBuilder15 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(15, 1, stringBuilder);
			handler.AppendLiteral("  DesktopOwner=");
			handler.AppendFormatted(DesktopEmbedService.GetDiagnosticInfo());
			stringBuilder15.AppendLine(ref handler);
			sb.AppendLine();
			sb.AppendLine("【事件监听 (SetWinEventHook)】");
			stringBuilder = sb;
			StringBuilder stringBuilder16 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(24, 2, stringBuilder);
			handler.AppendLiteral("  CLOAK Hook句柄=0x");
			handler.AppendFormatted(_winEventHook, "X8");
			handler.AppendLiteral(" 回调已注册=");
			handler.AppendFormatted(_winEventDelegate != null);
			stringBuilder16.AppendLine(ref handler);
			sb.AppendLine("  前台/菜单Hook=已禁用(V2.4.34，停止桌面焦点竞争)");
			stringBuilder = sb;
			StringBuilder stringBuilder17 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(17, 1, stringBuilder);
			handler.AppendLiteral("  WinEvent触发恢复次数=");
			handler.AppendFormatted(_totalWinEventRecoveries);
			stringBuilder17.AppendLine(ref handler);
			sb.AppendLine();
			sb.AppendLine("【恢复执行统计】");
			stringBuilder = sb;
			StringBuilder stringBuilder18 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(8, 1, stringBuilder);
			handler.AppendLiteral("  总恢复次数=");
			handler.AppendFormatted(_totalRecoveries);
			stringBuilder18.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder19 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder);
			handler.AppendLiteral("  Timer触发的恢复=");
			handler.AppendFormatted(_totalTimerRecoveries);
			stringBuilder19.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder20 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder);
			handler.AppendLiteral("  WinEvent触发的恢复=");
			handler.AppendFormatted(_totalWinEventRecoveries);
			stringBuilder20.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder21 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(29, 1, stringBuilder);
			handler.AppendLiteral("  Progman检测(ToggleDesktop恢复)=");
			handler.AppendFormatted(_totalProgmanRecoveries);
			stringBuilder21.AppendLine(ref handler);
			lock (_lastRecoveryTick)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder22 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder);
				handler.AppendLiteral("  冷却中窗口数=");
				handler.AppendFormatted(_lastRecoveryTick.Count);
				stringBuilder22.AppendLine(ref handler);
			}
			sb.AppendLine();
			sb.AppendLine("【定期保护属性重应用 (5s周期)】");
			stringBuilder = sb;
			StringBuilder stringBuilder23 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
			handler.AppendLiteral("  总周期数=");
			handler.AppendFormatted(_totalReapplyCycles);
			stringBuilder23.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder24 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder);
			handler.AppendLiteral("  实际修复所有者关系次数=");
			handler.AppendFormatted(_totalParentRestored);
			stringBuilder24.AppendLine(ref handler);
			sb.AppendLine();
			stringBuilder = sb;
			StringBuilder stringBuilder25 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(30, 2, stringBuilder);
			handler.AppendLiteral("【10ms恢复Timer】ticks=");
			handler.AppendFormatted(_tickCount);
			handler.AppendLiteral(" protected=");
			handler.AppendFormatted(Protected.Count);
			stringBuilder25.AppendLine(ref handler);
			sb.AppendLine();
			string logDir = App.GetAppDataDir();
			sb.AppendLine("【调试日志文件】");
			sb.AppendLine("  debug_wind_diag.log — 本文件（Win+D诊断快照）");
			sb.AppendLine("  debug_wndproc.log   — Win+D防护运行日志（Timer/恢复事件）");
			sb.AppendLine("  debug_embed.log     — 桌面嵌入初始化日志（WorkerW查找过程）");
			stringBuilder = sb;
			StringBuilder stringBuilder26 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(8, 1, stringBuilder);
			handler.AppendLiteral("  日志目录: ");
			handler.AppendFormatted(logDir);
			stringBuilder26.AppendLine(ref handler);
			sb.AppendLine();
			sb.AppendLine("========== 诊断快照结束 ==========");
			WriteDiagLog(sb.ToString());
			Log($"[诊断] 启动快照已写入 debug_wind_diag.log，共 {hwnds.Length} 个受保护窗口");
		}
		catch (Exception ex)
		{
			Log("[诊断] ⚠ 诊断快照写入异常: " + ex.Message);
		}
	}

	private static void WriteDiagLog(string text)
	{
		try
		{
			string dir = App.GetAppDataDir();
			if (!Directory.Exists(dir))
			{
				Directory.CreateDirectory(dir);
			}
			File.WriteAllText(Path.Combine(dir, "debug_wind_diag.log"), text, Encoding.UTF8);
		}
		catch
		{
		}
	}

	private static void Log(string message)
	{
		if (_logEnabled)
		{
			LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_wndproc.log"), $"{DateTime.Now:HH:mm:ss.fff} [WinDHook] {message}{Environment.NewLine}");
		}
	}
}
