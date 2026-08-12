using System;
using System.Runtime.InteropServices;

namespace DesktopTool.Services;

internal static class DesktopVisualService
{
	private static nint _listViewHwnd = IntPtr.Zero;

	public const string SchemaVersion = "2.0.0";

	private const uint LVM_FIRST = 4096u;

	private const uint LVM_GETITEMCOUNT = 4100u;

	private const int GWL_STYLE = -16;

	private const uint LVS_AUTOARRANGE = 256u;

	public static bool Initialize()
	{
		_listViewHwnd = FindDesktopListView();
		return _listViewHwnd != IntPtr.Zero;
	}

	public static nint GetListViewHandle()
	{
		return _listViewHwnd;
	}

	public static int GetIconCount()
	{
		if (_listViewHwnd == IntPtr.Zero)
		{
			return 0;
		}
		return SendMessage(_listViewHwnd, 4100u, 0, 0);
	}

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	private static extern nint FindWindowEx(nint hwndParent, nint hwndChildAfter, string? lpszClass, string? lpszWindow);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int SendMessage(nint hWnd, uint Msg, int wParam, int lParam);

	private static nint FindDesktopListView()
	{
		nint progman = FindWindow("Progman", null);
		if (progman == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		nint defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
		if (defView != IntPtr.Zero)
		{
			nint listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
			if (listView != IntPtr.Zero)
			{
				return listView;
			}
		}
		nint worker = IntPtr.Zero;
		while (true)
		{
			worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
			if (worker == IntPtr.Zero)
			{
				break;
			}
			defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
			if (defView != IntPtr.Zero)
			{
				nint listView2 = FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
				if (listView2 != IntPtr.Zero)
				{
					return listView2;
				}
			}
		}
		return IntPtr.Zero;
	}
}
