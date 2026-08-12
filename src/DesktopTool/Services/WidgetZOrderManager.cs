using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace DesktopTool.Services;

public static class WidgetZOrderManager
{
	private static readonly object _lock = new object();

	private static readonly List<(nint hwnd, bool topLayer)> _widgets = new List<(nint, bool)>();

	private static readonly List<nint> _raised = new List<nint>();

	private static bool _settlePending;

	public static void Register(nint hwnd, bool topLayer)
	{
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		lock (_lock)
		{
			if (!_widgets.Exists(((nint hwnd, bool topLayer) w) => w.hwnd == hwnd))
			{
				_widgets.Add((hwnd, topLayer));
			}
		}
	}

	public static void Unregister(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		lock (_lock)
		{
			_widgets.RemoveAll(((nint hwnd, bool topLayer) w) => w.hwnd == hwnd);
			_raised.Remove(hwnd);
		}
	}

	public static void Raise(nint hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		bool changed;
		lock (_lock)
		{
			if (!_widgets.Exists(((nint hwnd, bool topLayer) w) => w.hwnd == hwnd))
			{
				_widgets.Add((hwnd, true));
			}
			changed = !_raised.Contains(hwnd);
			if (changed)
			{
				_raised.Add(hwnd);
			}
		}
		if (changed)
		{
			Apply();
			ScheduleSettle();
		}
	}

	public static void OnHoverExpandedChanged(nint hwnd, bool expanded)
	{
		if (expanded)
		{
			Raise(hwnd);
		}
		else
		{
			Restore(hwnd);
		}
	}

	public static void Restore(nint hwnd)
	{
		if (hwnd != IntPtr.Zero)
		{
			bool changed;
			lock (_lock)
			{
				changed = _raised.Remove(hwnd);
			}
			if (changed)
			{
				Apply();
				ScheduleSettle();
			}
		}
	}

	private static void ScheduleSettle()
	{
		if (!_settlePending)
		{
			_settlePending = true;
			Dispatcher.CurrentDispatcher.BeginInvoke((Action)delegate
			{
				_settlePending = false;
				Apply();
			}, DispatcherPriority.Background);
		}
	}

	private static void Apply()
	{
		List<(nint, bool)> widgets;
		List<nint> raised;
		lock (_lock)
		{
			widgets = new List<(nint, bool)>(_widgets);
			raised = new List<nint>(_raised);
		}
		if (raised.Count == 0)
		{
			return;
		}
		nint deskOwner = DesktopEmbedService.DesktopOwner;
		nint after = ((deskOwner != IntPtr.Zero) ? deskOwner : NativeMethods.HWND_BOTTOM);
		foreach (var w in widgets)
		{
			if (!raised.Contains(w.Item1))
			{
				NativeMethods.SetWindowPos(w.Item1, after, 0, 0, 0, 0, 19u);
			}
		}
		for (int i = raised.Count - 1; i >= 0; i--)
		{
			NativeMethods.SetWindowPos(raised[i], after, 0, 0, 0, 0, 19u);
		}
	}
}
