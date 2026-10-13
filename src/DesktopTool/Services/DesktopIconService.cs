using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopTool.Models;
using Microsoft.Win32;

namespace DesktopTool.Services;

public class DesktopIconService
{
	private class ManifestEntry
	{
		public string HiddenPath { get; set; } = string.Empty;

		public string OriginalPath { get; set; } = string.Empty;

		public bool IsDirectory { get; set; }
	}

	private class ManifestData
	{
		public List<ManifestEntry> Entries { get; set; } = new List<ManifestEntry>();
	}

	public sealed class ModeSwitchFailure
	{
		public string? Name { get; set; }

		public string? Path { get; set; }

		public string? Reason { get; set; }
	}

	public sealed class ModeSwitchResult
	{
		public int Success { get; set; }

		public int Fail { get; set; }

		public List<ModeSwitchFailure> Failures { get; } = new List<ModeSwitchFailure>();
	}

	private static nint _cachedDesktopHwnd = IntPtr.Zero;

	private static CancellationTokenSource? _assocChangedCts;

	private static readonly DateTime _serviceStartTime = DateTime.UtcNow;

	private const int StartupAssocChangedDelaySeconds = 30;

	private static readonly HashSet<string> _pendingNotifPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private static bool _batchFlushScheduled;

	private static FileSystemWatcher? _autoRefreshWatcher;

	private static Timer? _autoRefreshTimer;

	private static readonly HashSet<string> _autoRefreshPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private static string? _lastMoveError;

	private static string? _lastRestoreFailReason;

	private static string? _lastRestoredActualPath;

	public const string SafeSubfolderName = "DesktopTool_hidden";

	private static readonly string ManifestFilePath = Path.Combine(App.GetAppDataDir(), "recovery_manifest.json");

	private static readonly object _manifestLock = new object();

	private static readonly object _pathDiagLock = new object();

	private static int _lastRestoreSeq = 0;

	private static string? _lastRestoredOwner;

	private const string ExplorerAdvancedKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced";

	private static int? _originalShowSuperHidden;

	private static int? _originalHidden;

	private static int _tempOriginalHidden = 2;

	private static int _tempOriginalShowSuperHidden = 0;

	private static volatile bool _tempShowActive;

	private static readonly object _tempShowLock = new object();

	private const string TempShowStateKey = "Software\\DesktopTool\\TempShowHidden";

	public static string? CustomHiddenDir { get; set; }

	public static string HiddenDir => CustomHiddenDir ?? GetDefaultHiddenDir();

	public static string UserSelectedMode { get; set; } = "Attributes";

	public static Func<string, bool>? IsPathManagedCallback { get; set; }

	public static string? LastMoveError => _lastMoveError;

	public static string? LastRestoreFailReason => _lastRestoreFailReason;

	public static string? LastRestoredActualPath => _lastRestoredActualPath;

	public static bool IsTempShowHiddenActive => _tempShowActive;

