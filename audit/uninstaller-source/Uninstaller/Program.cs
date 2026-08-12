using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace Uninstaller;

internal class Program
{
	private class ManifestWrapper
	{
		[JsonPropertyName("entries")]
		public List<ManifestEntryRecord>? Entries { get; set; }
	}

	private class ManifestEntryRecord
	{
		[JsonPropertyName("hiddenPath")]
		public string HiddenPath { get; set; } = string.Empty;

		[JsonPropertyName("originalPath")]
		public string OriginalPath { get; set; } = string.Empty;

		[JsonPropertyName("isDirectory")]
		public bool IsDirectory { get; set; }
	}

	private class ZoneRecord
	{
		[JsonPropertyName("groups")]
		public List<GroupRecord>? Groups { get; set; }

		[JsonPropertyName("icons")]
		public List<IconRecord>? IconsLegacy { get; set; }
	}

	private class GroupRecord
	{
		[JsonPropertyName("icons")]
		public List<IconRecord>? Icons { get; set; }
	}

	private class IconRecord
	{
		[JsonPropertyName("filePath")]
		public string FilePath { get; set; } = string.Empty;

		[JsonPropertyName("hiddenPath")]
		public string? HiddenPath { get; set; }

		[JsonPropertyName("hiddenMethod")]
		public string? HiddenMethod { get; set; }
	}

	private static readonly string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopTool");

	private static readonly string ZonesFile = Path.Combine(AppDataDir, "zones.json");

	private static readonly string MappingsFile = Path.Combine(AppDataDir, "mappings.json");

	private static readonly string TodoFile = Path.Combine(AppDataDir, "todos.json");

	private static readonly string WindowStateFile = Path.Combine(AppDataDir, "window_state.json");

	private static readonly string SettingsFile = Path.Combine(AppDataDir, "settings.json");

	private static readonly string UninstallingSentinel = Path.Combine(AppDataDir, ".uninstalling");

	private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

	private const string UninstallKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall";

	private const string AppName = "DesktopTool";

	private const string AppId = "{B8F4A1D2-6C3E-4F8A-9E5B-1D7C2A3F4E6B}_is1";

	private static readonly string UninstallLogPath = Path.Combine(Path.GetTempPath(), "DesktopTool_Uninstall.log");

	private const uint FILE_ATTRIBUTE_HIDDEN = 2u;

	private const uint FILE_ATTRIBUTE_SYSTEM = 4u;

	private const uint FILE_ATTRIBUTE_NORMAL = 128u;

	private const uint INVALID_FILE_ATTRIBUTES = uint.MaxValue;

	private const int WM_CLOSE = 16;

	[STAThread]
	private static int Main(string[] args)
	{
		try
		{
			Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", Path.GetTempPath());
		}
		catch
		{
		}
		try
		{
			Environment.CurrentDirectory = Path.GetTempPath();
		}
		catch
		{
		}
		bool num = args.Any((string a) => a.Equals("-silent", StringComparison.OrdinalIgnoreCase) || a.Equals("/silent", StringComparison.OrdinalIgnoreCase));
		bool flag = args.Any((string a) => a.Equals("-recovery", StringComparison.OrdinalIgnoreCase) || a.Equals("/recovery", StringComparison.OrdinalIgnoreCase));
		if (num || flag)
		{
			if (flag && IsDesktopToolStillInstalled())
			{
				DiagLog("Recovery mode: DesktopTool 仍在安装中，跳过恢复");
				return 0;
			}
			return RunSilent();
		}
		Application application = new Application();
		UninstallWindow window = new UninstallWindow();
		application.Run(window);
		return 0;
	}

	private static bool IsDesktopToolStillInstalled()
	{
		try
		{
			string path = Path.Combine(AppDataDir, "install_path.txt");
			if (!File.Exists(path))
			{
				return false;
			}
			string text = File.ReadAllText(path).Trim();
			if (string.IsNullOrEmpty(text) || !Directory.Exists(text))
			{
				return false;
			}
			return File.Exists(Path.Combine(text, "DesktopTool.exe"));
		}
		catch
		{
			return false;
		}
	}

