using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace DesktopTool.Services;

public static class NativeDragHelper
{
	public interface INativeDragHost
	{
		nint Hwnd { get; }

		bool IsRealChild { get; }

		nint DesktopOwner { get; }

		UIElement DragElement { get; }

		double HostMinWidth { get; }

		double HostMinHeight { get; }

		void BringToTop();

		void SetSuppressSave(bool suppress);

		void SetPosition(int x, int y, int w, int h);

		void OnDragCommitted(int resizeHit);

		void OnResizeGlowTick();

		void OnResizeGlowEnd();
	}

	private static readonly string _dragLog = Path.Combine(App.GetAppDataDir(), "debug_drag.log");

	public static Rect GetDipRect(nint hwnd)
	{
		if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var r))
		{
			return Rect.Empty;
		}
		double dpi = 1.0;
		try
		{
			nint mon = NativeMethods.MonitorFromWindow(hwnd, 2u);
			if (mon != IntPtr.Zero)
			{
				NativeMethods.GetDpiForMonitor(mon, 0, out var dx, out var _);
				if (dx != 0)
				{
					dpi = (double)dx / 96.0;
				}
			}
		}
		catch
		{
		}
		if (dpi <= 0.0)
		{
			dpi = 1.0;
		}
		return new Rect((double)r.Left / dpi, (double)r.Top / dpi, (double)r.Width / dpi, (double)r.Height / dpi);
	}

	public static void Begin(INativeDragHost host, int resizeHit)
	{
		nint hwnd = host.Hwnd;
		if (hwnd == IntPtr.Zero || host.DragElement == null || Mouse.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}
		WinDHookService.NotifyProtectedWindowInteraction();
		NativeMethods.GetCursorPos(out var startCursor);
		NativeMethods.GetWindowRect(hwnd, out var startRect);
		NativeMethods.RECT originalRect = startRect;
		bool isRealChild = host.IsRealChild;
		host.SetSuppressSave(suppress: true);
		host.BringToTop();
		UIElement el = host.DragElement;
		bool ownerIsProgman = DesktopEmbedService.IsProgmanOwned(hwnd);
		var (parentLeft, parentTop) = DesktopEmbedService.GetOwnerOrigin(hwnd);
		try
		{
			nint ownerH = DesktopEmbedService.GetOwnerHwnd(hwnd);
			StringBuilder sb = new StringBuilder(64);
			string ownerClass = ((ownerH != IntPtr.Zero && NativeMethods.GetClassName(ownerH, sb, sb.Capacity) > 0) ? sb.ToString() : "<null>");
			DragDiag($"[拖拽帧诊断] hwnd=0x{hwnd:X8} ownerIsProgman={ownerIsProgman} ownerClass={ownerClass} parentLeft={parentLeft} parentTop={parentTop}");
		}
		catch (Exception ex)
		{
			DragDiag("[拖拽帧诊断-异常] " + ex.Message);
		}
		nint desktopBottom = ((DesktopEmbedService.DesktopOwner != IntPtr.Zero) ? DesktopEmbedService.DesktopOwner : NativeMethods.HWND_BOTTOM);
		DesktopEmbedService.IsAnyDragActive = true;
		nint prevOwner = NativeMethods.GetWindowLongPtr(hwnd, (int)NativeMethods.GWLP_HWNDPARENT);
		bool detachedOwner = false;
		if (ownerIsProgman && prevOwner != IntPtr.Zero)
		{
			NativeMethods.SetWindowLongPtr(hwnd, (int)NativeMethods.GWLP_HWNDPARENT, IntPtr.Zero);
			NativeMethods.SetWindowPos(hwnd, desktopBottom, 0, 0, 0, 0, 51u);
			detachedOwner = true;
			DragLog($"[拖拽开始-剥离owner] hwnd=0x{hwnd:X8} prevOwner=0x{prevOwner:X8}");
		}
		NativeMethods.GetWindowRect(hwnd, out startRect);
		NativeMethods.GetCursorPos(out startCursor);
		try
		{
			Mouse.Capture(el, CaptureMode.Element);
		}
		catch
		{
		}
		bool cleaned = false;
		MouseEventHandler onMove = null;
		MouseButtonEventHandler onUp = null;
		MouseEventHandler onLost = null;
		int moveCount = 0;
		onMove = delegate
		{
			if ((NativeMethods.GetAsyncKeyState(1) & 0x8000) == 0)
			{
				DragLog($"[拖拽-物理键态收尾] hwnd=0x{hwnd:X8} 左键已松开但拖拽态残留，强制 Cleanup");
				Cleanup();
			}
			else
			{
				NativeMethods.GetCursorPos(out var lpPoint);
				int num = lpPoint.X - startCursor.X;
				int num2 = lpPoint.Y - startCursor.Y;
				int num3 = startRect.Left;
				int num4 = startRect.Top;
				int num5 = startRect.Width;
				int num6 = startRect.Height;
				if (resizeHit == 0)
				{
					num3 = startRect.Left + num;
					num4 = startRect.Top + num2;
					num5 = 0;
					num6 = 0;
				}
				else
				{
					switch (resizeHit)
					{
					case 10:
						num3 = startRect.Right + num - startRect.Width;
						num5 = startRect.Width - num;
						break;
					case 11:
						num5 = startRect.Width + num;
						break;
					case 12:
						num4 = startRect.Bottom + num2 - startRect.Height;
						num6 = startRect.Height - num2;
						break;
					case 15:
						num6 = startRect.Height + num2;
						break;
					case 13:
						num3 = startRect.Right + num - startRect.Width;
						num5 = startRect.Width - num;
						num4 = startRect.Bottom + num2 - startRect.Height;
						num6 = startRect.Height - num2;
						break;
					case 14:
						num5 = startRect.Width + num;
						num4 = startRect.Bottom + num2 - startRect.Height;
						num6 = startRect.Height - num2;
						break;
					case 16:
						num3 = startRect.Right + num - startRect.Width;
						num5 = startRect.Width - num;
						num6 = startRect.Height + num2;
						break;
					case 17:
						num5 = startRect.Width + num;
						num6 = startRect.Height + num2;
						break;
					}
					double num7 = PresentationSource.FromVisual(el)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
					if (num7 <= 0.0)
					{
						num7 = 1.0;
					}
					int num8 = (int)(host.HostMinWidth * num7);
					int num9 = (int)(host.HostMinHeight * num7);
					if (num5 < num8)
					{
						num5 = num8;
						num3 = startRect.Right - num8;
					}
					if (num6 < num9)
					{
						num6 = num9;
						num4 = startRect.Bottom - num9;
					}
				}
				num3 -= parentLeft;
				num4 -= parentTop;
				host.SetPosition(num3, num4, num5, num6);
				if (resizeHit != 0)
				{
					host.OnResizeGlowTick();
				}
				if (++moveCount % 20 == 0)
				{
					NativeMethods.GetWindowRect(hwnd, out var lpRect);
					DragLog($"[拖拽中#{moveCount}] hwnd=0x{hwnd:X8} isRealChild={isRealChild} ownerDetached={detachedOwner} k=({parentLeft},{parentTop}) cursor=({lpPoint.X},{lpPoint.Y}) target=({num3},{num4}) actual=({lpRect.Left},{lpRect.Top})");
				}
			}
		};
		onUp = delegate
		{
			Cleanup();
		};
		onLost = delegate
		{
			Cleanup();
		};
		el.MouseMove += onMove;
		el.MouseLeftButtonUp += onUp;
		el.LostMouseCapture += onLost;
		void Cleanup()
		{
			if (!cleaned)
			{
				cleaned = true;
				el.MouseMove -= onMove;
				el.MouseLeftButtonUp -= onUp;
				el.LostMouseCapture -= onLost;
				try
				{
					Mouse.Capture(el, CaptureMode.None);
				}
				catch
				{
				}
				host.SetSuppressSave(suppress: false);
				if (moveCount > 0)
				{
					try
					{
						host.OnDragCommitted(resizeHit);
					}
					catch (Exception ex2)
					{
						DragLog($"[OnDragCommitted-异常] hwnd=0x{hwnd:X8} ex={ex2.GetType().Name}:{ex2.Message}");
					}
					if (detachedOwner && NativeMethods.IsWindow(hwnd))
					{
						nint owner = ((prevOwner != IntPtr.Zero && NativeMethods.IsWindow(prevOwner)) ? prevOwner : NativeMethods.FindWindow("Progman", null));
						if (owner != IntPtr.Zero)
						{
							NativeMethods.SetWindowLongPtr(hwnd, (int)NativeMethods.GWLP_HWNDPARENT, owner);
							NativeMethods.SetWindowPos(hwnd, desktopBottom, 0, 0, 0, 0, 19u);
							DragLog($"[拖拽结束-还原owner] hwnd=0x{hwnd:X8} owner=0x{owner:X8}");
							try
							{
								NativeMethods.RECT rr = default(NativeMethods.RECT);
								NativeMethods.GetWindowRect(hwnd, out rr);
								DragLog($"[还原后复核] hwnd=0x{hwnd:X8} 实际窗=({rr.Left},{rr.Top})");
							}
							catch
							{
							}
						}
					}
				}
				else
				{
					if (detachedOwner && NativeMethods.IsWindow(hwnd))
					{
						nint owner2 = ((prevOwner != IntPtr.Zero && NativeMethods.IsWindow(prevOwner)) ? prevOwner : NativeMethods.FindWindow("Progman", null));
						if (owner2 != IntPtr.Zero)
						{
							NativeMethods.SetWindowLongPtr(hwnd, (int)NativeMethods.GWLP_HWNDPARENT, owner2);
							NativeMethods.SetWindowPos(hwnd, desktopBottom, 0, 0, 0, 0, 19u);
						}
					}
					NativeMethods.SetWindowPos(hwnd, desktopBottom, originalRect.Left, originalRect.Top, 0, 0, 17u);
					DragLog($"[拖拽结束-零位移复原-桌面带] hwnd=0x{hwnd:X8} original=({originalRect.Left},{originalRect.Top})");
				}
				if (resizeHit != 0)
				{
					host.OnResizeGlowEnd();
				}
				DesktopEmbedService.IsAnyDragActive = false;
			}
		}
	}

	private static void DragLog(string msg)
	{
		try
		{
			File.AppendAllText(_dragLog, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	private static void DragDiag(string msg)
	{
		try
		{
			File.AppendAllText(_dragLog, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
		}
		catch
		{
		}
	}
}
