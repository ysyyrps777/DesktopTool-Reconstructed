using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopTool.Models;

namespace DesktopTool.Services;

public class DataService
{
	private class WindowStateData
	{
		public double Left { get; set; } = 1200.0;

		public double Top { get; set; } = 100.0;

		public double Width { get; set; } = 320.0;

		public double Height { get; set; } = 400.0;

		public int PosSchema { get; set; }

		public bool TodoWindowOpen { get; set; }

		public bool TodoCollapsed { get; set; }

		public bool TodoLocked { get; set; }

		public bool TodoColorFilterCollapsed { get; set; }

		public bool TodoFilterAreaCollapsed { get; set; }
	}

	public class BackupInfo
	{
		public string Id { get; set; } = "";

		public DateTime CreatedAt { get; set; }

		public string Name => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

		public int ItemCount { get; set; }

		public string AppVersion { get; set; } = "";

		public int ZoneCount { get; set; }

		public int TodoCount { get; set; }

		public int MappingCount { get; set; }

		public int IconCount { get; set; }

		public int RecurringCount { get; set; }

		public bool HasSettings { get; set; }

		public string Details { get; set; } = "";
	}

	private static readonly string AppDataDir = App.GetAppDataDir();

	private static readonly string BackupsDir = Path.Combine(AppDataDir, "backups");

	private static readonly string ZonesFile = Path.Combine(AppDataDir, "zones.json");

	private static readonly string TodosFile = Path.Combine(AppDataDir, "todos.json");

	private static readonly string WindowStateFile = Path.Combine(AppDataDir, "window_state.json");

	private static readonly string SettingsFile = Path.Combine(AppDataDir, "settings.json");

	private static readonly string DataVersionFile = Path.Combine(AppDataDir, "data_version.json");

	private static readonly string RecurringTasksFile = Path.Combine(AppDataDir, "recurring_tasks.json");

	private const int CurrentDataVersion = 1;

	private const int MaxBackupFiles = 200;

	private const int MaxBackupAgeDays = 15;

	private const int BackupCleanupIntervalMs = 3600000;

	private static readonly object _backupCleanupLock = new object();

	private static Timer? _backupCleanupTimer;

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private static readonly string MappingsFile = Path.Combine(AppDataDir, "mappings.json");

	private static readonly object _atomicWriteLock = new object();

	private static readonly string[] BackupDataFiles = new string[7] { "zones.json", "todos.json", "mappings.json", "recurring_tasks.json", "settings.json", "data_version.json", "window_state.json" };

	private static readonly string[] WindowStateTodoKeys = new string[5] { "TodoWindowOpen", "TodoCollapsed", "TodoLocked", "TodoColorFilterCollapsed", "TodoFilterAreaCollapsed" };

	private static string ManualBackupDir => Path.Combine(AppDataDir, "backups", "manual");

	public List<RecurringTask> LoadRecurringTasks()
	{
		try
		{
			if (!File.Exists(RecurringTasksFile))
			{
				return new List<RecurringTask>();
			}
			return JsonSerializer.Deserialize<List<RecurringTask>>(File.ReadAllText(RecurringTasksFile), JsonOptions) ?? new List<RecurringTask>();
		}
		catch (Exception ex)
		{
			LogError("LoadRecurringTasks", ex);
			return new List<RecurringTask>();
		}
	}

	public void SaveRecurringTasks(List<RecurringTask> tasks)
	{
		try
		{
			string json = JsonSerializer.Serialize(tasks, JsonOptions);
			File.WriteAllText(RecurringTasksFile, json);
		}
		catch (Exception ex)
		{
			LogError("SaveRecurringTasks", ex);
		}
	}

	public static void InitBackupMaintenance()
	{
		try
		{
			Task.Run(delegate
			{
				CleanupBackups();
			});
			_backupCleanupTimer = new Timer(delegate
			{
				CleanupBackups();
			}, null, 3600000, 3600000);
		}
		catch
		{
		}
	}

	public static void CleanupBackups()
	{
		try
		{
			if (!Monitor.TryEnter(_backupCleanupLock))
			{
				return;
			}
			try
			{
				EnforceBackupCapAndAge();
			}
			finally
			{
				Monitor.Exit(_backupCleanupLock);
			}
		}
		catch
		{
		}
	}

