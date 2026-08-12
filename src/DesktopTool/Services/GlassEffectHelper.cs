using System;
using System.Runtime.InteropServices;

namespace DesktopTool.Services;

internal static class GlassEffectHelper
{
	private enum AccentState
	{
		ACCENT_DISABLED,
		ACCENT_ENABLE_GRADIENT,
		ACCENT_ENABLE_TRANSPARENTGRADIENT,
		ACCENT_ENABLE_BLURBEHIND,
		ACCENT_ENABLE_ACRYLICBLURBEHIND,
		ACCENT_INVALID_STATE
	}

	private struct AccentPolicy
	{
		public AccentState AccentState;

		public int AccentFlags;

		public int GradientColor;

		public int AnimationId;
	}

	private struct WindowCompositionAttributeData
	{
		public WindowCompositionAttribute Attribute;

		public nint Data;

		public int SizeOfData;
	}

	private enum WindowCompositionAttribute
	{
		WCA_ACCENT_POLICY = 19
	}

	private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

	private const int DWMWCP_ROUND = 2;

	private const int DWMWA_BORDER_COLOR = 34;

	private const int DWMWA_COLOR_NONE = -2;

	private const int DefaultGlassTintABGR = -870704614;

	[DllImport("user32.dll")]
	private static extern int SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

	[DllImport("gdi32.dll")]
	private static extern nint CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

	[DllImport("user32.dll")]
	private static extern int SetWindowRgn(nint hWnd, nint hRgn, bool bRedraw);

	[DllImport("user32.dll")]
	private static extern int DeleteObject(nint hObject);

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

	[DllImport("user32.dll")]
	private static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(nint hwnd);

	public static void EnableBlur(nint hwnd)
	{
		ApplyAccent(hwnd, AccentState.ACCENT_ENABLE_BLURBEHIND, 0, 0);
	}

	public static void EnableBlur(nint hwnd, int tintAlpha)
	{
		int gradient = (Math.Max(0, Math.Min(255, tintAlpha)) << 24) | 0xFFFFFF;
		ApplyAccent(hwnd, AccentState.ACCENT_ENABLE_BLURBEHIND, gradient, 0);
	}

	public static void EnableAcrylicWithTint(nint hwnd)
	{
		ApplyAccent(hwnd, AccentState.ACCENT_ENABLE_BLURBEHIND, -870704614, 0);
	}

	public static void EnableAcrylicWithTint(nint hwnd, int gradientColorABGR)
	{
		ApplyAccent(hwnd, AccentState.ACCENT_ENABLE_BLURBEHIND, gradientColorABGR, 0);
	}

	public static void EnableAcrylic(nint hwnd)
	{
		try
		{
			if (ApplyAccent(hwnd, AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, -870704614, 2) == 0)
			{
				ApplyAccent(hwnd, AccentState.ACCENT_ENABLE_BLURBEHIND, -870704614, 0);
			}
		}
		catch
		{
			ApplyAccent(hwnd, AccentState.ACCENT_ENABLE_BLURBEHIND, -870704614, 0);
		}
	}

	private static void SetBorderColorNone(nint hwnd)
	{
		try
		{
			if (Environment.OSVersion.Version.Build >= 22000)
			{
				int borderColor = -2;
				DwmSetWindowAttribute(hwnd, 34, ref borderColor, 4);
			}
		}
		catch
		{
		}
	}

	private static int ApplyAccent(nint hwnd, AccentState state, int gradientColor, int accentFlags)
	{
		try
		{
			AccentPolicy structure = new AccentPolicy
			{
				AccentState = state,
				AccentFlags = accentFlags,
				GradientColor = gradientColor
			};
			int accentStructSize = Marshal.SizeOf(structure);
			nint accentPtr = Marshal.AllocHGlobal(accentStructSize);
			Marshal.StructureToPtr(structure, accentPtr, fDeleteOld: false);
			WindowCompositionAttributeData data = new WindowCompositionAttributeData
			{
				Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
				SizeOfData = accentStructSize,
				Data = accentPtr
			};
			int result = SetWindowCompositionAttribute(hwnd, ref data);
			Marshal.FreeHGlobal(accentPtr);
			if (state == AccentState.ACCENT_ENABLE_BLURBEHIND || state == AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND)
			{
				SetBorderColorNone(hwnd);
			}
			return result;
		}
		catch
		{
			return -1;
		}
	}

	public static void DisableBlur(nint hwnd)
	{
		try
		{
			AccentPolicy structure = new AccentPolicy
			{
				AccentState = AccentState.ACCENT_DISABLED
			};
			int accentStructSize = Marshal.SizeOf(structure);
			nint accentPtr = Marshal.AllocHGlobal(accentStructSize);
			Marshal.StructureToPtr(structure, accentPtr, fDeleteOld: false);
			WindowCompositionAttributeData data = new WindowCompositionAttributeData
			{
				Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
				SizeOfData = accentStructSize,
				Data = accentPtr
			};
			SetWindowCompositionAttribute(hwnd, ref data);
			Marshal.FreeHGlobal(accentPtr);
		}
		catch
		{
		}
	}

	public static void ApplyRoundCorners(nint hwnd, int width, int height, int cornerRadius = 10)
	{
		try
		{
			if (width > 0 && height > 0 && !TryEnableWin11RoundCorners(hwnd))
			{
				double scale = GetDpiScale(hwnd);
				int physWidth = (int)Math.Round((double)width * scale);
				int physHeight = (int)Math.Round((double)height * scale);
				int physRadius = (int)Math.Round((double)cornerRadius * scale);
				nint hRgn = CreateRoundRectRgn(0, 0, physWidth + 1, physHeight + 1, physRadius, physRadius);
				SetWindowRgn(hwnd, hRgn, bRedraw: true);
				InvalidateRect(hwnd, IntPtr.Zero, bErase: true);
			}
		}
		catch
		{
		}
	}

	private static double GetDpiScale(nint hwnd)
	{
		try
		{
			uint dpi = GetDpiForWindow(hwnd);
			if (dpi >= 48 && dpi <= 960)
			{
				return (double)dpi / 96.0;
			}
		}
		catch
		{
		}
		return 1.0;
	}

	private static bool TryEnableWin11RoundCorners(nint hwnd)
	{
		try
		{
			if (Environment.OSVersion.Version.Build < 22000)
			{
				return false;
			}
			int cornerPreference = 2;
			int num = DwmSetWindowAttribute(hwnd, 33, ref cornerPreference, 4);
			if (num == 0)
			{
				InvalidateRect(hwnd, IntPtr.Zero, bErase: true);
			}
			return num == 0;
		}
		catch
		{
			return false;
		}
	}
}