	private static int RunSilent()
	{
		int num = 0;
		DateTime now = DateTime.Now;
		DiagLog("========== DesktopTool 卸载开始 ==========");
		try
		{
			DiagLog("阶段 1/5：终止 DesktopTool 进程...");
			KillDesktopToolProcess();
			Thread.Sleep(600);
			DiagLog("阶段 1/5：完成");
			DiagLog("阶段 2/5：恢复桌面图标...");
			int value = RestoreAllWithFallback();
			DiagLog($"阶段 2/5：完成，恢复 {value} 个文件");
			List<string> hiddenDirContents = GetHiddenDirContents(Path.Combine(AppDataDir, "hidden"));
			if (hiddenDirContents.Count > 0)
			{
				DiagLog($"!!! 警告：hidden 目录中还有 {hiddenDirContents.Count} 个未恢复的文件 !!!");
				foreach (string item in hiddenDirContents)
				{
					DiagLog("  残留文件: " + item);
				}
				num = 2;
			}
			DiagLog("阶段 3/5：清理注册表...");
			CleanRegistry();
			DiagLog("阶段 4/5：清理快捷方式...");
			CleanShortcuts();
			DiagLog("阶段 5/5：清理应用数据（保护残留文件）...");
			CleanAppDataDir(keepUserFiles: true, hiddenDirContents.Count > 0);
			DiagLog("阶段 5/5：清理 Scheduled Task...");
			CleanScheduledTask();
			double totalSeconds = (DateTime.Now - now).TotalSeconds;
			DiagLog($"========== 卸载完成（耗时 {totalSeconds:F1}s，退出码 {num}）==========");
			CleanUninstallingSentinel();
			return num;
		}
		catch (Exception ex)
		{
			DiagLog("!!! 致命异常：" + ex.GetType().Name + ": " + ex.Message);
			DiagLog(ex.StackTrace ?? "(无堆栈)");
			CleanUninstallingSentinel();
			return 1;
		}
	}

	public static void KillDesktopToolProcess()
	{
		WriteUninstallingSentinel();
		try
		{
			Process me = Process.GetCurrentProcess();
			List<Process> list = (from p in Process.GetProcessesByName("DesktopTool")
				where p.Id != me.Id
				select p).ToList();
			if (list.Count == 0)
			{
				DiagLog("KillDesktopTool: 没有运行中的 DesktopTool 进程");
				return;
			}
			DiagLog($"KillDesktopTool: 发现 {list.Count} 个 DesktopTool 进程，尝试优雅关闭...");
			foreach (Process item in list)
			{
				try
				{
					if (item.MainWindowHandle != IntPtr.Zero)
					{
						DiagLog($"KillDesktopTool: 发送 WM_CLOSE 到 PID={item.Id}");
						PostMessage(item.MainWindowHandle, 16, IntPtr.Zero, IntPtr.Zero);
					}
				}
				catch (Exception ex)
				{
					DiagLog($"KillDesktopTool: WM_CLOSE 失败 PID={item.Id}: {ex.Message}");
				}
			}
			Stopwatch stopwatch = Stopwatch.StartNew();
			while (stopwatch.ElapsedMilliseconds < 3000)
			{
				if (list.Where(delegate(Process p)
				{
					try
					{
						return !p.HasExited;
					}
					catch
					{
						return false;
					}
				}).ToList().Count == 0)
				{
					DiagLog("KillDesktopTool: 所有进程已优雅关闭");
					break;
				}
				Thread.Sleep(200);
			}
			List<Process> list2 = list.Where(delegate(Process p)
			{
				try
				{
					return !p.HasExited;
				}
				catch
				{
					return false;
				}
			}).ToList();
			if (list2.Count > 0)
			{
				DiagLog($"KillDesktopTool: {list2.Count} 个进程未响应，强制终止");
				foreach (Process item2 in list2)
				{
					try
					{
						item2.Kill(entireProcessTree: true);
						item2.WaitForExit(3000);
					}
					catch (Exception ex2)
					{
						DiagLog($"KillDesktopTool: Force kill PID={item2.Id} failed: {ex2.Message}");
					}
				}
			}
			foreach (Process item3 in list)
			{
				try
				{
					item3.Dispose();
				}
				catch
				{
				}
			}
			Thread.Sleep(600);
			DiagLog("KillDesktopTool: 完成");
		}
		catch (Exception ex3)
		{
			DiagLog("KillDesktopTool: 异常: " + ex3.Message);
		}
	}