	private static void EnforceBackupCapAndAge()
	{
		try
		{
			if (!Directory.Exists(BackupsDir))
			{
				return;
			}
			List<FileInfo> files = new DirectoryInfo(BackupsDir).EnumerateFiles("*.bak").ToList();
			if (files.Count == 0)
			{
				return;
			}
			DateTime cutoff = DateTime.UtcNow.AddDays(-15.0);
			int expiredDeleted = 0;
			files.RemoveAll(delegate(FileInfo f)
			{
				if (f.LastWriteTimeUtc < cutoff)
				{
					try
					{
						f.Delete();
						expiredDeleted++;
					}
					catch
					{
					}
					return true;
				}
				return false;
			});
			if (files.Count > 200)
			{
				files.Sort((FileInfo a, FileInfo b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
				int toDelete = files.Count - 200;
				for (int i = 0; i < toDelete; i++)
				{
					try
					{
						files[i].Delete();
					}
					catch
					{
					}
				}
				DebugLog($"[DataService] backups cleanup: kept {200}, deleted {toDelete} oldest + {expiredDeleted} expired");
			}
			else if (expiredDeleted > 0)
			{
				DebugLog($"[DataService] backups cleanup: deleted {expiredDeleted} expired backups, count now {files.Count}");
			}
		}
		catch
		{
		}
	}

	public DataService()
	{
		try
		{
			Directory.CreateDirectory(AppDataDir);
			Directory.CreateDirectory(BackupsDir);
			int version = LoadDataVersion();
			if (version < 1)
			{
				BackupAllData(version);
				MigrateData(version, 1);
				SaveDataVersion(1);
			}
		}
		catch (Exception value)
		{
			try
			{
				LogHelper.Append(Path.Combine(Path.GetTempPath(), "DesktopTool_Crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] DataService初始化异常: {value}\n");
			}
			catch
			{
			}
		}
	}

	private int LoadDataVersion()
	{
		try
		{
			if (File.Exists(DataVersionFile))
			{
				return JsonDocument.Parse(File.ReadAllText(DataVersionFile)).RootElement.GetProperty("version").GetInt32();
			}
		}
		catch
		{
		}
		return 0;
	}

	private void SaveDataVersion(int version)
	{
		try
		{
			string content = JsonSerializer.Serialize(new { version }, JsonOptions);
			AtomicWrite(DataVersionFile, content);
		}
		catch
		{
		}
	}

	public void BackupAllData(int oldVersion)
	{
		try
		{
			string suffix = $"_v{oldVersion}_{DateTime.Now:yyyyMMdd_HHmmss}.json.bak";
			if (File.Exists(ZonesFile))
			{
				File.Copy(ZonesFile, Path.Combine(BackupsDir, "zones" + suffix), overwrite: true);
			}
			if (File.Exists(MappingsFile))
			{
				File.Copy(MappingsFile, Path.Combine(BackupsDir, "mappings" + suffix), overwrite: true);
			}
			if (File.Exists(TodosFile))
			{
				File.Copy(TodosFile, Path.Combine(BackupsDir, "todos" + suffix), overwrite: true);
			}
			if (File.Exists(SettingsFile))
			{
				File.Copy(SettingsFile, Path.Combine(BackupsDir, "settings" + suffix), overwrite: true);
			}
			if (File.Exists(WindowStateFile))
			{
				File.Copy(WindowStateFile, Path.Combine(BackupsDir, "window_state" + suffix), overwrite: true);
			}
		}
		catch
		{
		}
	}

	private static void MigrateData(int fromVersion, int toVersion)
	{
	}

	private static void RestoreFromBakIfMissing(string filePath)
	{
		if (File.Exists(filePath))
		{
			return;
		}
		string bak = filePath + ".bak";
		if (!File.Exists(bak))
		{
			return;
		}
		try
		{
			File.Copy(bak, filePath, overwrite: true);
			DebugLog("[DataService] 主文件缺失，已从 .bak 恢复: " + Path.GetFileName(filePath));
		}
		catch (Exception ex)
		{
			DebugLog("[DataService] 从 .bak 恢复失败 " + Path.GetFileName(filePath) + ": " + ex.Message);
		}
	}

	public List<IconZone> LoadZones()
	{
		try
		{
			RestoreFromBakIfMissing(ZonesFile);
			if (File.Exists(ZonesFile))
			{
				List<IconZone> zones = JsonSerializer.Deserialize<List<IconZone>>(File.ReadAllText(ZonesFile), JsonOptions) ?? new List<IconZone>();
				foreach (IconZone zone in zones)
				{
					if (zone.Groups.Count == 0)
					{
						zone.Groups.Add(new IconGroup
						{
							Name = "默认分组",
							Icons = ((zone.Icons.Count > 0) ? zone.Icons : new List<ZoneIcon>())
						});
						zone.Icons = new List<ZoneIcon>();
					}
					if (zone.ActiveGroupIndex < 0 || zone.ActiveGroupIndex >= zone.Groups.Count)
					{
						zone.ActiveGroupIndex = 0;
					}
				}
				if (zones.Count > 0)
				{
					DebugLog($"LoadZones: {zones.Count} 个收纳区 — Zone[0] pos=({zones[0].X:F0},{zones[0].Y:F0}) size={zones[0].Width:F0}x{zones[0].Height:F0}");
				}
				return zones;
			}
		}
		catch
		{
			TryBackupCorruptedFile(ZonesFile);
		}
		return new List<IconZone>();
	}

	public void SaveZones(List<IconZone> zones)
	{
		try
		{
			string c = JsonSerializer.Serialize(zones, JsonOptions);
			AtomicWrite(ZonesFile, c);
			if (zones.Count > 0)
			{
				DebugLog($"SaveZones: {zones.Count} 个收纳区 — Zone[0] pos=({zones[0].X:F0},{zones[0].Y:F0}) size={zones[0].Width:F0}x{zones[0].Height:F0}");
			}
		}
		catch (Exception ex)
		{
			LogError("SaveZones", ex);
		}
	}

	public List<MappingZone> LoadMappings()
	{
		try
		{
			RestoreFromBakIfMissing(MappingsFile);
			if (File.Exists(MappingsFile))
			{
				return JsonSerializer.Deserialize<List<MappingZone>>(File.ReadAllText(MappingsFile), JsonOptions) ?? new List<MappingZone>();
			}
		}
		catch
		{
			TryBackupCorruptedFile(MappingsFile);
		}
		return new List<MappingZone>();
	}

	public void SaveMappings(List<MappingZone> mappings)
	{
		try
		{
			string c = JsonSerializer.Serialize(mappings, JsonOptions);
			AtomicWrite(MappingsFile, c);
		}
		catch (Exception ex)
		{
			LogError("SaveMappings", ex);
		}
	}

	private static void LogError(string method, Exception ex)
	{
		LogHelper.Append(Path.Combine(AppDataDir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {method} failed: {ex.Message}\n{ex.StackTrace}\n");
	}

	private static void DebugLog(string message)
	{
		try
		{
			LogHelper.Append(Path.Combine(AppDataDir, "window_state_debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	public List<TodoItem> LoadTodos()
	{
		try
		{
			RestoreFromBakIfMissing(TodosFile);
			if (File.Exists(TodosFile))
			{
				return JsonSerializer.Deserialize<List<TodoItem>>(File.ReadAllText(TodosFile), JsonOptions) ?? new List<TodoItem>();
			}
		}
		catch
		{
			TryBackupCorruptedFile(TodosFile);
		}
		return new List<TodoItem>();
	}

	public void SaveTodos(List<TodoItem> todos)
	{
		try
		{
			string c = JsonSerializer.Serialize(todos, JsonOptions);
			AtomicWrite(TodosFile, c);
		}
		catch (Exception ex)
		{
			LogError("SaveTodos", ex);
		}
	}

	public AppSettings LoadSettings()
	{
		try
		{
			RestoreFromBakIfMissing(SettingsFile);
			if (File.Exists(SettingsFile))
			{
				return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOptions) ?? new AppSettings();
			}
		}
		catch
		{
			TryBackupCorruptedFile(SettingsFile);
		}
		return new AppSettings();
	}

	public static AppSettings? LoadSettingsStatic()
	{
		try
		{
			if (File.Exists(SettingsFile))
			{
				return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOptions);
			}
		}
		catch
		{
		}
		return null;
	}

	public void SaveSettings(AppSettings settings)
	{
		if (App.IsRestoringBackup)
		{
			return;
		}
		try
		{
			string c = JsonSerializer.Serialize(settings, JsonOptions);
			AtomicWrite(SettingsFile, c);
		}
		catch (Exception ex)
		{
			LogError("SaveSettings", ex);
		}
	}

	public void SaveTodoWindowState(double left, double top, double width, double height)
	{
		try
		{
			WindowStateData existing = LoadWindowStateData();
			WindowStateData data = new WindowStateData
			{
				Left = WindowGeometryPersistence.ToPhysical(left),
				Top = WindowGeometryPersistence.ToPhysical(top),
				Width = WindowGeometryPersistence.ToPhysical(width),
				Height = WindowGeometryPersistence.ToPhysical(height),
				PosSchema = 1,
				TodoWindowOpen = (existing?.TodoWindowOpen ?? false),
				TodoCollapsed = (existing?.TodoCollapsed ?? false),
				TodoLocked = (existing?.TodoLocked ?? false),
				TodoColorFilterCollapsed = (existing?.TodoColorFilterCollapsed ?? false),
				TodoFilterAreaCollapsed = (existing?.TodoFilterAreaCollapsed ?? false)
			};
			DebugLog($"SaveTodoWindowState: Left={left:F0}(phys {data.Left:F0}) Top={top:F0}(phys {data.Top:F0}) Size={width:F0}x{height:F0}(phys {data.Width:F0}x{data.Height:F0}) PosSchema={data.PosSchema} TodoWindowOpen={data.TodoWindowOpen} TodoCollapsed={data.TodoCollapsed}");
			AtomicWrite(WindowStateFile, JsonSerializer.Serialize(data, JsonOptions));
			WindowStateData verify = LoadWindowStateData();
			if (verify != null)
			{
				DebugLog($"SaveTodoWindowState 写入验证: Left={verify.Left:F0} Top={verify.Top:F0} Size={verify.Width:F0}x{verify.Height:F0} TodoWindowOpen={verify.TodoWindowOpen}");
			}
		}
		catch
		{
		}
	}

	public (double Left, double Top, double Width, double Height)? LoadTodoWindowState()
	{
		WindowStateData data = LoadWindowStateData();
		string desc = ((data == null) ? "null" : $"Left={data.Left:F0}, Top={data.Top:F0}, Size={data.Width:F0}x{data.Height:F0}, Open={data.TodoWindowOpen}");
		DebugLog("LoadTodoWindowState: data=[" + desc + "]");
		if (data == null)
		{
			return null;
		}
		if (data.PosSchema == 1)
		{
			return (WindowGeometryPersistence.ToDip(data.Left), WindowGeometryPersistence.ToDip(data.Top), WindowGeometryPersistence.ToDip(data.Width), WindowGeometryPersistence.ToDip(data.Height));
		}
		return (data.Left, data.Top, data.Width, data.Height);
	}

	public void SaveTodoWindowOpen(bool isOpen)
	{
		if (App.IsRestoringBackup)
		{
			return;
		}
		try
		{
			WindowStateData data = LoadWindowStateData() ?? new WindowStateData();
			data.TodoWindowOpen = isOpen;
			string json = JsonSerializer.Serialize(data, JsonOptions);
			AtomicWrite(WindowStateFile, json);
			DebugLog($"SaveTodoWindowOpen: isOpen={isOpen} at {(from s in Environment.StackTrace.Split('\n').Skip(1).Take(3)
				select s.Trim()).DefaultIfEmpty("").Aggregate((string a, string b) => a + " <- " + b)}");
			if (!isOpen)
			{
				WindowStateData verify = LoadWindowStateData();
				if (verify != null && verify.TodoWindowOpen)
				{
					LogError("SaveTodoWindowOpen", new InvalidOperationException($"写入验证失败：写入 TodoWindowOpen=false 但读回为 {verify.TodoWindowOpen}"));
					data.TodoWindowOpen = false;
					AtomicWrite(WindowStateFile, JsonSerializer.Serialize(data, JsonOptions));
					WindowStateData retryVerify = LoadWindowStateData();
					DebugLog("SaveTodoWindowOpen 重试: TodoWindowOpen=" + ((retryVerify != null) ? retryVerify.TodoWindowOpen.ToString() : "null"));
				}
			}
		}
		catch (Exception ex)
		{
			LogError("SaveTodoWindowOpen", ex);
		}
	}

	public bool WasTodoWindowOpen()
	{
		WindowStateData data = LoadWindowStateData();
		bool result = data?.TodoWindowOpen ?? false;
		DebugLog($"WasTodoWindowOpen: result={result} TodoCollapsed={data?.TodoCollapsed} TodoLocked={data?.TodoLocked}");
		return result;
	}

	public void SaveTodoCollapsed(bool collapsed)
	{
		try
		{
			WindowStateData data = LoadWindowStateData() ?? new WindowStateData();
			data.TodoCollapsed = collapsed;
			AtomicWrite(WindowStateFile, JsonSerializer.Serialize(data, JsonOptions));
		}
		catch (Exception ex)
		{
			LogError("SaveTodoCollapsed", ex);
		}
	}

	public bool LoadTodoCollapsed()
	{
		return LoadWindowStateData()?.TodoCollapsed ?? false;
	}

	public void SaveTodoLocked(bool locked)
	{
		try
		{
			WindowStateData data = LoadWindowStateData() ?? new WindowStateData();
			data.TodoLocked = locked;
			AtomicWrite(WindowStateFile, JsonSerializer.Serialize(data, JsonOptions));
		}
		catch (Exception ex)
		{
			LogError("SaveTodoLocked", ex);
		}
	}

	public bool LoadTodoLocked()
	{
		return LoadWindowStateData()?.TodoLocked ?? false;
	}

	public void SaveTodoColorFilterCollapsed(bool collapsed)
	{
		try
		{
			WindowStateData data = LoadWindowStateData() ?? new WindowStateData();
			data.TodoColorFilterCollapsed = collapsed;
			AtomicWrite(WindowStateFile, JsonSerializer.Serialize(data, JsonOptions));
		}
		catch (Exception ex)
		{
			LogError("SaveTodoColorFilterCollapsed", ex);
		}
	}

	public bool LoadTodoColorFilterCollapsed()
	{
		return LoadWindowStateData()?.TodoColorFilterCollapsed ?? false;
	}

	public void SaveTodoFilterAreaCollapsed(bool collapsed)
	{
		try
		{
			WindowStateData data = LoadWindowStateData() ?? new WindowStateData();
			data.TodoFilterAreaCollapsed = collapsed;
			AtomicWrite(WindowStateFile, JsonSerializer.Serialize(data, JsonOptions));
		}
		catch (Exception ex)
		{
			LogError("SaveTodoFilterAreaCollapsed", ex);
		}
	}

	public bool LoadTodoFilterAreaCollapsed()
	{
		return LoadWindowStateData()?.TodoFilterAreaCollapsed ?? false;
	}

	private WindowStateData? LoadWindowStateData()
	{
		try
		{
			if (File.Exists(WindowStateFile))
			{
				return JsonSerializer.Deserialize<WindowStateData>(File.ReadAllText(WindowStateFile), JsonOptions);
			}
		}
		catch
		{
		}
		return null;
	}

	private static void AtomicWrite(string filePath, string content)
	{
		lock (_atomicWriteLock)
		{
			string dir = Path.GetDirectoryName(filePath);
			if (dir != null)
			{
				Directory.CreateDirectory(dir);
			}
			if (File.Exists(filePath))
			{
				string bak = filePath + ".bak";
				try
				{
					File.Copy(filePath, bak, overwrite: true);
				}
				catch
				{
				}
			}
			string text = filePath + ".tmp." + Guid.NewGuid().ToString("N").Substring(0, 8);
			File.WriteAllText(text, content);
			File.Move(text, filePath, overwrite: true);
		}
	}

	private static void TryBackupCorruptedFile(string filePath)
	{
		try
		{
			if (File.Exists(filePath))
			{
				string name = Path.GetFileNameWithoutExtension(filePath);
				string ext = Path.GetExtension(filePath);
				string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
				File.Copy(filePath, Path.Combine(BackupsDir, $"{name}_corrupted_{ts}{ext}.bak"), overwrite: true);
			}
		}
		catch
		{
		}
	}

	private static int CountJsonArray(string path)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
			if (doc.RootElement.ValueKind == JsonValueKind.Array)
			{
				return doc.RootElement.GetArrayLength();
			}
		}
		catch
		{
		}
		return 0;
	}

	private static (int zones, int icons) CountZones(string path)
	{
		int zones = 0;
		int icons = 0;
		try
		{
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
			if (doc.RootElement.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement z in doc.RootElement.EnumerateArray())
				{
					zones++;
					if (!z.TryGetProperty("Groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
					{
						continue;
					}
					foreach (JsonElement item in groups.EnumerateArray())
					{
						if (item.TryGetProperty("Icons", out var ic) && ic.ValueKind == JsonValueKind.Array)
						{
							icons += ic.GetArrayLength();
						}
					}
				}
			}
		}
		catch
		{
		}
		return (zones: zones, icons: icons);
	}

	private static Dictionary<string, JsonElement> ReadJsonObject(string path)
	{
		Dictionary<string, JsonElement> dict = new Dictionary<string, JsonElement>();
		try
		{
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
			if (doc.RootElement.ValueKind == JsonValueKind.Object)
			{
				foreach (JsonProperty p in doc.RootElement.EnumerateObject())
				{
					dict[p.Name] = p.Value.Clone();
				}
			}
		}
		catch
		{
		}
		return dict;
	}

	private static void RestoreWindowStatePartial(string backupFile)
	{
		try
		{
			Dictionary<string, JsonElement> current = ReadJsonObject(WindowStateFile);
			Dictionary<string, JsonElement> backup = ReadJsonObject(backupFile);
			Dictionary<string, JsonElement> curByLower = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<string, JsonElement> kv in current)
			{
				curByLower[kv.Key.ToLowerInvariant()] = kv.Value;
			}
			Dictionary<string, JsonElement> backupByLower = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<string, JsonElement> kv2 in backup)
			{
				backupByLower[kv2.Key.ToLowerInvariant()] = kv2.Value;
			}
			string[] windowStateTodoKeys = WindowStateTodoKeys;
			for (int i = 0; i < windowStateTodoKeys.Length; i++)
			{
				string lower = windowStateTodoKeys[i].ToLowerInvariant();
				if (backupByLower.TryGetValue(lower, out var v))
				{
					curByLower[lower] = v;
				}
			}
			Dictionary<string, JsonElement> result = new Dictionary<string, JsonElement>();
			foreach (KeyValuePair<string, JsonElement> kv3 in current)
			{
				string lower2 = kv3.Key.ToLowerInvariant();
				result[kv3.Key] = (curByLower.TryGetValue(lower2, out var merged) ? merged : kv3.Value);
			}
			File.WriteAllText(WindowStateFile, JsonSerializer.Serialize(result, JsonOptions));
		}
		catch (Exception ex)
		{
			DebugLog("RestoreWindowStatePartial 失败: " + ex.Message);
		}
	}

	private static string FormatDisplayVersion(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return raw;
		}
		string s = raw.Trim();
		if (s.StartsWith("V", StringComparison.OrdinalIgnoreCase))
		{
			return s;
		}
		if (!Version.TryParse(s, out Version v))
		{
			return s;
		}
		return $"V{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
	}

	public static List<BackupInfo> GetManualBackups()
	{
		List<BackupInfo> list = new List<BackupInfo>();
		try
		{
			string dir = ManualBackupDir;
			if (!Directory.Exists(dir))
			{
				return list;
			}
			string[] directories = Directory.GetDirectories(dir);
			foreach (string folder in directories)
			{
				string id = Path.GetFileName(folder);
				if (!DateTime.TryParseExact(id, "yyyyMMdd_HHmmss_fff", null, DateTimeStyles.None, out var dt))
				{
					continue;
				}
				int count = 0;
				string ver = "";
				try
				{
					count = Directory.GetFiles(folder).Count((string f) => !Path.GetFileName(f).Equals("backup_meta.json", StringComparison.OrdinalIgnoreCase));
				}
				catch
				{
				}
				string metaPath = Path.Combine(folder, "backup_meta.json");
				if (File.Exists(metaPath))
				{
					try
					{
						Dictionary<string, JsonElement> meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(metaPath));
						if (meta != null && meta.TryGetValue("AppVersion", out var v))
						{
							ver = FormatDisplayVersion(v.GetString() ?? "");
						}
					}
					catch
					{
					}
				}
				(int zones, int icons) tuple = CountZones(Path.Combine(folder, "zones.json"));
				int zones = tuple.zones;
				int icons = tuple.icons;
				int todos = CountJsonArray(Path.Combine(folder, "todos.json"));
				int mappings = CountJsonArray(Path.Combine(folder, "mappings.json"));
				int recurring = CountJsonArray(Path.Combine(folder, "recurring_tasks.json"));
				bool hasSettings = File.Exists(Path.Combine(folder, "settings.json"));
				List<string> parts = new List<string>();
				if (zones > 0)
				{
					parts.Add($"{zones} 个收纳区");
				}
				if (todos > 0)
				{
					parts.Add($"{todos} 条待办");
				}
				if (mappings > 0)
				{
					parts.Add($"{mappings} 个映射区");
				}
				if (icons > 0)
				{
					parts.Add($"{icons} 个收纳图标");
				}
				if (recurring > 0)
				{
					parts.Add($"{recurring} 个循环任务");
				}
				string details = ((parts.Count > 0) ? string.Join(" · ", parts) : "无配置数据");
				list.Add(new BackupInfo
				{
					Id = id,
					CreatedAt = dt,
					ItemCount = count,
					AppVersion = ver,
					ZoneCount = zones,
					TodoCount = todos,
					MappingCount = mappings,
					IconCount = icons,
					RecurringCount = recurring,
					HasSettings = hasSettings,
					Details = details
				});
			}
			list.Sort((BackupInfo a, BackupInfo b) => b.CreatedAt.CompareTo(a.CreatedAt));
		}
		catch (Exception ex)
		{
			DebugLog("GetManualBackups 失败: " + ex.Message);
		}
		return list;
	}

	public static string CreateManualBackup()
	{
		try
		{
			string dir = Path.Combine(ManualBackupDir, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
			Directory.CreateDirectory(dir);
			int copied = 0;
			string[] backupDataFiles = BackupDataFiles;
			foreach (string file in backupDataFiles)
			{
				string src = Path.Combine(AppDataDir, file);
				if (File.Exists(src))
				{
					File.Copy(src, Path.Combine(dir, file), overwrite: true);
					copied++;
				}
			}
			Dictionary<string, object> meta = new Dictionary<string, object>
			{
				["CreatedAt"] = DateTime.Now.ToString("o"),
				["AppVersion"] = FormatDisplayVersion(typeof(DataService).Assembly.GetName().Version?.ToString() ?? ""),
				["ItemCount"] = copied
			};
			File.WriteAllText(Path.Combine(dir, "backup_meta.json"), JsonSerializer.Serialize(meta, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
			return Path.GetFileName(dir);
		}
		catch (Exception ex)
		{
			DebugLog("CreateManualBackup 失败: " + ex.Message);
			return "";
		}
	}

	public static bool RestoreManualBackup(string id)
	{
		try
		{
			string srcDir = Path.Combine(ManualBackupDir, id);
			if (!Directory.Exists(srcDir))
			{
				return false;
			}
			string[] files = Directory.GetFiles(srcDir);
			foreach (string file in files)
			{
				string name = Path.GetFileName(file);
				if (!name.Equals("backup_meta.json", StringComparison.OrdinalIgnoreCase))
				{
					if (name.Equals("window_state.json", StringComparison.OrdinalIgnoreCase))
					{
						RestoreWindowStatePartial(file);
					}
					else
					{
						File.Copy(file, Path.Combine(AppDataDir, name), overwrite: true);
					}
				}
			}
			return true;
		}
		catch (Exception ex)
		{
			DebugLog("RestoreManualBackup 失败: " + ex.Message);
			return false;
		}
	}

	public static void DeleteManualBackup(string id)
	{
		try
		{
			string dir = Path.Combine(ManualBackupDir, id);
			if (Directory.Exists(dir))
			{
				Directory.Delete(dir, recursive: true);
			}
		}
		catch (Exception ex)
		{
			DebugLog("DeleteManualBackup 失败: " + ex.Message);
		}
	}
}
