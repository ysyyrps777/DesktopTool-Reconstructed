using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace DesktopTool.Services;

internal static class ElevationHelper
{
	public const string WaitArgPrefix = "--dl-wait=";

	private const uint PROCESS_QUERY_LIMITED_INFORMATION = 4096u;

	private const int TokenElevation = 20;

	public static bool EnsureNormalIntegrity(string[] args, Action<string> log)
	{
		try
		{
			string waitArg = args?.FirstOrDefault((string a) => a.StartsWith("--dl-wait=", StringComparison.OrdinalIgnoreCase));
			if (waitArg != null)
			{
				if (int.TryParse(waitArg.Substring("--dl-wait=".Length), out var oldPid))
				{
					WaitForProcessExit(oldPid, 8000, log);
				}
				log("[提权自愈] 本实例由降权重启产生，跳过检测");
				return false;
			}
			if (!IsCurrentProcessElevated())
			{
				return false;
			}
			bool? shellElevated = IsShellProcessElevated();
			if (shellElevated != false)
			{
				log("[提权自愈] 本进程已提权，但 explorer 提权状态=" + (shellElevated?.ToString() ?? "未知") + "，无完整性落差，保持现状");
				return false;
			}
			string exe;
			try
			{
				exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
			}
			catch
			{
				exe = string.Empty;
			}
			if (string.IsNullOrEmpty(exe))
			{
				log("[提权自愈] ⚠ 取不到自身 EXE 路径，放弃降权");
				return false;
			}
			int myPid = Environment.ProcessId;
			string workDir = Path.GetDirectoryName(exe) ?? string.Empty;
			string newArgs = (((args == null) ? string.Empty : string.Join(" ", args.Select(Quote))) + " --dl-wait=" + myPid).Trim();
			log("[提权自愈] ★ 检测到 UIPI 落差（本进程=高完整性 / explorer=中完整性），尝试以 explorer 令牌降权重启：" + exe + " " + newArgs);
			if (!DesktopSelectionHelper.TryShellExecuteViaDesktopShell(exe, newArgs, workDir, out var diag))
			{
				log("[提权自愈] ⚠ 降权启动失败（" + diag + "），继续以管理员权限运行（拖放可能显示禁用，但兜底落盘仍可用）");
				return false;
			}
			if (!WaitForSiblingProcess(myPid, exe, 5000))
			{
				log("[提权自愈] ⚠ 未在 5s 内检测到降权新实例，放弃退出，继续以管理员权限运行");
				return false;
			}
			log("[提权自愈] ✔ 降权新实例已就绪，当前高完整性实例退出");
			return true;
		}
		catch (Exception ex)
		{
			try
			{
				log("[提权自愈] ⚠ 异常，忽略：" + ex.Message);
			}
			catch
			{
			}
			return false;
		}
	}

	private static string Quote(string a)
	{
		if (!a.Contains(' ') || a.StartsWith("\""))
		{
			return a;
		}
		return "\"" + a + "\"";
	}

	public static bool IsCurrentProcessElevated()
	{
		try
		{
			using WindowsIdentity id = WindowsIdentity.GetCurrent();
			return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch
		{
			return false;
		}
	}

	public static bool? IsShellProcessElevated()
	{
		nint hProc = 0;
		nint hToken = 0;
		try
		{
			nint shell = NativeMethods.GetShellWindow();
			if (shell == IntPtr.Zero)
			{
				return null;
			}
			NativeMethods.GetWindowThreadProcessId(shell, out var pid);
			if (pid == 0)
			{
				return null;
			}
			hProc = NativeMethods.OpenProcess(4096u, bInheritHandle: false, pid);
			if (hProc == 0)
			{
				return null;
			}
			if (!NativeMethods.OpenProcessToken(hProc, 8u, out hToken) || hToken == 0)
			{
				return null;
			}
			uint size = 4u;
			nint buf = Marshal.AllocHGlobal((int)size);
			try
			{
				if (!GetTokenInformation(hToken, 20, buf, size, out var _))
				{
					return null;
				}
				return Marshal.ReadInt32(buf) != 0;
			}
			finally
			{
				Marshal.FreeHGlobal(buf);
			}
		}
		catch
		{
			return null;
		}
		finally
		{
			if (hToken != 0)
			{
				try
				{
					NativeMethods.CloseHandle(hToken);
				}
				catch
				{
				}
			}
			if (hProc != 0)
			{
				try
				{
					NativeMethods.CloseHandle(hProc);
				}
				catch
				{
				}
			}
		}
	}

	private static void WaitForProcessExit(int pid, int timeoutMs, Action<string> log)
	{
		try
		{
			using Process p = Process.GetProcessById(pid);
			if (!p.WaitForExit(timeoutMs))
			{
				log($"[提权自愈] ⚠ 等待旧实例(PID={pid})退出超时，继续启动");
			}
		}
		catch
		{
		}
	}

	private static bool WaitForSiblingProcess(int myPid, string exePath, int timeoutMs)
	{
		string name = Path.GetFileNameWithoutExtension(exePath);
		Stopwatch sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < timeoutMs)
		{
			try
			{
				if (Process.GetProcessesByName(name).Any((Process p) => p.Id != myPid))
				{
					return true;
				}
			}
			catch
			{
			}
			Thread.Sleep(200);
		}
		return false;
	}

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetTokenInformation(nint TokenHandle, int TokenInformationClass, nint TokenInformation, uint TokenInformationLength, out uint ReturnLength);
}