	private static void WriteUninstallingSentinel()
	{
		try
		{
			Directory.CreateDirectory(AppDataDir);
			File.WriteAllText(UninstallingSentinel, DateTime.Now.ToString("O"));
			DiagLog("WriteUninstallingSentinel: 已写入哨兵文件");
		}
		catch (Exception ex)
		{
			DiagLog("WriteUninstallingSentinel: 写入失败: " + ex.Message);
		}
	}

	public static void CleanUninstallingSentinel()
	{
		try
		{
			if (File.Exists(UninstallingSentinel))
			{
				File.Delete(UninstallingSentinel);
				DiagLog("CleanUninstallingSentinel: 已删除哨兵文件");
			}
		}
		catch
		{
		}
	}

	public static int RestoreAllWithFallback()
	{
		int num = RestoreFromZonesJson();
		int num2 = 0 + num;
		DiagLog($"策略1 (zones.json): 恢复 {num} 个");
		int num3 = RestoreFromManifestJson();
		int num4 = num2 + num3;
		DiagLog($"策略2 (manifest): 恢复 {num3} 个");
		int num5 = RestoreFromHiddenDirScan();
		int result = num4 + num5;
		DiagLog($"策略3 (直接扫描): 恢复 {num5} 个");
		RefreshDesktop();
		return result;
	}

	private static int RestoreFromZonesJson()
	{
		try
		{
			if (!File.Exists(ZonesFile))
			{
				DiagLog("  zones.json 不存在，跳过策略1");
				return 0;
			}
			List<ZoneRecord> list = JsonSerializer.Deserialize<List<ZoneRecord>>(File.ReadAllText(ZonesFile));
			if (list == null || list.Count == 0)
			{
				DiagLog("  zones.json 为空，跳过策略1");
				return 0;
			}
			int num = 0;
			bool flag = false;
			int num2 = 0;
			int num3 = 0;
			foreach (ZoneRecord item in list)
			{
				if (item.Groups != null)
				{
					foreach (GroupRecord group in item.Groups)
					{
						if (group.Icons == null)
						{
							continue;
						}
						foreach (IconRecord icon in group.Icons)
						{
							num2++;
							if (!string.IsNullOrEmpty(icon.HiddenMethod) && !string.IsNullOrEmpty(icon.HiddenPath))
							{
								num3++;
							}
							if (RestoreIcon(icon))
							{
								num++;
								icon.HiddenMethod = null;
								icon.HiddenPath = null;
								flag = true;
							}
						}
					}
				}
				if (item.IconsLegacy == null)
				{
					continue;
				}
				foreach (IconRecord item2 in item.IconsLegacy)
				{
					num2++;
					if (!string.IsNullOrEmpty(item2.HiddenMethod) && !string.IsNullOrEmpty(item2.HiddenPath))
					{
						num3++;
					}
					if (RestoreIcon(item2))
					{
						num++;
						item2.HiddenMethod = null;
						item2.HiddenPath = null;
						flag = true;
					}
				}
			}
			DiagLog($"  zones.json: 共 {num2} 个图标条目，{num3} 个有隐藏标记，成功恢复 {num} 个");
			if (flag)
			{
				try
				{
					string contents = JsonSerializer.Serialize(list, new JsonSerializerOptions
					{
						WriteIndented = true,
						PropertyNamingPolicy = JsonNamingPolicy.CamelCase
					});
					string text = ZonesFile + ".tmp." + Guid.NewGuid().ToString("N").Substring(0, 8);
					File.WriteAllText(text, contents);
					File.Move(text, ZonesFile, overwrite: true);
					DiagLog($"  zones.json 已更新（{num} 个图标标记为已恢复）");
				}
				catch (Exception ex)
				{
					DiagLog("  zones.json 更新失败（不影响恢复结果）: " + ex.Message);
				}
			}
			return num;
		}
		catch (Exception ex2)
		{
			DiagLog("  zones.json 解析异常: " + ex2.Message);
			return 0;
		}
	}