	public static bool IsExplorerShowingHiddenFiles()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced");
			if (key == null)
			{
				return false;
			}
			object hidden = key.GetValue("Hidden");
			object super = key.GetValue("ShowSuperHidden");
			bool num = hidden is int h && h == 1;
			bool showSuper = super is int s && s == 1;
			return num || showSuper;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsSystemFilesHidden()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced");
			if (key == null)
			{
				return true;
			}
			return key.GetValue("ShowSuperHidden") is int intVal && intVal == 1;
		}
		catch
		{
			return true;
		}
	}

	public static bool EnableSeRestorePrivilege()
	{
		nint tokenHandle = IntPtr.Zero;
		try
		{
			if (!NativeMethods.OpenProcessToken(Process.GetCurrentProcess().Handle, 40u, out tokenHandle))
			{
				int err = Marshal.GetLastWin32Error();
				LogMigrationDiag($"    SeRestorePrivilege: OpenProcessToken failed, err={err}");
				return false;
			}
			if (!NativeMethods.LookupPrivilegeValue(null, "SeRestorePrivilege", out var luid))
			{
				int err2 = Marshal.GetLastWin32Error();
				LogMigrationDiag($"    SeRestorePrivilege: LookupPrivilegeValue failed, err={err2}");
				return false;
			}
			NativeMethods.TOKEN_PRIVILEGES tp = new NativeMethods.TOKEN_PRIVILEGES
			{
				PrivilegeCount = 1u,
				Luid = luid,
				Attributes = 2u
			};
			if (NativeMethods.AdjustTokenPrivileges(tokenHandle, DisableAllPrivileges: false, ref tp, 0u, IntPtr.Zero, IntPtr.Zero))
			{
				int lastError = Marshal.GetLastWin32Error();
				switch (lastError)
				{
				case 0:
					LogMigrationDiag("    SeRestorePrivilege: ✅ enabled");
					return true;
				case 1300:
					LogMigrationDiag("    SeRestorePrivilege: ❌ not available in token (standard user, err=1300)");
					break;
				default:
					LogMigrationDiag($"    SeRestorePrivilege: ❌ AdjustTokenPrivileges returned error={lastError}");
					break;
				}
			}
			else
			{
				int err3 = Marshal.GetLastWin32Error();
				LogMigrationDiag($"    SeRestorePrivilege: ❌ AdjustTokenPrivileges failed, err={err3}");
			}
			return false;
		}
		catch (Exception ex)
		{
			LogMigrationDiag("    SeRestorePrivilege: ❌ exception: " + ex.GetType().Name + ": " + ex.Message);
			return false;
		}
		finally
		{
			if (tokenHandle != IntPtr.Zero)
			{
				NativeMethods.CloseHandle(tokenHandle);
			}
		}
	}

	public static bool IsAttributeHidden(string filePath)
	{
		try
		{
			if (string.IsNullOrEmpty(filePath))
			{
				return false;
			}
			uint attrs = NativeMethods.GetFileAttributes(filePath);
			if (attrs == uint.MaxValue)
			{
				return false;
			}
			return (attrs & 2) != 0;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsFullyAttributeHidden(string filePath)
	{
		try
		{
			if (string.IsNullOrEmpty(filePath))
			{
				return false;
			}
			uint attrs = NativeMethods.GetFileAttributes(filePath);
			if (attrs == uint.MaxValue)
			{
				return false;
			}
			return (attrs & 6) == 6;
		}
		catch
		{
			return false;
		}
	}

	public static List<string> FindOrphanAttributeHiddenFiles(IEnumerable<string> knownPaths)
	{
		List<string> orphans = new List<string>();
		try
		{
			HashSet<string> known = new HashSet<string>((knownPaths ?? Enumerable.Empty<string>()).Where((string p) => !string.IsNullOrEmpty(p)).Select(delegate(string p)
			{
				try
				{
					return Path.GetFullPath(p).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				}
				catch
				{
					return p.Trim();
				}
			}), StringComparer.OrdinalIgnoreCase);
			HashSet<string> systemExclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "thumbs.db" };
			string[] thirdPartyHiddenPrefixes = new string[1] { "$Documentstore_" };
			string[] desktopFolderPaths = GetDesktopFolderPaths();
			foreach (string desktopPath in desktopFolderPaths)
			{
				if (string.IsNullOrEmpty(desktopPath) || !Directory.Exists(desktopPath))
				{
					continue;
				}
				IEnumerable<string> entries;
				try
				{
					entries = Directory.GetFiles(desktopPath).Concat(Directory.GetDirectories(desktopPath));
				}
				catch
				{
					continue;
				}
				foreach (string entry in entries)
				{
					try
					{
						string name = Path.GetFileName(entry);
						if (systemExclusions.Contains(name))
						{
							continue;
						}
						bool isThirdPartyHidden = false;
						string[] array = thirdPartyHiddenPrefixes;
						foreach (string prefix in array)
						{
							if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
							{
								isThirdPartyHidden = true;
								break;
							}
						}
						if (!isThirdPartyHidden && IsFullyAttributeHidden(entry))
						{
							string normalized = Path.GetFullPath(entry).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
							if (!known.Contains(normalized))
							{
								orphans.Add(entry);
							}
						}
					}
					catch
					{
					}
				}
			}
		}
		catch (Exception)
		{
		}
		return orphans;
	}

	public static List<string> RestoreOrphanHiddenFiles(IEnumerable<string> orphanPaths)
	{
		List<string> restored = new List<string>();
		foreach (string path in orphanPaths)
		{
			try
			{
				if (TryAttributeRestore(path))
				{
					restored.Add(Path.GetFileName(path));
				}
			}
			catch
			{
			}
		}
		return restored;
	}

	public static bool TryAttributeHide(string filePath)
	{
		try
		{
			if (string.IsNullOrEmpty(filePath))
			{
				return false;
			}
			string full = Path.GetFullPath(filePath);
			if (!File.Exists(full) && !Directory.Exists(full))
			{
				return false;
			}
			NativeMethods.SetLastError(0u);
			uint attrs = NativeMethods.GetFileAttributes(full);
			if (attrs == uint.MaxValue)
			{
				int err = Marshal.GetLastWin32Error();
				LogHideError(full, $"GetFileAttributes returned INVALID, Win32Err={err}");
				return false;
			}
			EnableSeRestorePrivilege();
			NativeMethods.SetLastError(0u);
			uint targetAttrs = attrs | 2 | 4;
			bool setAttrOk = NativeMethods.SetFileAttributes(full, targetAttrs);
			int setAttrErr = ((!setAttrOk) ? Marshal.GetLastWin32Error() : 0);
			uint verify = NativeMethods.GetFileAttributes(full);
			int verifyErr = ((verify == uint.MaxValue) ? Marshal.GetLastWin32Error() : 0);
			bool hasHS = verify != uint.MaxValue && (verify & 6) == 6;
			if (hasHS)
			{
				LogMigrationDiag("    TryAttributeHide H+S ✅ ok");
				return true;
			}
			LogMigrationDiag($"    TryAttributeHide H+S FAILED: SetFileAttributes ok={setAttrOk}, err={setAttrErr} (5=ACCESS_DENIED, 2=FILE_NOT_FOUND, 3=PATH_NOT_FOUND), verify attrs={verify}, verify err={verifyErr}, file exists={File.Exists(full) || Directory.Exists(full)}");
			if (setAttrErr == 5 || (setAttrOk && !hasHS))
			{
				string forceReason = ((setAttrErr == 5) ? "ACCESS_DENIED" : "partial-success (H set but S not)");
				LogMigrationDiag("    ➜ " + forceReason + ", trying ForceSetFileAttributesViaHandle...");
				if (ForceSetFileAttributesViaHandle(full, targetAttrs))
				{
					verify = NativeMethods.GetFileAttributes(full);
					if (verify != uint.MaxValue && (verify & 6) == 6)
					{
						LogMigrationDiag("    TryAttributeHide H+S ✅ ok (via ForceSetFileAttributesViaHandle)");
						return true;
					}
					LogMigrationDiag($"    ForceSetFileAttributesViaHandle reported success but verify failed, attrs={verify}");
				}
				LogMigrationDiag("    ➜ Trying ForceSetFileAttributesViaReplace (copy+delete+replace)...");
				if (ForceSetFileAttributesViaReplace(full, targetAttrs))
				{
					verify = NativeMethods.GetFileAttributes(full);
					if (verify != uint.MaxValue && (verify & 6) == 6)
					{
						LogMigrationDiag("    TryAttributeHide H+S ✅ ok (via ForceSetFileAttributesViaReplace)");
						return true;
					}
					LogMigrationDiag($"    ForceSetFileAttributesViaReplace reported success but verify failed, attrs={verify}");
				}
			}
			if (verify != uint.MaxValue && (verify & 2) != 0)
			{
				uint rollback = verify & 0xFFFFFFF9u;
				if (rollback == 0)
				{
					rollback = 128u;
				}
				if (RollbackFileAttributes(full, rollback))
				{
					LogHideError(full, "H+S failed (only H set, rolled back to prevent ghosting)");
					LogMigrationDiag("    H+S: partial H set, rolled back ✅");
				}
				else
				{
					LogHideError(full, "H+S failed (only H set, ROLLBACK ALSO FAILED — file may show as semi-transparent)");
					LogMigrationDiag("    H+S: partial H set, ROLLBACK FAILED ⚠ — file likely H-only → semi-transparent ghost!");
				}
			}
			else
			{
				LogMigrationDiag("    H+S: completely failed, no residue");
			}
			bool explorerShowsHidden = IsExplorerShowingHiddenFiles();
			LogMigrationDiag($"    TryAttributeHide fallback check: explorerShowsHidden={explorerShowsHidden}");
			if (explorerShowsHidden)
			{
				LogMigrationDiag("    ❌ REJECTED: Explorer shows hidden files, HIDDEN-only unsafe");
				return false;
			}
			LogMigrationDiag("    Fallback: HIDDEN-only attempt (Explorer hides hidden files)");
			attrs |= 2;
			NativeMethods.SetLastError(0u);
			bool num = NativeMethods.SetFileAttributes(full, attrs);
			if (!num)
			{
				int err2 = Marshal.GetLastWin32Error();
				LogHideError(full, $"HIDDEN only failed, Win32Err={err2}" + ((err2 == 5) ? " (ACCESS_DENIED)" : ""));
				LogMigrationDiag($"    ❌ HIDDEN-only FAILED: Win32Err={err2}{((err2 == 5) ? " (ACCESS_DENIED)" : "")}");
			}
			else
			{
				LogMigrationDiag("    ✅ HIDDEN-only success (fallback)");
			}
			return num;
		}
		catch (Exception ex)
		{
			LogHideError(filePath, "Exception: " + ex.GetType().Name + ": " + ex.Message);
			return false;
		}
	}

	public static bool TryAttributeRestore(string filePath)
	{
		try
		{
			if (string.IsNullOrEmpty(filePath))
			{
				return false;
			}
			string full = Path.GetFullPath(filePath);
			if (!File.Exists(full) && !Directory.Exists(full))
			{
				return false;
			}
			uint attrs = NativeMethods.GetFileAttributes(full);
			if (attrs == uint.MaxValue)
			{
				return false;
			}
			attrs &= 0xFFFFFFF9u;
			if (attrs == 0)
			{
				attrs = 128u;
			}
			return NativeMethods.SetFileAttributes(full, attrs);
		}
		catch
		{
			return false;
		}
	}

	public static (string? Method, string? HiddenPath) HideFile(string filePath)
	{
		if (UserSelectedMode == "Physical")
		{
			string hidden = MoveToHidden(filePath);
			if (hidden != null)
			{
				return (Method: "Physical", HiddenPath: hidden);
			}
			return (Method: null, HiddenPath: null);
		}
		if (TryAttributeHide(filePath))
		{
			NotifyDesktopChange(filePath);
			return (Method: "Attributes", HiddenPath: null);
		}
		string physical = MoveToHidden(filePath);
		if (physical != null)
		{
			return (Method: "Physical", HiddenPath: physical);
		}
		return (Method: null, HiddenPath: null);
	}

	public static bool CanUseAttributeHiding()
	{
		try
		{
			string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
			{
				return false;
			}
			string testFile = Path.Combine(desktop, $"._dt_attr_test_{Guid.NewGuid():N}.tmp");
			File.WriteAllText(testFile, "test");
			if (!TryAttributeHide(testFile))
			{
				try
				{
					File.Delete(testFile);
				}
				catch
				{
				}
				return false;
			}
			bool hidden = IsAttributeHidden(testFile);
			TryAttributeRestore(testFile);
			try
			{
				File.Delete(testFile);
			}
			catch
			{
			}
			return hidden;
		}
		catch
		{
			return false;
		}
	}

	public static string DetectPreviousMode(IEnumerable<IconZone> zones)
	{
		int physicalCount = 0;
		int attributeCount = 0;
		foreach (IconZone zone in zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (icon.HiddenMethod == "Physical")
					{
						physicalCount++;
					}
					else if (icon.HiddenMethod == "Attributes")
					{
						attributeCount++;
					}
				}
			}
		}
		if (physicalCount > 0 && attributeCount > 0)
		{
			return "Mixed";
		}
		if (physicalCount > 0)
		{
			return "Physical";
		}
		if (attributeCount > 0)
		{
			return "Attributes";
		}
		return "None";
	}

	public static bool RestoreFile(string filePath, string? hiddenMethod, string? hiddenPath)
	{
		try
		{
			if (hiddenMethod == "Attributes")
			{
				LogPathDiag($"[RestoreFile] Attributes 分支: filePath={filePath} | 进入前 LastRestoredActualPath(残留)={_lastRestoredActualPath} owner={_lastRestoredOwner}");
				bool result = TryAttributeRestore(filePath);
				SetLastRestored(filePath, filePath);
				return result;
			}
			if (hiddenMethod == "Physical" && !string.IsNullOrEmpty(hiddenPath))
			{
				return RestoreFromHidden(filePath, hiddenPath);
			}
			return false;
		}
		catch (Exception ex)
		{
			_lastRestoreFailReason = "还原操作异常：" + ex.GetType().Name + ": " + ex.Message;
			LogMigrationDiag("    RestoreFile: TOP-LEVEL EXCEPTION — " + ex.GetType().Name + ": " + ex.Message);
			return false;
		}
	}

	public static (bool Success, string? ActualPath, string? FailReason) TryRestoreFile(string filePath, string? hiddenMethod, string? hiddenPath)
	{
		if (hiddenMethod == "Attributes")
		{
			bool ok = TryAttributeRestore(filePath);
			SetLastRestored(filePath, filePath);
			return (Success: ok, ActualPath: ok ? filePath : null, FailReason: ok ? null : "属性恢复失败");
		}
		if (hiddenMethod == "Physical" && !string.IsNullOrEmpty(hiddenPath))
		{
			try
			{
				if (!File.Exists(hiddenPath) && !Directory.Exists(hiddenPath))
				{
					if (File.Exists(filePath) || Directory.Exists(filePath))
					{
						return (Success: true, ActualPath: filePath, FailReason: null);
					}
					return (Success: false, ActualPath: null, FailReason: "隐藏文件不存在: " + hiddenPath);
				}
				string originalFileName = Path.GetFileName(Path.GetFullPath(filePath));
				if (!RestoreFromHidden(filePath, hiddenPath))
				{
					return (Success: false, ActualPath: null, FailReason: LastRestoreFailReason ?? "恢复操作失败");
				}
				string actualPath = filePath;
				if (!File.Exists(filePath) && !Directory.Exists(filePath))
				{
					string desktopDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
					if (desktopDir != null && Directory.Exists(desktopDir))
					{
						string nameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
						string ext = Path.GetExtension(originalFileName);
						for (int n = 1; n <= 100; n++)
						{
							string candidatePath = Path.Combine(desktopDir, $"{nameWithoutExt} ({n}){ext}");
							if (File.Exists(candidatePath) || Directory.Exists(candidatePath))
							{
								actualPath = candidatePath;
								break;
							}
						}
					}
				}
				return (Success: true, ActualPath: actualPath, FailReason: null);
			}
			catch (Exception ex)
			{
				return (Success: false, ActualPath: null, FailReason: ex.Message);
			}
		}
		return (Success: false, ActualPath: null, FailReason: "无效的隐藏方式");
	}

	public static nint GetDesktopListViewHandle()
	{
		nint progman = NativeMethods.FindWindow("Progman", null);
		if (progman == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		nint defView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
		if (defView != IntPtr.Zero)
		{
			return NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
		}
		nint worker = IntPtr.Zero;
		do
		{
			worker = NativeMethods.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
			if (worker != IntPtr.Zero)
			{
				defView = NativeMethods.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
				if (defView != IntPtr.Zero)
				{
					return NativeMethods.FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
				}
			}
		}
		while (worker != IntPtr.Zero);
		return IntPtr.Zero;
	}

	public static int GetIconCount()
	{
		nint lv = GetDesktopListViewHandle();
		if (lv == IntPtr.Zero)
		{
			return 0;
		}
		return ((IntPtr)NativeMethods.SendMessage(lv, 4100u, IntPtr.Zero, IntPtr.Zero)).ToInt32();
	}

	public static (int X, int Y) GetIconPosition(int index)
	{
		nint lv = GetDesktopListViewHandle();
		if (lv == IntPtr.Zero)
		{
			return (X: 0, Y: 0);
		}
		NativeMethods.POINT pt = default(NativeMethods.POINT);
		nint ptr = Marshal.AllocHGlobal(Marshal.SizeOf(pt));
		Marshal.StructureToPtr(pt, ptr, fDeleteOld: false);
		NativeMethods.SendMessage(lv, 4112u, index, ptr);
		pt = Marshal.PtrToStructure<NativeMethods.POINT>(ptr);
		Marshal.FreeHGlobal(ptr);
		NativeMethods.ClientToScreen(lv, out pt);
		return (X: pt.X, Y: pt.Y);
	}

	public static string? GetIconName(int index)
	{
		nint lv = GetDesktopListViewHandle();
		if (lv == IntPtr.Zero)
		{
			return null;
		}
		StringBuilder sb = new StringBuilder(260);
		NativeMethods.SendMessage(lv, 4211u, index, sb);
		return sb.ToString();
	}

	public static List<(int Index, string Name, int X, int Y)> EnumerateIcons()
	{
		List<(int, string, int, int)> list = new List<(int, string, int, int)>();
		int count = GetIconCount();
		for (int i = 0; i < count; i++)
		{
			string name = GetIconName(i);
			var (x, y) = GetIconPosition(i);
			if (!string.IsNullOrEmpty(name))
			{
				list.Add((i, name, x, y));
			}
		}
		return list;
	}

	public static string? MoveToHidden(string filePath)
	{
		try
		{
			if (string.IsNullOrEmpty(filePath))
			{
				return null;
			}
			string full = Path.GetFullPath(filePath);
			if (!File.Exists(full) && !Directory.Exists(full))
			{
				return null;
			}
			if (!IsOnDesktop(full))
			{
				return null;
			}
			string hiddenDir = HiddenDir;
			try
			{
				Directory.CreateDirectory(hiddenDir);
			}
			catch (UnauthorizedAccessException)
			{
				_lastMoveError = "无法创建隐藏目录：" + hiddenDir + "，请检查用户数据目录的读写权限，或尝试在设置中更换隐藏目录位置";
				return null;
			}
			catch (IOException ex2)
			{
				_lastMoveError = "无法创建隐藏目录（IO异常）：" + ex2.Message;
				return null;
			}
			string baseName = Path.GetFileName(full);
			string naturalDest = Path.Combine(hiddenDir, baseName);
			if ((File.Exists(naturalDest) || Directory.Exists(naturalDest)) && !IsInManifest(naturalDest))
			{
				LogMigrationDiag("  MoveToHidden: found orphan '" + baseName + "' in hidden dir, attempting cleanup");
				try
				{
					if (Directory.Exists(naturalDest))
					{
						RemoveHiddenSystemAttrRecursive(naturalDest);
						if (SafeDeleteDirectory(naturalDest))
						{
							LogMigrationDiag("  MoveToHidden: ✅ orphan dir cleaned: " + baseName);
						}
						else if (Directory.Exists(naturalDest))
						{
							LogMigrationDiag("  MoveToHidden: ⚠ orphan dir undeletable, REUSING: " + baseName);
							AddManifestEntry(naturalDest, full, isDirectory: true);
							return naturalDest;
						}
					}
					else
					{
						RemoveHiddenSystemAttr(naturalDest);
						File.Delete(naturalDest);
						LogMigrationDiag("  MoveToHidden: ✅ orphan file cleaned: " + baseName);
					}
				}
				catch (Exception ex3)
				{
					LogMigrationDiag("  MoveToHidden: ⚠ orphan cleanup exception: " + ex3.Message);
					if ((File.Exists(naturalDest) || Directory.Exists(naturalDest)) && !IsInManifest(naturalDest))
					{
						LogMigrationDiag("  MoveToHidden: ⚠ REUSING orphan after exception: " + baseName);
						AddManifestEntry(naturalDest, full, Directory.Exists(full));
						return naturalDest;
					}
				}
			}
			string dest = ResolveCollisionFreePath(hiddenDir, Path.GetFileName(full));
			bool isDir = Directory.Exists(full);
			if (isDir)
			{
				MoveDirectoryCrossVolume(full, dest);
			}
			else
			{
				MoveFileCrossVolume(full, dest);
			}
			int num;
			if (!File.Exists(dest))
			{
				num = (Directory.Exists(dest) ? 1 : 0);
				if (num == 0)
				{
					goto IL_01df;
				}
			}
			else
			{
				num = 1;
			}
			AddManifestEntry(dest, full, isDir);
			goto IL_01df;
			IL_01df:
			return (num != 0) ? dest : null;
		}
		catch (Exception ex4)
		{
			_lastMoveError = ((ex4 is UnauthorizedAccessException) ? ("没有权限访问此路径，请检查文件权限设置（" + ex4.Message + "）") : ex4.Message);
			return null;
		}
	}

	private static void MoveFileCrossVolume(string source, string dest)
	{
		try
		{
			File.Move(source, dest);
		}
		catch (IOException) when (IsCrossVolume(source, dest))
		{
			if (File.Exists(dest))
			{
				throw new IOException("目标路径已存在文件，无法静默覆盖：" + dest);
			}
			File.Copy(source, dest, overwrite: false);
			if (!TrySafeDeleteFile(source))
			{
				try
				{
					File.Delete(dest);
				}
				catch
				{
				}
				throw new IOException("跨卷移动失败：无法删除源文件 '" + source + "'，操作已回滚。");
			}
		}
		catch (UnauthorizedAccessException)
		{
			RemoveHiddenSystemAttr(source);
			RemoveReadOnlyAttr(source);
			try
			{
				File.Move(source, dest);
			}
			catch (IOException) when (IsCrossVolume(source, dest))
			{
				if (File.Exists(dest))
				{
					throw new IOException("目标路径已存在文件，无法静默覆盖：" + dest);
				}
				File.Copy(source, dest, overwrite: false);
				if (!TrySafeDeleteFile(source))
				{
					try
					{
						File.Delete(dest);
					}
					catch
					{
					}
					throw new IOException("跨卷移动失败：无法删除源文件 '" + source + "'，操作已回滚。");
				}
			}
			catch (UnauthorizedAccessException)
			{
				try
				{
					if (File.Exists(dest))
					{
						throw new IOException("目标路径已存在文件，无法静默覆盖：" + dest);
					}
					File.Copy(source, dest, overwrite: false);
					File.Delete(source);
				}
				catch (UnauthorizedAccessException)
				{
					if (File.Exists(dest))
					{
						throw new IOException("目标路径已存在文件，无法静默覆盖：" + dest);
					}
					RemoveHiddenSystemAttr(dest);
					RemoveReadOnlyAttr(dest);
					File.Copy(source, dest, overwrite: false);
					File.Delete(source);
				}
			}
		}
	}

	private static void MoveDirectoryCrossVolume(string source, string dest)
	{
		try
		{
			Directory.Move(source, dest);
		}
		catch (IOException) when (!IsCrossVolume(source, dest))
		{
			TryCopyAndDeleteRecursive(source, dest);
		}
		catch (IOException) when (IsCrossVolume(source, dest))
		{
			TryCopyAndDeleteRecursive(source, dest);
		}
		catch (UnauthorizedAccessException)
		{
			RemoveHiddenSystemAttr(source);
			RemoveReadOnlyAttr(source);
			try
			{
				Directory.Move(source, dest);
			}
			catch (IOException) when (!IsCrossVolume(source, dest))
			{
				TryCopyAndDeleteRecursive(source, dest);
			}
			catch (IOException) when (IsCrossVolume(source, dest))
			{
				TryCopyAndDeleteRecursive(source, dest);
			}
			catch (UnauthorizedAccessException)
			{
				TryCopyAndDeleteRecursive(source, dest);
			}
		}
	}

	private static void TryCopyAndDeleteRecursive(string source, string dest)
	{
		List<string> failures = CopyDirectoryRecursive(source, dest);
		if (failures.Count > 0)
		{
			_lastMoveError = $"文件夹移动失败：{failures.Count} 个文件无法复制（源文件已保留），例如：{Path.GetFileName(failures.First())}";
		}
		else if (!VerifyDirectoryCopy(source, dest))
		{
			_lastMoveError = "文件夹移动失败：目标目录验证不通过，源文件已保留";
		}
		else if (!SafeDeleteDirectory(source))
		{
			LogMigrationDiag("    TryCopyAndDeleteRecursive: source delete failed, setting H+S fallback: " + Path.GetFileName(source));
			SetHiddenSystemRecursive(source);
		}
	}

	private static bool VerifyDirectoryCopy(string sourceDir, string destDir)
	{
		try
		{
			if (!Directory.Exists(destDir))
			{
				return false;
			}
			var (srcCount, srcSize) = CountFilesRecursive(sourceDir);
			var (destCount, destSize) = CountFilesRecursive(destDir);
			return srcCount == destCount && srcSize == destSize;
		}
		catch (Exception)
		{
			return true;
		}
	}

	private static (int Count, long Size) CountFilesRecursive(string dirPath)
	{
		int count = 0;
		long size = 0L;
		try
		{
			string[] files = Directory.GetFiles(dirPath);
			foreach (string file in files)
			{
				try
				{
					count++;
					size += new FileInfo(file).Length;
				}
				catch
				{
				}
			}
			files = Directory.GetDirectories(dirPath);
			for (int i = 0; i < files.Length; i++)
			{
				(int Count, long Size) tuple = CountFilesRecursive(files[i]);
				int subCount = tuple.Count;
				long subSize = tuple.Size;
				count += subCount;
				size += subSize;
			}
		}
		catch
		{
		}
		return (Count: count, Size: size);
	}

	public static long GetItemSize(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				return new FileInfo(path).Length;
			}
			if (Directory.Exists(path))
			{
				return CountFilesRecursive(path).Size;
			}
			return 0L;
		}
		catch
		{
			return 0L;
		}
	}

	private static void RemoveReadOnlyAttr(string path)
	{
		try
		{
			FileAttributes attrs = File.GetAttributes(path);
			if ((attrs & FileAttributes.ReadOnly) != FileAttributes.None)
			{
				attrs &= ~FileAttributes.ReadOnly;
				File.SetAttributes(path, attrs);
			}
		}
		catch
		{
		}
	}

	private static void SafeDeleteFile(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (UnauthorizedAccessException)
		{
			RemoveHiddenSystemAttr(path);
			RemoveReadOnlyAttr(path);
			File.Delete(path);
		}
	}

	private static bool TrySafeDeleteFile(string path)
	{
		try
		{
			File.Delete(path);
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			try
			{
				RemoveHiddenSystemAttr(path);
				RemoveReadOnlyAttr(path);
				File.Delete(path);
				return true;
			}
			catch
			{
				return false;
			}
		}
		catch
		{
			return false;
		}
	}

	private static bool SafeDeleteDirectory(string path)
	{
		if (!Directory.Exists(path))
		{
			return true;
		}
		for (int attempt = 1; attempt <= 5; attempt++)
		{
			try
			{
				try
				{
					RemoveHiddenSystemAttrRecursive(path);
				}
				catch
				{
				}
				try
				{
					RemoveReadOnlyRecursive(path);
				}
				catch
				{
				}
				Directory.Delete(path, recursive: true);
				if (!Directory.Exists(path))
				{
					if (attempt > 1)
					{
						LogMigrationDiag($"    SafeDeleteDirectory ✅ succeeded on attempt {attempt}/{5}: {Path.GetFileName(path)}");
					}
					return true;
				}
				LogMigrationDiag($"    SafeDeleteDirectory ⚠ Directory.Delete claimed success but dir still exists (attempt {attempt}): {Path.GetFileName(path)}");
			}
			catch (DirectoryNotFoundException)
			{
				return true;
			}
			catch (UnauthorizedAccessException ex2)
			{
				LogMigrationDiag($"    SafeDeleteDirectory ⚠ UnauthorizedAccess (attempt {attempt}/{5}): {Path.GetFileName(path)} — {ex2.Message}");
				try
				{
					RemoveHiddenSystemAttrRecursive(path);
				}
				catch
				{
				}
				try
				{
					RemoveReadOnlyRecursive(path);
				}
				catch
				{
				}
			}
			catch (IOException ex3)
			{
				LogMigrationDiag($"    SafeDeleteDirectory ⚠ IO locked (attempt {attempt}/{5}): {Path.GetFileName(path)} — {ex3.Message}");
				try
				{
					RemoveHiddenSystemAttrRecursive(path);
				}
				catch
				{
				}
				try
				{
					RemoveReadOnlyRecursive(path);
				}
				catch
				{
				}
			}
			catch (Exception ex4)
			{
				LogMigrationDiag($"    SafeDeleteDirectory ⚠ Unexpected error (attempt {attempt}/{5}): {Path.GetFileName(path)} — {ex4.GetType().Name}: {ex4.Message}");
			}
			if (attempt < 5)
			{
				int delayMs = 200 * (1 << attempt - 1);
				LogMigrationDiag($"    SafeDeleteDirectory ⏳ retrying in {delayMs}ms...");
				Thread.Sleep(delayMs);
			}
		}
		LogMigrationDiag($"    SafeDeleteDirectory ❌ FAILED after {5} attempts: {Path.GetFileName(path)}");
		try
		{
			string[] files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
			LogMigrationDiag($"    SafeDeleteDirectory \ud83d\udcc1 contents before abandon: {files.Length} files in '{Path.GetFileName(path)}'");
			foreach (string f in files.Take(5))
			{
				LogMigrationDiag($"      - {Path.GetFileName(f)} (attrs={File.GetAttributes(f)})");
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool IsCrossVolume(string path1, string path2)
	{
		string root1 = Path.GetPathRoot(path1)?.ToUpperInvariant() ?? "";
		string root2 = Path.GetPathRoot(path2)?.ToUpperInvariant() ?? "";
		if (root1 != root2 && !string.IsNullOrEmpty(root1))
		{
			return !string.IsNullOrEmpty(root2);
		}
		return false;
	}

	private static List<string> CopyDirectoryRecursive(string sourceDir, string destDir)
	{
		ConcurrentBag<string> failures = new ConcurrentBag<string>();
		Directory.CreateDirectory(destDir);
		string[] files = Directory.GetFiles(sourceDir);
		ParallelOptions fileOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
		};
		Parallel.ForEach(files, fileOptions, delegate(string file)
		{
			string text = Path.Combine(destDir, Path.GetFileName(file));
			try
			{
				CopySingleFileWithRetry(file, text);
				try
				{
					FileInfo fileInfo = new FileInfo(file);
					FileInfo fileInfo2 = new FileInfo(text);
					if (!fileInfo2.Exists || fileInfo2.Length != fileInfo.Length)
					{
						CopySingleFileWithRetry(file, text);
						fileInfo2.Refresh();
						if (!fileInfo2.Exists || fileInfo2.Length != fileInfo.Length)
						{
							failures.Add(file);
						}
					}
				}
				catch
				{
				}
			}
			catch (Exception)
			{
				failures.Add(file);
			}
		});
		string[] directories = Directory.GetDirectories(sourceDir);
		foreach (string subDir in directories)
		{
			string destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
			foreach (string f in CopyDirectoryRecursive(subDir, destSubDir))
			{
				failures.Add(f);
			}
		}
		return failures.ToList();
	}

	private static void CopySingleFileWithRetry(string source, string dest)
	{
		try
		{
			File.Copy(source, dest, overwrite: true);
		}
		catch (UnauthorizedAccessException)
		{
			try
			{
				RemoveReadOnlyAttr(source);
				RemoveHiddenSystemAttr(source);
			}
			catch
			{
			}
			try
			{
				RemoveReadOnlyAttr(dest);
				RemoveHiddenSystemAttr(dest);
			}
			catch
			{
			}
			try
			{
				File.Copy(source, dest, overwrite: true);
				return;
			}
			catch
			{
			}
			try
			{
				if (File.Exists(dest))
				{
					RemoveHiddenSystemAttr(dest);
					RemoveReadOnlyAttr(dest);
					File.Delete(dest);
				}
				File.Copy(source, dest, overwrite: true);
			}
			catch
			{
				throw;
			}
		}
	}

	public static List<(string FileName, string ProcessName)> CheckFileLocks(string hiddenPath)
	{
		List<(string, string)> result = new List<(string, string)>();
		if (string.IsNullOrEmpty(hiddenPath))
		{
			return result;
		}
		List<string> files = new List<string>();
		if (File.Exists(hiddenPath))
		{
			files.Add(hiddenPath);
		}
		else
		{
			if (!Directory.Exists(hiddenPath))
			{
				return result;
			}
			CollectFilesRecursive(hiddenPath, files);
		}
		if (files.Count == 0)
		{
			return result;
		}
		Dictionary<string, HashSet<string>> rmProcNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
		try
		{
			uint sessionHandle;
			int rmResult = NativeMethods.RmStartSession(out sessionHandle, 0, Guid.NewGuid().ToString("N"));
			if (rmResult == 0)
			{
				try
				{
					nint[] filePtrs = new nint[files.Count];
					try
					{
						for (int i = 0; i < files.Count; i++)
						{
							filePtrs[i] = Marshal.StringToHGlobalUni(files[i]);
						}
						rmResult = NativeMethods.RmRegisterResourcesEx(sessionHandle, (uint)files.Count, filePtrs, 0u, null, 0u, null);
					}
					finally
					{
						for (int j = 0; j < filePtrs.Length; j++)
						{
							if (filePtrs[j] != IntPtr.Zero)
							{
								Marshal.FreeHGlobal(filePtrs[j]);
							}
						}
					}
					if (rmResult == 0)
					{
						uint nProcInfo = 0u;
						uint nProcInfoNeeded = 0u;
						if (NativeMethods.RmGetList(sessionHandle, out nProcInfoNeeded, ref nProcInfo, null, out var lpdwRebootReasons) == 0 && nProcInfoNeeded != 0)
						{
							nProcInfo = nProcInfoNeeded;
							NativeMethods.RM_PROCESS_INFO[] procInfos = new NativeMethods.RM_PROCESS_INFO[nProcInfoNeeded];
							if (NativeMethods.RmGetList(sessionHandle, out lpdwRebootReasons, ref nProcInfo, procInfos, out var _) == 0)
							{
								for (int k = 0; k < nProcInfo; k++)
								{
									string procName = procInfos[k].strAppName;
									if (string.IsNullOrEmpty(procName))
									{
										continue;
									}
									foreach (string item in files)
									{
										string key = Path.GetFileName(item);
										if (!rmProcNames.ContainsKey(key))
										{
											rmProcNames[key] = new HashSet<string>();
										}
										rmProcNames[key].Add(procName);
									}
								}
							}
						}
					}
				}
				finally
				{
					NativeMethods.RmEndSession(sessionHandle);
				}
			}
		}
		catch
		{
		}
		foreach (string file in files)
		{
			if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string fileName = Path.GetFileName(file);
			if (IsFileWriteLocked(file))
			{
				if (!result.Any(((string, string) x) => string.Equals(x.Item1, fileName, StringComparison.OrdinalIgnoreCase)))
				{
					result.Add(new ValueTuple<string, string>(item2: (!rmProcNames.TryGetValue(fileName, out var procs) || procs.Count <= 0) ? "其他程序（编辑中）" : string.Join(", ", procs), item1: fileName));
				}
			}
			else if (IsFileOpenInKnownEditor(fileName) && !result.Any(((string, string) x) => string.Equals(x.Item1, fileName, StringComparison.OrdinalIgnoreCase)))
			{
				result.Add((fileName, "记事本（编辑中）"));
			}
		}
		return result;
	}

	private static void CollectFilesRecursive(string dirPath, List<string> files)
	{
		try
		{
			string[] files2 = Directory.GetFiles(dirPath);
			foreach (string file in files2)
			{
				files.Add(file);
			}
			files2 = Directory.GetDirectories(dirPath);
			for (int i = 0; i < files2.Length; i++)
			{
				CollectFilesRecursive(files2[i], files);
			}
		}
		catch
		{
		}
	}

	public static bool RestoreFromHidden(string originalPath, string hiddenPath)
	{
		string requestedPath = originalPath;
		SetLastRestored(originalPath, requestedPath);
		try
		{
			if (!File.Exists(hiddenPath) && !Directory.Exists(hiddenPath))
			{
				if (!File.Exists(originalPath) && !Directory.Exists(originalPath))
				{
					_lastRestoreFailReason = "隐藏副本不存在（" + hiddenPath + "），且原位置文件也不存在。可能被外部清理（如 Steam 卸载、云盘同步失败）。";
					LogMigrationDiag("    RestoreFromHidden: hiddenPath AND originalPath both missing: " + hiddenPath);
					return false;
				}
				return true;
			}
			bool isDir = Directory.Exists(hiddenPath);
			if (!isDir && IsAnyFileLocked(hiddenPath))
			{
				_lastRestoreFailReason = "文件「" + Path.GetFileName(hiddenPath) + "」正在被其他程序占用";
				LogMigrationDiag("    RestoreFromHidden: LOCKED — " + _lastRestoreFailReason);
				return false;
			}
			if (File.Exists(originalPath) || Directory.Exists(originalPath))
			{
				bool restoredAttributes = false;
				if (isDir && Directory.Exists(originalPath))
				{
					RemoveHiddenSystemAttrRecursive(originalPath);
					LogMigrationDiag("    RestoreFromHidden (dir branch): removing H+S from desktop, attempting delete hidden copy: " + Path.GetFileName(hiddenPath));
					try
					{
						RemoveHiddenSystemAttrRecursive(hiddenPath);
					}
					catch (Exception ex)
					{
						LogMigrationDiag("    RestoreFromHidden ⚠ RemoveHiddenSystemAttrRecursive(hidden) failed: " + ex.Message);
					}
					if (!SafeDeleteDirectory(hiddenPath))
					{
						LogMigrationDiag("    RestoreFromHidden: ❌ hidden copy delete FAILED — keeping manifest, returning false to preserve tracking");
						_lastRestoreFailReason = "无法删除隐藏副本「" + Path.GetFileName(hiddenPath) + "」（可能被其他程序占用），已保留跟踪记录待下次清理";
						return false;
					}
					try
					{
						RemoveManifestEntry(hiddenPath);
					}
					catch
					{
					}
					LogMigrationDiag("    RestoreFromHidden: ✅ hidden copy deleted + manifest removed");
					restoredAttributes = true;
				}
				if (restoredAttributes)
				{
					LogMigrationDiag("    RestoreFromHidden: H+S attributes restored, hidden copy deleted");
					return true;
				}
				string dir = Path.GetDirectoryName(originalPath);
				string nameNoExt = Path.GetFileNameWithoutExtension(originalPath);
				string ext = Path.GetExtension(originalPath);
				string oldPath = originalPath;
				int n = 1;
				string newPath;
				do
				{
					newPath = Path.Combine(dir ?? "", $"{nameNoExt} ({n}){ext}");
					n++;
				}
				while (File.Exists(newPath) || Directory.Exists(newPath));
				originalPath = newPath;
				LogMigrationDiag("    RestoreFromHidden: target conflict, renamed " + Path.GetFileName(oldPath) + " → " + Path.GetFileName(newPath));
			}
			string dir2 = Path.GetDirectoryName(originalPath);
			if (dir2 != null && !Directory.Exists(dir2))
			{
				string userDesktop = GetUserDesktopPath();
				string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
				if (originalPath.StartsWith(userDesktop, StringComparison.OrdinalIgnoreCase) || originalPath.StartsWith(publicDesktop, StringComparison.OrdinalIgnoreCase))
				{
					string fileName = Path.GetFileName(originalPath);
					string desktopRoot = (IsPublicDesktopPath(originalPath) ? publicDesktop : userDesktop);
					originalPath = Path.Combine(desktopRoot, fileName);
					if (File.Exists(originalPath) || Directory.Exists(originalPath))
					{
						string nameNoExt2 = Path.GetFileNameWithoutExtension(fileName);
						string ext2 = Path.GetExtension(fileName);
						int n2 = 1;
						do
						{
							originalPath = Path.Combine(desktopRoot, $"{nameNoExt2} ({n2}){ext2}");
							n2++;
						}
						while (File.Exists(originalPath) || Directory.Exists(originalPath));
					}
					LogMigrationDiag("    RestoreFromHidden: parent dir deleted, redirected to desktop root: " + originalPath);
				}
				else
				{
					Directory.CreateDirectory(dir2);
				}
			}
			if (isDir)
			{
				RemoveHiddenSystemAttrRecursive(hiddenPath);
			}
			else
			{
				RemoveHiddenSystemAttr(hiddenPath);
			}
			try
			{
				if (File.Exists(hiddenPath))
				{
					MoveFileCrossVolume(hiddenPath, originalPath);
				}
				else
				{
					if (!Directory.Exists(hiddenPath))
					{
						return false;
					}
					MoveDirectoryCrossVolume(hiddenPath, originalPath);
				}
			}
			catch (Exception) when (IsPublicDesktopPath(originalPath))
			{
				string fileName2 = Path.GetFileName(originalPath);
				string userDesktop2 = GetUserDesktopPath();
				string userDesktopPath = Path.Combine(userDesktop2, fileName2);
				if (File.Exists(userDesktopPath) || Directory.Exists(userDesktopPath))
				{
					string nameNoExt3 = Path.GetFileNameWithoutExtension(fileName2);
					string ext3 = Path.GetExtension(fileName2);
					int n3 = 1;
					do
					{
						userDesktopPath = Path.Combine(userDesktop2, $"{nameNoExt3} ({n3}){ext3}");
						n3++;
					}
					while (File.Exists(userDesktopPath) || Directory.Exists(userDesktopPath));
				}
				Directory.CreateDirectory(userDesktop2);
				if (File.Exists(hiddenPath))
				{
					MoveFileCrossVolume(hiddenPath, userDesktopPath);
				}
				else if (Directory.Exists(hiddenPath))
				{
					MoveDirectoryCrossVolume(hiddenPath, userDesktopPath);
				}
				originalPath = userDesktopPath;
				SetLastRestored(userDesktopPath, requestedPath);
			}
			if (File.Exists(originalPath) && originalPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					File.SetLastWriteTime(originalPath, DateTime.Now);
				}
				catch
				{
				}
				try
				{
					IconHelper.RepairWhiteIcon(originalPath);
				}
				catch
				{
				}
				try
				{
					IconHelper.FixShortcutIconReference(originalPath);
				}
				catch
				{
				}
			}
			try
			{
				uint finalAttrs = NativeMethods.GetFileAttributes(originalPath);
				if (finalAttrs != uint.MaxValue && (finalAttrs & 6) == 6)
				{
					LogMigrationDiag("    RestoreFromHidden: ⚠ final attr check found H+S on restored file, removing...");
					uint cleanAttrs = finalAttrs & 0xFFFFFFF9u;
					NativeMethods.SetFileAttributes(originalPath, cleanAttrs);
					LogMigrationDiag($"    RestoreFromHidden: ✅ H+S removed, final attrs=0x{finalAttrs:X}");
				}
			}
			catch (Exception ex3)
			{
				LogMigrationDiag("    RestoreFromHidden: ⚠ final attr check failed: " + ex3.Message);
			}
			RefreshDesktop(originalPath);
			RemoveManifestEntry(hiddenPath);
			SetLastRestored(originalPath, requestedPath);
			return true;
		}
		catch (Exception ex4)
		{
			_lastRestoreFailReason = "文件操作异常：" + ex4.Message;
			LogMigrationDiag("    RestoreFromHidden: EXCEPTION — " + ex4.GetType().Name + ": " + ex4.Message);
			return false;
		}
	}

	private static bool IsAnyFileLocked(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				if (IsFileWriteLocked(path))
				{
					return true;
				}
				if (IsFileOpenInKnownEditor(Path.GetFileName(path)))
				{
					return true;
				}
				return false;
			}
			if (Directory.Exists(path))
			{
				List<string> files = new List<string>();
				CollectFilesRecursive(path, files);
				foreach (string file in files)
				{
					if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
					{
						if (IsFileWriteLocked(file))
						{
							return true;
						}
						if (IsFileOpenInKnownEditor(Path.GetFileName(file)))
						{
							return true;
						}
					}
				}
				return false;
			}
		}
		catch
		{
			return true;
		}
		return false;
	}

	private static bool IsFileLockedByOS(string filePath)
	{
		nint handle = NativeMethods.CreateFileW(filePath, 2147483648u, 0u, IntPtr.Zero, 3u, 128u, IntPtr.Zero);
		if (handle == new IntPtr(-1))
		{
			if (Marshal.GetLastWin32Error() == 32)
			{
				return true;
			}
			return false;
		}
		NativeMethods.CloseHandle(handle);
		return false;
	}

	private static bool IsFileWriteLocked(string filePath)
	{
		nint handle = NativeMethods.CreateFileW(filePath, 1073741824u, 1u, IntPtr.Zero, 3u, 128u, IntPtr.Zero);
		if (handle == new IntPtr(-1))
		{
			if (Marshal.GetLastWin32Error() == 32)
			{
				return true;
			}
			return false;
		}
		NativeMethods.CloseHandle(handle);
		return false;
	}

	private static bool IsFileOpenInKnownEditor(string fileName)
	{
		HashSet<string> textExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			".txt", ".bat", ".cmd", ".log", ".ini", ".cfg", ".reg", ".ps1", ".vbs", ".js",
			".css", ".html", ".xml", ".json", ".csv"
		};
		string ext = Path.GetExtension(fileName);
		if (string.IsNullOrEmpty(ext) || !textExts.Contains(ext))
		{
			return false;
		}
		if (Path.GetFileNameWithoutExtension(fileName).Length == 0)
		{
			return false;
		}
		string[] array = new string[2] { "notepad", "wordpad" };
		foreach (string exeName in array)
		{
			try
			{
				Process[] processesByName = Process.GetProcessesByName(exeName);
				foreach (Process proc in processesByName)
				{
					try
					{
						if ((proc.MainWindowTitle ?? "").Contains(fileName, StringComparison.OrdinalIgnoreCase))
						{
							return true;
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
		return false;
	}

	private static void FlushPendingNotifications()
	{
		try
		{
			List<string> paths;
			lock (_pendingNotifPaths)
			{
				paths = _pendingNotifPaths.ToList();
				_pendingNotifPaths.Clear();
				_batchFlushScheduled = false;
			}
			if (paths.Count == 0)
			{
				return;
			}
			try
			{
				nint lv = GetDesktopListViewHandle();
				if (lv != IntPtr.Zero)
				{
					NativeMethods.PostMessage(lv, 4117u, IntPtr.Zero, -1);
					NativeMethods.InvalidateRect(lv, IntPtr.Zero, bErase: true);
				}
			}
			catch
			{
			}
			try
			{
				nint desktop = NativeMethods.FindWindow("Progman", null);
				if (desktop != IntPtr.Zero)
				{
					NativeMethods.PostMessage(desktop, 26u, IntPtr.Zero, IntPtr.Zero);
				}
			}
			catch
			{
			}
		}
		catch (Exception)
		{
		}
	}

	public static void NotifyDesktopChange(string? affectedPath = null)
	{
		try
		{
			bool inStartupGrace = (DateTime.UtcNow - _serviceStartTime).TotalSeconds < 30.0;
			if (affectedPath != null)
			{
				if (inStartupGrace)
				{
					lock (_pendingNotifPaths)
					{
						_pendingNotifPaths.Add(affectedPath);
					}
					if (!_batchFlushScheduled)
					{
						_batchFlushScheduled = true;
						Task.Delay(TimeSpan.FromSeconds(Math.Max(0.5, 30.0 - (DateTime.UtcNow - _serviceStartTime).TotalSeconds))).ContinueWith(delegate
						{
							FlushPendingNotifications();
						});
					}
					return;
				}
				nint ptr = Marshal.StringToHGlobalUni(affectedPath);
				NativeMethods.SHChangeNotify(8192u, 5u, ptr, IntPtr.Zero);
				Marshal.FreeHGlobal(ptr);
				if (affectedPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
				{
					ptr = Marshal.StringToHGlobalUni(affectedPath);
					NativeMethods.SHChangeNotify(2u, 5u, ptr, IntPtr.Zero);
					Marshal.FreeHGlobal(ptr);
				}
			}
			if (inStartupGrace)
			{
				return;
			}
			nint lv = _cachedDesktopHwnd;
			if (lv == IntPtr.Zero)
			{
				lv = (_cachedDesktopHwnd = GetDesktopListViewHandle());
			}
			if (lv != IntPtr.Zero)
			{
				NativeMethods.PostMessage(lv, 4117u, IntPtr.Zero, -1);
				NativeMethods.InvalidateRect(lv, IntPtr.Zero, bErase: true);
			}
			nint desktop = NativeMethods.FindWindow("Progman", null);
			if (desktop != IntPtr.Zero)
			{
				NativeMethods.PostMessage(desktop, 26u, IntPtr.Zero, IntPtr.Zero);
			}
			try
			{
				if (Application.Current == null)
				{
					return;
				}
				Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
				{
					try
					{
						if (affectedPath != null)
						{
							nint num = Marshal.StringToHGlobalUni(affectedPath);
							NativeMethods.SHChangeNotify(2u, 5u, num, IntPtr.Zero);
							Marshal.FreeHGlobal(num);
						}
						_assocChangedCts?.Cancel();
						_assocChangedCts?.Dispose();
						_assocChangedCts = new CancellationTokenSource();
						CancellationToken token = _assocChangedCts.Token;
						Task.Delay(800, token).ContinueWith(delegate
						{
							if (token.IsCancellationRequested)
							{
								return;
							}
							try
							{
								NativeMethods.SHChangeNotify(134217728u, 0u, IntPtr.Zero, IntPtr.Zero);
								IconHelper.ClearSystemIconCache();
							}
							catch
							{
							}
						}, TaskContinuationOptions.NotOnCanceled);
						nint desktopListViewHandle = GetDesktopListViewHandle();
						if (desktopListViewHandle != IntPtr.Zero)
						{
							NativeMethods.PostMessage(desktopListViewHandle, 4117u, IntPtr.Zero, -1);
						}
					}
					catch
					{
					}
				});
			}
			catch
			{
			}
		}
		catch
		{
		}
	}

	public static void RefreshDesktop(string? affectedPath = null)
	{
		NotifyDesktopChange(affectedPath);
	}

	public static void NotifyItemUnhidden(string path)
	{
		try
		{
			nint lv = GetDesktopListViewHandle();
			if (lv != IntPtr.Zero)
			{
				NativeMethods.PostMessage(lv, 256u, new IntPtr(116), IntPtr.Zero);
				NativeMethods.PostMessage(lv, 257u, new IntPtr(116), new IntPtr(3221225472L));
				NativeMethods.PostMessage(lv, 4117u, IntPtr.Zero, -1);
				NativeMethods.InvalidateRect(lv, IntPtr.Zero, bErase: true);
			}
			if (!((DateTime.UtcNow - _serviceStartTime).TotalSeconds < 30.0))
			{
				nint pc = Marshal.StringToHGlobalUni(path);
				NativeMethods.SHChangeNotify(2u, 4101u, pc, IntPtr.Zero);
				Marshal.FreeHGlobal(pc);
				nint pu = Marshal.StringToHGlobalUni(path);
				NativeMethods.SHChangeNotify(8192u, 5u, pu, IntPtr.Zero);
				Marshal.FreeHGlobal(pu);
				nint progman = NativeMethods.FindWindow("Progman", null);
				if (progman != IntPtr.Zero)
				{
					NativeMethods.PostMessage(progman, 26u, IntPtr.Zero, IntPtr.Zero);
				}
				NativeMethods.SHChangeNotify(134217728u, 4096u, IntPtr.Zero, IntPtr.Zero);
			}
		}
		catch
		{
		}
	}

	public static void ForceDesktopViewRefresh()
	{
		try
		{
			if (Application.Current == null)
			{
				return;
			}
			Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				try
				{
					nint desktopListViewHandle = GetDesktopListViewHandle();
					if (desktopListViewHandle != IntPtr.Zero)
					{
						NativeMethods.PostMessage(desktopListViewHandle, 256u, new IntPtr(116), IntPtr.Zero);
						NativeMethods.PostMessage(desktopListViewHandle, 257u, new IntPtr(116), new IntPtr(3221225472L));
					}
				}
				catch
				{
				}
			});
		}
		catch
		{
		}
	}

	public static void EnsureDesktopAutoRefresh()
	{
		if (_autoRefreshWatcher != null)
		{
			return;
		}
		try
		{
			string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
			if (!string.IsNullOrEmpty(desktopPath) && Directory.Exists(desktopPath))
			{
				_autoRefreshTimer = new Timer(delegate
				{
					TriggerAutoRefresh();
				}, null, -1, -1);
				_autoRefreshWatcher = new FileSystemWatcher(desktopPath)
				{
					NotifyFilter = (NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite),
					IncludeSubdirectories = false,
					EnableRaisingEvents = true
				};
				_autoRefreshWatcher.Created += delegate(object _, FileSystemEventArgs e)
				{
					ScheduleAutoRefresh(e.FullPath);
				};
				_autoRefreshWatcher.Changed += delegate(object _, FileSystemEventArgs e)
				{
					ScheduleAutoRefresh(e.FullPath);
				};
				_autoRefreshWatcher.Renamed += delegate(object _, RenamedEventArgs e)
				{
					ScheduleAutoRefresh(e.FullPath);
				};
				_autoRefreshWatcher.Deleted += delegate(object _, FileSystemEventArgs e)
				{
					ScheduleAutoRefresh(e.FullPath);
				};
			}
		}
		catch (Exception)
		{
		}
	}

	private static void ScheduleAutoRefresh(string? path)
	{
		if ((DateTime.UtcNow - _serviceStartTime).TotalSeconds < 30.0)
		{
			return;
		}
		if (!string.IsNullOrEmpty(path))
		{
			lock (_autoRefreshPaths)
			{
				_autoRefreshPaths.Add(path);
			}
		}
		try
		{
			_autoRefreshTimer?.Change(450, -1);
		}
		catch
		{
		}
	}

	private static void TriggerAutoRefresh()
	{
		try
		{
			List<string> paths;
			lock (_autoRefreshPaths)
			{
				paths = _autoRefreshPaths.ToList();
				_autoRefreshPaths.Clear();
			}
			if (IsPathManagedCallback != null && paths.Count > 0)
			{
				bool anyManaged = false;
				foreach (string p in paths)
				{
					try
					{
						if (IsPathManagedCallback(p))
						{
							anyManaged = true;
							break;
						}
					}
					catch
					{
					}
				}
				if (!anyManaged)
				{
					return;
				}
			}
			ForceDesktopViewRefresh();
		}
		catch
		{
		}
	}

	public static string[] GetDesktopFolderPaths()
	{
		List<string> paths = new List<string>();
		AddIfValid(GetKnownFolderPath(new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")));
		AddIfValid(GetKnownFolderPath(new Guid("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25")));
		AddIfValid(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
		AddIfValid(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
		AddIfValid(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
		return paths.ToArray();
		void AddIfValid(string? p)
		{
			if (!string.IsNullOrEmpty(p) && Directory.Exists(p) && !paths.Contains<string>(p, StringComparer.OrdinalIgnoreCase))
			{
				paths.Add(p);
			}
		}
	}

	private static bool IsOnDesktop(string filePath)
	{
		try
		{
			string dir = Path.GetDirectoryName(Path.GetFullPath(filePath).Trim());
			if (string.IsNullOrEmpty(dir))
			{
				return false;
			}
			dir = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string norm = NormalizePath(dir);
			string[] desktopFolderPaths = GetDesktopFolderPaths();
			for (int i = 0; i < desktopFolderPaths.Length; i++)
			{
				string ndp = NormalizePath(desktopFolderPaths[i]);
				if (string.Equals(norm, ndp, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				if (norm.StartsWith(ndp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	private static string NormalizePath(string path)
	{
		try
		{
			return Path.GetFullPath(path).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
		catch
		{
			return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}
	}

	private static string? GetKnownFolderPath(Guid knownFolder)
	{
		try
		{
			if (NativeMethods.SHGetKnownFolderPath(knownFolder, 0u, IntPtr.Zero, out var pPath) == 0 && pPath != IntPtr.Zero)
			{
				string? result = Marshal.PtrToStringUni(pPath);
				Marshal.FreeCoTaskMem(pPath);
				return result;
			}
		}
		catch
		{
		}
		return null;
	}

	private static bool IsPublicDesktopPath(string path)
	{
		try
		{
			string dir = Path.GetDirectoryName(Path.GetFullPath(path));
			if (string.IsNullOrEmpty(dir))
			{
				return false;
			}
			dir = NormalizePath(dir);
			string publicDesktop = GetKnownFolderPath(new Guid("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25"));
			if (publicDesktop != null && string.Equals(dir, NormalizePath(publicDesktop), StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
			if (!string.IsNullOrEmpty(commonDesktop) && string.Equals(dir, NormalizePath(commonDesktop), StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	private static string GetUserDesktopPath()
	{
		return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
	}

	private static string GetDefaultHiddenDir()
	{
		return Path.Combine(App.GetAppDataDir(), "hidden");
	}

	public static bool IsDangerousHiddenPath(string? path, out string? dangerReason)
	{
		dangerReason = null;
		if (string.IsNullOrEmpty(path))
		{
			return false;
		}
		try
		{
			string normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			StringComparer comparer = StringComparer.OrdinalIgnoreCase;
			if (Path.GetPathRoot(normalized) == normalized)
			{
				dangerReason = "隐藏路径「" + normalized + "」是盘符根目录，还原操作将扫描整个磁盘，可能导致系统文件被错误移出。";
				return true;
			}
			string exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
			if (!string.IsNullOrEmpty(exeDir))
			{
				string exeDirNormalized = Path.GetFullPath(exeDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if (comparer.Equals(normalized, exeDirNormalized) || normalized.StartsWith(exeDirNormalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					dangerReason = "隐藏路径「" + normalized + "」位于程序所在目录，还原操作可能将程序文件错误地移到桌面。";
					return true;
				}
			}
			string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			if (!string.IsNullOrEmpty(winDir))
			{
				string winDirNormalized = Path.GetFullPath(winDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if (comparer.Equals(normalized, winDirNormalized) || normalized.StartsWith(winDirNormalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					dangerReason = "隐藏路径「" + normalized + "」位于 Windows 系统目录，还原操作可能破坏系统文件。";
					return true;
				}
			}
			string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
			if (!string.IsNullOrEmpty(sysDir))
			{
				string sysDirNormalized = Path.GetFullPath(sysDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if (comparer.Equals(normalized, sysDirNormalized) || normalized.StartsWith(sysDirNormalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					dangerReason = "隐藏路径「" + normalized + "」位于 System32 目录，还原操作可能破坏系统文件。";
					return true;
				}
			}
			string progDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			if (!string.IsNullOrEmpty(progDir))
			{
				string progDirNormalized = Path.GetFullPath(progDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if (comparer.Equals(normalized, progDirNormalized) || normalized.StartsWith(progDirNormalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					dangerReason = "隐藏路径「" + normalized + "」位于 Program Files 目录，还原操作可能破坏已安装程序的文件。";
					return true;
				}
			}
			string progX86Dir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
			if (!string.IsNullOrEmpty(progX86Dir))
			{
				string progX86DirNormalized = Path.GetFullPath(progX86Dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if (comparer.Equals(normalized, progX86DirNormalized) || normalized.StartsWith(progX86DirNormalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					dangerReason = "隐藏路径「" + normalized + "」位于 Program Files (x86) 目录，还原操作可能破坏已安装程序的文件。";
					return true;
				}
			}
			string[] desktopFolderPaths = GetDesktopFolderPaths();
			foreach (string dp in desktopFolderPaths)
			{
				if (!string.IsNullOrEmpty(dp))
				{
					string dpNorm = Path.GetFullPath(dp).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					string dpParent = Path.GetDirectoryName(dpNorm);
					if (comparer.Equals(normalized, dpNorm) || (!string.IsNullOrEmpty(dpParent) && comparer.Equals(normalized, dpParent)) || normalized.StartsWith(dpNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrEmpty(dpParent) && normalized.StartsWith(dpParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
					{
						dangerReason = "隐藏路径「" + normalized + "」位于桌面目录或其父目录，还原操作会把桌面原生图标当成孤儿文件反复恢复，已阻止。请改回默认的 AppData\\DesktopTool\\hidden。";
						return true;
					}
				}
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	private static string ResolveCollisionFreePath(string directory, string desiredName)
	{
		string dest = Path.Combine(directory, desiredName);
		if (!File.Exists(dest) && !Directory.Exists(dest))
		{
			return dest;
		}
		string baseName = Path.GetFileNameWithoutExtension(desiredName);
		string ext = Path.GetExtension(desiredName);
		int n = 1;
		do
		{
			dest = Path.Combine(directory, $"{baseName} ({n}){ext}");
			n++;
		}
		while (File.Exists(dest) || Directory.Exists(dest));
		return dest;
	}

	public static int RecoverOrphanedFiles(HashSet<string> trackedHiddenPaths, HashSet<string>? trackedOriginalPaths = null)
	{
		string hiddenDir = HiddenDir;
		if (IsDangerousHiddenPath(hiddenDir, out string _))
		{
			return 0;
		}
		if (!Directory.Exists(hiddenDir))
		{
			return 0;
		}
		int recovered = 0;
		string[] desktopPaths = GetDesktopFolderPaths();
		if (desktopPaths.Length == 0)
		{
			return 0;
		}
		string restoredDir = desktopPaths[0];
		try
		{
			string hiddenNorm = Path.GetFullPath(hiddenDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string restNorm = Path.GetFullPath(restoredDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (string.Equals(hiddenNorm, restNorm, StringComparison.OrdinalIgnoreCase) || hiddenNorm.StartsWith(restNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				return 0;
			}
		}
		catch
		{
		}
		try
		{
			foreach (string entry in Directory.EnumerateFileSystemEntries(hiddenDir))
			{
				if (trackedHiddenPaths.Contains(NormalizePath(entry)))
				{
					continue;
				}
				string fileName = Path.GetFileName(entry);
				if (trackedOriginalPaths != null && trackedOriginalPaths.Count > 0 && trackedOriginalPaths.Any(delegate(string p)
				{
					try
					{
						return string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase);
					}
					catch
					{
						return false;
					}
				}))
				{
					continue;
				}
				string restoredPath = Path.Combine(restoredDir, fileName);
				try
				{
					if (File.Exists(restoredPath) || Directory.Exists(restoredPath))
					{
						string nameNoExt = Path.GetFileNameWithoutExtension(fileName);
						string ext = Path.GetExtension(fileName);
						int n = 1;
						do
						{
							restoredPath = Path.Combine(restoredDir, $"{nameNoExt} ({n}){ext}");
							n++;
						}
						while (File.Exists(restoredPath) || Directory.Exists(restoredPath));
					}
					if (File.Exists(entry))
					{
						MoveFileCrossVolume(entry, restoredPath);
					}
					else if (Directory.Exists(entry))
					{
						MoveDirectoryCrossVolume(entry, restoredPath);
					}
					recovered++;
					try
					{
						nint ntfPtr = Marshal.StringToHGlobalUni(restoredPath);
						NativeMethods.SHChangeNotify(8192u, 5u, ntfPtr, IntPtr.Zero);
						Marshal.FreeHGlobal(ntfPtr);
					}
					catch
					{
					}
					try
					{
						nint lv2 = GetDesktopListViewHandle();
						if (lv2 != IntPtr.Zero)
						{
							NativeMethods.PostMessage(lv2, 4117u, IntPtr.Zero, -1);
						}
					}
					catch
					{
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
		return recovered;
	}

	public static int ValidateTrackedFiles(Dictionary<string, Action> trackedPaths)
	{
		int removed = 0;
		foreach (KeyValuePair<string, Action> trackedPath in trackedPaths)
		{
			trackedPath.Deconstruct(out var key, out var value);
			string path = key;
			Action action = value;
			string normalized = NormalizePath(path);
			if (!File.Exists(normalized) && !Directory.Exists(normalized))
			{
				action();
				removed++;
			}
		}
		return removed;
	}

	private static void LogExplorerDiag(string message)
	{
		LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_explorer_settings.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
	}

	private static void LogHideError(string filePath, string reason)
	{
		LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_startup.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [TryAttributeHide] FAILED File={filePath}, Reason={reason}{Environment.NewLine}");
	}

	private static void LogMigrationDiag(string message)
	{
		LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_migration.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
	}

	private static void LogPathDiag(string message)
	{
		try
		{
			LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_filepath.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	private static void SetLastRestored(string path, string? owner)
	{
		lock (_pathDiagLock)
		{
			_lastRestoredActualPath = path;
			_lastRestoredOwner = owner;
			_lastRestoreSeq++;
		}
	}

	public static void SyncIconFilePath(ZoneIcon icon, string callerTag)
	{
		string actual;
		string owner;
		int seq;
		lock (_pathDiagLock)
		{
			actual = _lastRestoredActualPath;
			owner = _lastRestoredOwner;
			seq = _lastRestoreSeq;
		}
		if (!string.IsNullOrEmpty(actual) && !string.Equals(actual, icon.FilePath, StringComparison.OrdinalIgnoreCase))
		{
			bool crossContam = !string.Equals(owner, icon.FilePath, StringComparison.OrdinalIgnoreCase);
			LogPathDiag($"[SyncIconFilePath] {(crossContam ? "⚠ CROSS-CONTAM(已阻止写入) " : "")}{callerTag} | displayName={icon.DisplayName} | before={icon.FilePath} | actual={actual} | owner={owner} | seq={seq}");
			if (!crossContam)
			{
				icon.FilePath = actual;
			}
		}
	}

	public static int RepairCrossContaminatedFilePaths(IList<IconZone> zones)
	{
		if (zones == null)
		{
			return 0;
		}
		int repaired = 0;
		try
		{
			string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
			_ = HiddenDir;
			HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (IconZone zone in zones)
			{
				foreach (IconGroup group in zone.Groups)
				{
					foreach (ZoneIcon ic in group.Icons)
					{
						if (!string.IsNullOrEmpty(ic.FilePath))
						{
							used.Add(ic.FilePath);
						}
					}
				}
			}
			foreach (IconZone zone2 in zones)
			{
				foreach (IconGroup group2 in zone2.Groups)
				{
					foreach (IGrouping<string, ZoneIcon> item in (from grp in group2.Icons.Where((ZoneIcon i) => !string.IsNullOrEmpty(i.FilePath)).GroupBy<ZoneIcon, string>((ZoneIcon i) => i.FilePath, StringComparer.OrdinalIgnoreCase)
						where grp.Count() > 1
						select grp).ToList())
					{
						string canonicalName = Path.GetFileNameWithoutExtension(item.Key);
						foreach (ZoneIcon icon in item)
						{
							if (string.IsNullOrEmpty(icon.DisplayName) || !string.Equals(icon.DisplayName, canonicalName, StringComparison.OrdinalIgnoreCase))
							{
								string correct = FindCorrectFilePathForIcon(icon, desktop);
								if (!string.IsNullOrEmpty(correct) && !string.Equals(correct, icon.FilePath, StringComparison.OrdinalIgnoreCase) && !used.Contains(correct))
								{
									LogPathDiag($"[RepairCrossContam] ✅ displayName={icon.DisplayName} | {icon.FilePath} → {correct}");
									icon.FilePath = correct;
									used.Add(correct);
									repaired++;
								}
								else
								{
									LogPathDiag($"[RepairCrossContam] ⚠ 无法定位正确文件（保持原样）: displayName={icon.DisplayName} | broken={icon.FilePath} | candidate={correct ?? "(null)"}");
								}
							}
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			LogPathDiag("[RepairCrossContam] 异常: " + ex.Message);
		}
		if (repaired > 0)
		{
			LogPathDiag($"[RepairCrossContam] 共修复 {repaired} 个串味图标");
		}
		return repaired;
	}

	private static string? FindCorrectFilePathForIcon(ZoneIcon icon, string desktop)
	{
		string name = icon.DisplayName;
		if (string.IsNullOrEmpty(name))
		{
			return null;
		}
		string ext = Path.GetExtension(icon.FilePath);
		if (string.IsNullOrEmpty(ext))
		{
			ext = ".lnk";
		}
		string deskPath = Path.Combine(desktop, name + ext);
		if (File.Exists(deskPath) || Directory.Exists(deskPath))
		{
			return deskPath;
		}
		if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath))
		{
			string fn = Path.GetFileName(icon.HiddenPath);
			if (!string.IsNullOrEmpty(fn))
			{
				string fromHidden = Path.Combine(desktop, fn);
				if (!string.Equals(fromHidden, icon.FilePath, StringComparison.OrdinalIgnoreCase))
				{
					return fromHidden;
				}
			}
		}
		return null;
	}

	public static void VerifyAndLogIconPaths(IList<IconZone> zones, string tag)
	{
		if (zones == null)
		{
			return;
		}
		try
		{
			int total = 0;
			int dupGroups = 0;
			int missing = 0;
			StringBuilder sb = new StringBuilder();
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(32, 2, stringBuilder);
			handler.AppendLiteral("[VerifyAndLogIconPaths] tag=");
			handler.AppendFormatted(tag);
			handler.AppendLiteral(" 时间=");
			handler.AppendFormatted(DateTime.Now, "HH:mm:ss.fff");
			stringBuilder2.AppendLine(ref handler);
			foreach (IconZone zone in zones)
			{
				foreach (IconGroup group in zone.Groups)
				{
					foreach (IGrouping<string, ZoneIcon> g in (from source in @group.Icons.GroupBy<ZoneIcon, string>((ZoneIcon i) => i.FilePath ?? "", StringComparer.OrdinalIgnoreCase)
						where source.Count() > 1
						select source).ToList())
					{
						dupGroups++;
						stringBuilder = sb;
						StringBuilder stringBuilder3 = stringBuilder;
						handler = new StringBuilder.AppendInterpolatedStringHandler(27, 3, stringBuilder);
						handler.AppendLiteral("  ⚠ 共享FilePath(");
						handler.AppendFormatted(g.Key);
						handler.AppendLiteral(") 被 ");
						handler.AppendFormatted(g.Count());
						handler.AppendLiteral(" 个图标引用: ");
						handler.AppendFormatted(string.Join(" | ", g.Select((ZoneIcon i) => i.DisplayName)));
						stringBuilder3.AppendLine(ref handler);
					}
					foreach (ZoneIcon icon in group.Icons)
					{
						total++;
						if (!string.IsNullOrEmpty(icon.FilePath) && !File.Exists(icon.FilePath) && !Directory.Exists(icon.FilePath))
						{
							missing++;
							stringBuilder = sb;
							StringBuilder stringBuilder4 = stringBuilder;
							handler = new StringBuilder.AppendInterpolatedStringHandler(39, 2, stringBuilder);
							handler.AppendLiteral("  ⚠ FilePath不存在: displayName=");
							handler.AppendFormatted(icon.DisplayName);
							handler.AppendLiteral(" filePath=");
							handler.AppendFormatted(icon.FilePath);
							stringBuilder4.AppendLine(ref handler);
						}
					}
				}
			}
			stringBuilder = sb;
			StringBuilder stringBuilder5 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(39, 3, stringBuilder);
			handler.AppendLiteral("  汇总: total=");
			handler.AppendFormatted(total);
			handler.AppendLiteral(" 共享FilePath分组=");
			handler.AppendFormatted(dupGroups);
			handler.AppendLiteral(" 不存在FilePath=");
			handler.AppendFormatted(missing);
			stringBuilder5.AppendLine(ref handler);
			LogPathDiag(sb.ToString());
		}
		catch (Exception ex)
		{
			LogPathDiag("[VerifyAndLogIconPaths] 异常: " + ex.Message);
		}
	}

	private static bool ForceSetFileAttributesViaHandle(string full, uint targetAttrs)
	{
		try
		{
			nint hFile = NativeMethods.CreateFileW(full, 384u, 7u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
			if (hFile == -1 || hFile == IntPtr.Zero)
			{
				int err = Marshal.GetLastWin32Error();
				LogMigrationDiag($"    ForceSetViaHandle: CreateFileW failed, err={err}");
				return false;
			}
			try
			{
				NativeMethods.FILE_BASIC_INFO info = new NativeMethods.FILE_BASIC_INFO
				{
					CreationTime = -1L,
					LastAccessTime = -1L,
					LastWriteTime = -1L,
					ChangeTime = -1L,
					FileAttributes = targetAttrs
				};
				if (!NativeMethods.SetFileInformationByHandle(hFile, 0, ref info, (uint)Marshal.SizeOf<NativeMethods.FILE_BASIC_INFO>()))
				{
					int err2 = Marshal.GetLastWin32Error();
					LogMigrationDiag($"    ForceSetViaHandle: SetFileInformationByHandle failed, err={err2}");
					return false;
				}
				LogMigrationDiag("    ForceSetViaHandle: ✅ success");
				return true;
			}
			finally
			{
				NativeMethods.CloseHandle(hFile);
			}
		}
		catch (Exception ex)
		{
			LogMigrationDiag("    ForceSetViaHandle: ❌ exception: " + ex.GetType().Name + ": " + ex.Message);
			return false;
		}
	}

	private static bool ForceSetFileAttributesViaReplace(string full, uint targetAttrs)
	{
		try
		{
			string? directoryName = Path.GetDirectoryName(full);
			string name = Path.GetFileName(full);
			string tempFile = Path.Combine(directoryName, "~dttmp_" + name);
			try
			{
				File.Delete(tempFile);
			}
			catch
			{
			}
			File.Copy(full, tempFile, overwrite: true);
			LogMigrationDiag("    ForceSetViaReplace: copied to temp=" + tempFile);
			NativeMethods.SetLastError(0u);
			if (!NativeMethods.SetFileAttributes(tempFile, targetAttrs))
			{
				int err = Marshal.GetLastWin32Error();
				LogMigrationDiag($"    ForceSetViaReplace: SetFileAttributes on temp failed, err={err}");
				if (!ForceSetFileAttributesViaHandle(tempFile, targetAttrs))
				{
					LogMigrationDiag("    ForceSetViaReplace: handle method on temp also failed, giving up");
					try
					{
						File.Delete(tempFile);
					}
					catch
					{
					}
					return false;
				}
			}
			uint verifyTemp = NativeMethods.GetFileAttributes(tempFile);
			if (verifyTemp == uint.MaxValue || (verifyTemp & 6) != 6)
			{
				LogMigrationDiag($"    ForceSetViaReplace: temp file H+S verify failed, attrs={verifyTemp}");
				try
				{
					File.Delete(tempFile);
				}
				catch
				{
				}
				return false;
			}
			File.Delete(full);
			LogMigrationDiag("    ForceSetViaReplace: original deleted");
			File.Move(tempFile, full);
			LogMigrationDiag("    ForceSetViaReplace: ✅ success (copy-delete-replace)");
			return true;
		}
		catch (Exception ex)
		{
			LogMigrationDiag("    ForceSetViaReplace: ❌ exception: " + ex.GetType().Name + ": " + ex.Message);
			return false;
		}
	}

	private static bool RollbackFileAttributes(string full, uint rollbackAttrs)
	{
		NativeMethods.SetLastError(0u);
		if (NativeMethods.SetFileAttributes(full, rollbackAttrs))
		{
			uint verify = NativeMethods.GetFileAttributes(full);
			if (verify != uint.MaxValue && (verify & 2) == 0)
			{
				LogMigrationDiag($"    Rollback: L1 SetFileAttributes ✅ (attrs now {verify})");
				return true;
			}
			LogMigrationDiag($"    Rollback: L1 SetFileAttributes ok but H still set, attrs={verify}");
		}
		else
		{
			int err = Marshal.GetLastWin32Error();
			LogMigrationDiag($"    Rollback: L1 SetFileAttributes failed, err={err}");
		}
		LogMigrationDiag("    Rollback: L2 trying ForceSetFileAttributesViaHandle...");
		if (ForceSetFileAttributesViaHandle(full, rollbackAttrs))
		{
			uint verify2 = NativeMethods.GetFileAttributes(full);
			if (verify2 != uint.MaxValue && (verify2 & 2) == 0)
			{
				LogMigrationDiag($"    Rollback: L2 Handle ✅ (attrs now {verify2})");
				return true;
			}
			LogMigrationDiag($"    Rollback: L2 Handle ok but H still set, attrs={verify2}");
		}
		LogMigrationDiag("    Rollback: L3 trying ForceSetFileAttributesViaReplace...");
		if (ForceSetFileAttributesViaReplace(full, rollbackAttrs))
		{
			uint verify3 = NativeMethods.GetFileAttributes(full);
			if (verify3 != uint.MaxValue && (verify3 & 2) == 0)
			{
				LogMigrationDiag($"    Rollback: L3 Replace ✅ (attrs now {verify3})");
				return true;
			}
			LogMigrationDiag($"    Rollback: L3 Replace ok but H still set, attrs={verify3}");
		}
		LogMigrationDiag("    Rollback: ❌ ALL LEVELS FAILED — file stuck in semi-transparent state!");
		LogHideError(full, "H+S rollback ALL LEVELS FAILED — file stuck H-only → semi-transparent ghost");
		return false;
	}

	private static void AddManifestEntry(string hiddenPath, string originalPath, bool isDirectory)
	{
		lock (_manifestLock)
		{
			try
			{
				ManifestData manifest = LoadManifestInternal();
				if (!manifest.Entries.Any((ManifestEntry e) => string.Equals(e.HiddenPath, hiddenPath, StringComparison.OrdinalIgnoreCase)))
				{
					manifest.Entries.Add(new ManifestEntry
					{
						HiddenPath = hiddenPath,
						OriginalPath = originalPath,
						IsDirectory = isDirectory
					});
					SaveManifestInternal(manifest);
				}
			}
			catch
			{
			}
		}
	}

	public static void RemoveManifestEntry(string hiddenPath)
	{
		lock (_manifestLock)
		{
			try
			{
				ManifestData manifestData = LoadManifestInternal();
				manifestData.Entries.RemoveAll((ManifestEntry e) => string.Equals(e.HiddenPath, hiddenPath, StringComparison.OrdinalIgnoreCase));
				SaveManifestInternal(manifestData);
			}
			catch
			{
			}
		}
	}

	public static void UpdateManifestEntry(string oldHiddenPath, string newHiddenPath)
	{
		lock (_manifestLock)
		{
			try
			{
				ManifestData manifest = LoadManifestInternal();
				ManifestEntry entry = manifest.Entries.FirstOrDefault((ManifestEntry e) => string.Equals(e.HiddenPath, oldHiddenPath, StringComparison.OrdinalIgnoreCase));
				if (entry != null)
				{
					entry.HiddenPath = newHiddenPath;
					SaveManifestInternal(manifest);
				}
			}
			catch
			{
			}
		}
	}

	public static List<(string HiddenPath, string OriginalPath, bool IsDirectory)> GetManifestEntries()
	{
		lock (_manifestLock)
		{
			try
			{
				if (!File.Exists(ManifestFilePath))
				{
					return new List<(string, string, bool)>();
				}
				return LoadManifestInternal().Entries.Select((ManifestEntry e) => (HiddenPath: e.HiddenPath, OriginalPath: e.OriginalPath, IsDirectory: e.IsDirectory)).ToList();
			}
			catch
			{
				return new List<(string, string, bool)>();
			}
		}
	}

	private static bool IsInManifest(string hiddenPath)
	{
		lock (_manifestLock)
		{
			try
			{
				if (!File.Exists(ManifestFilePath))
				{
					return false;
				}
				return LoadManifestInternal().Entries.Any((ManifestEntry e) => string.Equals(e.HiddenPath, hiddenPath, StringComparison.OrdinalIgnoreCase));
			}
			catch
			{
				return false;
			}
		}
	}

	public static int CountManifestOrphans(IEnumerable<IconZone> zones)
	{
		List<(string, string, bool)> manifestEntries = GetManifestEntries();
		if (manifestEntries.Count == 0)
		{
			return 0;
		}
		HashSet<string> trackedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (IconZone zone in zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (icon.IsHidden && !string.IsNullOrEmpty(icon.HiddenPath))
					{
						trackedPaths.Add(icon.HiddenPath);
					}
				}
			}
		}
		int orphans = 0;
		foreach (var entry in manifestEntries)
		{
			if ((File.Exists(entry.Item1) || Directory.Exists(entry.Item1)) && !trackedPaths.Contains(entry.Item1))
			{
				orphans++;
			}
		}
		return orphans;
	}

	private static void SyncZoneIconFromManifest(IList<IconZone> zones, string originalPath, string? hiddenMethod, string? hiddenPath)
	{
		foreach (IconZone zone in zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (string.Equals(icon.FilePath, originalPath, StringComparison.OrdinalIgnoreCase))
					{
						icon.HiddenMethod = hiddenMethod;
						icon.HiddenPath = hiddenPath;
						return;
					}
				}
			}
		}
	}

	public static int RestoreAllFromManifest()
	{
		lock (_manifestLock)
		{
			try
			{
				if (IsDangerousHiddenPath(HiddenDir, out string _))
				{
					return 0;
				}
				if (!File.Exists(ManifestFilePath))
				{
					return 0;
				}
				ManifestData manifest = LoadManifestInternal();
				if (manifest.Entries.Count == 0)
				{
					return 0;
				}
				int restored = 0;
				List<ManifestEntry> failedEntries = new List<ManifestEntry>();
				foreach (ManifestEntry entry in manifest.Entries.OrderBy((ManifestEntry e) => e.OriginalPath.Count((char c) => c == Path.DirectorySeparatorChar)).ToList())
				{
					try
					{
						if (!File.Exists(entry.HiddenPath) && !Directory.Exists(entry.HiddenPath))
						{
							failedEntries.Add(entry);
							continue;
						}
						string targetPath = entry.OriginalPath;
						if (File.Exists(targetPath) || Directory.Exists(targetPath))
						{
							string dir = Path.GetDirectoryName(targetPath);
							string nameNoExt = Path.GetFileNameWithoutExtension(targetPath);
							string ext = Path.GetExtension(targetPath);
							int n = 1;
							do
							{
								targetPath = Path.Combine(dir ?? "", $"{nameNoExt} ({n}){ext}");
								n++;
							}
							while (File.Exists(targetPath) || Directory.Exists(targetPath));
						}
						string targetDir = Path.GetDirectoryName(targetPath);
						if (targetDir != null && !Directory.Exists(targetDir))
						{
							Directory.CreateDirectory(targetDir);
						}
						RemoveHiddenSystemAttr(entry.HiddenPath);
						try
						{
							if (entry.IsDirectory && Directory.Exists(entry.HiddenPath))
							{
								MoveDirectoryCrossVolume(entry.HiddenPath, targetPath);
								goto IL_0309;
							}
							if (File.Exists(entry.HiddenPath))
							{
								MoveFileCrossVolume(entry.HiddenPath, targetPath);
								goto IL_0309;
							}
							failedEntries.Add(entry);
						}
						catch (Exception) when (IsPublicDesktopPath(targetPath))
						{
							string fileName = Path.GetFileName(targetPath);
							string userDesktop = GetUserDesktopPath();
							string userDesktopPath2 = Path.Combine(userDesktop, fileName);
							if (File.Exists(userDesktopPath2) || Directory.Exists(userDesktopPath2))
							{
								string nameNoExt2 = Path.GetFileNameWithoutExtension(fileName);
								string ext2 = Path.GetExtension(fileName);
								int nn = 1;
								do
								{
									userDesktopPath2 = Path.Combine(userDesktop, $"{nameNoExt2} ({nn}){ext2}");
									nn++;
								}
								while (File.Exists(userDesktopPath2) || Directory.Exists(userDesktopPath2));
							}
							Directory.CreateDirectory(userDesktop);
							if (entry.IsDirectory && Directory.Exists(entry.HiddenPath))
							{
								MoveDirectoryCrossVolume(entry.HiddenPath, userDesktopPath2);
							}
							else if (File.Exists(entry.HiddenPath))
							{
								MoveFileCrossVolume(entry.HiddenPath, userDesktopPath2);
							}
							targetPath = userDesktopPath2;
							goto IL_0309;
						}
						goto end_IL_00a3;
						IL_0309:
						restored++;
						NotifyDesktopChange(targetPath);
						end_IL_00a3:;
					}
					catch
					{
						failedEntries.Add(entry);
					}
				}
				manifest.Entries = failedEntries;
				SaveManifestInternal(manifest);
				return restored;
			}
			catch
			{
				return 0;
			}
		}
	}

	public static int RestoreUntrackedFiles(HashSet<string> trackedHiddenPaths, string? desktopPath = null)
	{
		try
		{
			string hiddenDir = HiddenDir;
			if (IsDangerousHiddenPath(hiddenDir, out string _))
			{
				return 0;
			}
			if (!Directory.Exists(hiddenDir))
			{
				return 0;
			}
			string desktop = desktopPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			int restored = 0;
			foreach (string entry in Directory.EnumerateFileSystemEntries(hiddenDir))
			{
				string entryName = Path.GetFileName(entry);
				if (entryName.Equals("recovery_manifest.json", StringComparison.OrdinalIgnoreCase) || entryName.StartsWith("."))
				{
					continue;
				}
				string fullPath = Path.GetFullPath(entry);
				if (trackedHiddenPaths.Contains(fullPath))
				{
					continue;
				}
				try
				{
					string fileName = Path.GetFileName(entry);
					string targetPath = ResolveCollisionFreePath(desktop, fileName);
					RemoveHiddenSystemAttr(fullPath);
					if (Directory.Exists(fullPath))
					{
						MoveDirectoryCrossVolume(fullPath, targetPath);
						goto IL_00d3;
					}
					if (File.Exists(fullPath))
					{
						MoveFileCrossVolume(fullPath, targetPath);
						goto IL_00d3;
					}
					goto end_IL_008f;
					IL_00d3:
					if (File.Exists(targetPath) || Directory.Exists(targetPath))
					{
						NotifyDesktopChange(targetPath);
						restored++;
					}
					end_IL_008f:;
				}
				catch (Exception)
				{
				}
			}
			return restored;
		}
		catch
		{
			return 0;
		}
	}

	public static void CleanManifest()
	{
		lock (_manifestLock)
		{
			try
			{
				DeleteManifestFile();
			}
			catch
			{
			}
		}
	}

	private static ManifestData LoadManifestInternal()
	{
		try
		{
			if (!File.Exists(ManifestFilePath))
			{
				return new ManifestData();
			}
			return JsonSerializer.Deserialize<ManifestData>(File.ReadAllText(ManifestFilePath)) ?? new ManifestData();
		}
		catch
		{
			return new ManifestData();
		}
	}

	private static void SaveManifestInternal(ManifestData manifest)
	{
		try
		{
			string dir = Path.GetDirectoryName(ManifestFilePath);
			if (dir != null)
			{
				Directory.CreateDirectory(dir);
			}
			string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
			{
				WriteIndented = false
			});
			File.WriteAllText(ManifestFilePath, json);
			try
			{
				uint attrs = NativeMethods.GetFileAttributes(ManifestFilePath);
				if (attrs != uint.MaxValue)
				{
					attrs |= 6;
					NativeMethods.SetFileAttributes(ManifestFilePath, attrs);
				}
			}
			catch
			{
			}
		}
		catch
		{
		}
	}

	private static void DeleteManifestFile()
	{
		try
		{
			if (File.Exists(ManifestFilePath))
			{
				RemoveHiddenSystemAttr(ManifestFilePath);
				File.Delete(ManifestFilePath);
			}
		}
		catch
		{
		}
	}

	private static void RemoveHiddenSystemAttr(string path)
	{
		try
		{
			uint attrs = NativeMethods.GetFileAttributes(path);
			if (attrs != uint.MaxValue)
			{
				attrs &= 0xFFFFFFF9u;
				NativeMethods.SetFileAttributes(path, attrs);
			}
		}
		catch
		{
		}
	}

	private static void RemoveHiddenSystemAttrRecursive(string dirPath)
	{
		try
		{
			RemoveHiddenSystemAttr(dirPath);
			string[] files = Directory.GetFiles(dirPath);
			for (int i = 0; i < files.Length; i++)
			{
				RemoveHiddenSystemAttr(files[i]);
			}
			files = Directory.GetDirectories(dirPath);
			for (int i = 0; i < files.Length; i++)
			{
				RemoveHiddenSystemAttrRecursive(files[i]);
			}
		}
		catch
		{
		}
	}

	private static void RemoveReadOnlyRecursive(string dirPath)
	{
		try
		{
			RemoveReadOnlyAttr(dirPath);
			string[] files = Directory.GetFiles(dirPath);
			for (int i = 0; i < files.Length; i++)
			{
				RemoveReadOnlyAttr(files[i]);
			}
			files = Directory.GetDirectories(dirPath);
			for (int i = 0; i < files.Length; i++)
			{
				RemoveReadOnlyRecursive(files[i]);
			}
		}
		catch
		{
		}
	}

	private static void SetHiddenSystemRecursive(string dirPath)
	{
		try
		{
			uint attrs = NativeMethods.GetFileAttributes(dirPath);
			if (attrs != uint.MaxValue)
			{
				attrs |= 6;
				NativeMethods.SetFileAttributes(dirPath, attrs);
			}
			string[] files = Directory.GetFiles(dirPath);
			foreach (string file in files)
			{
				try
				{
					uint fa = NativeMethods.GetFileAttributes(file);
					if (fa != uint.MaxValue)
					{
						fa |= 6;
						NativeMethods.SetFileAttributes(file, fa);
					}
				}
				catch
				{
				}
			}
			files = Directory.GetDirectories(dirPath);
			for (int i = 0; i < files.Length; i++)
			{
				SetHiddenSystemRecursive(files[i]);
			}
		}
		catch
		{
		}
	}

	public static string PredictHiddenPath(string originalPath)
	{
		string hiddenDir = HiddenDir;
		Directory.CreateDirectory(hiddenDir);
		return ResolveCollisionFreePath(hiddenDir, Path.GetFileName(originalPath));
	}

	public static bool MoveToHiddenPath(string filePath, string predeterminedHiddenPath)
	{
		try
		{
			string full = Path.GetFullPath(filePath);
			if (!File.Exists(full) && !Directory.Exists(full))
			{
				return false;
			}
			if (!IsOnDesktop(full))
			{
				return false;
			}
			string destDir = Path.GetDirectoryName(predeterminedHiddenPath);
			if (destDir != null)
			{
				Directory.CreateDirectory(destDir);
			}
			bool isDir = Directory.Exists(full);
			if (isDir)
			{
				MoveDirectoryCrossVolume(full, predeterminedHiddenPath);
			}
			else
			{
				MoveFileCrossVolume(full, predeterminedHiddenPath);
			}
			int num;
			if (!File.Exists(predeterminedHiddenPath))
			{
				num = (Directory.Exists(predeterminedHiddenPath) ? 1 : 0);
				if (num == 0)
				{
					goto IL_006e;
				}
			}
			else
			{
				num = 1;
			}
			AddManifestEntry(predeterminedHiddenPath, full, isDir);
			goto IL_006e;
			IL_006e:
			return (byte)num != 0;
		}
		catch
		{
			return false;
		}
	}

	private static int ReadShowSuperHidden()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced");
			if (key == null)
			{
				return 0;
			}
			return (key.GetValue("ShowSuperHidden") is int intVal) ? intVal : 0;
		}
		catch
		{
			return 0;
		}
	}

	public static bool SetExplorerSuperHidden(bool hide)
	{
		int targetValue = ((!hide) ? 1 : 0);
		try
		{
			using (RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced"))
			{
				if (key == null)
				{
					return false;
				}
				key.SetValue("ShowSuperHidden", targetValue, RegistryValueKind.DWord);
			}
			if (ReadShowSuperHidden() == targetValue)
			{
				return true;
			}
			return false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static void ConfigureExplorerForAttributeMode()
	{
		if (!_originalShowSuperHidden.HasValue)
		{
			_originalShowSuperHidden = ReadShowSuperHidden();
		}
		if (!_originalHidden.HasValue)
		{
			_originalHidden = ReadExplorerHidden();
		}
		bool showSuperOk = SetExplorerSuperHidden(hide: true);
		bool hiddenOk = SetExplorerHidden(show: false);
		LogMigrationDiag($"ConfigureExplorerForAttributeMode: ShowSuperHidden OK={showSuperOk}, Hidden OK={hiddenOk}, final Hidden={ReadExplorerHidden()}, final ShowSuperHidden={ReadShowSuperHidden()}");
		NotifyExplorerSettingsChanged();
	}

	private static bool SetSuperHiddenViaShell(bool hide)
	{
		try
		{
			LogExplorerDiag($"[ShellAPI] 开始设置 hide={hide} (目标 fShowSuperHidden={!hide})");
			NativeMethods.SHELLSTATE ss = default(NativeMethods.SHELLSTATE);
			NativeMethods.SHGetSetSettings(ref ss, 262144u, bSet: false);
			bool fShowSuperHidden = (ss.dwBits1 & 0x8000) != 0;
			LogExplorerDiag($"[ShellAPI] 当前 Shell State dwBits1=0x{ss.dwBits1:X8} fShowSuperHidden(bit15)={fShowSuperHidden}");
			bool targetBit = !hide;
			if (fShowSuperHidden == targetBit)
			{
				LogExplorerDiag("[ShellAPI] ✅ 已经是目标状态，无需修改");
				return true;
			}
			if (targetBit)
			{
				ss.dwBits1 |= 32768u;
			}
			else
			{
				ss.dwBits1 &= 4294934527u;
			}
			LogExplorerDiag($"[ShellAPI] 写回 dwBits1=0x{ss.dwBits1:X8} (bit15={targetBit})");
			NativeMethods.SHGetSetSettings(ref ss, 262144u, bSet: true);
			NativeMethods.SHELLSTATE ssVerify = default(NativeMethods.SHELLSTATE);
			NativeMethods.SHGetSetSettings(ref ssVerify, 262144u, bSet: false);
			bool isSet = (ssVerify.dwBits1 & 0x8000) != 0;
			if (isSet == targetBit)
			{
				LogExplorerDiag($"[ShellAPI] ✅ 验证通过 fShowSuperHidden={isSet}");
				return true;
			}
			LogExplorerDiag($"[ShellAPI] ❌ 验证失败: 期望={targetBit}, 实际={isSet}, dwBits1=0x{ssVerify.dwBits1:X8}");
			return false;
		}
		catch (Exception ex)
		{
			LogExplorerDiag("[ShellAPI] ❌ 异常: " + ex.GetType().Name + ": " + ex.Message);
			return false;
		}
	}

	public static (bool Success, string? ErrorMessage) EnsureShowSuperHiddenForAttributeMode()
	{
		LogExplorerDiag("[Ensure] ====== 开始 EnsureShowSuperHiddenForAttributeMode ======");
		if (!_originalShowSuperHidden.HasValue)
		{
			_originalShowSuperHidden = ReadShowSuperHidden();
			LogExplorerDiag($"[Ensure] 保存原始 ShowSuperHidden(reg)={_originalShowSuperHidden.Value} (0=隐藏,1=显示)");
		}
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			LogExplorerDiag($"[Ensure] Shell API 第 {attempt}/3 次尝试...");
			if (SetSuperHiddenViaShell(hide: true))
			{
				LogExplorerDiag("[Ensure] ✅ Shell API 设置成功");
				return (Success: true, ErrorMessage: null);
			}
			LogExplorerDiag($"[Ensure] Shell API 第 {attempt} 次失败，尝试注册表备用...");
			bool num = SetExplorerSuperHidden(hide: true);
			NotifyExplorerSettingsChanged();
			if (!num)
			{
				LogExplorerDiag($"[Ensure] ❌ 注册表写入也失败 (attempt={attempt})");
				if (attempt == 3)
				{
					return (Success: false, ErrorMessage: "无法自动勾选「隐藏受保护的操作系统文件」选项。\n\n请按以下步骤手动设置：\n1. 打开任意文件夹（如「此电脑」）\n2. 点击顶部「查看」→「选项」\n3. 在「文件夹选项」窗口切换到「查看」选项卡\n4. 在高级设置列表中勾选「隐藏受保护的操作系统文件(推荐)」\n5. 点击「确定」保存\n\n完成上述操作后，请重新切换到属性隐藏模式。");
				}
				Thread.Sleep(500);
				continue;
			}
			Thread.Sleep(500);
			int verifyValue = ReadShowSuperHidden();
			LogExplorerDiag($"[Ensure] 注册表验证 ShowSuperHidden={verifyValue} (期望=0)");
			if (verifyValue == 0)
			{
				LogExplorerDiag($"[Ensure] 注册表 ShowSuperHidden={verifyValue} OK，再次尝试 Shell API...");
				if (SetSuperHiddenViaShell(hide: true))
				{
					LogExplorerDiag("[Ensure] ✅ 第二次 Shell API 成功");
					return (Success: true, ErrorMessage: null);
				}
				LogExplorerDiag($"[Ensure] ⚠\ufe0f 注册表 OK 但 Shell API 仍失败 (attempt={attempt})");
			}
			else
			{
				LogExplorerDiag($"[Ensure] ❌ 注册表验证失败: ShowSuperHidden={verifyValue}，期望=0");
			}
			if (attempt < 3)
			{
				Thread.Sleep(500);
			}
		}
		try
		{
			NativeMethods.SHELLSTATE ssFinal = default(NativeMethods.SHELLSTATE);
			NativeMethods.SHGetSetSettings(ref ssFinal, 262144u, bSet: false);
			bool finalShellState = (ssFinal.dwBits1 & 0x8000) != 0;
			int finalRegValue = ReadShowSuperHidden();
			LogExplorerDiag($"[Ensure] 最终状态: Shell fShowSuperHidden={finalShellState} (1=显示), Registry ShowSuperHidden={finalRegValue} (1=显示)");
			if (!finalShellState && finalRegValue == 0)
			{
				LogExplorerDiag("[Ensure] ✅ 最终验证通过（系统文件已隐藏）");
				return (Success: true, ErrorMessage: null);
			}
			LogExplorerDiag("[Ensure] ❌ 最终验证失败 — 将弹窗告知用户");
		}
		catch
		{
		}
		return (Success: false, ErrorMessage: "已多次尝试自动勾选「隐藏受保护的操作系统文件」选项，但均未成功。\n\n请按以下步骤手动设置：\n1. 打开任意文件夹（如「此电脑」）\n2. 点击顶部「查看」→「选项」\n3. 在「文件夹选项」窗口切换到「查看」选项卡\n4. 在高级设置列表中勾选「隐藏受保护的操作系统文件(推荐)」\n5. 点击「确定」保存\n\n完成上述操作后，请重新切换到属性隐藏模式。");
	}

	public static void RestoreExplorerSuperHidden()
	{
		if (_originalShowSuperHidden.HasValue)
		{
			SetExplorerSuperHidden(_originalShowSuperHidden.Value == 0);
			_originalShowSuperHidden = null;
			NotifyExplorerSettingsChanged();
		}
	}

	private static int ReadExplorerHidden()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced");
			if (key == null)
			{
				return 2;
			}
			return (key.GetValue("Hidden") is int intVal) ? intVal : 2;
		}
		catch
		{
			return 2;
		}
	}

	public static bool SetExplorerHidden(bool show)
	{
		int targetValue = (show ? 1 : 2);
		try
		{
			using (RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced"))
			{
				if (key == null)
				{
					return false;
				}
				key.SetValue("Hidden", targetValue, RegistryValueKind.DWord);
			}
			if (ReadExplorerHidden() == targetValue)
			{
				return true;
			}
			return false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static void RestoreExplorerHidden()
	{
		if (_originalHidden.HasValue)
		{
			SetExplorerHidden(_originalHidden.Value == 1);
			_originalHidden = null;
		}
	}

	private static void NotifyExplorerSettingsChanged(uint timeoutMs = 3000u)
	{
		nint HWND_BROADCAST = new IntPtr(65535);
		nint result;
		uint shellTimeoutMs = Math.Min(timeoutMs, 1000u);
		try
		{
			nint policyPtr = Marshal.StringToHGlobalUni("Policy");
			try
			{
				NativeMethods.SendMessageTimeout(HWND_BROADCAST, 26u, IntPtr.Zero, policyPtr, 2u, timeoutMs, out result);
			}
			finally
			{
				Marshal.FreeHGlobal(policyPtr);
			}
		}
		catch (Exception)
		{
		}
		try
		{
			if (!((DateTime.UtcNow - _serviceStartTime).TotalSeconds < 30.0))
			{
				NativeMethods.SHChangeNotify(134217728u, 4096u, IntPtr.Zero, IntPtr.Zero);
			}
		}
		catch (Exception)
		{
		}
		try
		{
			nint progman = NativeMethods.FindWindow("Progman", null);
			if (progman == IntPtr.Zero)
			{
				return;
			}
			NativeMethods.SendMessageTimeout(progman, 26u, IntPtr.Zero, IntPtr.Zero, 2u, shellTimeoutMs, out result);
			NativeMethods.EnumWindows(delegate(nint hWnd, nint lParam)
			{
				if (NativeMethods.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
				{
					NativeMethods.SendMessageTimeout(hWnd, 26u, IntPtr.Zero, IntPtr.Zero, 2u, shellTimeoutMs, out result);
					return false;
				}
				return true;
			}, IntPtr.Zero);
		}
		catch (Exception)
		{
		}
	}

	private static bool SetAllObjectsViaShell(bool show)
	{
		try
		{
			NativeMethods.SHELLSTATE ss = default(NativeMethods.SHELLSTATE);
			NativeMethods.SHGetSetSettings(ref ss, 1u, bSet: false);
			if ((ss.dwBits1 & 1) != 0 == show)
			{
				return true;
			}
			if (show)
			{
				ss.dwBits1 |= 1u;
			}
			else
			{
				ss.dwBits1 &= 4294967294u;
			}
			NativeMethods.SHGetSetSettings(ref ss, 1u, bSet: true);
			NativeMethods.SHELLSTATE ssVerify = default(NativeMethods.SHELLSTATE);
			NativeMethods.SHGetSetSettings(ref ssVerify, 1u, bSet: false);
			return (ssVerify.dwBits1 & 1) != 0 == show;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static void LoadTempShowHiddenState()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\DesktopTool\\TempShowHidden");
			if (key == null)
			{
				_tempShowActive = false;
				return;
			}
			_tempShowActive = key.GetValue("Active") is int a && a == 1;
			if (key.GetValue("OriginalHidden") is int ohv)
			{
				_tempOriginalHidden = ohv;
			}
			if (key.GetValue("OriginalShowSuperHidden") is int osv)
			{
				_tempOriginalShowSuperHidden = osv;
			}
		}
		catch (Exception)
		{
			_tempShowActive = false;
		}
	}

	private static void SaveTempShowHiddenState()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\DesktopTool\\TempShowHidden");
			if (key != null)
			{
				key.SetValue("Active", _tempShowActive ? 1 : 0, RegistryValueKind.DWord);
				key.SetValue("OriginalHidden", _tempOriginalHidden, RegistryValueKind.DWord);
				key.SetValue("OriginalShowSuperHidden", _tempOriginalShowSuperHidden, RegistryValueKind.DWord);
			}
		}
		catch (Exception)
		{
		}
	}

	public static bool ReconcileTempShowHiddenOnStartup()
	{
		LoadTempShowHiddenState();
		if (_tempShowActive)
		{
			// A previous run ended before temporary visibility could be restored.
			// Restore the exact settings captured by that run instead of leaving
			// hidden items exposed for the whole next session.
			RestoreTempShowHidden();
			_tempShowActive = false;
			SaveTempShowHiddenState();
			NotifyExplorerSettingsChanged(300u);
			return true;
		}

		// Do not overwrite a visibility preference the user configured directly
		// in Explorer when DesktopTool does not own a temporary change.
		return false;
	}

	public static void RestoreTempShowHiddenIfActiveOnExit()
	{
		if (!_tempShowActive)
		{
			return;
		}
		try
		{
			RestoreTempShowHidden();
			_tempShowActive = false;
			SaveTempShowHiddenState();
		}
		catch (Exception)
		{
		}
	}

	public static bool SetTemporaryShowHiddenFiles(bool show)
	{
		lock (_tempShowLock)
		{
			try
			{
				if (show)
				{
					if (!_tempShowActive)
					{
						_tempOriginalHidden = ReadExplorerHidden();
						_tempOriginalShowSuperHidden = ReadShowSuperHidden();
						_tempShowActive = true;
						SaveTempShowHiddenState();
					}
					bool num = SetExplorerHidden(show: true);
					bool regSuperOk = SetExplorerSuperHidden(hide: false);
					if (!num || !regSuperOk)
					{
						RestoreTempShowHidden();
						_tempShowActive = false;
						SaveTempShowHiddenState();
						return false;
					}
					SetAllObjectsViaShell(show: true);
					SetSuperHiddenViaShell(hide: false);
					Thread.Sleep(200);
					NotifyExplorerSettingsChanged(300u);
					return true;
				}
				if (!_tempShowActive)
				{
					return true;
				}
				RestoreTempShowHidden();
				_tempShowActive = false;
				SaveTempShowHiddenState();
				Thread.Sleep(200);
				NotifyExplorerSettingsChanged(300u);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}

	public static bool SetHiddenFilesVisible(bool show)
	{
		lock (_tempShowLock)
		{
			try
			{
				bool hiddenOk = SetExplorerHidden(show);
				bool superHiddenOk = SetExplorerSuperHidden(hide: !show);
				if (!hiddenOk || !superHiddenOk)
				{
					return false;
				}

				SetAllObjectsViaShell(show);
				SetSuperHiddenViaShell(hide: !show);
				_tempShowActive = false;
				SaveTempShowHiddenState();
				Thread.Sleep(100);
				NotifyExplorerSettingsChanged(300u);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}

	private static void RestoreTempShowHidden()
	{
		bool showHidden = _tempOriginalHidden == 1;
		bool hideSuperHidden = _tempOriginalShowSuperHidden == 0;
		SetExplorerHidden(showHidden);
		SetExplorerSuperHidden(hideSuperHidden);
		try
		{
			SetAllObjectsViaShell(showHidden);
			SetSuperHiddenViaShell(hideSuperHidden);
		}
		catch (Exception)
		{
		}
	}

	public static int CountModeMigrationNeeded(IEnumerable<IconZone> zones, string targetMode)
	{
		int count = 0;
		foreach (IconZone zone in zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (icon.IsHidden && icon.HiddenMethod != targetMode)
					{
						count++;
					}
				}
			}
		}
		return count;
	}

	public static ModeSwitchResult BatchSwitchToPhysical(IList<IconZone> zones, Action<string?> reportFile, Action<int, int> reportProgress)
	{
		ModeSwitchResult result = new ModeSwitchResult();
		int total = CountModeMigrationNeeded(zones, "Physical");
		if (total == 0)
		{
			return result;
		}
		int current = 0;
		Directory.CreateDirectory(HiddenDir);
		foreach (IconZone zone in zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons.ToList())
				{
					if (!icon.IsHidden || icon.HiddenMethod == "Physical")
					{
						continue;
					}
					current++;
					string name = icon.DisplayName ?? Path.GetFileName(icon.FilePath);
					reportFile("正在迁移：" + name);
					reportProgress(current, total);
					try
					{
						if (icon.HiddenMethod == "Attributes")
						{
							TryAttributeRestore(icon.FilePath);
						}
						string hiddenPath = MoveToHidden(icon.FilePath);
						if (hiddenPath != null)
						{
							icon.HiddenMethod = "Physical";
							icon.HiddenPath = hiddenPath;
							result.Success++;
							continue;
						}
						if (icon.HiddenMethod == "Attributes")
						{
							TryAttributeHide(icon.FilePath);
						}
						result.Fail++;
						result.Failures.Add(new ModeSwitchFailure
						{
							Name = name,
							Path = icon.FilePath,
							Reason = "物理移动到隐藏目录失败（" + (LastMoveError ?? "未知原因") + "）。文件仍保留在桌面（属性隐藏状态），不会被丢弃。"
						});
					}
					catch (Exception ex)
					{
						result.Fail++;
						result.Failures.Add(new ModeSwitchFailure
						{
							Name = name,
							Path = icon.FilePath,
							Reason = "处理异常：" + ex.GetType().Name + ": " + ex.Message
						});
					}
				}
			}
		}
		RestoreExplorerSuperHidden();
		RestoreExplorerHidden();
		VerifyAndLogIconPaths(zones, "BatchSwitchToPhysical");
		return result;
	}

	public static ModeSwitchResult BatchSwitchToAttribute(IList<IconZone> zones, Action<string?> reportFile, Action<int, int> reportProgress)
	{
		ModeSwitchResult result = new ModeSwitchResult();
		LogMigrationDiag("========== BatchSwitchToAttribute START ==========");
		ConfigureExplorerForAttributeMode();
		int explorerHidden = ReadExplorerHidden();
		int explorerShowSuper = ReadShowSuperHidden();
		LogMigrationDiag($"Explorer settings: Hidden={explorerHidden} (1=显示隐藏文件,2=不显示), ShowSuperHidden={explorerShowSuper} (0=隐藏系统文件,1=显示)");
		int zoneTotal = CountModeMigrationNeeded(zones, "Attributes");
		LogMigrationDiag($"Phase 1: zoneTotal={zoneTotal} icons with IsHidden=true && HiddenMethod!=Attributes");
		int zoneProcessed = 0;
		foreach (IconZone zone in zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons.ToList())
				{
					if (!icon.IsHidden || icon.HiddenMethod == "Attributes")
					{
						continue;
					}
					zoneProcessed++;
					string name = icon.DisplayName ?? Path.GetFileName(icon.FilePath);
					LogMigrationDiag($"[{zoneProcessed}/{zoneTotal}] Processing: \"{name}\" | FilePath={icon.FilePath} | HiddenPath={icon.HiddenPath ?? "NULL"} | HiddenMethod={icon.HiddenMethod ?? "NULL"} | IsHidden={icon.IsHidden}");
					reportFile("正在迁移：" + name);
					reportProgress(zoneProcessed, zoneTotal);
					try
					{
						if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath))
						{
							_ = icon.HiddenPath;
							bool hiddenExists = File.Exists(icon.HiddenPath) || Directory.Exists(icon.HiddenPath);
							LogMigrationDiag($"  Step 1 (Restore): HiddenPath exists={hiddenExists}, checking locks...");
							List<(string, string)> locks = CheckFileLocks(icon.HiddenPath);
							if (locks.Count > 0)
							{
								result.Fail++;
								string lockInfo = string.Join(", ", locks.Select<(string, string), string>(((string FileName, string ProcessName) l) => l.ProcessName));
								result.Failures.Add(new ModeSwitchFailure
								{
									Name = name,
									Path = icon.HiddenPath,
									Reason = "文件被以下程序占用：" + lockInfo + "。无法从隐藏目录恢复，请关闭相关程序后重试切换。"
								});
								LogMigrationDiag("  ❌ FAIL [Locks] \"" + name + "\": 文件被占用: " + lockInfo);
								continue;
							}
							bool hsInHiddenApplied = false;
							if (TryAttributeHide(icon.HiddenPath))
							{
								hsInHiddenApplied = true;
								LogMigrationDiag("  Step 1a (Pre-H+S in hidden dir): ✅ success, now restoring with attributes preserved");
							}
							else
							{
								LogMigrationDiag("  Step 1a (Pre-H+S in hidden dir): ⚠ failed, will retry after restore");
							}
							bool restored = RestoreFromHidden(icon.FilePath, icon.HiddenPath);
							LogMigrationDiag($"  Step 1 (Restore): result={restored}, LastRestoreFailReason={LastRestoreFailReason ?? "N/A"}");
							if (!restored)
							{
								result.Fail++;
								result.Failures.Add(new ModeSwitchFailure
								{
									Name = name,
									Path = icon.HiddenPath,
									Reason = "从隐藏目录恢复到桌面失败：" + (LastRestoreFailReason ?? "未知原因") + "。文件仍保留在隐藏目录。"
								});
								LogMigrationDiag("  ❌ FAIL [Restore] \"" + name + "\": " + (LastRestoreFailReason ?? "未知原因"));
								continue;
							}
							icon.HiddenPath = null;
							if (!hsInHiddenApplied)
							{
								goto IL_05bd;
							}
							uint verify = NativeMethods.GetFileAttributes(icon.FilePath);
							if (verify != uint.MaxValue && (verify & 6) == 6)
							{
								icon.HiddenMethod = "Attributes";
								result.Success++;
								LogMigrationDiag($"  ✅ SUCCESS [{result.Success}] \"{name}\" → Attributes (H+S set in hidden dir, preserved after move)");
								continue;
							}
							LogMigrationDiag($"  ⚠ H+S was NOT preserved after File.Move! attrs={verify}, will retry Step 2");
							goto IL_05bd;
						}
						string skipReason = ((icon.HiddenMethod != "Physical") ? ("HiddenMethod=\"" + icon.HiddenMethod + "\" (not Physical)") : "HiddenPath is null/empty");
						LogMigrationDiag("  Step 1 (Restore): SKIPPED — " + skipReason);
						if (!File.Exists(icon.FilePath) && !Directory.Exists(icon.FilePath))
						{
							LogMigrationDiag("  ⚠ CRITICAL: FilePath does NOT exist! File may be in hidden dir with inconsistent zones data.");
						}
						goto IL_05bd;
						IL_05bd:
						bool fileExistsBeforeHS = File.Exists(icon.FilePath) || Directory.Exists(icon.FilePath);
						LogMigrationDiag($"  Step 2 (H+S): FilePath exists={fileExistsBeforeHS}, attempting TryAttributeHide...");
						bool hsSuccess = false;
						for (int retry = 0; retry < 3; retry++)
						{
							if (TryAttributeHide(icon.FilePath))
							{
								hsSuccess = true;
								break;
							}
							if (retry < 2)
							{
								LogMigrationDiag($"  Step 2 retry {retry + 1}/3: waiting 50ms for Explorer to release handles...");
								Thread.Sleep(50);
							}
						}
						if (hsSuccess)
						{
							icon.HiddenMethod = "Attributes";
							result.Success++;
							LogMigrationDiag($"  ✅ SUCCESS [{result.Success}] \"{name}\" → Attributes");
							continue;
						}
						bool fileExistsAfterHS = File.Exists(icon.FilePath) || Directory.Exists(icon.FilePath);
						LogMigrationDiag($"  ❌ FAIL [H+S] \"{name}\": TryAttributeHide returned false, file still exists at FilePath={fileExistsAfterHS}");
						string hiddenPath = MoveToHidden(icon.FilePath);
						if (hiddenPath != null)
						{
							icon.HiddenMethod = "Physical";
							icon.HiddenPath = hiddenPath;
							LogMigrationDiag("  ↳ Fallback to Physical: " + hiddenPath);
						}
						else
						{
							icon.HiddenMethod = "Attributes";
							icon.HiddenPath = null;
							LogMigrationDiag("  ↳ MoveToHidden ALSO FAILED! 保留 Attributes 追踪，待下次启动重试。LastMoveError=" + (LastMoveError ?? "N/A"));
						}
						result.Fail++;
						result.Failures.Add(new ModeSwitchFailure
						{
							Name = name,
							Path = icon.FilePath,
							Reason = "属性隐藏(H+S)失败且物理收纳也失败（" + (LastMoveError ?? "文件可能被其他程序占用") + "）。该图标暂时保留在桌面可见，将在下次启动本软件时自动重试隐藏；若仍失败，请关闭占用它的程序（如资源管理器预览窗格、杀毒软件、Windows 搜索索引）后重新收纳。"
						});
					}
					catch (Exception ex)
					{
						result.Fail++;
						result.Failures.Add(new ModeSwitchFailure
						{
							Name = name,
							Path = icon.FilePath,
							Reason = "处理异常：" + ex.GetType().Name + ": " + ex.Message
						});
						LogMigrationDiag($"  ❌ EXCEPTION \"{name}\": {ex.GetType().Name}: {ex.Message}");
					}
				}
			}
		}
		List<(string, string, bool)> remainingEntries = (from e in GetManifestEntries()
			where File.Exists(e.HiddenPath) || Directory.Exists(e.HiddenPath)
			select e).ToList();
		if (remainingEntries.Count > 0)
		{
			LogMigrationDiag($"Phase 2: Manifest cleanup, {remainingEntries.Count} orphan entries");
			int phase2Current = 0;
			foreach (var entry in remainingEntries)
			{
				phase2Current++;
				string name2 = Path.GetFileName(entry.Item2);
				LogMigrationDiag($"  [Phase2 {phase2Current}/{remainingEntries.Count}] Manifest: \"{name2}\" | HiddenPath={entry.Item1} | OriginalPath={entry.Item2}");
				reportFile("正在迁移：" + name2);
				reportProgress(zoneProcessed + phase2Current, zoneTotal + remainingEntries.Count);
				try
				{
					List<(string, string)> locks2 = CheckFileLocks(entry.Item1);
					if (locks2.Count > 0)
					{
						result.Fail++;
						string lockInfo2 = string.Join(", ", locks2.Select<(string, string), string>(((string FileName, string ProcessName) l) => l.ProcessName));
						result.Failures.Add(new ModeSwitchFailure
						{
							Name = name2,
							Path = entry.Item1,
							Reason = "文件被以下程序占用：" + lockInfo2 + "。无法从隐藏目录恢复，请关闭相关程序后重试切换。"
						});
						LogMigrationDiag("  ❌ FAIL [Locks] Manifest:" + name2 + ": " + lockInfo2);
						continue;
					}
					if (!RestoreFromHidden(entry.Item2, entry.Item1))
					{
						result.Fail++;
						result.Failures.Add(new ModeSwitchFailure
						{
							Name = name2,
							Path = entry.Item1,
							Reason = "从隐藏目录恢复到桌面失败：" + (LastRestoreFailReason ?? "未知原因") + "。"
						});
						LogMigrationDiag("  ❌ FAIL [Restore] Manifest:" + name2 + ": " + (LastRestoreFailReason ?? "未知"));
						continue;
					}
					string resolvedPath = ResolveRestoredPath(entry.Item2);
					if (TryAttributeHide(resolvedPath))
					{
						SyncZoneIconFromManifest(zones, entry.Item2, "Attributes", null);
						result.Success++;
						LogMigrationDiag("  ✅ SUCCESS Manifest:" + name2);
						continue;
					}
					string hiddenPath2 = MoveToHidden(resolvedPath);
					if (hiddenPath2 != null)
					{
						SyncZoneIconFromManifest(zones, entry.Item2, "Physical", hiddenPath2);
					}
					result.Fail++;
					result.Failures.Add(new ModeSwitchFailure
					{
						Name = name2,
						Path = resolvedPath,
						Reason = "属性隐藏(H+S)失败且物理收纳也失败（" + (LastMoveError ?? "文件可能被占用") + "）。该文件已恢复到桌面可见，请在关闭占用程序后重新收纳。"
					});
					LogMigrationDiag("  ❌ FAIL [H+S] Manifest:" + name2);
				}
				catch (Exception ex2)
				{
					result.Fail++;
					result.Failures.Add(new ModeSwitchFailure
					{
						Name = name2,
						Path = entry.Item1,
						Reason = "处理异常：" + ex2.GetType().Name + ": " + ex2.Message
					});
					LogMigrationDiag($"  ❌ EXCEPTION Manifest:{name2}: {ex2.GetType().Name}: {ex2.Message}");
				}
			}
		}
		else if (zoneTotal == 0 && zoneProcessed == 0)
		{
			LogMigrationDiag("No icons to migrate (zoneTotal=0, no orphan entries)");
		}
		if (Directory.Exists(HiddenDir))
		{
			HashSet<string> manifestHiddenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var entry2 in GetManifestEntries())
			{
				try
				{
					manifestHiddenPaths.Add(Path.GetFullPath(entry2.HiddenPath));
				}
				catch
				{
				}
			}
			string[] hiddenFiles;
			try
			{
				hiddenFiles = Directory.GetFiles(HiddenDir);
			}
			catch
			{
				hiddenFiles = Array.Empty<string>();
			}
			List<string> phase3Orphans = hiddenFiles.Where((string f) => !manifestHiddenPaths.Contains(Path.GetFullPath(f))).ToList();
			if (phase3Orphans.Count > 0)
			{
				LogMigrationDiag($"Phase 3: Direct hidden-dir scan, {phase3Orphans.Count} untracked orphans (not in manifest)");
				int phase3Current = 0;
				string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
				foreach (string hiddenFilePath in phase3Orphans)
				{
					phase3Current++;
					string name3 = Path.GetFileName(hiddenFilePath);
					string inferredDesktopPath = Path.Combine(desktopDir, name3);
					LogMigrationDiag($"  [Phase3 {phase3Current}/{phase3Orphans.Count}] Untracked: \"{name3}\" | Hidden={hiddenFilePath} | InferredDest={inferredDesktopPath}");
					reportFile("正在迁移：" + name3);
					reportProgress(zoneProcessed + phase3Current, zoneTotal + phase3Orphans.Count);
					try
					{
						bool isDir = Directory.Exists(hiddenFilePath);
						AddManifestEntry(hiddenFilePath, inferredDesktopPath, isDir);
						if (!RestoreFromHidden(inferredDesktopPath, hiddenFilePath))
						{
							result.Fail++;
							result.Failures.Add(new ModeSwitchFailure
							{
								Name = name3,
								Path = hiddenFilePath,
								Reason = "从隐藏目录恢复到桌面失败：" + (LastRestoreFailReason ?? "未知原因") + "。"
							});
							LogMigrationDiag("  ❌ FAIL [Restore] Phase3:" + name3 + ": " + (LastRestoreFailReason ?? "未知"));
							continue;
						}
						string resolvedPath2 = ResolveRestoredPath(inferredDesktopPath);
						bool hsSuccess2 = false;
						for (int retry2 = 0; retry2 < 3; retry2++)
						{
							if (TryAttributeHide(resolvedPath2))
							{
								hsSuccess2 = true;
								break;
							}
							if (retry2 < 2)
							{
								LogMigrationDiag($"  Phase3 retry {retry2 + 1}/3: waiting 50ms...");
								Thread.Sleep(50);
							}
						}
						if (hsSuccess2)
						{
							SyncZoneIconFromManifest(zones, inferredDesktopPath, "Attributes", null);
							result.Success++;
							LogMigrationDiag("  ✅ SUCCESS Phase3:" + name3 + " → Attributes");
							continue;
						}
						string rehiddenPath = MoveToHidden(resolvedPath2);
						if (rehiddenPath != null)
						{
							SyncZoneIconFromManifest(zones, inferredDesktopPath, "Physical", rehiddenPath);
							LogMigrationDiag("  ↳ Phase3 fallback to Physical: " + rehiddenPath);
						}
						else
						{
							LogMigrationDiag("  ↳ Phase3 MoveToHidden fallback ALSO FAILED! LastMoveError=" + (LastMoveError ?? "N/A"));
						}
						result.Fail++;
						result.Failures.Add(new ModeSwitchFailure
						{
							Name = name3,
							Path = resolvedPath2,
							Reason = "属性隐藏(H+S)失败且物理收纳也失败（" + (LastMoveError ?? "文件可能被占用") + "）。该文件已恢复到桌面可见，请在关闭占用程序后重新收纳。"
						});
					}
					catch (Exception ex3)
					{
						result.Fail++;
						result.Failures.Add(new ModeSwitchFailure
						{
							Name = name3,
							Path = hiddenFilePath,
							Reason = "处理异常：" + ex3.GetType().Name + ": " + ex3.Message
						});
						LogMigrationDiag($"  ❌ EXCEPTION Phase3:{name3}: {ex3.GetType().Name}: {ex3.Message}");
					}
				}
			}
		}
		LogMigrationDiag($"========== BatchSwitchToAttribute END: success={result.Success}, fail={result.Fail}, total={result.Success + result.Fail} ==========");
		VerifyAndLogIconPaths(zones, "BatchSwitchToAttribute");
		return result;
	}

	private static string ResolveRestoredPath(string originalPath)
	{
		if (File.Exists(originalPath) || Directory.Exists(originalPath))
		{
			return originalPath;
		}
		string parentDir = Path.GetDirectoryName(originalPath) ?? "";
		string baseName = Path.GetFileNameWithoutExtension(originalPath);
		string ext = Path.GetExtension(originalPath);
		List<string> candidates = (Directory.Exists(parentDir) ? (from p in Directory.GetFiles(parentDir).Concat(Directory.GetDirectories(parentDir))
			where Path.GetFileNameWithoutExtension(p).StartsWith(baseName, StringComparison.OrdinalIgnoreCase) && Path.GetExtension(p).Equals(ext, StringComparison.OrdinalIgnoreCase)
			orderby File.GetLastWriteTime(p) descending
			select p).ToList() : new List<string>());
		if (candidates.Count <= 0)
		{
			return originalPath;
		}
		return candidates[0];
	}

	public static int ReleaseUntrackedHiddenFiles(Action<string>? logAction = null)
	{
		string hiddenDir = HiddenDir;
		if (!Directory.Exists(hiddenDir))
		{
			return 0;
		}
		HashSet<string> manifestTracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in GetManifestEntries())
		{
			try
			{
				manifestTracked.Add(Path.GetFullPath(entry.HiddenPath));
			}
			catch
			{
			}
		}
		string[] allEntries;
		try
		{
			allEntries = Directory.GetFileSystemEntries(hiddenDir);
		}
		catch
		{
			return 0;
		}
		List<string> untracked = allEntries.Where((string e) => !manifestTracked.Contains(Path.GetFullPath(e))).ToList();
		if (untracked.Count == 0)
		{
			return 0;
		}
		logAction?.Invoke($"[ReleaseUntracked] 发现 {untracked.Count} 个无收纳记录的文件滞留在 hidden 目录，正在释放到桌面…");
		int released = 0;
		string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
		foreach (string hiddenPath in untracked)
		{
			string name = Path.GetFileName(hiddenPath);
			string destPath = Path.Combine(desktopDir, name);
			try
			{
				if (Directory.Exists(hiddenPath))
				{
					RemoveHiddenSystemAttrRecursive(hiddenPath);
					if (Directory.Exists(destPath))
					{
						if (SafeDeleteDirectory(hiddenPath))
						{
							LogMigrationDiag("  ReleaseUntrackedHiddenFiles: ✅ orphan dir deleted: [" + name + "]");
							released++;
						}
						else
						{
							LogMigrationDiag("  ReleaseUntrackedHiddenFiles: ⚠ cannot delete orphan dir [" + name + "], keeping it in hidden");
						}
						continue;
					}
					try
					{
						MoveDirectoryCrossVolume(hiddenPath, destPath);
						if (Directory.Exists(destPath))
						{
							released++;
						}
					}
					catch (Exception)
					{
					}
					continue;
				}
				RemoveHiddenSystemAttr(hiddenPath);
				if (File.Exists(destPath))
				{
					string nameNoExt = Path.GetFileNameWithoutExtension(name);
					string ext = Path.GetExtension(name);
					int n = 1;
					do
					{
						destPath = Path.Combine(desktopDir, $"{nameNoExt} ({n}){ext}");
						n++;
					}
					while (File.Exists(destPath));
				}
				MoveFileCrossVolume(hiddenPath, destPath);
				if (File.Exists(destPath))
				{
					released++;
				}
				else
				{
					logAction?.Invoke("[ReleaseUntracked] 移动失败（目标不存在）: " + name);
				}
			}
			catch (Exception ex2)
			{
				logAction?.Invoke($"[ReleaseUntracked] 异常: {name} — {ex2.GetType().Name}: {ex2.Message}");
			}
		}
		if (released > 0)
		{
			logAction?.Invoke($"[ReleaseUntracked] 完成：{released}/{untracked.Count} 个文件已释放到桌面");
		}
		return released;
	}
}
