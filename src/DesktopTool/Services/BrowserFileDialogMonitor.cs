using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace DesktopTool.Services;

/// <summary>
/// Temporarily exposes attribute-hidden desktop items while a browser-owned
/// Open/Save common dialog is present, then restores the user's prior Explorer
/// visibility settings after the dialog closes.
/// </summary>
public sealed class BrowserFileDialogMonitor : IDisposable
{
	private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"chrome",
		"msedge",
		"firefox",
		"brave",
		"opera",
		"opera_gx",
		"vivaldi",
		"arc",
		"chromium",
		"thorium",
		"qqbrowser",
		"360chrome",
		"iexplore"
	};

	private readonly object _stateLock = new();
	private Timer? _timer;
	private bool _ownsTemporaryVisibility;
	private int _noDialogChecks;
	private int _checking;
	private volatile bool _disposed;

	public event Action<bool>? VisibilityChanged;

	public void Start()
	{
		lock (_stateLock)
		{
			if (_disposed || _timer != null)
			{
				return;
			}

			_timer = new Timer(CheckBrowserDialogs, null, TimeSpan.FromMilliseconds(250.0), TimeSpan.FromMilliseconds(600.0));
		}
	}

	private void CheckBrowserDialogs(object? state)
	{
		if (_disposed || Interlocked.Exchange(ref _checking, 1) != 0)
		{
			return;
		}

		try
		{
			bool dialogOpen = HasBrowserFileDialog(out List<nint> dialogWindows);
			if (dialogOpen)
			{
				_noDialogChecks = 0;
				if (!_ownsTemporaryVisibility && !DesktopIconService.IsTempShowHiddenActive && DesktopIconService.SetTemporaryShowHiddenFiles(show: true))
				{
					_ownsTemporaryVisibility = true;
					RefreshFileDialogs(dialogWindows);
					RaiseVisibilityChanged(showing: true);
				}
				return;
			}

			if (!_ownsTemporaryVisibility)
			{
				_noDialogChecks = 0;
				return;
			}

			// Debounce briefly so switching between two browser file dialogs does
			// not flash the desktop icons between them.
			_noDialogChecks++;
			if (_noDialogChecks >= 2 && DesktopIconService.SetTemporaryShowHiddenFiles(show: false))
			{
				_ownsTemporaryVisibility = false;
				_noDialogChecks = 0;
				RaiseVisibilityChanged(showing: false);
			}
		}
		catch
		{
		}
		finally
		{
			Volatile.Write(ref _checking, 0);
		}
	}

	private static bool HasBrowserFileDialog(out List<nint> dialogWindows)
	{
		bool found = false;
		dialogWindows = new List<nint>();
		List<nint> foundWindows = dialogWindows;
		NativeMethods.EnumWindows(delegate(nint hwnd, nint lParam)
		{
			if (!NativeMethods.IsWindowVisible(hwnd))
			{
				return true;
			}

			StringBuilder className = new(64);
			if (NativeMethods.GetClassName(hwnd, className, className.Capacity) <= 0 || !string.Equals(className.ToString(), "#32770", StringComparison.Ordinal))
			{
				return true;
			}

			NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
			if (processId == 0)
			{
				return true;
			}

			try
			{
				using Process process = Process.GetProcessById((int)processId);
				if (BrowserProcessNames.Contains(process.ProcessName))
				{
					found = true;
					foundWindows.Add(hwnd);
				}
			}
			catch
			{
			}

			return true;
		}, IntPtr.Zero);
		return found;
	}

	private static void RefreshFileDialogs(IEnumerable<nint> dialogWindows)
	{
		foreach (nint hwnd in dialogWindows)
		{
			try
			{
				NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYDOWN, new IntPtr(NativeMethods.VK_F5), IntPtr.Zero);
				NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYUP, new IntPtr(NativeMethods.VK_F5), IntPtr.Zero);
			}
			catch
			{
			}
		}
	}

	private void RaiseVisibilityChanged(bool showing)
	{
		try
		{
			VisibilityChanged?.Invoke(showing);
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		Timer? timer;
		lock (_stateLock)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			timer = _timer;
			_timer = null;
		}

		if (timer != null)
		{
			using ManualResetEvent callbacksFinished = new(false);
			if (timer.Dispose(callbacksFinished))
			{
				callbacksFinished.WaitOne(TimeSpan.FromSeconds(10.0));
			}
		}

		if (_ownsTemporaryVisibility)
		{
			DesktopIconService.SetTemporaryShowHiddenFiles(show: false);
			_ownsTemporaryVisibility = false;
			RaiseVisibilityChanged(showing: false);
		}
	}
}