	private static int RestoreFromManifestJson()
	{
		string[] obj = new string[2]
		{
			Path.Combine(AppDataDir, "recovery_manifest.json"),
			Path.Combine(AppDataDir, "hidden", "recovery_manifest.json")
		};
		string text = null;
		string[] array = obj;
		foreach (string text2 in array)
		{
			if (File.Exists(text2))
			{
				text = text2;
				break;
			}
		}
		if (text == null)
		{
			DiagLog("  manifest 不存在（新旧位置均未找到），跳过策略2");
			return 0;
		}
		DiagLog("  manifest 位置: " + text);
		try
		{
			ManifestWrapper manifestWrapper = JsonSerializer.Deserialize<ManifestWrapper>(File.ReadAllText(text));
			if (manifestWrapper?.Entries == null || manifestWrapper.Entries.Count == 0)
			{
				DiagLog("  manifest 为空，跳过策略2");
				return 0;
			}
			int num = 0;
			foreach (ManifestEntryRecord entry in manifestWrapper.Entries)
			{
				if (string.IsNullOrEmpty(entry.OriginalPath) || string.IsNullOrEmpty(entry.HiddenPath))
				{
					continue;
				}
				try
				{
					if (!File.Exists(entry.HiddenPath) && !Directory.Exists(entry.HiddenPath))
					{
						continue;
					}
					string text3 = entry.OriginalPath;
					if (File.Exists(text3) || Directory.Exists(text3))
					{
						string directoryName = Path.GetDirectoryName(text3);
						string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(text3);
						string extension = Path.GetExtension(text3);
						int num2 = 1;
						do
						{
							text3 = Path.Combine(directoryName ?? "", $"{fileNameWithoutExtension} ({num2}){extension}");
							num2++;
						}
						while (File.Exists(text3) || Directory.Exists(text3));
					}
					string directoryName2 = Path.GetDirectoryName(text3);
					if (directoryName2 != null && !Directory.Exists(directoryName2))
					{
						Directory.CreateDirectory(directoryName2);
					}
					RemoveHiddenAndSystem(entry.HiddenPath);
					if (entry.IsDirectory && Directory.Exists(entry.HiddenPath))
					{
						MoveDirectoryCrossVolume(entry.HiddenPath, text3);
						goto IL_0229;
					}
					if (File.Exists(entry.HiddenPath))
					{
						MoveFileCrossVolume(entry.HiddenPath, text3);
						goto IL_0229;
					}
					goto end_IL_00ee;
					IL_0229:
					num++;
					end_IL_00ee:;
				}
				catch (Exception ex)
				{
					DiagLog($"  manifest 条目恢复失败: {entry.HiddenPath} -> {entry.OriginalPath}, {ex.Message}");
				}
			}
			DeleteFileSafe(text);
			return num;
		}
		catch (Exception ex2)
		{
			DiagLog("  manifest 解析异常: " + ex2.Message);
			return 0;
		}
	}

	private static int RestoreFromHiddenDirScan()
	{
		string text = Path.Combine(AppDataDir, "hidden");
		if (!Directory.Exists(text))
		{
			DiagLog("  hidden 目录不存在，跳过策略3");
			return 0;
		}
		string manifestPath = Path.Combine(text, "recovery_manifest.json");
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
		{
			folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
		}
		if (string.IsNullOrEmpty(folderPath))
		{
			DiagLog("  无法获取桌面路径，跳过策略3");
			return 0;
		}
		int num = 0;
		try
		{
			List<string> list = (from e in Directory.EnumerateFileSystemEntries(text)
				where !string.Equals(e, manifestPath, StringComparison.OrdinalIgnoreCase)
				select e).ToList();
			DiagLog($"  hidden 目录中有 {list.Count} 个文件/文件夹需要处理");
			foreach (string item in list)
			{
				try
				{
					string fileName = Path.GetFileName(item);
					string text2 = Path.Combine(folderPath, fileName);
					if (File.Exists(text2) || Directory.Exists(text2))
					{
						string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
						string extension = Path.GetExtension(fileName);
						int num2 = 1;
						do
						{
							text2 = Path.Combine(folderPath, $"{fileNameWithoutExtension} ({num2}){extension}");
							num2++;
						}
						while (File.Exists(text2) || Directory.Exists(text2));
					}
					RemoveHiddenAndSystem(item);
					if (File.Exists(item))
					{
						MoveFileCrossVolume(item, text2);
						goto IL_01af;
					}
					if (Directory.Exists(item))
					{
						MoveDirectoryCrossVolume(item, text2);
						goto IL_01af;
					}
					goto end_IL_00e1;
					IL_01af:
					DiagLog("  策略3 成功: " + fileName + " -> " + text2);
					num++;
					end_IL_00e1:;
				}
				catch (Exception ex)
				{
					DiagLog("  策略3 失败: " + Path.GetFileName(item) + ", " + ex.Message);
				}
			}
		}
		catch (Exception ex2)
		{
			DiagLog("  策略3 扫描异常: " + ex2.Message);
		}
		return num;
	}

