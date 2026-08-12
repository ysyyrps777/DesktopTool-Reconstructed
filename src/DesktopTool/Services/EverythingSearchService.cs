using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DesktopTool.Services;

public static class EverythingSearchService
{
	public class SearchResult
	{
		public string FileName { get; set; } = string.Empty;

		public string FullPath { get; set; } = string.Empty;

		public bool IsFolder { get; set; }

		public long Size { get; set; }

		public DateTime? Modified { get; set; }

		public string SizeText
		{
			get
			{
				if (!IsFolder)
				{
					return FormatSize(Size);
				}
				return "文件夹";
			}
		}

		public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
	}

	private const string Dll = "Everything64.dll";

	private const uint EVERYTHING_REQUEST_FILE_NAME = 1u;

	private const uint EVERYTHING_REQUEST_PATH = 2u;

	private const uint EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME = 4u;

	private const uint EVERYTHING_REQUEST_SIZE = 16u;

	private const uint EVERYTHING_REQUEST_DATE_MODIFIED = 64u;

	private const uint EVERYTHING_SORT_NAME_ASCENDING = 1u;

	private const uint EVERYTHING_SORT_DATE_MODIFIED_DESCENDING = 14u;

	private const uint EVERYTHING_OK = 0u;

	private const uint EVERYTHING_ERROR_IPC = 2u;

	private static readonly object _engineLifecycleLock = new object();

	private static DateTime _lastKillRestartAttempt = DateTime.MinValue;

	private const int KillRestartCooldownMs = 15000;

	public static string? LastDllReason { get; private set; }

	public static bool IsApp64Bit => Environment.Is64BitProcess;

