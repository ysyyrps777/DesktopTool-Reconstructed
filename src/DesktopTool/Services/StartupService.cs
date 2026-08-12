using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DesktopTool.Services;

public static class StartupService
{
	[ComImport]
	[Guid("00021401-0000-0000-C000-000000000046")]
	private class ShellLink
	{
	}

	[ComImport]
	[Guid("000214EE-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellLinkW
	{
		void GetPath([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, nint pfd, uint fFlags);

		void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);

		void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

		void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

		void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
	}

	[ComImport]
	[Guid("0000010b-0000-0000-c000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPersistFile
	{
		void GetClassID(out Guid pClassId);

		void IsDirty();

		void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint mode);

		void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
	}

	private enum MsixStartupState
	{
		Disabled,
		DisabledByUser,
		Enabled,
		DisabledBySystem
	}

	private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	private const string StartupApprovedKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved";

	private const string AppName = "DesktopTool-Reconstructed";

	private static readonly string StartupFolder = ResolveStartupFolder();

	private static readonly string ShortcutPath = Path.Combine(StartupFolder, "DesktopTool-Reconstructed.lnk");

	private static readonly string DiagLogPath = Path.Combine(Path.GetTempPath(), "DesktopTool_Crash.log");

	private static readonly Guid FOLDERID_Startup = new Guid("B97D20BB-F46A-4C97-BA10-5E360843085E");

	private static string? _cachedExePath;

	private static readonly object _exePathLock = new object();

	private static readonly string RecoveryTaskName = "DesktopToolReconstructedRecovery";

	private static readonly string RecoveryBatchPath = Path.Combine(App.GetAppDataDir(), "recover.bat");

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

	private static string ResolveStartupFolder()
	{
		string folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
		if (IsValidAbsoluteDir(folder))
		{
			DiagLog("[StartupService] StartupFolder(托管) = " + folder);
			return folder;
		}
		try
		{
			if (SHGetKnownFolderPath(FOLDERID_Startup, 0u, IntPtr.Zero, out var pPath) == 0 && pPath != IntPtr.Zero)
			{
				string shellPath = Marshal.PtrToStringUni(pPath);
				Marshal.FreeCoTaskMem(pPath);
				if (IsValidAbsoluteDir(shellPath))
				{
					DiagLog($"[StartupService] StartupFolder(Shell兜底) = {shellPath}（托管返回={folder ?? "null"}）");
					return shellPath;
				}
			}
		}
		catch (Exception ex)
		{
			DiagLog("[StartupService] SHGetKnownFolderPath 异常: " + ex.Message);
		}
		DiagLog("[StartupService] StartupFolder 兜底后仍不可靠: " + (folder ?? "null"));
		return folder ?? string.Empty;
	}

	private static bool IsValidAbsoluteDir(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		if (!Path.IsPathRooted(path))
		{
			return false;
		}
		try
		{
			return Directory.Exists(path);
		}
		catch
		{
			return false;
		}
	}

	public static bool IsAutoStartEnabled()
	{
		bool shortcut = IsShortcutValid();
		bool reg = HasRegistryRunKey();
		bool approvedDisabled = IsStartupApprovedDisabled();
		bool num = reg && !approvedDisabled;
		DiagLog($"[StartupService] IsAutoStartEnabled: shortcut={shortcut}, reg={reg}, approvedDisabled={approvedDisabled}");
		if (num)
		{
			return true;
		}
		return shortcut;
	}

	private static bool IsStartupApprovedDisabled()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run", writable: false);
			if (key?.GetValue("DesktopTool-Reconstructed") is byte[] data)
			{
				return !IsStartupApprovedEnabled(data);
			}
		}
		catch
		{
		}
		return false;
	}

	public static (bool shortcutExists, bool regKeyExists, string shortcutTarget, string regValue) GetAutoStartDiagnostics()
	{
		string shortcutTarget = "";
		string regValue = "";
		try
		{
			if (File.Exists(ShortcutPath))
			{
				shortcutTarget = ResolveShortcutTarget(ShortcutPath) ?? "(无法解析)";
			}
		}
		catch
		{
		}
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: false);
			regValue = key?.GetValue("DesktopTool-Reconstructed")?.ToString() ?? "";
		}
		catch
		{
		}
		string approvedDiag = GetStartupApprovedDiagnostics();
		if (!string.IsNullOrEmpty(approvedDiag))
		{
			DiagLog("[StartupService] StartupApproved 诊断: " + approvedDiag);
		}
		return (shortcutExists: HasStartupShortcut(), regKeyExists: HasRegistryRunKey(), shortcutTarget: shortcutTarget, regValue: regValue);
	}

	public static (bool actual, string? errorDetails) SetAutoStart(bool enabled)
	{
		List<string> failures = new List<string>();
		(bool, string) scr = default((bool, string));
		(bool, string) reg = default((bool, string));
		if (enabled)
		{
			try
			{
				string exe = GetCurrentExePath();
				if (!string.IsNullOrEmpty(exe))
				{
					string root = Path.GetPathRoot(exe);
					if (!string.IsNullOrEmpty(root))
					{
						uint dt = NativeMethods.GetDriveType(root);
						if (dt != 3 && dt != 6)
						{
							return (actual: false, errorDetails: "当前程序位于可移动/U盘或网络位置，无法设置开机自启动。请将程序放到本地磁盘（如 C 盘）的固定目录后再开启。");
						}
					}
				}
			}
			catch (Exception ex)
			{
				DiagLog("[StartupService] 自启动本地盘检测异常: " + ex.Message);
			}
			scr = CreateStartupShortcut();
			if (!scr.Item1)
			{
				failures.Add("• Startup 快捷方式：" + scr.Item2);
			}
			reg = SetRegistryRunKey();
			if (!reg.Item1)
			{
				failures.Add("• 注册表 Run 键：" + reg.Item2);
			}
			if (scr.Item1 || reg.Item1)
			{
				WriteEnabledStartupApproved();
			}
			else
			{
				DeleteStartupApprovedEntries();
			}
		}
		else
		{
			DeleteStartupShortcut();
			DeleteRegistryRunKey();
			DeleteStartupApprovedEntries();
		}
		bool actual = IsAutoStartEnabled();
		if (actual != enabled && enabled && (scr.Item1 || reg.Item1))
		{
			DiagLog("[StartupService] SetAutoStart：即时验证失败但存在成功写入，600ms 后重试验证（消除 Shell 缓存延迟误报）");
			try
			{
				Thread.Sleep(600);
			}
			catch
			{
			}
			actual = IsAutoStartEnabled();
			if (actual == enabled)
			{
				DiagLog("[StartupService] SetAutoStart：重试验证成功，视为设置成功（不弹窗）");
			}
		}
		if (actual != enabled && enabled && reg.Item1 && !IsStartupApprovedDisabled())
		{
			DiagLog("[StartupService] SetAutoStart：Run 键已成功写入且未被禁用，视为设置成功（绕开 shortcut 解析 API 异常误判）");
			actual = true;
		}
		if (actual != enabled)
		{
			(bool, bool, string, string) diag = GetAutoStartDiagnostics();
			List<string> detailLines = new List<string>();
			string msg = $"[StartupService] SetAutoStart({enabled}) 操作后实际状态={actual}，可能被安全软件拦截";
			DiagLog(msg);
			if (enabled)
			{
				if (failures.Count > 0)
				{
					detailLines.AddRange(failures);
				}
				else
				{
					detailLines.Add("• 所有写入操作未报错，但验证失败");
					detailLines.Add("• 可能是安全软件（杀毒/防火墙）静默阻止了写入");
				}
				detailLines.Add("• EXE 路径：" + GetCurrentExePath());
				detailLines.Add("• Startup 快捷方式：" + (diag.Item1 ? "已创建" : "不存在"));
				detailLines.Add("• 注册表 Run 值：" + (diag.Item2 ? diag.Item4 : "不存在"));
			}
			else
			{
				detailLines.Add("• 关闭自启动时操作未报错，但验证仍显示已启用");
				detailLines.Add("• 可能是安全软件恢复了注册表或快捷方式");
			}
			string errorDetails = string.Join("\n", detailLines);
			try
			{
				EventLog.WriteEntry("DesktopTool-Reconstructed", msg + "\n" + errorDetails, EventLogEntryType.Warning, 1002);
			}
			catch
			{
			}
			return (actual: actual, errorDetails: errorDetails);
		}
		return (actual: actual, errorDetails: null);
	}

	public static void RefreshAutoStartPaths()
	{
		if (HasRegistryRunKey())
		{
			SetRegistryRunKey();
		}
		if (HasStartupShortcut())
		{
			CreateStartupShortcut();
		}
	}

	public static void EnsureSilentArgInRegistryKey()
	{
		try
		{
			if (!HasRegistryRunKey())
			{
				return;
			}
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: false);
			string currentValue = key?.GetValue("DesktopTool-Reconstructed")?.ToString() ?? "";
			if (!string.IsNullOrWhiteSpace(currentValue))
			{
				if (currentValue.Contains("--silent"))
				{
					DiagLog("[StartupService] EnsureSilentArg: 注册表 Run 键已包含 --silent，无需更新");
					return;
				}
				DiagLog("[StartupService] EnsureSilentArg: 注册表 Run 键不含 --silent（当前值=" + currentValue + "），更新为新格式");
				SetRegistryRunKey();
			}
		}
		catch (Exception ex)
		{
			DiagLog("[StartupService] EnsureSilentArg 异常: " + ex.Message);
		}
	}

	public static bool TryRestoreAutoStartFromIntent(bool userIntent)
	{
		if (!userIntent)
		{
			return false;
		}
		if (IsAutoStartEnabled())
		{
			return false;
		}
		DiagLog("[StartupService] ★ TryRestoreAutoStartFromIntent: 检测到用户意图启用但实际未启用，尝试恢复…");
		var (actual, errorDetails) = SetAutoStart(enabled: true);
		if (actual)
		{
			DiagLog("[StartupService] ★ 自启动恢复成功（用户意图驱动）");
		}
		else
		{
			DiagLog("[StartupService] ★ 自启动恢复失败：" + (errorDetails ?? "未知原因"));
		}
		return true;
	}

	public static void EnsureAutoStart()
	{
		if (!IsAutoStartEnabled())
		{
			SetAutoStart(enabled: true);
			return;
		}
		CreateStartupShortcut();
		SetRegistryRunKey();
	}

	private static bool HasStartupShortcut()
	{
		try
		{
			return File.Exists(ShortcutPath);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsShortcutValid()
	{
		try
		{
			if (!File.Exists(ShortcutPath))
			{
				return false;
			}
			string target = ResolveShortcutTarget(ShortcutPath);
			if (string.IsNullOrEmpty(target))
			{
				return false;
			}
			if (!target.EndsWith("DesktopTool.exe", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			return File.Exists(target);
		}
		catch
		{
			return false;
		}
	}

	private static (bool Success, string? Error) CreateStartupShortcut()
	{
		try
		{
			string exe = GetCurrentExePath();
			if (string.IsNullOrEmpty(exe))
			{
				return (Success: false, Error: "无法获取当前程序路径（ProcessPath 为空）");
			}
			if (!File.Exists(exe))
			{
				return (Success: false, Error: "EXE 文件不存在：" + exe);
			}
			Directory.CreateDirectory(StartupFolder);
			if (File.Exists(ShortcutPath))
			{
				if (string.Equals(ResolveShortcutTarget(ShortcutPath), exe, StringComparison.OrdinalIgnoreCase))
				{
					return (Success: true, Error: null);
				}
				try
				{
					File.Delete(ShortcutPath);
				}
				catch
				{
				}
			}
			try
			{
				CreateShortcut(ShortcutPath, exe, "", "DesktopTool - 桌面收纳工具");
			}
			catch (Exception ex)
			{
				DiagLog("[StartupService] COM 创建快捷方式异常: " + ex.Message + "，将改用 PowerShell");
			}
			if (!File.Exists(ShortcutPath))
			{
				try
				{
					CreateShortcutViaPowerShell(ShortcutPath, exe);
				}
				catch (Exception ex2)
				{
					DiagLog("[StartupService] PowerShell 创建异常: " + ex2.Message);
				}
			}
			string resolved = ResolveShortcutTarget(ShortcutPath);
			if (string.IsNullOrEmpty(resolved) && File.Exists(ShortcutPath))
			{
				Thread.Sleep(120);
				resolved = ResolveShortcutTarget(ShortcutPath);
				if (!string.IsNullOrEmpty(resolved))
				{
					DiagLog("[StartupService] Shell 瞬时读取空，重试解析后已恢复");
				}
			}
			if (string.IsNullOrEmpty(resolved) || !string.Equals(resolved, exe, StringComparison.OrdinalIgnoreCase))
			{
				bool fileExists = File.Exists(ShortcutPath);
				DiagLog($"[StartupService] ★ 快捷方式 Target 校验失败（实际={resolved ?? "null"}，文件存在={fileExists}），删除并用 PowerShell 重建");
				try
				{
					if (fileExists)
					{
						File.Delete(ShortcutPath);
					}
				}
				catch
				{
				}
				try
				{
					CreateShortcutViaPowerShell(ShortcutPath, exe);
				}
				catch (Exception ex3)
				{
					DiagLog("[StartupService] PowerShell 重建异常: " + ex3.Message);
				}
				resolved = ResolveShortcutTarget(ShortcutPath);
			}
			if (string.IsNullOrEmpty(resolved))
			{
				bool exists = File.Exists(ShortcutPath);
				return (Success: false, Error: exists ? ("快捷方式目标无法解析（文件已生成但 Target 读取为空，可能受安全软件/Shell 钩子干扰）：" + ShortcutPath) : ("快捷方式创建失败（文件未生成，可能被安全软件拦截或 PowerShell 不可用）：" + ShortcutPath));
			}
			if (!string.Equals(resolved, exe, StringComparison.OrdinalIgnoreCase))
			{
				return (Success: false, Error: $"快捷方式目标不匹配（期望：{exe}，实际：{resolved}）");
			}
			return (Success: true, Error: null);
		}
		catch (UnauthorizedAccessException ex4)
		{
			return (Success: false, Error: "权限不足，无法写入 Startup 文件夹\n  " + StartupFolder + "\n  " + ex4.Message);
		}
		catch (COMException ex5)
		{
			return (Success: false, Error: "Shell 快捷方式创建失败（COM）：" + ex5.Message);
		}
		catch (IOException ex6)
		{
			return (Success: false, Error: "文件操作失败：" + ex6.Message + "\n  Startup 路径：" + StartupFolder);
		}
		catch (Exception ex7)
		{
			return (Success: false, Error: ex7.GetType().Name + "：" + ex7.Message);
		}
	}

	private static void CreateShortcutViaPowerShell(string shortcutPath, string targetPath, int retries = 1)
	{
		string wsDir = Path.GetDirectoryName(targetPath) ?? "";
		string psScript = $"\r\n$ws = New-Object -ComObject WScript.Shell\r\n$shortcut = $ws.CreateShortcut('{shortcutPath.Replace("'", "''")}')\r\n$shortcut.TargetPath = '{targetPath.Replace("'", "''")}'\r\n$shortcut.Description = 'DesktopTool - 桌面收纳工具'\r\n$shortcut.WorkingDirectory = '{wsDir.Replace("'", "''")}'\r\n$shortcut.Save()\r\nif (-not (Test-Path '{shortcutPath.Replace("'", "''")}')) {{ throw '快捷方式保存后文件仍不存在' }}\r\n";
		for (int attempt = 0; attempt <= retries; attempt++)
		{
			if (attempt > 0)
			{
				DiagLog($"[StartupService] PowerShell 创建重试 ({attempt}/{retries})");
			}
			string tmpPs1;
			try
			{
				tmpPs1 = Path.Combine(Path.GetTempPath(), $"dt_sc_{Guid.NewGuid():N}.ps1");
				File.WriteAllText(tmpPs1, psScript, Encoding.UTF8);
			}
			catch (Exception ex)
			{
				DiagLog("[StartupService] PowerShell 脚本写入临时文件失败: " + ex.Message);
				continue;
			}
			ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + tmpPs1 + "\"")
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardError = true
			};
			try
			{
				using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 powershell.exe");
				string err = proc.StandardError.ReadToEnd();
				proc.WaitForExit(8000);
				for (int i = 0; i < 20; i++)
				{
					if (File.Exists(shortcutPath))
					{
						break;
					}
					Thread.Sleep(50);
				}
				if (File.Exists(shortcutPath))
				{
					break;
				}
				if (!string.IsNullOrWhiteSpace(err))
				{
					DiagLog("[StartupService] PowerShell stderr: " + err.Trim());
				}
			}
			catch (Exception ex2)
			{
				DiagLog($"[StartupService] PowerShell 创建异常(attempt {attempt}): {ex2.Message}");
			}
			finally
			{
				try
				{
					if (tmpPs1 != null && File.Exists(tmpPs1))
					{
						File.Delete(tmpPs1);
					}
				}
				catch
				{
				}
			}
		}
	}

	private static void DeleteStartupShortcut()
	{
		try
		{
			if (File.Exists(ShortcutPath))
			{
				File.Delete(ShortcutPath);
				DiagLog("[StartupService] 已删除 Startup 快捷方式: " + ShortcutPath);
			}
			else
			{
				DiagLog("[StartupService] Startup 快捷方式不存在（无需删除）");
			}
		}
		catch (Exception ex)
		{
			DiagLog("[StartupService] 删除 Startup 快捷方式失败: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static void WriteEnabledStartupApproved()
	{
		EnsureWriteEnabledStartupApproved("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run", "DesktopTool-Reconstructed");
		FixStartupApprovedForShortcut();
		NotifyExplorerStartupChanged();
		if (!VerifyStartupApprovedWritten("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run", "DesktopTool-Reconstructed"))
		{
			DiagLog("[StartupService] ★★★ StartupApproved 验证失败，任务管理器可能不显示该启动项！");
		}
	}

	private static void EnsureWriteEnabledStartupApproved(string subKey, string valueName)
	{
		byte[] enabledData = new byte[12]
		{
			2, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0
		};
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
			if (key != null)
			{
				key.SetValue(valueName, enabledData, RegistryValueKind.Binary);
				return;
			}
		}
		catch (Exception ex)
		{
			DiagLog("[StartupService] StartupApproved OpenSubKey 失败：" + ex.Message);
		}
		try
		{
			using RegistryKey createdKey = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
			if (createdKey != null)
			{
				createdKey.SetValue(valueName, enabledData, RegistryValueKind.Binary);
				if (!VerifyStartupApprovedWritten(subKey, valueName))
				{
					DiagLog("[StartupService] StartupApproved 创建后验证失败");
				}
				return;
			}
		}
		catch (Exception ex2)
		{
			DiagLog("[StartupService] StartupApproved CreateSubKey 失败：" + ex2.Message);
		}
		try
		{
			Registry.SetValue("HKEY_CURRENT_USER\\" + subKey, valueName, enabledData, RegistryValueKind.Binary);
		}
		catch (Exception ex3)
		{
			DiagLog("[StartupService] StartupApproved Registry.SetValue 也失败：" + ex3.Message);
		}
	}

	private static bool VerifyStartupApprovedWritten(string subKey, string valueName)
	{
		try
		{
			using RegistryKey readKey = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
			if (readKey == null)
			{
				return false;
			}
			if (!(readKey.GetValue(valueName) is byte[] data) || data.Length < 4)
			{
				return false;
			}
			return IsStartupApprovedEnabled(data);
		}
		catch
		{
			return false;
		}
	}

	private static void FixStartupApprovedForShortcut()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\StartupFolder", writable: true);
			if (key == null)
			{
				return;
			}
			byte[] enabledData = new byte[12]
			{
				2, 0, 0, 0, 0, 0, 0, 0, 0, 0,
				0, 0
			};
			int fixedCount = 0;
			string[] valueNames = key.GetValueNames();
			foreach (string valueName in valueNames)
			{
				if ((valueName.IndexOf("DesktopTool-Reconstructed", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(valueName, ShortcutPath, StringComparison.OrdinalIgnoreCase) || valueName.EndsWith("DesktopTool-Reconstructed.lnk", StringComparison.OrdinalIgnoreCase)) && key.GetValue(valueName) is byte[] existing && existing.Length >= 4 && !IsStartupApprovedEnabled(existing))
				{
					key.SetValue(valueName, enabledData, RegistryValueKind.Binary);
					fixedCount++;
					DiagLog("[StartupService] StartupApproved\\StartupFolder 修复条目: " + valueName);
				}
			}
			if (fixedCount > 0)
			{
				DiagLog($"[StartupService] StartupApproved\\StartupFolder: 修复了 {fixedCount} 个条目");
			}
		}
		catch
		{
		}
	}

	private static void NotifyExplorerStartupChanged()
	{
		try
		{
			NativeMethods.SHChangeNotify(134217728u, 0u, 0, 0);
			DiagLog("[StartupService] ★ SHChangeNotify(SHCNE_ASSOCCHANGED) 已发送");
		}
		catch (Exception ex)
		{
			DiagLog("[StartupService] SHChangeNotify 失败: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static void DeleteStartupApprovedEntries()
	{
		try
		{
			using RegistryKey runKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run", writable: true);
			if (runKey != null && runKey.GetValue("DesktopTool-Reconstructed") != null)
			{
				runKey.DeleteValue("DesktopTool-Reconstructed", throwOnMissingValue: false);
				DiagLog("[StartupService] 已清除 StartupApproved\\Run\\DesktopTool");
			}
		}
		catch (Exception ex)
		{
			DiagLog("[StartupService] 清除 StartupApproved\\Run 失败: " + ex.GetType().Name + ": " + ex.Message);
		}
		try
		{
			using RegistryKey folderKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\StartupFolder", writable: true);
			if (folderKey == null)
			{
				return;
			}
			string[] valueNames = folderKey.GetValueNames();
			foreach (string name in valueNames)
			{
				if (name.IndexOf("DesktopTool-Reconstructed", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(name, ShortcutPath, StringComparison.OrdinalIgnoreCase) || name.EndsWith("DesktopTool-Reconstructed.lnk", StringComparison.OrdinalIgnoreCase))
				{
					folderKey.DeleteValue(name, throwOnMissingValue: false);
					DiagLog("[StartupService] 已清除 StartupApproved\\StartupFolder\\" + name);
				}
			}
		}
		catch (Exception ex2)
		{
			DiagLog("[StartupService] 清除 StartupApproved\\StartupFolder 失败: " + ex2.GetType().Name + ": " + ex2.Message);
		}
	}

	public static string GetStartupApprovedDiagnostics()
	{
		List<string> results = new List<string>();
		try
		{
			using RegistryKey runKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run", writable: false);
			if (runKey?.GetValue("DesktopTool-Reconstructed") is byte[] runBytes)
			{
				string status = (IsStartupApprovedEnabled(runBytes) ? "已启用" : "已禁用");
				string hex = BitConverter.ToString(runBytes);
				string typeHex = ((runBytes.Length >= 4) ? $"0x{runBytes[0]:X2}{runBytes[1]:X2}{runBytes[2]:X2}{runBytes[3]:X2}" : "?");
				results.Add($"StartupApproved\\Run\\{"DesktopTool-Reconstructed"}: {status} type={typeHex} (raw={hex})");
			}
			else
			{
				results.Add("StartupApproved\\Run\\DesktopTool: (不存在)");
			}
		}
		catch
		{
		}
		try
		{
			using RegistryKey folderKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\StartupFolder", writable: false);
			if (folderKey?.GetValue("DesktopTool-Reconstructed") is byte[] folderBytes)
			{
				string status2 = (IsStartupApprovedEnabled(folderBytes) ? "已启用" : "已禁用");
				string hex2 = BitConverter.ToString(folderBytes);
				string typeHex2 = ((folderBytes.Length >= 4) ? $"0x{folderBytes[0]:X2}{folderBytes[1]:X2}{folderBytes[2]:X2}{folderBytes[3]:X2}" : "?");
				results.Add($"StartupApproved\\StartupFolder\\{"DesktopTool-Reconstructed"}: {status2} type={typeHex2} (raw={hex2})");
			}
			else
			{
				results.Add("StartupApproved\\StartupFolder\\DesktopTool: (不存在)");
			}
		}
		catch
		{
		}
		return string.Join("; ", results);
	}

	private static bool IsStartupApprovedEnabled(byte[] data)
	{
		if (data == null || data.Length < 4)
		{
			return true;
		}
		return ((data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24)) & 1) == 0;
	}

	private static bool RunValueMatches(string? runValue)
	{
		if (string.IsNullOrWhiteSpace(runValue))
		{
			return false;
		}
		string exe = GetCurrentExePath();
		if (!string.IsNullOrEmpty(exe) && runValue.Contains(exe, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return runValue.IndexOf("DesktopTool.exe", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool HasRegistryRunKey()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: false);
			if (key == null)
			{
				return false;
			}
			object val = key.GetValue("DesktopTool-Reconstructed");
			if (val == null)
			{
				return false;
			}
			string str = val.ToString();
			if (string.IsNullOrWhiteSpace(str))
			{
				return false;
			}
			if (RunValueMatches(str))
			{
				return true;
			}
			DiagLog("[StartupService] HasRegistryRunKey：首次读取未匹配，120ms 后重试一次（值=\"" + str + "\"）");
			Thread.Sleep(120);
			using RegistryKey retryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: false);
			if (RunValueMatches(retryKey?.GetValue("DesktopTool-Reconstructed")?.ToString()))
			{
				DiagLog("[StartupService] HasRegistryRunKey：重试后匹配成功（Shell 缓存已刷新）");
				return true;
			}
			DiagLog("[StartupService] HasRegistryRunKey：值存在但内容不匹配（实际=\"" + str + "\"）");
			return false;
		}
		catch
		{
			return false;
		}
	}

	private static (bool Success, string? Error) SetRegistryRunKey()
	{
		try
		{
			string exe = GetCurrentExePath();
			if (string.IsNullOrEmpty(exe))
			{
				return (Success: false, Error: "无法获取当前程序路径（ProcessPath 为空）");
			}
			if (!File.Exists(exe))
			{
				return (Success: false, Error: "EXE 文件不存在：" + exe);
			}
			string runValue = exe + " --silent";
			bool methodASuccess = false;
			try
			{
				using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
				if (key == null)
				{
					DiagLog("[StartupService] SetRegistryRunKey 方法A：OpenSubKey 返回 null");
				}
				else
				{
					key.SetValue("DesktopTool-Reconstructed", runValue, RegistryValueKind.String);
					DiagLog("[StartupService] SetRegistryRunKey 方法A：写入成功");
					methodASuccess = true;
				}
			}
			catch (Exception ex)
			{
				DiagLog("[StartupService] SetRegistryRunKey 方法A 异常：" + ex.GetType().Name + ": " + ex.Message);
			}
			if (!methodASuccess)
			{
				try
				{
					Registry.SetValue("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run", "DesktopTool-Reconstructed", runValue, RegistryValueKind.String);
					DiagLog("[StartupService] SetRegistryRunKey 方法B（静态方法）：写入成功");
				}
				catch (Exception ex2)
				{
					DiagLog("[StartupService] SetRegistryRunKey 方法B 异常：" + ex2.GetType().Name + ": " + ex2.Message);
					return (Success: false, Error: $"注册表写入失败（方法A+B均失败）：\n  HKCU\\{"Software\\Microsoft\\Windows\\CurrentVersion\\Run"}\\{"DesktopTool-Reconstructed"}\n  {ex2.Message}");
				}
			}
			bool verified = false;
			try
			{
				using RegistryKey verifyKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: false);
				string verifyValue = verifyKey?.GetValue("DesktopTool-Reconstructed")?.ToString();
				if (!string.IsNullOrEmpty(verifyValue))
				{
					if (RunValueMatches(verifyValue))
					{
						verified = true;
						DiagLog("[StartupService] 注册表写入验证通过（新句柄读取）：实际值=" + verifyValue);
					}
					else
					{
						DiagLog("[StartupService] 注册表写入验证失败：值不匹配（实际=\"" + verifyValue + "\"）");
					}
				}
				else
				{
					DiagLog("[StartupService] 注册表写入验证失败：新句柄读取值为空或 null");
				}
			}
			catch (Exception ex3)
			{
				DiagLog("[StartupService] 注册表验证异常（新句柄）：" + ex3.GetType().Name + ": " + ex3.Message);
			}
			if (!verified)
			{
				try
				{
					DiagLog("[StartupService] 注册表验证失败，300ms 后重试一次写入+验证");
					Thread.Sleep(300);
					using (RegistryKey key2 = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true))
					{
						key2?.SetValue("DesktopTool-Reconstructed", runValue, RegistryValueKind.String);
					}
					using RegistryKey retryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: false);
					if (RunValueMatches(retryKey?.GetValue("DesktopTool-Reconstructed")?.ToString()))
					{
						verified = true;
						DiagLog("[StartupService] 注册表重试写入验证通过");
					}
				}
				catch (Exception ex4)
				{
					DiagLog("[StartupService] 注册表重试异常：" + ex4.Message);
				}
			}
			if (!verified)
			{
				return (Success: false, Error: "注册表写入后验证失败：多次重试仍未读到正确的值\n  HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\DesktopTool\n  可能是安全软件静默拦截了写入（建议：将 DesktopTool 加入杀毒/电脑管家/火绒白名单，或以管理员身份运行本程序）");
			}
			return (Success: true, Error: null);
		}
		catch (UnauthorizedAccessException ex5)
		{
			return (Success: false, Error: $"权限不足，无法写入注册表\n  HKCU\\{"Software\\Microsoft\\Windows\\CurrentVersion\\Run"}\\{"DesktopTool-Reconstructed"}\n  {ex5.Message}");
		}
		catch (SecurityException ex6)
		{
			return (Success: false, Error: "注册表安全限制：" + ex6.Message + "\n  路径：HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run");
		}
		catch (Exception ex7)
		{
			return (Success: false, Error: ex7.GetType().Name + "：" + ex7.Message);
		}
	}

	private static void DeleteRegistryRunKey()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
			if (key != null)
			{
				key.DeleteValue("DesktopTool-Reconstructed", throwOnMissingValue: false);
				DiagLog("[StartupService] 已删除注册表 Run 键: Software\\Microsoft\\Windows\\CurrentVersion\\Run\\DesktopTool");
			}
			else
			{
				DiagLog("[StartupService] 注册表 Run 键不存在（无需删除）");
			}
		}
		catch (Exception ex)
		{
			DiagLog("[StartupService] 删除注册表 Run 键失败: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static string GetCurrentExePath()
	{
		lock (_exePathLock)
		{
			if (!string.IsNullOrEmpty(_cachedExePath) && File.Exists(_cachedExePath))
			{
				return _cachedExePath;
			}
			string foundPath = null;
			try
			{
				string p = Process.GetCurrentProcess().MainModule?.FileName;
				if (!string.IsNullOrEmpty(p) && File.Exists(p))
				{
					foundPath = p;
				}
			}
			catch
			{
			}
			if (foundPath == null)
			{
				try
				{
					string p2 = Environment.ProcessPath;
					if (!string.IsNullOrEmpty(p2))
					{
						string cleaned = (p2.Contains('\0') ? p2.Split('\0')[0] : p2);
						if (File.Exists(cleaned))
						{
							foundPath = cleaned;
						}
					}
				}
				catch
				{
				}
			}
			if (foundPath == null)
			{
				try
				{
					string p3 = Assembly.GetExecutingAssembly().Location;
					if (!string.IsNullOrEmpty(p3) && File.Exists(p3))
					{
						foundPath = p3;
					}
				}
				catch
				{
				}
			}
			if (foundPath == null)
			{
				try
				{
					string baseDir = AppContext.BaseDirectory;
					if (!string.IsNullOrEmpty(baseDir))
					{
						string procName = Process.GetCurrentProcess().ProcessName;
						string[] files = Directory.GetFiles(baseDir, "*.exe");
						foreach (string f in files)
						{
							if (string.Equals(Path.GetFileNameWithoutExtension(f), procName, StringComparison.OrdinalIgnoreCase) && File.Exists(f))
							{
								foundPath = f;
								break;
							}
						}
					}
				}
				catch
				{
				}
			}
			if (!string.IsNullOrEmpty(foundPath))
			{
				_cachedExePath = foundPath;
				return foundPath;
			}
			DiagLog("[StartupService] GetCurrentExePath: ★★★ 全部方法都失败，无法获取 EXE 路径！");
			return string.Empty;
		}
	}

	private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string description)
	{
		IShellLinkW lnk = (IShellLinkW)new ShellLink();
		lnk.SetPath(targetPath);
		lnk.SetArguments(arguments);
		lnk.SetDescription(description);
		string dir = Path.GetDirectoryName(targetPath);
		if (!string.IsNullOrEmpty(dir))
		{
			lnk.SetWorkingDirectory(dir);
		}
		((IPersistFile)lnk).Save(shortcutPath, fRemember: false);
	}

	private static string? ResolveShortcutTarget(string lnkPath)
	{
		try
		{
			IShellLinkW obj = (IShellLinkW)new ShellLink();
			((IPersistFile)obj).Load(lnkPath, 0u);
			StringBuilder sb = new StringBuilder(260);
			obj.GetPath(sb, sb.Capacity, IntPtr.Zero, 0u);
			string result = sb.ToString();
			if (result.Contains('\0'))
			{
				result = result.Split('\0')[0];
			}
			if (!string.IsNullOrEmpty(result) && result.Length >= 4)
			{
				return result;
			}
		}
		catch
		{
		}
		try
		{
			Thread.Sleep(150);
			IShellLinkW obj3 = (IShellLinkW)new ShellLink();
			((IPersistFile)obj3).Load(lnkPath, 0u);
			StringBuilder sb2 = new StringBuilder(260);
			obj3.GetPath(sb2, sb2.Capacity, IntPtr.Zero, 0u);
			string r2 = sb2.ToString();
			if (r2.Contains('\0'))
			{
				r2 = r2.Split('\0')[0];
			}
			if (!string.IsNullOrEmpty(r2) && r2.Length >= 4)
			{
				DiagLog("[StartupService] ResolveShortcutTarget：首次解析空，重试成功");
				return r2;
			}
		}
		catch
		{
		}
		string bin = ResolveShortcutTargetBinary(lnkPath);
		if (!string.IsNullOrEmpty(bin))
		{
			return bin;
		}
		return null;
	}

	private static string? ResolveShortcutTargetBinary(string lnkPath)
	{
		try
		{
			if (!File.Exists(lnkPath))
			{
				return null;
			}
			byte[] bytes = File.ReadAllBytes(lnkPath);
			if (bytes.Length < 76)
			{
				return null;
			}
			List<string> candidates = new List<string>();
			int i = 0;
			while (i + 1 < bytes.Length)
			{
				char c = (char)(bytes[i] | (bytes[i + 1] << 8));
				if (char.IsLetterOrDigit(c) || c == ':' || c == '\\' || c == '.' || c == ' ' || c == '-' || c == '_' || (c > '一' && c < '鿿'))
				{
					StringBuilder sb = new StringBuilder();
					int j;
					for (j = i; j + 1 < bytes.Length; j += 2)
					{
						char cc = (char)(bytes[j] | (bytes[j + 1] << 8));
						if (!char.IsLetterOrDigit(cc) && cc != ':' && cc != '\\' && cc != '.' && cc != ' ' && cc != '-' && cc != '_' && (cc <= '一' || cc >= '鿿'))
						{
							break;
						}
						sb.Append(cc);
					}
					string s = sb.ToString().Trim();
					if (s.Length >= 4 && (s.Contains('\\') || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
					{
						candidates.Add(s);
					}
					i = j + 2;
				}
				else
				{
					i += 2;
				}
			}
			foreach (string c2 in candidates)
			{
				if (c2.IndexOf("DesktopTool.exe", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return c2;
				}
			}
			foreach (string c3 in candidates)
			{
				if (c3.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
				{
					return c3;
				}
			}
		}
		catch (Exception ex)
		{
			DiagLog("[StartupService] ResolveShortcutTargetBinary 异常: " + ex.Message);
		}
		return null;
	}

	public static void LogScheduledTaskDiagnostics()
	{
		try
		{
			bool taskExists = CheckScheduledTaskExists();
			DiagLog($"[StartupService] ScheduledTask 'DesktopToolReconstructedRecovery': Exists={taskExists}");
			if (taskExists)
			{
				bool hasDelay = CheckScheduledTaskHasDelay();
				DiagLog($"[StartupService] ScheduledTask HasDelay={hasDelay}");
			}
		}
		catch (Exception ex)
		{
			DiagLog("[StartupService] LogScheduledTaskDiagnostics 异常: " + ex.Message);
		}
	}

	public static async Task FixRecoveryScheduledTaskAsync()
	{
		await Task.Run(delegate
		{
			try
			{
				if (!CheckScheduledTaskExists())
				{
					DiagLog("[StartupService] FixRecoveryTask: 任务不存在，无需修复");
				}
				else if (CheckScheduledTaskHasDelay())
				{
					DiagLog("[StartupService] FixRecoveryTask: 任务已有延迟，无需修复");
				}
				else
				{
					DiagLog("[StartupService] ★ 检测到旧版计划任务（无 30s 延迟），正在修复…");
					DeleteAndRecreateScheduledTask();
					DiagLog("[StartupService] ★ 计划任务修复完成（已添加 30s 冷启动延迟）");
				}
			}
			catch (Exception ex)
			{
				DiagLog("[StartupService] FixRecoveryTask 异常: " + ex.Message);
			}
		});
	}

	private static bool CheckScheduledTaskExists()
	{
		try
		{
			using Process proc = Process.Start(new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + RecoveryTaskName + "\"")
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			proc?.WaitForExit(3000);
			return proc != null && proc.ExitCode == 0;
		}
		catch
		{
			return false;
		}
	}

	private static bool CheckScheduledTaskHasDelay()
	{
		try
		{
			using Process proc = Process.Start(new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + RecoveryTaskName + "\" /FO CSV /V")
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			string output = proc?.StandardOutput.ReadToEnd();
			proc?.WaitForExit(5000);
			if (!string.IsNullOrEmpty(output))
			{
				return output.Contains("00:00:30") || output.Contains("0000:30");
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	private static void DeleteAndRecreateScheduledTask()
	{
		try
		{
			using Process proc = Process.Start(new ProcessStartInfo("schtasks.exe", "/Delete /TN \"" + RecoveryTaskName + "\" /F")
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			proc?.WaitForExit(5000);
			DiagLog($"[StartupService] 删除旧任务: exitCode={proc?.ExitCode}");
		}
		catch (Exception ex)
		{
			DiagLog("[StartupService] 删除旧任务失败: " + ex.Message);
			return;
		}
		try
		{
			string createArgs = $"/Create /TN \"{RecoveryTaskName}\" /SC ONLOGON /DELAY 0000:30 /TR \"\\\"{RecoveryBatchPath}\\\"\" /F /RL LIMITED";
			using Process proc2 = Process.Start(new ProcessStartInfo("schtasks.exe", createArgs)
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			proc2?.WaitForExit(5000);
			DiagLog($"[StartupService] 重新创建任务(带延迟): exitCode={proc2?.ExitCode}");
		}
		catch (Exception ex2)
		{
			DiagLog("[StartupService] 重新创建任务失败: " + ex2.Message);
		}
	}

	private static void DiagLog(string message)
	{
		LogHelper.Append(DiagLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
	}

	public static void LogAllStartupItems()
	{
		try
		{
			DiagLog("[StartupService] === 枚举当前用户启动项 ===");
			string[] valueNames;
			using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: false))
			{
				if (runKey != null)
				{
					valueNames = runKey.GetValueNames();
					foreach (string name in valueNames)
					{
						DiagLog($"[StartupService] Run\\{name} = {runKey.GetValue(name)}");
					}
				}
			}
			try
			{
				valueNames = Directory.GetFiles(StartupFolder, "*.lnk");
				foreach (string f in valueNames)
				{
					string t = ResolveShortcutTarget(f);
					DiagLog("[StartupService] Startup\\" + Path.GetFileName(f) + " -> " + (t ?? "(无法解析)"));
				}
			}
			catch
			{
			}
			valueNames = new string[2] { "Run", "StartupFolder" };
			foreach (string sub in valueNames)
			{
				using RegistryKey ak = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\" + sub, writable: false);
				if (ak != null)
				{
					string[] valueNames2 = ak.GetValueNames();
					foreach (string name2 in valueNames2)
					{
						string status = ((ak.GetValue(name2) is byte[] b && b.Length >= 4) ? (IsStartupApprovedEnabled(b) ? "启用" : "禁用") : "?");
						DiagLog($"[StartupService] StartupApproved\\{sub}\\{name2} = {status}");
					}
				}
			}
		}
		catch (Exception ex)
		{
			DiagLog("[StartupService] LogAllStartupItems 异常: " + ex.Message);
		}
	}

	public static bool IsPackaged()
	{
		return false;
	}
}
