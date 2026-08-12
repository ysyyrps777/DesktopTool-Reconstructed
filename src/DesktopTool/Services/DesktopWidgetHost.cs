using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopTool.Services;

public sealed class DesktopWidgetHost : IWidgetHost, IDisposable
{
	private readonly HwndSource _source;

	private readonly FrameworkElement _content;

	private bool _disposed;

	public bool IsDragging { get; set; }

	public bool AllowMouseActivate { get; set; }

	public nint HostHwnd => _source.Handle;

	public Window? OwnerForDialogs => Application.Current.MainWindow;

	public bool IsValid
	{
		get
		{
			if (!_disposed)
			{
				return NativeMethods.IsWindow(_source.Handle);
			}
			return false;
		}
	}

	public double Left
	{
		get
		{
			return GetRect().Left;
		}
		set
		{
			SetBounds(value, Top, Width, Height);
		}
	}

	public double Top
	{
		get
		{
			return GetRect().Top;
		}
		set
		{
			SetBounds(Left, value, Width, Height);
		}
	}

	public double Width
	{
		get
		{
			return GetRect().Width;
		}
		set
		{
			SetBounds(Left, Top, value, Height);
		}
	}

	public double Height
	{
		get
		{
			return GetRect().Height;
		}
		set
		{
			SetBounds(Left, Top, Width, value);
		}
	}

	public double DpiScaleY
	{
		get
		{
			try
			{
				return (double)NativeMethods.GetDpiForWindow(HostHwnd) / 96.0;
			}
			catch
			{
				return 1.0;
			}
		}
	}

	public bool IsVisible
	{
		get
		{
			if (!_disposed)
			{
				return NativeMethods.IsWindowVisible(_source.Handle);
			}
			return false;
		}
	}

	public event EventHandler? LocationChanged;

	public event EventHandler? SizeChanged;

	public event EventHandler? Deactivated;

	public event EventHandler? Closed;