	[DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
	private static extern void Everything_SetSearchW(string lpSearchString);

	[DllImport("Everything64.dll")]
	private static extern void Everything_SetRequestFlags(uint dwRequestFlags);

	[DllImport("Everything64.dll")]
	private static extern void Everything_SetSort(uint dwSortType);

	[DllImport("Everything64.dll")]
	private static extern void Everything_SetMax(uint dwMax);

	[DllImport("Everything64.dll")]
	private static extern void Everything_SetMatchCase(bool bEnable);

	[DllImport("Everything64.dll")]
	private static extern void Everything_SetRegex(bool bEnable);

	[DllImport("Everything64.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Everything_QueryW(bool bWait);

	[DllImport("Everything64.dll")]
	private static extern uint Everything_GetLastError();

	[DllImport("Everything64.dll")]
	private static extern uint Everything_GetNumResults();

	[DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
	private static extern void Everything_GetResultFullPathNameW(uint nIndex, StringBuilder lpString, uint nMaxCount);

	[DllImport("Everything64.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Everything_IsFolderResult(uint nIndex);

	[DllImport("Everything64.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Everything_GetResultSize(uint nIndex, out long lpFileSize);

	[DllImport("Everything64.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Everything_GetResultDateModified(uint nIndex, out long lpFileTime);

	[DllImport("Everything64.dll")]
	private static extern void Everything_CleanUp();

	[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint LoadLibrary(string lpFileName);

	[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint GetModuleHandle(string lpFileName);

	public static void LogEverythingDiag(string message)
	{
		try
		{
			string text = App.GetAppDataDir();
			Directory.CreateDirectory(text);
			LogHelper.Append(Path.Combine(text, "debug_everything.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	private static string? GetFileVersion(string path)
	{
		try
		{
			return FileVersionInfo.GetVersionInfo(path).FileVersion;
		}
		catch
		{
			return null;
		}
	}

	private static bool? IsFile64Bit(string path)
	{
		try
		{
			using FileStream fs = File.OpenRead(path);
			using BinaryReader br = new BinaryReader(fs);
			if (br.ReadUInt16() != 23117)
			{
				return null;
			}
			fs.Seek(60L, SeekOrigin.Begin);
			int e_lfanew = br.ReadInt32();
			fs.Seek(e_lfanew + 4, SeekOrigin.Begin);
			return br.ReadUInt16() == 34404;
		}
		catch
		{
			return null;
		}
	}

	public static bool IsCompatibleBitness(string exeOrSetupPath)
	{
		bool? bit = IsFile64Bit(exeOrSetupPath);
		if (!bit.HasValue)
		{
			return false;
		}
		return bit == IsApp64Bit;
	}

	public static bool IsInstalledEverythingCompatible()
	{
		string exe = FindEverythingExe();
		if (exe == null)
		{
			return false;
		}
		return IsCompatibleBitness(exe);
	}

	private static bool EnsureEngineDllLoaded()
	{
		if (GetModuleHandle("Everything64.dll") != IntPtr.Zero)
		{
			return true;
		}
		List<string> candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "Everything64.dll") };
		string exe = FindEverythingExe();
		if (exe != null)
		{
			candidates.Add(Path.Combine(Path.GetDirectoryName(exe), "Everything64.dll"));
		}
		foreach (string dll in candidates)
		{
			if (!File.Exists(dll))
			{
				continue;
			}
			try
			{
				bool? bit = IsFile64Bit(dll);
				if (bit == false)
				{
					LogEverythingDiag("[EnsureEngineDllLoaded] 跳过 32 位 DLL（本程序 x64 不兼容）: " + dll);
					LastDllReason = dll + " 是 32 位，本程序为 64 位，无法加载。";
				}
				else if (LoadLibrary(dll) != IntPtr.Zero)
				{
					LastDllReason = null;
					LogEverythingDiag("[EnsureEngineDllLoaded] 已加载 DLL: " + dll + " | 位数=" + ((bit == true) ? "x64" : "未知"));
					return true;
				}
			}
			catch (Exception ex)
			{
				LastDllReason = "加载 " + dll + " 失败: " + ex.Message;
				LogEverythingDiag("[EnsureEngineDllLoaded] 加载异常 " + dll + ": " + ex.Message);
			}
		}
		LastDllReason = "未找到 Everything64.dll（IPC 客户端）。它可能未随 Everything 安装包提供，或 Everything 尚未安装。";
		return false;
	}

	public static bool PrepareAfterInstall()
	{
		if (!EnsureEngineDllLoaded())
		{
			LogEverythingDiag("[PrepareAfterInstall] SDK DLL 仍无法加载: " + (LastDllReason ?? "null"));
			return false;
		}
		string exe = FindEverythingExe();
		if (exe != null)
		{
			string src64 = Path.Combine(Path.GetDirectoryName(exe), "Everything64.dll");
			string baseDll = Path.Combine(AppContext.BaseDirectory, "Everything64.dll");
			if (!File.Exists(baseDll) && File.Exists(src64))
			{
				try
				{
					File.Copy(src64, baseDll, overwrite: true);
					LogEverythingDiag("[PrepareAfterInstall] 已复制 Everything64.dll → " + baseDll);
				}
				catch (Exception ex)
				{
					LogEverythingDiag("[PrepareAfterInstall] 复制失败: " + ex.Message);
				}
			}
		}
		return EnsureEngineRunning();
	}

	public static void PrepareInstallConfig()
	{
		try
		{
			string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			EnsureEverythingConfig(string.IsNullOrEmpty(pf) ? "C:\\Program Files\\Everything\\Everything.exe" : Path.Combine(pf, "Everything", "Everything.exe"));
			LogEverythingDiag("[PrepareInstallConfig] 已预写隐藏配置（安装前）");
		}
		catch (Exception ex)
		{
			LogEverythingDiag("[PrepareInstallConfig] 异常: " + ex.Message);
		}
	}

	public static void HideRunningEngineWindow()
	{
		try
		{
			Process[] processesByName = Process.GetProcessesByName("Everything");
			foreach (Process p in processesByName)
			{
				int pid = p.Id;
				Task.Run(delegate
				{
					try
					{
						HideEverythingMainWindow(pid);
					}
					catch
					{
					}
				});
			}
		}
		catch (Exception ex)
		{
			LogEverythingDiag("[HideRunningEngineWindow] 异常: " + ex.Message);
		}
	}

	public static bool RestartEngine()
	{
		lock (_engineLifecycleLock)
		{
			_lastKillRestartAttempt = DateTime.Now;
			KillEverythingProcesses();
			Thread.Sleep(800);
			return StartEngineNow();
		}
	}

	private static bool StartEngineNow()
	{
		for (int i = 0; i < 10; i++)
		{
			if (!IsProcessRunning())
			{
				break;
			}
			Thread.Sleep(300);
		}
		string exe = FindEverythingExe();
		if (exe == null)
		{
			LogEverythingDiag("[StartEngineNow] 未找到 Everything.exe");
			return false;
		}
		if (IsFile64Bit(exe) == false)
		{
			LastDllReason = "已安装的 Everything 是 32 位（" + exe + "），但本程序为 64 位，IPC 不兼容。请改用 Everything x64 安装包。";
			LogEverythingDiag("[StartEngineNow] 位数不兼容(x86): " + exe);
			return false;
		}
		if (IsProcessRunning())
		{
			LogEverythingDiag("[StartEngineNow] 仍有实例残留，尝试再次结束（可能无权限结束提权实例）");
			KillEverythingProcesses();
			for (int j = 0; j < 4; j++)
			{
				if (!IsProcessRunning())
				{
					break;
				}
				Thread.Sleep(300);
			}
		}
		try
		{
			EnsureEverythingConfig(exe);
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = exe,
				Arguments = "-startup",
				UseShellExecute = true,
				WindowStyle = ProcessWindowStyle.Hidden,
				Verb = "runas"
			};
			LogEverythingDiag("[StartEngineNow] 启动引擎(隐藏+提权): " + exe);
			using Process proc = Process.Start(startInfo);
			int pid = proc?.Id ?? 0;
			if (pid != 0)
			{
				Task.Run(delegate
				{
					HideEverythingMainWindow(pid);
				});
			}
		}
		catch (Win32Exception ex)
		{
			LastDllReason = "启动失败（可能用户取消了授权）：" + ex.Message;
			LogEverythingDiag("[StartEngineNow] Win32Exception(可能用户取消授权): " + ex.Message);
			return false;
		}
		catch (Exception ex2)
		{
			LastDllReason = "启动失败: " + ex2.Message;
			LogEverythingDiag("[StartEngineNow] 启动失败: " + ex2.Message);
			return false;
		}
		for (int i2 = 0; i2 < 30; i2++)
		{
			if (IsEngineAvailable())
			{
				LogEverythingDiag("[StartEngineNow] 引擎就绪");
				return true;
			}
			Thread.Sleep(500);
		}
		bool final = IsEngineAvailable();
		LogEverythingDiag($"[StartEngineNow] 最终可用={final}");
		return final;
	}

	public static bool IsProcessRunning()
	{
		try
		{
			return Process.GetProcessesByName("Everything").Length != 0;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsEngineAvailable()
	{
		try
		{
			if (!EnsureEngineDllLoaded())
			{
				LogEverythingDiag("[IsEngineAvailable] DLL 未加载，返回 false | LastDllReason=" + (LastDllReason ?? "null"));
				return false;
			}
			Everything_SetSearchW(string.Empty);
			Everything_SetRequestFlags(1u);
			Everything_SetMax(1u);
			Everything_QueryW(bWait: true);
			uint err = Everything_GetLastError();
			bool num = err != 2;
			if (!num)
			{
				LogEverythingDiag($"[IsEngineAvailable] 进程运行={IsProcessRunning()} | GetLastError={err} ({err switch
				{
					2u => "IPC错误", 
					0u => "OK", 
					_ => "其它", 
				}}) | 结果=False");
			}
			return num;
		}
		catch (DllNotFoundException ex)
		{
			LogEverythingDiag("[IsEngineAvailable] DllNotFoundException: " + ex.Message);
			return false;
		}
		catch (BadImageFormatException ex2)
		{
			LogEverythingDiag("[IsEngineAvailable] BadImageFormatException: " + ex2.Message);
			return false;
		}
		catch (Exception ex3)
		{
			LogEverythingDiag("[IsEngineAvailable] 异常: " + ex3.Message);
			return false;
		}
	}

	public static bool IsEngineHealthy()
	{
		if (!IsEngineAvailable())
		{
			return false;
		}
		try
		{
			Everything_SetSearchW(string.Empty);
			Everything_SetRequestFlags(1u);
			Everything_SetMax(1u);
			if (!Everything_QueryW(bWait: true))
			{
				return false;
			}
			if (Everything_GetLastError() == 2)
			{
				return false;
			}
			return Everything_GetNumResults() != 0;
		}
		catch (Exception ex)
		{
			LogEverythingDiag("[IsEngineHealthy] 异常: " + ex.Message);
			return false;
		}
	}

	public static string? FindBundledSetup()
	{
		try
		{
			string baseDir = AppContext.BaseDirectory;
			string[] array = new string[7]
			{
				Path.Combine(baseDir, "Tools", "Everything", "Everything-x64-setup.exe"),
				Path.Combine(baseDir, "Tools", "Everything", "Everything64.exe"),
				Path.Combine(baseDir, "Everything-x64-setup.exe"),
				Path.Combine(baseDir, "Everything64.exe"),
				Path.Combine(baseDir, "Tools", "Everything", "Everything-Setup.exe"),
				Path.Combine(baseDir, "Everything", "Everything-Setup.exe"),
				Path.Combine(baseDir, "Everything-Setup.exe")
			};
			foreach (string c in array)
			{
				if (File.Exists(c))
				{
					bool? bit = IsFile64Bit(c);
					LogEverythingDiag($"[FindBundledSetup] 命中: {c} | 位数={((!bit.HasValue) ? "未知" : ((bit == true) ? "x64" : "x86"))} | 版本={GetFileVersion(c) ?? "未知"}");
					return c;
				}
			}
			string toolDir = Path.Combine(baseDir, "Tools", "Everything");
			if (Directory.Exists(toolDir))
			{
				array = Directory.GetFiles(toolDir, "Everything*x64*.exe");
				foreach (string f in array)
				{
					if (File.Exists(f))
					{
						LogEverythingDiag("[FindBundledSetup] 兜底命中(x64): " + f);
						return f;
					}
				}
				array = Directory.GetFiles(toolDir, "Everything*.exe");
				foreach (string f2 in array)
				{
					string fn = Path.GetFileName(f2);
					if (!fn.Equals("everything.exe", StringComparison.OrdinalIgnoreCase) && !fn.Equals("everything1.5.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(f2))
					{
						bool? bit2 = IsFile64Bit(f2);
						LogEverythingDiag("[FindBundledSetup] 兜底命中: " + f2 + " | 位数=" + ((!bit2.HasValue) ? "未知" : ((bit2 == true) ? "x64" : "x86")));
						return f2;
					}
				}
			}
			LogEverythingDiag("[FindBundledSetup] 未找到任何安装包");
		}
		catch (Exception ex)
		{
			LogEverythingDiag("[FindBundledSetup] 异常: " + ex.Message);
		}
		return null;
	}

	public static bool LaunchInstaller(bool silent = true)
	{
		string setup = FindBundledSetup();
		if (setup == null)
		{
			LogEverythingDiag("[LaunchInstaller] 未找到安装包，返回 false");
			return false;
		}
		try
		{
			bool isSetup = Path.GetFileName(setup).Contains("setup", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(setup).EndsWith("-install.exe", StringComparison.OrdinalIgnoreCase);
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = setup,
				UseShellExecute = true
			};
			if (silent && isSetup)
			{
				psi.Arguments = "/S";
			}
			else if (!isSetup)
			{
				EnsureEverythingConfig(setup);
				psi.Arguments = "-startup";
				psi.WindowStyle = ProcessWindowStyle.Hidden;
				psi.Verb = "runas";
			}
			LogEverythingDiag($"[LaunchInstaller] 启动: {setup} | isSetup={isSetup} | silent={silent} | args={psi.Arguments}");
			using (Process proc = Process.Start(psi))
			{
				if (proc != null && !isSetup)
				{
					HideEverythingMainWindow(proc.Id);
				}
			}
			return true;
		}
		catch (Win32Exception ex)
		{
			LogEverythingDiag("[LaunchInstaller] Win32Exception(可能用户取消授权): " + ex.Message);
			return false;
		}
		catch (Exception ex2)
		{
			LogEverythingDiag("[LaunchInstaller] 异常: " + ex2.Message);
			return false;
		}
	}

	public static bool WaitForEngine(int timeoutMs = 30000, int pollMs = 800)
	{
		int start = Environment.TickCount;
		bool attemptedLaunch = false;
		while (Environment.TickCount - start < timeoutMs)
		{
			if (IsEngineAvailable())
			{
				return true;
			}
			if (!attemptedLaunch && !IsProcessRunning())
			{
				attemptedLaunch = true;
				if (EnsureEngineRunning())
				{
					return true;
				}
			}
			Thread.Sleep(pollMs);
		}
		return IsEngineAvailable();
	}

	public static string? FindEverythingExe()
	{
		try
		{
			string running = GetRunningEverythingPath();
			if (running != null)
			{
				LogEverythingDiag("[FindEverythingExe] 复用正在运行的实例: " + running);
				return running;
			}
			string toolDir = Path.Combine(AppContext.BaseDirectory, "Tools", "Everything");
			List<string> portable = new List<string>
			{
				Path.Combine(toolDir, "everything1.5.exe"),
				Path.Combine(toolDir, "Everything64.exe"),
				Path.Combine(toolDir, "everything.exe"),
				Path.Combine(AppContext.BaseDirectory, "Everything64.exe"),
				Path.Combine(AppContext.BaseDirectory, "everything.exe")
			};
			if (Directory.Exists(toolDir))
			{
				string[] files = Directory.GetFiles(toolDir, "Everything*.exe");
				foreach (string f in files)
				{
					if (f.IndexOf("setup", StringComparison.OrdinalIgnoreCase) < 0 && !f.EndsWith("-install.exe", StringComparison.OrdinalIgnoreCase) && !portable.Contains(f))
					{
						portable.Add(f);
					}
				}
			}
			foreach (string c in portable)
			{
				if (File.Exists(c) && IsFile64Bit(c) == true)
				{
					LogEverythingDiag("[FindEverythingExe] 优先使用随附便携版: " + c);
					return c;
				}
			}
			List<string> candidates = new List<string>();
			string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			string pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
			string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (!string.IsNullOrEmpty(pf))
			{
				AddIf(candidates, Path.Combine(pf, "Everything", "Everything.exe"));
			}
			if (!string.IsNullOrEmpty(pfX86))
			{
				AddIf(candidates, Path.Combine(pfX86, "Everything", "Everything.exe"));
			}
			if (!string.IsNullOrEmpty(local))
			{
				AddIf(candidates, Path.Combine(local, "Everything", "Everything.exe"));
			}
			RegistryKey[] array = new RegistryKey[2]
			{
				Registry.CurrentUser,
				Registry.LocalMachine
			};
			foreach (RegistryKey root in array)
			{
				try
				{
					using RegistryKey key = root.OpenSubKey("Software\\Everything");
					string loc = key?.GetValue("InstallLocation") as string;
					if (!string.IsNullOrEmpty(loc))
					{
						AddIf(candidates, Path.Combine(loc, "Everything.exe"));
					}
					string exe = key?.GetValue("Exe") as string;
					if (!string.IsNullOrEmpty(exe))
					{
						AddIf(candidates, exe);
					}
				}
				catch
				{
				}
			}
			foreach (string c2 in candidates)
			{
				if (File.Exists(c2))
				{
					LogEverythingDiag("[FindEverythingExe] 兜底使用已安装: " + c2);
					return c2;
				}
			}
		}
		catch (Exception ex)
		{
			LogEverythingDiag("[FindEverythingExe] 异常: " + ex.Message);
		}
		return null;
		static bool AddIf(List<string> list, string path)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
			{
				return false;
			}
			if (IsFile64Bit(path) == true)
			{
				list.Add(path);
				return true;
			}
			return false;
		}
	}

	private static string? GetRunningEverythingPath()
	{
		try
		{
			Process[] processesByName = Process.GetProcessesByName("Everything");
			foreach (Process p in processesByName)
			{
				try
				{
					string path = p.MainModule?.FileName;
					if (!string.IsNullOrEmpty(path))
					{
						return path;
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
		return null;
	}

	public static bool WaitForInstall(int timeoutMs = 30000, int pollMs = 1000)
	{
		int start = Environment.TickCount;
		while (Environment.TickCount - start < timeoutMs)
		{
			string exe = FindEverythingExe();
			if (exe != null)
			{
				bool? bit = IsFile64Bit(exe);
				LogEverythingDiag($"[WaitForInstall] 检测到 Everything.exe: {exe} | 位数={((!bit.HasValue) ? "未知" : ((bit == true) ? "x64" : "x86"))} | 版本={GetFileVersion(exe) ?? "未知"}");
				return true;
			}
			Thread.Sleep(pollMs);
		}
		string final = FindEverythingExe();
		LogEverythingDiag("[WaitForInstall] 超时，最终 exe=" + (final ?? "null"));
		return final != null;
	}

	public static bool EnsureEngineRunning()
	{
		if (IsEngineAvailable())
		{
			return true;
		}
		lock (_engineLifecycleLock)
		{
			if (IsEngineAvailable())
			{
				return true;
			}
			string exe = FindEverythingExe();
			if (exe == null)
			{
				LogEverythingDiag("[EnsureEngineRunning] 未找到 Everything.exe");
				return false;
			}
			if (IsProcessRunning())
			{
				bool ipcOk = false;
				for (int i = 0; i < 3; i++)
				{
					if (ipcOk)
					{
						break;
					}
					if (IsEngineAvailable())
					{
						ipcOk = true;
						break;
					}
					Thread.Sleep(300);
				}
				if (!ipcOk)
				{
					bool? bit = IsFile64Bit(exe);
					if (bit == false)
					{
						LastDllReason = "已安装的 Everything 是 32 位（" + exe + "），但本程序为 64 位，IPC 不兼容。请改用 Everything x64 安装包。";
					}
					if ((DateTime.Now - _lastKillRestartAttempt).TotalMilliseconds < 15000.0)
					{
						LogEverythingDiag("[EnsureEngineRunning] 距上次结束/重启不足冷却窗口，跳过本次 kill，仅等待现有实例建立 IPC");
						for (int j = 0; j < 12; j++)
						{
							if (IsEngineAvailable())
							{
								LogEverythingDiag("[EnsureEngineRunning] 冷却期内等待到 IPC 就绪");
								return true;
							}
							Thread.Sleep(400);
						}
						LogEverythingDiag("[EnsureEngineRunning] 冷却期内 IPC 仍不通，放弃本次（不 kill）");
						return false;
					}
					_lastKillRestartAttempt = DateTime.Now;
					LogEverythingDiag("[EnsureEngineRunning] 进程运行但 IPC 持续不通（exe位数=" + ((!bit.HasValue) ? "未知" : ((bit == true) ? "x64" : "x86")) + "），结束不可达实例以在本会话重启");
					KillEverythingProcesses();
					Thread.Sleep(800);
				}
			}
			return StartEngineNow();
		}
	}

	private static void HideEverythingMainWindow(int pid)
	{
		if (pid <= 0)
		{
			return;
		}
		HashSet<nint> centered = new HashSet<nint>();
		int emptyStreak = 0;
		for (int i = 0; i < 120; i++)
		{
			List<nint> toHide = new List<nint>();
			List<nint> dialogWins = new List<nint>();
			NativeMethods.EnumWindows(delegate(nint hwnd, nint _lparam)
			{
				NativeMethods.GetWindowThreadProcessId(hwnd, out var lpdwProcessId);
				if (lpdwProcessId != (uint)pid)
				{
					return true;
				}
				StringBuilder stringBuilder = new StringBuilder(256);
				NativeMethods.GetClassName(hwnd, stringBuilder, stringBuilder.Capacity);
				if (stringBuilder.ToString() == "#32770")
				{
					dialogWins.Add(hwnd);
				}
				else
				{
					toHide.Add(hwnd);
				}
				return true;
			}, IntPtr.Zero);
			if (dialogWins.Count > 0)
			{
				foreach (nint d in dialogWins)
				{
					if (centered.Add(d))
					{
						CenterWindowOnScreen(d);
					}
				}
				Thread.Sleep(100);
			}
			else if (toHide.Count > 0)
			{
				foreach (nint item in toHide)
				{
					NativeMethods.ShowWindow(item, 0);
					LogEverythingDiag("[HideEverythingMainWindow] 已隐藏 Everything 窗口（无左下角小标题）");
				}
				Thread.Sleep(300);
				List<nint> again = new List<nint>();
				NativeMethods.EnumWindows(delegate(nint hwnd, nint _lparam)
				{
					NativeMethods.GetWindowThreadProcessId(hwnd, out var lpdwProcessId);
					if (lpdwProcessId != (uint)pid)
					{
						return true;
					}
					StringBuilder stringBuilder = new StringBuilder(256);
					NativeMethods.GetClassName(hwnd, stringBuilder, stringBuilder.Capacity);
					if (stringBuilder.ToString() != "#32770")
					{
						again.Add(hwnd);
					}
					return true;
				}, IntPtr.Zero);
				if (again.Count == 0)
				{
					return;
				}
				emptyStreak = 0;
			}
			else
			{
				if (++emptyStreak > 15)
				{
					return;
				}
				Thread.Sleep(100);
			}
		}
		LogEverythingDiag("[HideEverythingMainWindow] 轮询结束（窗口已隐藏或进程未创建可见窗口）");
	}

	private static void CenterWindowOnScreen(nint hwnd)
	{
		if (NativeMethods.GetWindowRect(hwnd, out var r))
		{
			int w = r.Right - r.Left;
			int h = r.Bottom - r.Top;
			if (w > 0 && h > 0)
			{
				int cx = NativeMethods.GetSystemMetrics(0);
				int cy = NativeMethods.GetSystemMetrics(1);
				int x = Math.Max(0, (cx - w) / 2);
				int y = Math.Max(0, (cy - h) / 2);
				NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 21u);
				LogEverythingDiag($"[CenterWindowOnScreen] 已将授权对话框居中: hwnd=0x{hwnd:X8}");
			}
		}
	}

	private static void KillEverythingProcesses()
	{
		try
		{
			Process[] processesByName = Process.GetProcessesByName("Everything");
			foreach (Process p in processesByName)
			{
				try
				{
					p.Kill();
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			LogEverythingDiag("[KillEverythingProcesses] 异常: " + ex.Message);
		}
	}

	private static void EnsureEverythingConfig(string exePath)
	{
		try
		{
			ApplyTo(Path.Combine(Path.GetDirectoryName(exePath), Path.GetFileNameWithoutExtension(exePath) + ".ini"));
			string appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			if (!string.IsNullOrEmpty(appDataDir))
			{
				ApplyTo(Path.Combine(appDataDir, "Everything", "Everything.ini"));
			}
			try
			{
				using RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\Everything");
				try
				{
					key.DeleteValue("run_as_admin", throwOnMissingValue: false);
				}
				catch
				{
				}
				key.SetValue("check_for_updates_on_startup", 0, RegistryValueKind.DWord);
				key.SetValue("show_tray_icon", 0, RegistryValueKind.DWord);
				key.SetValue("show_in_taskbar", 0, RegistryValueKind.DWord);
				try
				{
					key.DeleteValue("startup_window", throwOnMissingValue: false);
				}
				catch
				{
				}
				try
				{
					key.DeleteValue("minimized", throwOnMissingValue: false);
				}
				catch
				{
				}
				LogEverythingDiag("[EnsureEverythingConfig] 已写入注册表 HKCU\\Software\\Everything");
			}
			catch (Exception ex)
			{
				LogEverythingDiag("[EnsureEverythingConfig] 注册表写入跳过: " + ex.Message);
			}
			try
			{
				using RegistryKey layers = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers", writable: true);
				layers?.DeleteValue(exePath, throwOnMissingValue: false);
				LogEverythingDiag("[EnsureEverythingConfig] 已清除 AppCompat RunAsAdmin 垫片: " + exePath);
			}
			catch (Exception ex2)
			{
				LogEverythingDiag("[EnsureEverythingConfig] 清除 AppCompat 垫片跳过: " + ex2.Message);
			}
		}
		catch (Exception ex3)
		{
			LogEverythingDiag("[EnsureEverythingConfig] 异常: " + ex3.Message);
		}
		static void ApplyTo(string iniPath)
		{
			try
			{
				List<string> l = (File.Exists(iniPath) ? new List<string>(File.ReadAllLines(iniPath)) : new List<string>());
				RemoveIniValue(l, "run_as_admin");
				SetIniValue(l, "check_for_updates_on_startup", "0");
				SetIniValue(l, "show_tray_icon", "0");
				SetIniValue(l, "show_in_taskbar", "0");
				RemoveIniValue(l, "minimized");
				RemoveIniValue(l, "startup_window");
				Directory.CreateDirectory(Path.GetDirectoryName(iniPath));
				File.WriteAllLines(iniPath, l);
				LogEverythingDiag("[EnsureEverythingConfig] 已写入配置: " + iniPath);
			}
			catch (Exception ex4)
			{
				LogEverythingDiag("[EnsureEverythingConfig] 写入失败(" + iniPath + "): " + ex4.Message);
			}
		}
	}

	private static void SetIniValue(List<string> lines, string key, string value)
	{
		bool inSection = false;
		for (int i = 0; i < lines.Count; i++)
		{
			string t = lines[i].Trim();
			if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal))
			{
				inSection = t.Equals("[Everything]", StringComparison.OrdinalIgnoreCase);
			}
			else if (inSection && t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
			{
				lines[i] = key + "=" + value;
				return;
			}
		}
		int idx = lines.FindIndex((string l) => l.Trim().Equals("[Everything]", StringComparison.OrdinalIgnoreCase));
		if (idx >= 0)
		{
			lines.Insert(idx + 1, key + "=" + value);
			return;
		}
		lines.Add("[Everything]");
		lines.Add(key + "=" + value);
	}

	private static void RemoveIniValue(List<string> lines, string key)
	{
		bool inSection = false;
		for (int i = lines.Count - 1; i >= 0; i--)
		{
			string t = lines[i].Trim();
			if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal))
			{
				inSection = t.Equals("[Everything]", StringComparison.OrdinalIgnoreCase);
			}
			else if (inSection && t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
			{
				lines.RemoveAt(i);
				break;
			}
		}
	}

	public static List<SearchResult>? Search(string keyword, uint max = 200u, string? searchPath = null, string? fileKind = null)
	{
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return new List<SearchResult>();
		}
		if (!EnsureEngineDllLoaded())
		{
			return null;
		}
		try
		{
			string query = keyword;
			if (!string.IsNullOrWhiteSpace(searchPath))
			{
				string p = searchPath.Trim().TrimEnd('\\');
				query = "path:\"" + p + "\\\" " + query;
			}
			if (!string.IsNullOrWhiteSpace(fileKind))
			{
				query = fileKind + " " + query;
			}
			Everything_SetSearchW(query);
			Everything_SetRequestFlags(87u);
			Everything_SetSort(14u);
			Everything_SetMatchCase(bEnable: false);
			Everything_SetRegex(bEnable: false);
			Everything_SetMax(max);
			if (!Everything_QueryW(bWait: true))
			{
				LogEverythingDiag("[Search] Everything_QueryW 返回 false | keyword=" + keyword);
				return null;
			}
			uint err = Everything_GetLastError();
			switch (err)
			{
			case 2u:
				LogEverythingDiag("[Search] 引擎未运行(IPC错误) | keyword=" + keyword);
				return null;
			default:
				LogEverythingDiag($"[Search] GetLastError={err}（非 IPC 非 OK）| keyword={keyword}");
				break;
			case 0u:
				break;
			}
			uint count = Everything_GetNumResults();
			List<SearchResult> list = new List<SearchResult>((int)count);
			StringBuilder buf = new StringBuilder(4096);
			for (uint i = 0u; i < count; i++)
			{
				buf.Clear();
				Everything_GetResultFullPathNameW(i, buf, (uint)buf.Capacity);
				string full = buf.ToString();
				SearchResult item = new SearchResult
				{
					FullPath = full,
					FileName = Path.GetFileName(full),
					IsFolder = Everything_IsFolderResult(i)
				};
				if (Everything_GetResultSize(i, out var size))
				{
					item.Size = size;
				}
				if (Everything_GetResultDateModified(i, out var ft) && ft > 0)
				{
					try
					{
						item.Modified = DateTime.FromFileTime(ft);
					}
					catch
					{
					}
				}
				list.Add(item);
			}
			return list;
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (BadImageFormatException)
		{
			return null;
		}
		catch
		{
			return null;
		}
		finally
		{
			try
			{
				Everything_CleanUp();
			}
			catch
			{
			}
		}
	}

	private static string FormatSize(long bytes)
	{
		if (bytes < 0)
		{
			return "";
		}
		string[] units = new string[5] { "B", "KB", "MB", "GB", "TB" };
		double s = bytes;
		int u = 0;
		while (s >= 1024.0 && u < units.Length - 1)
		{
			s /= 1024.0;
			u++;
		}
		if (u == 0)
		{
			return $"{bytes} {units[u]}";
		}
		return $"{s:0.##} {units[u]}";
	}
}
