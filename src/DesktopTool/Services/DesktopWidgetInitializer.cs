using System;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopTool.Services;

public static class DesktopWidgetInitializer
{
	public record Options(bool ToolWindow = false, bool NoActivate = true, bool RemoveMinimizeBox = true);

	public static readonly Options Default = new Options();

	public static void ApplyStyle(nint hwnd, Options opt)
	{
		int ex = NativeMethods.GetWindowLong(hwnd, -20);
		if (opt.NoActivate)
		{
			ex |= 0x8000000;
		}
		if (opt.ToolWindow)
		{
			ex |= 0x80;
		}
		NativeMethods.SetWindowLong(hwnd, -20, ex);
		if (opt.RemoveMinimizeBox)
		{
			int style = NativeMethods.GetWindowLong(hwnd, -16);
			NativeMethods.SetWindowLong(hwnd, -16, style & -131073);
		}
		NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 51u);
	}

	public static void Register(nint hwnd, HwndSource source, Options opt)
	{
		ApplyStyle(hwnd, opt);
		DesktopEmbedService.RegisterDesktopWindow(hwnd);
		if (source != null)
		{
			source.CompositionTarget.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
		}
		PlaceAboveDesktop(hwnd);
		DesktopEmbedService.WakeDwmComposition(hwnd);
	}

	public static void PlaceAboveDesktop(nint hwnd)
	{
		nint owner = DesktopEmbedService.DesktopOwner;
		if (owner != IntPtr.Zero)
		{
			NativeMethods.SetWindowPos(hwnd, owner, 0, 0, 0, 0, 19u);
		}
	}

	public static void SuspendForInteraction(nint hwnd)
	{
		int ex = NativeMethods.GetWindowLong(hwnd, -20);
		ex &= -134217729;
		ex &= -129;
		NativeMethods.SetWindowLong(hwnd, -20, ex);
		NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0, 99u);
	}

	public static void ResumeAfterInteraction(nint hwnd, Options opt)
	{
		int ex = NativeMethods.GetWindowLong(hwnd, -20);
		if (opt.NoActivate)
		{
			ex |= 0x8000000;
		}
		if (opt.ToolWindow)
		{
			ex |= 0x80;
		}
		NativeMethods.SetWindowLong(hwnd, -20, ex);
		PlaceAboveDesktop(hwnd);
	}
}