	public DesktopWidgetHost(FrameworkElement content, double left, double top, double width, double height)
	{
		_content = content ?? throw new ArgumentNullException("content");
		HwndSourceParameters ps = new HwndSourceParameters("DesktopToolWidget")
		{
			PositionX = (int)Math.Round(left),
			PositionY = (int)Math.Round(top),
			Width = (int)Math.Round(width),
			Height = (int)Math.Round(height),
			UsesPerPixelTransparency = true,
			WindowStyle = 0,
			ExtendedWindowStyle = 134742016
		};
		_source = new HwndSource(ps);
		_source.CompositionTarget.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
		if (VisualTreeHelper.GetParent(content) is AdornerDecorator oldDecorator)
		{
			oldDecorator.Child = null;
		}
		_source.RootVisual = new AdornerDecorator
		{
			Child = content
		};
		_source.AddHook(WndProc);
		_content.SizeChanged += delegate
		{
			this.SizeChanged?.Invoke(this, EventArgs.Empty);
		};
		ApplyDesktopWidgetStyles(_source.Handle);
		DesktopEmbedService.RegisterDesktopWindow(_source.Handle);
		_source.CompositionTarget.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
		PlaceAboveDesktop();
		Show();
		try
		{
			int afterEx = NativeMethods.GetWindowLong(_source.Handle, -20);
			LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_embed.log"), $"{DateTime.Now:HH:mm:ss.fff} [Widget] 映射区宿主 hwnd=0x{((IntPtr)_source.Handle).ToInt64():X} exStyle=0x{afterEx:X8} NOACTIVATE={(((afterEx & 0x8000000) != 0) ? "ON" : "OFF")}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	private static void ApplyDesktopWidgetStyles(nint hwnd)
	{
		DesktopWidgetInitializer.ApplyStyle(hwnd, DesktopWidgetInitializer.Default);
	}

	private void PlaceAboveDesktop()
	{
		if (_source != null && _source.Handle != IntPtr.Zero)
		{
			nint owner = DesktopEmbedService.DesktopOwner;
			if (owner != IntPtr.Zero)
			{
				NativeMethods.SetWindowPos(_source.Handle, owner, 0, 0, 0, 0, 19u);
			}
		}
	}

	private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		bool flag;
		switch (msg)
		{
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
		switch (msg)
		{
		case 33:
			handled = true;
			if (!AllowMouseActivate)
			{
				return 3;
			}
			return 1;
		case 2:
			handled = true;
			return IntPtr.Zero;
		case 16:
			if (DesktopEmbedService.ShouldCancelUserClose(hwnd))
			{
				DesktopEmbedService.HandleTaskbarRecreated();
				handled = true;
			}
			else
			{
				Close();
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
		if (msg == 28 && wParam == IntPtr.Zero && !NativeMethods.IsWindowVisible(hwnd))
		{
			NativeMethods.ShowWindow(hwnd, 4);
		}
		if (msg == 6 && wParam == IntPtr.Zero)
		{
			this.Deactivated?.Invoke(this, EventArgs.Empty);
		}
		return IntPtr.Zero;
	}

	private NativeMethods.RECT GetRect()
	{
		NativeMethods.GetWindowRect(_source.Handle, out var r);
		return r;
	}

	public void SetBounds(double left, double top, double width, double height)
	{
		if (_disposed)
		{
			return;
		}
		nint after;
		if (IsDragging && (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0)
		{
			after = ((AppPanelTracker.TopAppPanelHwnd != IntPtr.Zero) ? AppPanelTracker.TopAppPanelHwnd : NativeMethods.HWND_TOP);
		}
		else
		{
			after = ((AppPanelTracker.TopAppPanelHwnd != IntPtr.Zero) ? AppPanelTracker.TopAppPanelHwnd : ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM));
			if (after == DesktopEmbedService.DesktopOwner && DesktopEmbedService.DesktopOwner != IntPtr.Zero && NativeMethods.GetWindowLongPtr(_source.Handle, (int)NativeMethods.GWLP_HWNDPARENT) == IntPtr.Zero)
			{
				after = NativeMethods.HWND_TOP;
			}
		}
		if (after != NativeMethods.HWND_TOP && after != NativeMethods.HWND_BOTTOM && after != NativeMethods.HWND_TOPMOST && after != NativeMethods.HWND_NOTOPMOST && !NativeMethods.IsWindow(after))
		{
			nint heal = DesktopEmbedService.DesktopOwner;
			after = ((heal != IntPtr.Zero && NativeMethods.IsWindow(heal)) ? heal : NativeMethods.HWND_BOTTOM);
		}
		AppPanelTracker.TraceZ("SetBounds", _source.Handle, after);
		int tgtL = (int)Math.Round(left);
		int tgtT = (int)Math.Round(top);
		int tgtW = (int)Math.Round(width);
		int tgtH = (int)Math.Round(height);
		if (!NativeMethods.SetWindowPos(_source.Handle, after, tgtL, tgtT, tgtW, tgtH, 16u))
		{
			NativeMethods.SetWindowPos(_source.Handle, IntPtr.Zero, (int)Math.Round(left), (int)Math.Round(top), (int)Math.Round(width), (int)Math.Round(height), 20u);
		}
		this.LocationChanged?.Invoke(this, EventArgs.Empty);
	}

	public void ResizeOnly(double width, double height)
	{
		if (!_disposed)
		{
			NativeMethods.RECT r = GetRect();
			NativeMethods.SetWindowPos(_source.Handle, IntPtr.Zero, r.Left, r.Top, (int)Math.Round(width), (int)Math.Round(height), 20u);
		}
	}

	public void SetBoundsScreen(double screenLeft, double screenTop, double width, double height)
	{
		if (!_disposed)
		{
			SetBounds(screenLeft, screenTop, width, height);
		}
	}

	public void Show()
	{
		if (!_disposed)
		{
			NativeMethods.ShowWindow(_source.Handle, 4);
		}
	}

	public void Hide()
	{
		if (!_disposed)
		{
			NativeMethods.ShowWindow(_source.Handle, 0);
		}
	}

	public void ForceTopMost()
	{
		if (!_disposed)
		{
			NativeMethods.SetWindowPos(_source.Handle, NativeMethods.HWND_TOP, 0, 0, 0, 0, 19u);
		}
	}

	public void BringToTop()
	{
		if (!_disposed)
		{
			nint band = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
			AppPanelTracker.PlaceDesktopWindow(_source.Handle, band);
		}
	}

	public void Close()
	{
		Close(raiseClosed: true);
	}

	public void Close(bool raiseClosed)
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		if (raiseClosed)
		{
			try
			{
				this.Closed?.Invoke(this, EventArgs.Empty);
			}
			catch
			{
			}
		}
		try
		{
			DesktopEmbedService.UnregisterDesktopWindow(_source.Handle);
		}
		catch
		{
		}
		try
		{
			_source.Dispose();
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		Close();
	}
}
