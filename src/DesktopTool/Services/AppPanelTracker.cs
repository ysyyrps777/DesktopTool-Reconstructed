using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DesktopTool.Services;

public static class AppPanelTracker
{
	private static readonly Stack<nint> _panels = new Stack<nint>();

	private static DateTime _lastActivationTime = DateTime.MinValue;

	private static nint _lastActivationHwnd = IntPtr.Zero;

	private static DateTime _lastBottomTime = DateTime.MinValue;

	private static DateTime _lastRegisterTime = DateTime.MinValue;

	private static nint _lastRegisterHwnd = IntPtr.Zero;

	public static nint TopAppPanelHwnd
	{
		get
		{
			while (_panels.Count > 0 && !NativeMethods.IsWindow(_panels.Peek()))
			{
				nint dead = _panels.Pop();
				Log($"[Panel] TopAppPanelHwnd 清理死句柄 0x{dead:X8}（面板已销毁但未注销）");
			}
			if (_panels.Count <= 0)
			{
				return IntPtr.Zero;
			}
			return _panels.Peek();
		}
	}

	public static void Register(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		if (hwnd == _lastRegisterHwnd && (DateTime.Now - _lastRegisterTime).TotalMilliseconds < 300.0)
		{
			List<nint> list = new List<nint>(_panels);
			list.Remove(hwnd);
			_panels.Clear();
			foreach (nint h in list)
			{
				_panels.Push(h);
			}
			_panels.Push(hwnd);
			_lastActivationTime = DateTime.Now;
			_lastActivationHwnd = hwnd;
			_lastRegisterTime = DateTime.Now;
			return;
		}
		_lastRegisterTime = DateTime.Now;
		_lastRegisterHwnd = hwnd;
		List<nint> list2 = new List<nint>(_panels);
		list2.Remove(hwnd);
		_panels.Clear();
		foreach (nint h2 in list2)
		{
			_panels.Push(h2);
		}
		_panels.Push(hwnd);
		_lastActivationTime = DateTime.Now;
		_lastActivationHwnd = hwnd;
		NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, 19u);
		Log($"[Panel] Register 0x{hwnd:X8} → 仅面板置顶，挂件 Z 序保持不动");
	}

	public static void Unregister(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		List<nint> list = new List<nint>(_panels);
		list.Remove(hwnd);
		_panels.Clear();
		foreach (nint h in list)
		{
			_panels.Push(h);
		}
		if (TopAppPanelHwnd != IntPtr.Zero)
		{
			PushWidgetsToBottom();
			Log($"[Panel] Unregister 0x{hwnd:X8} → 仍有面板 0x{TopAppPanelHwnd:X8}，挂件回落桌面层级");
		}
		else
		{
			PushWidgetsToBottom();
			Log($"[Panel] Unregister 0x{hwnd:X8} → 无打开面板，挂件回落 HWND_BOTTOM（桌面层级，强制绕过去重）");
		}
	}

	public static void OnPanelDeactivated(nint hwnd)
	{
		if (hwnd == IntPtr.Zero || hwnd != TopAppPanelHwnd)
		{
			return;
		}
		if (hwnd == _lastActivationHwnd)
		{
			double elapsed = (DateTime.Now - _lastActivationTime).TotalMilliseconds;
			if (elapsed < 800.0)
			{
				Log($"[Panel] OnPanelDeactivated 0x{hwnd:X8} ⚡ 风暴防抖跳过（距 Register {elapsed:F0}ms < 800ms）");
				return;
			}
		}
		TryPushWidgetsToBottom();
		Log($"[Panel] OnPanelDeactivated 0x{hwnd:X8} → 挂件回落 HWND_BOTTOM（面板失焦，避免遮挡其它应用）");
	}

	public static void PlaceDesktopWindow(nint hwnd, nint restingTarget)
	{
		if (hwnd != IntPtr.Zero)
		{
			nint insertAfter = restingTarget;
			if (insertAfter != NativeMethods.HWND_TOP && insertAfter != NativeMethods.HWND_BOTTOM && insertAfter != NativeMethods.HWND_TOPMOST && insertAfter != NativeMethods.HWND_NOTOPMOST && !NativeMethods.IsWindow(insertAfter))
			{
				nint heal = DesktopEmbedService.DesktopOwner;
				nint healed = ((heal != IntPtr.Zero && NativeMethods.IsWindow(heal)) ? heal : NativeMethods.HWND_BOTTOM);
				Log($"[Z][自愈] PlaceDesktopWindow 检测到死句柄 insertAfter=0x{insertAfter:X8} → 0x{healed:X8}");
				insertAfter = healed;
			}
			TraceZ("PlaceDesktopWindow", hwnd, insertAfter);
			if (!NativeMethods.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, 16403u))
			{
				Log($"[Z][诊断] ❌ PlaceDesktopWindow SetWindowPos 失败 hwnd=0x{hwnd:X8} insertAfter=0x{insertAfter:X8}");
			}
		}
	}

	public static void TraceZ(string tag, nint hwnd, nint insertAfter)
	{
	}

	private static void TryPushWidgetsToBottom()
	{
		double elapsed = (DateTime.Now - _lastBottomTime).TotalMilliseconds;
		if (elapsed < 1000.0)
		{
			Log($"[Panel] PushToBottom ⚡ 去重跳过（距上次 {elapsed:F0}ms < 1000ms）");
		}
		else
		{
			_lastBottomTime = DateTime.Now;
			PushWidgetsToBottom();
		}
	}

	private static void PushWidgetsToBottom()
	{
		uint pid = (uint)Process.GetCurrentProcess().Id;
		nint deskOwner = DesktopEmbedService.DesktopOwner;
		nint insertAfter = ((deskOwner != IntPtr.Zero) ? deskOwner : NativeMethods.HWND_BOTTOM);
		List<nint> targets = new List<nint>();
		NativeMethods.EnumWindows(delegate(nint h, nint lParam)
		{
			NativeMethods.GetWindowThreadProcessId(h, out var lpdwProcessId);
			if (lpdwProcessId == pid && !_panels.Contains(h))
			{
				targets.Add(h);
			}
			return true;
		}, IntPtr.Zero);
		if (targets.Count == 0)
		{
			return;
		}
		Application app = Application.Current;
		if (app?.Dispatcher != null)
		{
			app.Dispatcher.BeginInvoke((Action)delegate
			{
				foreach (nint item in targets)
				{
					NativeMethods.SetWindowPos(item, insertAfter, 0, 0, 0, 0, 16403u);
				}
			}, DispatcherPriority.Background);
			return;
		}
		foreach (nint item2 in targets)
		{
			NativeMethods.SetWindowPos(item2, insertAfter, 0, 0, 0, 0, 16403u);
		}
	}

	private static void Log(string message)
	{
		try
		{
			LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_panel_zorder.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}
}
