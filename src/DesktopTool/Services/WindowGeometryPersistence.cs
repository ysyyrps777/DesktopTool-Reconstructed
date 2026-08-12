using System;

namespace DesktopTool.Services;

public static class WindowGeometryPersistence
{
	public static double GetPrimaryDpiScale()
	{
		try
		{
			nint hMon = NativeMethods.MonitorFromWindow(IntPtr.Zero, 1u);
			if (hMon != IntPtr.Zero && NativeMethods.GetDpiForMonitor(hMon, 0, out var dpiX, out var _) == 0)
			{
				return (double)dpiX / 96.0;
			}
		}
		catch
		{
		}
		return 1.0;
	}

	public static double ToPhysical(double dip)
	{
		return dip * GetPrimaryDpiScale();
	}

	public static double ToDip(double physical)
	{
		return physical / GetPrimaryDpiScale();
	}

	public static (double Left, double Top) ClampToVisibleScreen(double left, double top, double width, double height)
	{
		if (DesktopEmbedService.IsOnAnyScreen(left, top, width, height))
		{
			return (Left: left, Top: top);
		}
		var (cx, cy) = DesktopEmbedService.GetPrimaryScreenCenter(width, height, GetPrimaryDpiScale());
		return (Left: cx, Top: cy);
	}
}