	private static List<string> GetHiddenDirContents(string hiddenDir)
	{
		List<string> list = new List<string>();
		try
		{
			if (!Directory.Exists(hiddenDir))
			{
				return list;
			}
			string b = Path.Combine(hiddenDir, "recovery_manifest.json");
			foreach (string item in Directory.EnumerateFileSystemEntries(hiddenDir))
			{
				if (!string.Equals(item, b, StringComparison.OrdinalIgnoreCase))
				{
					list.Add(item);
				}
			}
		}
		catch
		{
		}
		return list;
	}

	public static int RestoreAllHiddenIcons()
	{
		return RestoreAllWithFallback();
	}

	private static void DeleteFileSafe(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return;
			}
			try
			{
				uint fileAttributes = GetFileAttributes(path);
				if (fileAttributes != uint.MaxValue)
				{
					fileAttributes &= 0xFFFFFFF9u;
					SetFileAttributes(path, fileAttributes);
				}
			}
			catch
			{
			}
			File.Delete(path);
		}
		catch
		{
		}
	}

	private static bool RestoreIcon(IconRecord icon)
	{
		try
		{
			if (string.IsNullOrEmpty(icon.FilePath))
			{
				return false;
			}
			if (icon.HiddenMethod == "Attributes")
			{
				return TryRemoveAttributes(icon.FilePath);
			}
			if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath))
			{
				return TryMoveBack(icon.HiddenPath, icon.FilePath);
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryRemoveAttributes(string filePath)
	{
		try
		{
			string fullPath = Path.GetFullPath(filePath);
			if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
			{
				return false;
			}
			uint fileAttributes = GetFileAttributes(fullPath);
			if (fileAttributes == uint.MaxValue)
			{
				return false;
			}
			fileAttributes &= 0xFFFFFFF9u;
			if (fileAttributes == 0)
			{
				fileAttributes = 128u;
			}
			return SetFileAttributes(fullPath, fileAttributes);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryMoveBack(string hiddenPath, string originalPath)
	{
		try
		{
			bool flag = File.Exists(hiddenPath);
			bool flag2 = Directory.Exists(hiddenPath);
			if (!flag && !flag2)
			{
				return false;
			}
			RemoveHiddenAndSystem(hiddenPath);
			if (File.Exists(originalPath) || Directory.Exists(originalPath))
			{
				string directoryName = Path.GetDirectoryName(originalPath);
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(originalPath);
				string extension = Path.GetExtension(originalPath);
				int num = 1;
				string text;
				do
				{
					text = Path.Combine(directoryName ?? "", $"{fileNameWithoutExtension} ({num}){extension}");
					num++;
				}
				while (File.Exists(text) || Directory.Exists(text));
				originalPath = text;
			}
			string directoryName2 = Path.GetDirectoryName(originalPath);
			if (directoryName2 != null && !Directory.Exists(directoryName2))
			{
				Directory.CreateDirectory(directoryName2);
			}
			if (flag)
			{
				MoveFileCrossVolume(hiddenPath, originalPath);
			}
			else
			{
				MoveDirectoryCrossVolume(hiddenPath, originalPath);
			}
			if (originalPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					File.SetLastWriteTime(originalPath, DateTime.Now);
				}
				catch
				{
				}
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsCrossVolume(string path1, string path2)
	{
		string text = Path.GetPathRoot(path1)?.ToUpperInvariant() ?? "";
		string text2 = Path.GetPathRoot(path2)?.ToUpperInvariant() ?? "";
		if (text != text2 && !string.IsNullOrEmpty(text))
		{
			return !string.IsNullOrEmpty(text2);
		}
		return false;
	}

	private static void MoveFileCrossVolume(string source, string dest)
	{
		try
		{
			File.Move(source, dest);
		}
		catch (IOException) when (IsCrossVolume(source, dest))
		{
			File.Copy(source, dest, overwrite: true);
			File.Delete(source);
		}
	}

	private static void MoveDirectoryCrossVolume(string source, string dest)
	{
		try
		{
			Directory.Move(source, dest);
		}
		catch (IOException) when (IsCrossVolume(source, dest))
		{
			CopyDirectoryRecursive(source, dest);
			Directory.Delete(source, recursive: true);
		}
	}

	private static void CopyDirectoryRecursive(string sourceDir, string destDir)
	{
		Directory.CreateDirectory(destDir);
		string[] files = Directory.GetFiles(sourceDir);
		foreach (string text in files)
		{
			string destFileName = Path.Combine(destDir, Path.GetFileName(text));
			File.Copy(text, destFileName, overwrite: true);
		}
		files = Directory.GetDirectories(sourceDir);
		foreach (string text2 in files)
		{
			CopyDirectoryRecursive(text2, Path.Combine(destDir, Path.GetFileName(text2)));
		}
	}

	private static void RemoveHiddenAndSystem(string path)
	{
		try
		{
			uint fileAttributes = GetFileAttributes(path);
			if (fileAttributes != uint.MaxValue)
			{
				fileAttributes &= 0xFFFFFFF9u;
				SetFileAttributes(path, fileAttributes);
			}
		}
		catch
		{
		}
	}

	private static void RefreshDesktop()
	{
		try
		{
			SHChangeNotify(134217728, 0u, IntPtr.Zero, IntPtr.Zero);
		}
		catch
		{
		}
	}

	public static void CleanRegistry()
	{
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
			if (registryKey != null && registryKey.GetValue("DesktopTool") != null)
			{
				registryKey.DeleteValue("DesktopTool", throwOnMissingValue: false);
			}
		}
		catch
		{
		}
		try
		{
			using RegistryKey registryKey2 = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall", writable: true);
			if (registryKey2 != null)
			{
				try
				{
					registryKey2.DeleteSubKeyTree("{B8F4A1D2-6C3E-4F8A-9E5B-1D7C2A3F4E6B}_is1", throwOnMissingSubKey: false);
					return;
				}
				catch
				{
					return;
				}
			}
		}
		catch
		{
		}
	}

	public static void CleanShortcuts()
	{
		try
		{
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DesktopTool.lnk");
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
		try
		{
			string path2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "桌面工具箱.lnk");
			if (File.Exists(path2))
			{
				File.Delete(path2);
			}
		}
		catch
		{
		}
		try
		{
			string path3 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "桌面工具箱.lnk");
			if (File.Exists(path3))
			{
				File.Delete(path3);
			}
		}
		catch
		{
		}
	}

	public static void CleanAppDataDir(bool keepUserFiles, bool protectHiddenFiles = false)
	{
		try
		{
			string[] array;
			if (keepUserFiles)
			{
				string text = Path.Combine(AppDataDir, "hidden");
				if (protectHiddenFiles && Directory.Exists(text))
				{
					DiagLog("CleanAppDataDir: 检测到 hidden 目录中有残留文件，保留不删除");
					string path = Path.Combine(text, "recovery_manifest.json");
					try
					{
						DeleteFileSafe(path);
					}
					catch
					{
					}
					string path2 = Path.Combine(AppDataDir, "backups");
					if (Directory.Exists(path2))
					{
						try
						{
							Directory.Delete(path2, recursive: true);
						}
						catch
						{
						}
					}
					array = new string[3] { "recover.ps1", "recover.bat", "install_path.txt" };
					foreach (string path3 in array)
					{
						string path4 = Path.Combine(AppDataDir, path3);
						if (File.Exists(path4))
						{
							try
							{
								File.Delete(path4);
							}
							catch
							{
							}
						}
					}
					try
					{
						string path5 = Path.Combine(AppDataDir, "Recovery.exe");
						if (File.Exists(path5))
						{
							File.Delete(path5);
						}
					}
					catch
					{
					}
					DiagLog("CleanAppDataDir: hidden 目录保留，仅删除管理文件");
					return;
				}
				string path6 = Path.Combine(AppDataDir, "backups");
				if (Directory.Exists(path6))
				{
					try
					{
						Directory.Delete(path6, recursive: true);
					}
					catch
					{
					}
				}
				array = new string[3] { "recover.ps1", "recover.bat", "install_path.txt" };
				foreach (string path7 in array)
				{
					string path8 = Path.Combine(AppDataDir, path7);
					if (File.Exists(path8))
					{
						try
						{
							File.Delete(path8);
						}
						catch
						{
						}
					}
				}
				try
				{
					string path9 = Path.Combine(AppDataDir, "Recovery.exe");
					if (File.Exists(path9))
					{
						File.Delete(path9);
					}
				}
				catch
				{
				}
				if (Directory.Exists(text))
				{
					DiagLog("CleanAppDataDir: hidden 目录保留（空目录，供后续恢复兜底）");
				}
				return;
			}
			array = new string[5] { ZonesFile, MappingsFile, TodoFile, WindowStateFile, SettingsFile };
			foreach (string path10 in array)
			{
				if (File.Exists(path10))
				{
					try
					{
						File.Delete(path10);
					}
					catch
					{
					}
				}
			}
			string path11 = Path.Combine(AppDataDir, "backups");
			if (Directory.Exists(path11))
			{
				try
				{
					Directory.Delete(path11, recursive: true);
				}
				catch
				{
				}
			}
			string path12 = Path.Combine(AppDataDir, "hidden");
			if (Directory.Exists(path12))
			{
				foreach (string item in Directory.EnumerateFiles(path12, "*", SearchOption.AllDirectories))
				{
					try
					{
						uint fileAttributes = GetFileAttributes(item);
						if (fileAttributes != uint.MaxValue)
						{
							fileAttributes &= 0xFFFFFFF9u;
							SetFileAttributes(item, fileAttributes);
						}
						File.Delete(item);
					}
					catch
					{
					}
				}
				try
				{
					Directory.Delete(path12, recursive: true);
				}
				catch
				{
				}
			}
			if (Directory.Exists(AppDataDir) && !Directory.EnumerateFileSystemEntries(AppDataDir).Any())
			{
				Directory.Delete(AppDataDir);
			}
		}
		catch
		{
		}
	}

	public static void CleanScheduledTask()
	{
		try
		{
			Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = "schtasks.exe",
				Arguments = "/Delete /TN \"DesktopToolRecovery\" /F",
				UseShellExecute = true,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			});
			process?.WaitForExit(5000);
			process?.Dispose();
		}
		catch
		{
		}
	}

	public static void ScheduleDirectoryDelete(string dirPath)
	{
		try
		{
			string text = Path.Combine(Path.GetTempPath(), "desktoptool_cleanup.bat");
			string contents = $"@echo off\r\ntimeout /t 3 /nobreak >nul\r\n:retry\r\nrmdir /s /q \"{dirPath}\" 2>nul\r\nif exist \"{dirPath}\" (\r\n    timeout /t 2 /nobreak >nul\r\n    goto retry\r\n)\r\ndel \"%~f0\" >nul 2>&1";
			File.WriteAllText(text, contents);
			Process.Start(new ProcessStartInfo
			{
				FileName = "cmd.exe",
				Arguments = "/c \"" + text + "\"",
				UseShellExecute = true,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			});
		}
		catch
		{
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern uint GetFileAttributes(string lpFileName);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetFileAttributes(string lpFileName, uint dwFileAttributes);

	[DllImport("shell32.dll")]
	private static extern void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool PostMessage(nint hWnd, int Msg, nint wParam, nint lParam);

	private static void DiagLog(string message)
	{
		try
		{
			string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
			File.AppendAllText(UninstallLogPath, text + Environment.NewLine, Encoding.UTF8);
		}
		catch
		{
		}
	}
}
