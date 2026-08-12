using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace DesktopTool.Services;

public static class ContextMenuAutoCloser
{
	private sealed class Tracker : IDisposable
	{
		private readonly Func<bool> _isOpen;

		private readonly Action _close;

		private readonly Dispatcher _dispatcher;

		private readonly int _pid = Process.GetCurrentProcess().Id;

		private WinEventDelegate? _proc;

		private nint _hook;

		public Tracker(Func<bool> isOpen, Action close, Dispatcher dispatcher)
		{
			_isOpen = isOpen;
			_close = close;
			_dispatcher = dispatcher;
			_proc = OnWinEvent;
			_hook = SetWinEventHook(3u, 3u, IntPtr.Zero, _proc, 0u, 0u, 2u);
		}

		private void OnWinEvent(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
		{
			if (hwnd == IntPtr.Zero)
			{
				return;
			}
			GetWindowThreadProcessId(hwnd, out var fgPid);
			if (fgPid == _pid || fgPid == 0)
			{
				return;
			}
			_dispatcher.BeginInvoke((Action)delegate
			{
				if (_isOpen())
				{
					_close();
				}
			}, DispatcherPriority.Normal);
		}

		public void Dispose()
		{
			if (_hook != IntPtr.Zero)
			{
				UnhookWinEvent(_hook);
				_hook = IntPtr.Zero;
			}
			_proc = null;
		}
	}

	private delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

	private static readonly Dictionary<object, Tracker> _trackers = new Dictionary<object, Tracker>();

	private const uint EVENT_SYSTEM_FOREGROUND = 3u;

	private const uint WINEVENT_OUTOFCONTEXT = 0u;

	private const uint WINEVENT_SKIPOWNTHREAD = 2u;

	public static void Attach(ContextMenu? menu)
	{
		if (menu != null)
		{
			Attach(menu, () => menu.IsOpen, delegate
			{
				menu.IsOpen = false;
			}, menu.Dispatcher);
		}
	}

	public static void Attach(Popup? popup)
	{
		if (popup != null)
		{
			Attach(popup, () => popup.IsOpen, delegate
			{
				popup.IsOpen = false;
			}, popup.Dispatcher);
		}
	}

	private static void Attach(object target, Func<bool> isOpen, Action close, Dispatcher dispatcher)
	{
		lock (_trackers)
		{
			if (!_trackers.ContainsKey(target))
			{
				_trackers[target] = new Tracker(isOpen, close, dispatcher);
			}
		}
	}

	public static void Detach(ContextMenu? menu)
	{
		Detach((object?)menu);
	}

	public static void Detach(Popup? popup)
	{
		Detach((object?)popup);
	}

	private static void Detach(object? target)
	{
		if (target == null)
		{
			return;
		}
		Tracker t = null;
		lock (_trackers)
		{
			if (_trackers.TryGetValue(target, out t))
			{
				_trackers.Remove(target);
			}
		}
		t?.Dispose();
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnhookWinEvent(nint hWinEventHook);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);
}
