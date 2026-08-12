using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopTool.Models;
using DesktopTool.ViewModels;

namespace DesktopTool.Services;

public static class CrashRecoveryService
{
	private static readonly string LockFilePath = Path.Combine(Path.GetTempPath(), "DesktopTool_Running.lock");

	private static readonly string PersistentLogPath = Path.Combine(App.GetAppDataDir(), "debug_crashrecovery.log");

	public static Task? RecoveryTask { get; set; }

	private static void PersistentLog(string message)
	{
		string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CrashRecovery] {message}";
		LogHelper.Append(PersistentLogPath, line + Environment.NewLine);
	}

	public static bool DetectAbnormalExit(MainViewModel? mainVm)
	{
		bool lockFileExists = File.Exists(LockFilePath);
		bool wasAbnormalExit = lockFileExists;
		int zonesCount = (mainVm?.Zones?.Count).GetValueOrDefault();
		PersistentLog($"DetectAbnormalExit: lock存在={lockFileExists}, zones数量={zonesCount}, SkipHidingForStartup初始值={mainVm?.SkipHidingForStartup ?? false}");
		if (lockFileExists)
		{
			bool isStaleLock = false;
			try
			{
				string pidText = File.ReadAllText(LockFilePath, Encoding.UTF8).Trim();
				if (int.TryParse(pidText, out var lockPid) && lockPid > 0)
				{
					try
					{
						using Process proc = Process.GetProcessById(lockPid);
						isStaleLock = false;
						PersistentLog($"锁文件 PID={lockPid} 仍在运行（进程名={proc.ProcessName}），视为真正的异常退出");
					}
					catch (ArgumentException)
					{
						isStaleLock = true;
						PersistentLog($"锁文件 PID={lockPid} 已不存在 → 僵尸锁文件（非真正崩溃）");
					}
					catch (InvalidOperationException)
					{
						isStaleLock = true;
						PersistentLog($"锁文件 PID={lockPid} 已退出 → 僵尸锁文件（非真正崩溃）");
					}
				}
				else
				{
					PersistentLog("锁文件内容无效 '" + pidText + "'，视为僵尸锁");
					isStaleLock = true;
				}
			}
			catch (Exception ex3)
			{
				PersistentLog("读取锁文件失败: " + ex3.Message + "，保守视为异常退出");
				isStaleLock = false;
			}
			if (isStaleLock)
			{
				wasAbnormalExit = false;
				try
				{
					File.Delete(LockFilePath);
				}
				catch (Exception ex4)
				{
					PersistentLog("清理僵尸锁文件失败: " + ex4.Message);
				}
				PersistentLog("僵尸锁已清理，本次启动不执行 CrashRecovery");
			}
			else
			{
				PersistentLog("检测到上次异常退出（lock文件残留），进程可能上次被强制终止或崩溃");
				if (mainVm != null && HasHiddenFilesForRecovery(mainVm))
				{
					mainVm.SkipHidingForStartup = true;
					PersistentLog("有隐藏文件需恢复 → SkipHidingForStartup=true");
				}
				else
				{
					PersistentLog("无隐藏文件需恢复（覆盖安装或已手动恢复） → SkipHidingForStartup=false");
				}
			}
		}
		else
		{
			PersistentLog("锁文件不存在，上次为正常退出");
		}
		CreateLockFile();
		PersistentLog($"已创建本次运行的锁文件 PID={Environment.ProcessId}");
		return wasAbnormalExit;
	}

	private static bool HasHiddenFilesForRecovery(MainViewModel mainVm)
	{
		foreach (IconZone zone in mainVm.Zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (icon.IsHidden && !string.IsNullOrEmpty(icon.HiddenPath) && (File.Exists(icon.HiddenPath) || Directory.Exists(icon.HiddenPath)))
					{
						return true;
					}
				}
			}
		}
		return false;
	}

	public static int PerformRecovery(MainViewModel? mainVm)
	{
		Thread.Sleep(10000);
		PersistentLog("PerformRecovery: 开始执行图标恢复...");
		try
		{
			File.Delete(LockFilePath);
		}
		catch
		{
		}
		PersistentLog("已删除旧锁文件: " + LockFilePath);
		int totalRecovered = 0;
		try
		{
			if (mainVm != null && mainVm.Zones.Count > 0)
			{
				int trackedHidden = 0;
				foreach (IconZone zone2 in mainVm.Zones)
				{
					foreach (IconGroup group in zone2.Groups)
					{
						foreach (ZoneIcon icon in group.Icons)
						{
							if (icon.IsHidden)
							{
								trackedHidden++;
							}
						}
					}
				}
				if (trackedHidden > 0)
				{
					PersistentLog($"策略1跳过：{trackedHidden} 个隐藏图标由有效 zone 持有（不还原到桌面，避免与收纳区重复；启动后由 ReapplyHiddenAttributes 维持隐藏）");
				}
			}
			int manifestRestored = DesktopIconService.RestoreAllFromManifest();
			PersistentLog($"manifest 恢复了 {manifestRestored} 个文件");
			totalRecovered += manifestRestored;
			int orphanRecovered = DesktopIconService.RecoverOrphanedFiles(mainVm?.GetAllTrackedHiddenPaths() ?? new HashSet<string>());
			PersistentLog($"orphan 扫描恢复了 {orphanRecovered} 个文件");
			totalRecovered += orphanRecovered;
			if (mainVm != null)
			{
				Dispatcher dispatcher = Application.Current?.Dispatcher;
				if (dispatcher != null && !dispatcher.CheckAccess())
				{
					dispatcher.Invoke(delegate
					{
						mainVm.SaveZonesNow();
					});
				}
				else
				{
					mainVm.SaveZonesNow();
				}
				int cleanedZones = 0;
				int zi;
				for (zi = mainVm.Zones.Count - 1; zi >= 0; zi--)
				{
					IconZone zone = mainVm.Zones[zi];
					int hiddenIconCount = 0;
					int totalIconCount = 0;
					foreach (IconGroup group2 in zone.Groups)
					{
						foreach (ZoneIcon icon2 in group2.Icons)
						{
							totalIconCount++;
							if (icon2.IsHidden)
							{
								hiddenIconCount++;
							}
						}
					}
					if (totalIconCount <= 0 || hiddenIconCount != 0)
					{
						continue;
					}
					PersistentLog($"后验证: Zone「{zone.Name}」有 {totalIconCount} 个图标但 0 个隐藏 → 解散中途崩溃残留，自动清理");
					if (dispatcher != null && !dispatcher.CheckAccess())
					{
						dispatcher.Invoke(delegate
						{
							mainVm.Zones.RemoveAt(zi);
						});
					}
					else
					{
						mainVm.Zones.RemoveAt(zi);
					}
					cleanedZones++;
				}
				if (cleanedZones > 0)
				{
					if (dispatcher != null && !dispatcher.CheckAccess())
					{
						dispatcher.Invoke(delegate
						{
							mainVm.SaveZonesNow();
						});
					}
					else
					{
						mainVm.SaveZonesNow();
					}
					PersistentLog($"后验证: 已清理 {cleanedZones} 个残留 zone");
				}
			}
			PersistentLog($"共计恢复 {totalRecovered} 个文件");
		}
		catch (Exception ex)
		{
			PersistentLog($"恢复失败: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
		}
		PersistentLog($"PerformRecovery: 完成，totalRecovered={totalRecovered}");
		return totalRecovered;
	}

	public static bool DetectAndRecover(MainViewModel? mainVm)
	{
		bool num = File.Exists(LockFilePath);
		if (num)
		{
			int totalRecovered = PerformRecovery(mainVm);
			if (mainVm != null && totalRecovered > 0)
			{
				mainVm.SkipHidingForStartup = true;
			}
		}
		CreateLockFile();
		return num;
	}

	public static void MarkCleanExit()
	{
		try
		{
			if (File.Exists(LockFilePath))
			{
				File.Delete(LockFilePath);
				PersistentLog("MarkCleanExit: 已删除锁文件 " + LockFilePath);
			}
			else
			{
				PersistentLog("MarkCleanExit: 锁文件不存在（可能已被清除）");
			}
		}
		catch (Exception ex)
		{
			PersistentLog("MarkCleanExit: 删除锁文件失败: " + ex.Message);
		}
	}

	public static void HandleSystemShutdown(MainViewModel? mainVm)
	{
		try
		{
			if (mainVm == null)
			{
				return;
			}
			foreach (IconZone zone in mainVm.Zones)
			{
				mainVm.RestoreZoneIcons(zone);
			}
			mainVm.SaveZonesNow();
		}
		catch (Exception)
		{
		}
		finally
		{
			MarkCleanExit();
		}
	}

	private static void CreateLockFile()
	{
		try
		{
			int pid = Environment.ProcessId;
			File.WriteAllText(LockFilePath, pid.ToString(), Encoding.UTF8);
		}
		catch
		{
		}
	}
}
