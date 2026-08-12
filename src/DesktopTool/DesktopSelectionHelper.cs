using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DesktopTool;

internal static class DesktopSelectionHelper
{
	[ComImport]
	[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellWindows
	{
		void GetTypeInfoCount(out uint pctinfo);

		void GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo);

		void GetIDsOfNames(ref Guid riid, nint rgszNames, uint cNames, uint lcid, nint rgDispId);

		void Invoke(int dispIdMember, ref Guid riid, uint lcid, ushort wFlags, nint pDispParams, nint pVarResult, nint pExcepInfo, nint puArgErr);

		void get_Count(out int count);

		void Item(object index, [MarshalAs(UnmanagedType.IDispatch)] out object folder);

		void _NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

		void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hWnd, int swClass, out int plCookie);

		void RegisterPending(int lThreadId, ref object pvarloc, ref object pvarlocRoot, int swClass, out int plCookie);

		void Revoke(int lCookie);

		void OnNavigate(int lCookie, ref object pvarLoc);

		void OnActivated(int lCookie, bool fActive);

		void FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions, [MarshalAs(UnmanagedType.IDispatch)] out object ppdispOut);

		void OnCreated(int lCookie, [MarshalAs(UnmanagedType.IUnknown)] object punk);

		void ProcessAttachDetach(bool fAttach);
	}

	private static readonly object _cacheLock = new object();

	private static List<string>? _cache;

	private static DateTime _cacheTime;

	private const int CacheTtlMs = 500;

	private static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

	private const int SWC_DESKTOP = 8;

	private const int SWFO_NEEDDISPATCH = 1;

	public static string LastDiag { get; private set; } = "(未调用)";

	public static List<string> GetDesktopSelectedPathsCached()
	{
		lock (_cacheLock)
		{
			if (_cache != null && (DateTime.Now - _cacheTime).TotalMilliseconds < 500.0)
			{
				return new List<string>(_cache);
			}
		}
		List<string> fresh = GetDesktopSelectedPaths();
		lock (_cacheLock)
		{
			_cache = fresh;
			_cacheTime = DateTime.Now;
		}
		return new List<string>(fresh);
	}

	public static List<string> GetDesktopSelectedPaths()
	{
		List<string> result = new List<string>();
		try
		{
			result = GetSelectionFromDesktop(out var diag);
			if (result.Count > 0)
			{
				LastDiag = "桌面/" + diag;
				return result;
			}
			string diag2;
			List<string> selectionFromExplorerWindows = GetSelectionFromExplorerWindows(out diag2);
			LastDiag = "桌面空(" + diag + ") → 资源管理器/" + diag2;
			return selectionFromExplorerWindows;
		}
		catch (Exception ex)
		{
			LastDiag = "异常:" + ex.Message;
			return result;
		}
	}

	public static bool TryShellExecuteViaDesktopShell(string file, string args, string workDir, out string diag)
	{
		diag = "未知";
		IShellWindows shellWindows = null;
		try
		{
			Type t = Type.GetTypeFromCLSID(CLSID_ShellWindows);
			if (t == null)
			{
				diag = "ShellWindows类型缺失";
				return false;
			}
			shellWindows = Activator.CreateInstance(t) as IShellWindows;
			if (shellWindows == null)
			{
				diag = "ShellWindows创建失败";
				return false;
			}
			object loc = 0;
			object locRoot = 0;
			shellWindows.FindWindowSW(ref loc, ref locRoot, 8, out var _, 1, out var disp);
			if (disp == null)
			{
				diag = "FindWindowSW未返回桌面";
				return false;
			}
			dynamic win = disp;
			dynamic app = win.Document.Application;
			app.ShellExecute(file, args, workDir, string.Empty, 1);
			diag = "已通过explorer令牌启动";
			return true;
		}
		catch (Exception ex)
		{
			diag = "异常:" + ex.Message;
			return false;
		}
		finally
		{
			if (shellWindows != null)
			{
				try
				{
					Marshal.ReleaseComObject(shellWindows);
				}
				catch
				{
				}
			}
		}
	}

	private static List<string> GetSelectionFromDesktop(out string diag)
	{
		List<string> result = new List<string>();
		diag = "未知";
		IShellWindows shellWindows = null;
		try
		{
			Type t = Type.GetTypeFromCLSID(CLSID_ShellWindows);
			if (t == null)
			{
				diag = "ShellWindows类型缺失";
				return result;
			}
			shellWindows = Activator.CreateInstance(t) as IShellWindows;
			if (shellWindows == null)
			{
				diag = "ShellWindows创建失败";
				return result;
			}
			object loc = 0;
			object locRoot = 0;
			shellWindows.FindWindowSW(ref loc, ref locRoot, 8, out var _, 1, out var disp);
			if (disp == null)
			{
				diag = "FindWindowSW未返回桌面";
				return result;
			}
			dynamic win = disp;
			DesktopSelectionHelper.CollectSelectedItems(win, result);
			diag = $"取得{result.Count}项";
		}
		catch (Exception ex)
		{
			diag = "异常:" + ex.Message;
		}
		finally
		{
			if (shellWindows != null)
			{
				try
				{
					Marshal.ReleaseComObject(shellWindows);
				}
				catch
				{
				}
			}
		}
		return result;
	}

	private static List<string> GetSelectionFromExplorerWindows(out string diag)
	{
		List<string> result = new List<string>();
		diag = "未知";
		try
		{
			Type shellType = Type.GetTypeFromProgID("Shell.Application");
			if (shellType == null)
			{
				diag = "Shell.Application缺失";
				return result;
			}
			dynamic shell = Activator.CreateInstance(shellType);
			try
			{
				dynamic windows = shell.Windows();
				int count = windows.Count;
				for (int i = 0; i < count; i++)
				{
					try
					{
						dynamic win = windows.Item(i);
						DesktopSelectionHelper.CollectSelectedItems(win, result);
						if (result.Count > 0)
						{
							break;
						}
					}
					catch
					{
					}
				}
				diag = $"扫描{count}窗口取得{result.Count}项";
			}
			finally
			{
				try
				{
					Marshal.ReleaseComObject(shell);
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			diag = "异常:" + ex.Message;
		}
		return result;
	}

	private static void CollectSelectedItems(dynamic win, List<string> result)
	{
		try
		{
			dynamic items = win.Document.SelectedItems();
			int itemCount = items.Count;
			for (int j = 0; j < itemCount; j++)
			{
				try
				{
					dynamic it = items.Item(j);
					string p = (string)it.Path;
					if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)) && !result.Contains<string>(p, StringComparer.OrdinalIgnoreCase))
					{
						result.Add(p);
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}
}
