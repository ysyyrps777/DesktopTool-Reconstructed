using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTool.Models;
using DesktopTool.Services;
using DesktopTool.Views;
using Microsoft.Win32;

namespace DesktopTool.ViewModels;

public class MainViewModel : ViewModelBase
{
	private readonly DataService _dataService;

	private readonly AppSettings _settings;

	private readonly List<ZoneWindow> _openZoneWindows = new List<ZoneWindow>();

	private readonly List<MappingWindow> _openMappingWindows = new List<MappingWindow>();

	private TodoWindow? _todoWindow;

	private CancellationTokenSource? _saveZonesCts;

	private List<(IconGroup Group, ZoneIcon Icon)>? _pendingZombies;

	public IReadOnlyList<ZoneWindow> OpenZoneWindows => _openZoneWindows;

	public ObservableCollection<IconZone> Zones { get; } = new ObservableCollection<IconZone>();

	public ObservableCollection<TodoItem> TodoItems { get; } = new ObservableCollection<TodoItem>();

	public ObservableCollection<RecurringTask> RecurringTasks { get; } = new ObservableCollection<RecurringTask>();

	public ObservableCollection<MappingZone> Mappings { get; } = new ObservableCollection<MappingZone>();

	public DataService DataService => _dataService;

	public AppSettings Settings => _settings;

	public ICommand CreateZoneCommand { get; }

	public ICommand CreateMappingCommand { get; }

	public ICommand CreateTodoCommand { get; }

	public ICommand ExitCommand { get; }

	public ICommand HideWindowCommand { get; }

	public bool HasTodoWindow => _todoWindow != null;

	public double GlobalOpacity
	{
		get
		{
			return _settings.BackgroundOpacity;
		}
		set
		{
			if (Math.Abs(_settings.BackgroundOpacity - value) > 0.01)
			{
				_settings.BackgroundOpacity = Math.Clamp(value, 0.2, 1.0);
				OnPropertyChanged("GlobalOpacity");
				_dataService.SaveSettings(_settings);
			}
		}
	}

	public string PanelColor
	{
		get
		{
			return _settings.PanelColor;
		}
		set
		{
			if (_settings.PanelColor != value)
			{
				_settings.PanelColor = value;
				OnPropertyChanged("PanelColor");
				OnPropertyChanged("PanelColorBrush");
				_dataService.SaveSettings(_settings);
			}
		}
	}

	public Brush PanelColorBrush
	{
		get
		{
			try
			{
				return new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.PanelColor ?? "#1A1A1A"));
			}
			catch
			{
				return new SolidColorBrush(Color.FromRgb(26, 26, 26));
			}
		}
	}

	public int IconSpacing
	{
		get
		{
			return _settings.IconSpacing;
		}
		set
		{
			if (_settings.IconSpacing != value)
			{
				_settings.IconSpacing = Math.Clamp(value, 10, 100);
				OnPropertyChanged("IconSpacing");
				OnPropertyChanged("IconSpacingThickness");
				_dataService.SaveSettings(_settings);
			}
		}
	}

	public Thickness IconSpacingThickness
	{
		get
		{
			double s = (double)_settings.IconSpacing / 10.0;
			return new Thickness(s, s / 2.0, s, s / 2.0);
		}
	}

	public int FontSizeLevel => _settings.FontSizeLevel;

	public double FontScaleMultiplier => _settings.FontSizeLevel switch
	{
		0 => 0.88, 
		1 => 1.0, 
		2 => 1.15, 
		_ => 1.3, 
	};

	public double SystemDesktopFontSize { get; } = QueryDesktopIconFontSize();

	public double DesktopFontRatio { get; } = ComputeFontRatio();

	public string WindowStyle
	{
		get
		{
			return _settings.WindowStyle;
		}
		set
		{
			if (!(_settings.WindowStyle == value))
			{
				_settings.WindowStyle = value;
				OnPropertyChanged("WindowStyle");
				OnPropertyChanged("IsGlassStyle");
				_dataService.SaveSettings(_settings);
				RefreshAllWindowStyles();
			}
		}
	}

	public bool IsGlassStyle => _settings.WindowStyle == "Glass";

	public bool IsWin10 => Environment.OSVersion.Version.Build < 22000;

	public string CompletedTodoStyle
	{
		get
		{
			return _settings.CompletedTodoStyle;
		}
		set
		{
			if (!(_settings.CompletedTodoStyle == value))
			{
				_settings.CompletedTodoStyle = value;
				OnPropertyChanged("CompletedTodoStyle");
				_dataService.SaveSettings(_settings);
				_todoWindow?.Dispatcher.Invoke(delegate
				{
					_todoWindow.ViewModel.RefreshCompletedStyle();
				});
			}
		}
	}

	public bool SkipHidingForStartup { get; set; }

	public event EventHandler? TodoResorted;

	public event EventHandler? TodoViewRefreshed;

	public void NotifyTodoViewRefreshed()
	{
		this.TodoViewRefreshed?.Invoke(this, EventArgs.Empty);
	}

	public void RefreshMappingFileCounts()
	{
		foreach (MappingZone mapping in Mappings)
		{
			mapping.RefreshFileCounts();
		}
	}

	private static double QueryDesktopIconFontSize()
	{
		try
		{
			NativeMethods.LOGFONT lf = default(NativeMethods.LOGFONT);
			if (NativeMethods.SystemParametersInfo(31u, (uint)Marshal.SizeOf<NativeMethods.LOGFONT>(), ref lf, 0u))
			{
				double h = Math.Abs(lf.lfHeight);
				if (h >= 6.0 && h <= 48.0)
				{
					return h;
				}
			}
		}
		catch
		{
		}
		return 12.0;
	}

	private static double ComputeFontRatio()
	{
		double raw = QueryDesktopIconFontSize() / 10.0;
		return Math.Clamp(1.0 + (raw - 1.0) * 0.3, 0.97, 1.15);
	}

	public void SetFontSizeLevel(int level)
	{
		if (_settings.FontSizeLevel != level)
		{
			_settings.FontSizeLevel = Math.Clamp(level, 0, 3);
			OnPropertyChanged("FontSizeLevel");
			OnPropertyChanged("FontScaleMultiplier");
			_dataService.SaveSettings(_settings);
			RefreshAllZoneIconSizes();
			_todoWindow?.RefreshFontSizes();
			RefreshAllMappingIconSizes();
		}
	}

	public MainViewModel(DataService dataService)
	{
		_dataService = dataService;
		_settings = _dataService.LoadSettings();
		DesktopIconService.UserSelectedMode = _settings.HidingMode ?? "Attributes";
		DesktopIconService.CustomHiddenDir = _settings.CustomHiddenPath;
		DesktopIconService.IsPathManagedCallback = delegate(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}
			try
			{
				string fullPath = Path.GetFullPath(path);
				foreach (IconZone zone in Zones)
				{
					foreach (IconGroup group in zone.Groups)
					{
						foreach (ZoneIcon current in group.Icons)
						{
							if (current.IsHidden && !string.IsNullOrEmpty(current.FilePath) && string.Equals(Path.GetFullPath(current.FilePath), fullPath, StringComparison.OrdinalIgnoreCase))
							{
								return true;
							}
						}
					}
				}
			}
			catch
			{
			}
			return false;
		};
		CreateZoneCommand = new RelayCommand(CreateZone);
		CreateMappingCommand = new RelayCommand(CreateMappingZone);
		CreateTodoCommand = new RelayCommand(delegate
		{
			CreateTodoWindow(null);
		}, (object? _) => _todoWindow == null);
		ExitCommand = new RelayCommand((Action<object?>)async delegate
		{
			if (TrayService.Instance != null)
			{
				TrayService.Instance.ExitApplication();
			}
			else
			{
				DesktopEmbedService.IsAppExiting = true;
				SaveZonesNow();
				SaveTodoWindowStateBeforeShutdown();
				Application.Current.Shutdown();
			}
		}, (Func<object?, bool>?)null);
		HideWindowCommand = new RelayCommand((Action<object?>)delegate
		{
			TrayService.Instance?.HideMainWindow();
		}, (Func<object?, bool>?)null);
		LoadData();
	}

	private void LoadData()
	{
		try
		{
			foreach (IconZone z in _dataService.LoadZones())
			{
				Zones.Add(z);
			}
		}
		catch (Exception ex)
		{
			LogStartupError("LoadZones", ex);
		}
		try
		{
			foreach (TodoItem t in _dataService.LoadTodos())
			{
				TodoItems.Add(t);
			}
		}
		catch (Exception ex2)
		{
			LogStartupError("LoadTodos", ex2);
		}
		try
		{
			foreach (RecurringTask r in _dataService.LoadRecurringTasks())
			{
				RecurringTasks.Add(r);
			}
		}
		catch (Exception ex3)
		{
			LogStartupError("LoadRecurringTasks", ex3);
		}
		try
		{
			foreach (MappingZone m in _dataService.LoadMappings())
			{
				Mappings.Add(m);
			}
		}
		catch (Exception ex4)
		{
			LogStartupError("LoadMappings", ex4);
		}
		MigrateTodoSortOrders();
		ResortTodosByDate(ascending: false);
		if (NeedsV2Migration())
		{
			try
			{
				BackupRawDataBeforeMigration();
			}
			catch (Exception ex5)
			{
				LogStartupError("BackupRawData", ex5);
			}
		}
		Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)async delegate
		{
			Task logTask = Task.Run(delegate
			{
				try
				{
					LogExplorerSettings();
				}
				catch (Exception ex10)
				{
					LogStartupError("LogExplorerSettings", ex10);
				}
			});
			try
			{
				await PerformStartupMigrationAsync();
			}
			catch (Exception ex6)
			{
				LogStartupError("V2Migration", ex6);
			}
			await logTask;
			try
			{
				DesktopVisualService.Initialize();
			}
			catch (Exception ex7)
			{
				LogStartupError("VisualServiceInit", ex7);
			}
			try
			{
				RestoreAllZoneWindows();
			}
			catch (Exception ex8)
			{
				LogStartupError("RestoreZoneWindows", ex8);
			}
			try
			{
				RestoreTodoWindowOnStartup();
			}
			catch (Exception ex9)
			{
				LogStartupError("RestoreTodoWindowOnStartup", ex9);
			}
			Task.Run(delegate
			{
				try
				{
					PerformStartupIntegrityCheck();
				}
				catch (Exception ex10)
				{
					LogStartupError("IntegrityCheck", ex10);
				}
				try
				{
					ReapplyHiddenAttributes();
				}
				catch (Exception ex11)
				{
					LogStartupError("ReapplyHiddenAttributes", ex11);
				}
			});
		});
	}

	private bool NeedsV2Migration()
	{
		return Zones.Any((IconZone z) => z.SchemaVersion != "2.0.0");
	}

	public static void BackupRawDataBeforeMigration()
	{
		try
		{
			string path = App.GetAppDataDir();
			string backupsDir = Path.Combine(path, "backups");
			Directory.CreateDirectory(backupsDir);
			string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			string suffix = "_pre_migration_" + ts + ".json.bak";
			string zonesFile = Path.Combine(path, "zones.json");
			string todosFile = Path.Combine(path, "todos.json");
			string manifestFile = Path.Combine(path, "recovery_manifest.json");
			string settingsFile = Path.Combine(path, "settings.json");
			if (File.Exists(zonesFile))
			{
				File.Copy(zonesFile, Path.Combine(backupsDir, "zones" + suffix), overwrite: true);
			}
			if (File.Exists(todosFile))
			{
				File.Copy(todosFile, Path.Combine(backupsDir, "todos" + suffix), overwrite: true);
			}
			if (File.Exists(manifestFile))
			{
				File.Copy(manifestFile, Path.Combine(backupsDir, "manifest" + suffix), overwrite: true);
			}
			if (File.Exists(settingsFile))
			{
				File.Copy(settingsFile, Path.Combine(backupsDir, "settings" + suffix), overwrite: true);
			}
		}
		catch (Exception)
		{
		}
	}

	private async Task PerformStartupMigrationAsync()
	{
		if (DesktopIconService.UserSelectedMode == "Physical")
		{
			return;
		}
		if (CrashRecoveryService.RecoveryTask != null)
		{
			try
			{
				await CrashRecoveryService.RecoveryTask;
			}
			catch (Exception ex)
			{
				LogStartupError("RecoveryAwait", ex);
			}
			finally
			{
				CrashRecoveryService.RecoveryTask = null;
			}
		}
		if (!NeedsV2Migration())
		{
			return;
		}
		await ProgressWindow.RunWithProgressAsync(Application.Current.MainWindow, "正在迁移收纳数据…", async delegate(Action<string?> reportFile, Action<int, int> reportProgress)
		{
			await Task.Run(delegate
			{
				PerformV2MigrationWithProgress(reportFile, reportProgress);
			});
		});
	}

	private void PerformV2MigrationWithProgress(Action<string?> reportFile, Action<int, int> reportProgress)
	{
		_dataService.BackupAllData(1);
		List<(IconZone, IconGroup, ZoneIcon)> physicalIcons = new List<(IconZone, IconGroup, ZoneIcon)>();
		List<(IconZone, IconGroup, ZoneIcon)> residualIcons = new List<(IconZone, IconGroup, ZoneIcon)>();
		foreach (IconZone zone in Zones)
		{
			if (zone.SchemaVersion == "2.0.0")
			{
				continue;
			}
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath))
					{
						physicalIcons.Add((zone, group, icon));
					}
					else if (icon.HiddenMethod == "Physical" || icon.HiddenMethod == null)
					{
						residualIcons.Add((zone, group, icon));
					}
				}
			}
		}
		foreach (var item2 in residualIcons)
		{
			ZoneIcon item = item2.Item3;
			item.HiddenMethod = null;
			item.HiddenPath = null;
		}
		_ = residualIcons.Count;
		_ = 0;
		int total = physicalIcons.Count;
		int restoredCount = 0;
		int failedCount = 0;
		int mergedCount = 0;
		int idx = 0;
		foreach (var item3 in physicalIcons)
		{
			ZoneIcon icon2 = item3.Item3;
			idx++;
			string name = icon2.DisplayName ?? Path.GetFileName(icon2.FilePath) ?? icon2.FilePath;
			reportFile("正在恢复：" + name);
			reportProgress(idx, total);
			try
			{
				bool num = File.Exists(icon2.FilePath) || Directory.Exists(icon2.FilePath);
				bool hiddenFileExists = File.Exists(icon2.HiddenPath) || Directory.Exists(icon2.HiddenPath);
				if (num)
				{
					icon2.HiddenMethod = null;
					icon2.HiddenPath = null;
					mergedCount++;
					if (hiddenFileExists)
					{
						try
						{
							DesktopIconService.RemoveManifestEntry(icon2.HiddenPath);
						}
						catch
						{
						}
					}
				}
				else if (hiddenFileExists)
				{
					(bool, string, string) restoreResult = DesktopIconService.TryRestoreFile(icon2.FilePath, icon2.HiddenMethod, icon2.HiddenPath);
					if (restoreResult.Item1)
					{
						if (restoreResult.Item2 != null && !string.Equals(restoreResult.Item2, icon2.FilePath, StringComparison.OrdinalIgnoreCase))
						{
							icon2.FilePath = restoreResult.Item2;
						}
						icon2.HiddenMethod = null;
						icon2.HiddenPath = null;
						restoredCount++;
					}
					else
					{
						failedCount++;
					}
				}
				else
				{
					icon2.HiddenMethod = null;
					icon2.HiddenPath = null;
				}
			}
			catch (Exception)
			{
				failedCount++;
			}
		}
		foreach (IconZone zone2 in Zones)
		{
			if (zone2.SchemaVersion != "2.0.0")
			{
				zone2.SchemaVersion = "2.0.0";
			}
		}
		try
		{
			string sentinel = Path.Combine(App.GetAppDataDir(), ".uninstalling");
			if (File.Exists(sentinel))
			{
				try
				{
					File.Delete(sentinel);
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		Application.Current.Dispatcher.Invoke(delegate
		{
			SaveZonesNow();
		});
	}

	private async Task PerformV2ModeUnificationAsync(string previousMode)
	{
		List<(IconZone Zone, IconGroup Group, ZoneIcon Icon)> iconsToRestore = new List<(IconZone, IconGroup, ZoneIcon)>();
		List<(IconZone Zone, IconGroup Group, ZoneIcon Icon)> iconsToHide = new List<(IconZone, IconGroup, ZoneIcon)>();
		int alreadyCorrect = 0;
		int zombieCount = 0;
		foreach (IconZone zone in Zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (string.IsNullOrEmpty(icon.FilePath))
					{
						continue;
					}
					if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath))
					{
						iconsToRestore.Add((zone, group, icon));
						continue;
					}
					if (icon.HiddenMethod == "Attributes")
					{
						if (File.Exists(icon.FilePath) || Directory.Exists(icon.FilePath))
						{
							if (!DesktopIconService.IsAttributeHidden(icon.FilePath))
							{
								iconsToHide.Add((zone, group, icon));
							}
							else
							{
								alreadyCorrect++;
							}
						}
						else
						{
							zombieCount++;
						}
						continue;
					}
					string oldMethod = icon.HiddenMethod;
					if (icon.HiddenMethod == "Physical" || string.IsNullOrEmpty(icon.HiddenMethod))
					{
						icon.HiddenMethod = null;
						icon.HiddenPath = null;
						_ = oldMethod == "Physical";
					}
					if (File.Exists(icon.FilePath) || Directory.Exists(icon.FilePath))
					{
						if (DesktopIconService.IsAttributeHidden(icon.FilePath))
						{
							icon.HiddenMethod = "Attributes";
							icon.HiddenPath = null;
							alreadyCorrect++;
						}
						else
						{
							iconsToHide.Add((zone, group, icon));
						}
					}
					else
					{
						zombieCount++;
					}
				}
			}
		}
		int totalWork = iconsToRestore.Count + iconsToHide.Count;
		LogStartupDiagnostic($"[V2ModeUnification] START previousMode={previousMode}, Restore(Physical→Desktop)={iconsToRestore.Count}, Hide(ApplyAttributes)={iconsToHide.Count}, AlreadyCorrect={alreadyCorrect}, Zombie={zombieCount}, TotalWork={totalWork}");
		if (totalWork == 0)
		{
			SaveZonesNow();
			return;
		}
		Window owner = Application.Current.MainWindow;
		try
		{
			await ProgressWindow.RunWithProgressAsync(owner, "正在统一收纳模式…", async delegate(Action<string?> reportFile, Action<int, int> reportProgress)
			{
				await Task.Run(delegate
				{
					int num = 0;
					int num2 = 0;
					foreach (var item4 in iconsToRestore)
					{
						ZoneIcon item = item4.Icon;
						num++;
						string text = item.DisplayName ?? Path.GetFileName(item.FilePath) ?? item.FilePath;
						reportFile("正在恢复：" + text);
						reportProgress(num, totalWork);
						try
						{
							bool num3 = File.Exists(item.FilePath) || Directory.Exists(item.FilePath);
							bool flag = !string.IsNullOrEmpty(item.HiddenPath) && (File.Exists(item.HiddenPath) || Directory.Exists(item.HiddenPath));
							if (num3)
							{
								item.HiddenMethod = null;
								item.HiddenPath = null;
							}
							else if (flag)
							{
								(bool, string, string) tuple = DesktopIconService.TryRestoreFile(item.FilePath, item.HiddenMethod, item.HiddenPath);
								if (tuple.Item1)
								{
									if (tuple.Item2 != null && !string.Equals(tuple.Item2, item.FilePath, StringComparison.OrdinalIgnoreCase))
									{
										item.FilePath = tuple.Item2;
									}
									item.HiddenMethod = null;
									item.HiddenPath = null;
								}
							}
							else
							{
								item.HiddenMethod = null;
								item.HiddenPath = null;
							}
						}
						catch (Exception)
						{
						}
					}
					foreach (var item5 in iconsToHide)
					{
						ZoneIcon item2 = item5.Icon;
						num++;
						string text2 = item2.DisplayName ?? Path.GetFileName(item2.FilePath) ?? item2.FilePath;
						reportFile("正在隐藏：" + text2);
						reportProgress(num, totalWork);
						try
						{
							if (File.Exists(item2.FilePath) || Directory.Exists(item2.FilePath))
							{
								if (DesktopIconService.IsAttributeHidden(item2.FilePath))
								{
									item2.HiddenMethod = "Attributes";
									item2.HiddenPath = null;
								}
								else if (DesktopIconService.TryAttributeHide(item2.FilePath))
								{
									item2.HiddenMethod = "Attributes";
									item2.HiddenPath = null;
									DesktopIconService.NotifyDesktopChange(item2.FilePath);
								}
								else
								{
									string text3 = DesktopIconService.MoveToHidden(item2.FilePath);
									if (text3 != null)
									{
										item2.HiddenMethod = "Physical";
										item2.HiddenPath = text3;
										DesktopIconService.NotifyDesktopChange(item2.FilePath);
									}
									else
									{
										num2++;
										int num4 = 0;
										try
										{
											num4 = Marshal.GetLastWin32Error();
										}
										catch
										{
										}
										LogStartupDiagnostic($"[V2ModeUnification] ATTRIBUTE_HIDE_FAILED File={item2.FilePath}, Win32Err={num4}" + ((num4 == 5) ? " (ACCESS_DENIED)" : "") + ", PhysicalFallback=FAILED");
									}
								}
							}
						}
						catch (Exception)
						{
							num2++;
						}
					}
					foreach (var item6 in iconsToRestore)
					{
						ZoneIcon item3 = item6.Icon;
						try
						{
							if (string.IsNullOrEmpty(item3.HiddenMethod) && string.IsNullOrEmpty(item3.HiddenPath) && (File.Exists(item3.FilePath) || Directory.Exists(item3.FilePath)))
							{
								if (!DesktopIconService.IsAttributeHidden(item3.FilePath))
								{
									if (DesktopIconService.TryAttributeHide(item3.FilePath))
									{
										item3.HiddenMethod = "Attributes";
										DesktopIconService.NotifyDesktopChange(item3.FilePath);
									}
									else
									{
										string text4 = DesktopIconService.MoveToHidden(item3.FilePath);
										if (text4 != null)
										{
											item3.HiddenMethod = "Physical";
											item3.HiddenPath = text4;
											DesktopIconService.NotifyDesktopChange(item3.FilePath);
										}
										else
										{
											num2++;
											int num5 = 0;
											try
											{
												num5 = Marshal.GetLastWin32Error();
											}
											catch
											{
											}
											LogStartupDiagnostic($"[V2ModeUnification] POST_RESTORE_HIDE_FAILED File={item3.FilePath}, Win32Err={num5}" + ((num5 == 5) ? " (ACCESS_DENIED)" : "") + ", PhysicalFallback=FAILED");
										}
									}
								}
								else
								{
									item3.HiddenMethod = "Attributes";
								}
							}
						}
						catch (Exception)
						{
							num2++;
						}
					}
					if (num2 > 0)
					{
						LogStartupDiagnostic($"[V2ModeUnification] WARNING: {num2} icons failed to attribute-hide");
					}
					LogStartupDiagnostic($"[V2ModeUnification] COMPLETE Restored={iconsToRestore.Count - num2}, Hidden={iconsToHide.Count - num2}, Failed={num2}, AlreadyCorrect={alreadyCorrect}");
					Application.Current.Dispatcher.Invoke(delegate
					{
						SaveZonesNow();
					});
				});
			});
		}
		catch (Exception ex)
		{
			_ = ex;
			await Task.Run(delegate
			{
				foreach (var item7 in iconsToRestore)
				{
					ZoneIcon item = item7.Icon;
					try
					{
						if (item.HiddenMethod == "Physical" && !string.IsNullOrEmpty(item.HiddenPath))
						{
							DesktopIconService.TryRestoreFile(item.FilePath, item.HiddenMethod, item.HiddenPath);
							item.HiddenMethod = null;
							item.HiddenPath = null;
						}
					}
					catch (Exception)
					{
					}
				}
				foreach (var item8 in iconsToHide)
				{
					ZoneIcon item2 = item8.Icon;
					try
					{
						if ((File.Exists(item2.FilePath) || Directory.Exists(item2.FilePath)) && DesktopIconService.TryAttributeHide(item2.FilePath))
						{
							item2.HiddenMethod = "Attributes";
							item2.HiddenPath = null;
						}
					}
					catch (Exception)
					{
					}
				}
				Application.Current.Dispatcher.Invoke(delegate
				{
					SaveZonesNow();
				});
			});
		}
		bool schemaChanged = false;
		foreach (IconZone zone2 in Zones)
		{
			if (!(zone2.SchemaVersion == "2.0.0") && !zone2.Groups.SelectMany((IconGroup g) => g.Icons).Any((ZoneIcon i) => i.HiddenMethod == "Physical"))
			{
				zone2.SchemaVersion = "2.0.0";
				schemaChanged = true;
			}
		}
		if (schemaChanged)
		{
			SaveZonesNow();
		}
	}

	private async Task PerformPhysicalToAttributeMigrationAsync()
	{
		List<(IconZone Zone, IconGroup Group, ZoneIcon Icon)> physicalIcons = new List<(IconZone, IconGroup, ZoneIcon)>();
		foreach (IconZone zone in Zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath))
					{
						physicalIcons.Add((zone, group, icon));
					}
				}
			}
		}
		if (physicalIcons.Count == 0)
		{
			return;
		}
		int total = physicalIcons.Count;
		LogStartupDiagnostic($"[PhysicalToAttrMigration] START Physical icons: {total}, ShowSuperHidden=1");
		Window owner = Application.Current.MainWindow;
		int migratedCount = 0;
		int failedCount = 0;
		int zombieCount = 0;
		try
		{
			await ProgressWindow.RunWithProgressAsync(owner, "正在切换到属性隐藏模式…", async delegate(Action<string?> reportFile, Action<int, int> reportProgress)
			{
				await Task.Run(delegate
				{
					int num = 0;
					foreach (var item2 in physicalIcons)
					{
						ZoneIcon item = item2.Icon;
						num++;
						string obj = item.DisplayName ?? Path.GetFileName(item.FilePath) ?? item.FilePath;
						reportFile(obj);
						reportProgress(num, total);
						try
						{
							bool flag = !string.IsNullOrEmpty(item.FilePath) && (File.Exists(item.FilePath) || Directory.Exists(item.FilePath));
							if (string.IsNullOrEmpty(item.HiddenPath) || (!File.Exists(item.HiddenPath) && !Directory.Exists(item.HiddenPath)))
							{
								item.HiddenMethod = null;
								item.HiddenPath = null;
								int num2 = zombieCount;
								zombieCount = num2 + 1;
							}
							else
							{
								if (flag)
								{
									try
									{
										DesktopIconService.RemoveManifestEntry(item.HiddenPath);
									}
									catch
									{
									}
									goto IL_01db;
								}
								(bool, string, string) tuple = DesktopIconService.TryRestoreFile(item.FilePath, item.HiddenMethod, item.HiddenPath);
								if (tuple.Item1)
								{
									if (tuple.Item2 != null && !string.Equals(tuple.Item2, item.FilePath, StringComparison.OrdinalIgnoreCase))
									{
										item.FilePath = tuple.Item2;
									}
									goto IL_01db;
								}
								int num2 = failedCount;
								failedCount = num2 + 1;
								LogStartupDiagnostic($"[PhysicalToAttrMigration] RESTORE_FAILED File={item.FilePath}, Reason={tuple.Item3 ?? "unknown"}");
							}
							goto end_IL_006c;
							IL_01db:
							if (string.IsNullOrEmpty(item.FilePath) || (!File.Exists(item.FilePath) && !Directory.Exists(item.FilePath)))
							{
								int num2 = failedCount;
								failedCount = num2 + 1;
								item.HiddenMethod = null;
								item.HiddenPath = null;
							}
							else if (DesktopIconService.TryAttributeHide(item.FilePath))
							{
								item.HiddenMethod = "Attributes";
								item.HiddenPath = null;
								DesktopIconService.NotifyDesktopChange(item.FilePath);
								int num2 = migratedCount;
								migratedCount = num2 + 1;
							}
							else
							{
								string text = DesktopIconService.MoveToHidden(item.FilePath);
								if (text != null)
								{
									item.HiddenMethod = "Physical";
									item.HiddenPath = text;
									int num2 = failedCount;
									failedCount = num2 + 1;
									LogStartupDiagnostic($"[PhysicalToAttrMigration] H+S_FAILED File={item.FilePath}, Fallback=Physical, HiddenPath={text}");
								}
								else
								{
									int num2 = failedCount;
									failedCount = num2 + 1;
									LogStartupDiagnostic("[PhysicalToAttrMigration] CRITICAL_BOTH_FAILED File=" + item.FilePath);
								}
							}
							end_IL_006c:;
						}
						catch (Exception ex2)
						{
							int num2 = failedCount;
							failedCount = num2 + 1;
							LogStartupDiagnostic($"[PhysicalToAttrMigration] EXCEPTION File={item.FilePath}, Error={ex2.GetType().Name}: {ex2.Message}");
						}
					}
					Application.Current.Dispatcher.Invoke(delegate
					{
						SaveZonesNow();
					});
				});
			});
		}
		catch (Exception ex)
		{
			_ = ex;
			await Task.Run(delegate
			{
				foreach (var item3 in physicalIcons)
				{
					ZoneIcon item = item3.Icon;
					try
					{
						if (item.HiddenMethod == "Physical" && !string.IsNullOrEmpty(item.HiddenPath) && !string.IsNullOrEmpty(item.FilePath))
						{
							(bool, string, string) tuple = DesktopIconService.TryRestoreFile(item.FilePath, item.HiddenMethod, item.HiddenPath);
							if (tuple.Item1)
							{
								if (tuple.Item2 != null)
								{
									item.FilePath = tuple.Item2;
								}
								if ((File.Exists(item.FilePath) || Directory.Exists(item.FilePath)) && DesktopIconService.TryAttributeHide(item.FilePath))
								{
									item.HiddenMethod = "Attributes";
									item.HiddenPath = null;
									migratedCount++;
								}
							}
						}
					}
					catch (Exception)
					{
					}
				}
				Application.Current.Dispatcher.Invoke(delegate
				{
					SaveZonesNow();
				});
			});
		}
		LogStartupDiagnostic($"[PhysicalToAttrMigration] COMPLETE Migrated={migratedCount}, Failed={failedCount}, Zombie={zombieCount}, Total={total}");
		bool schemaUpdated = false;
		foreach (IconZone zone2 in Zones)
		{
			if (!(zone2.SchemaVersion == "2.0.0") && !zone2.Groups.SelectMany((IconGroup g) => g.Icons).Any((ZoneIcon i) => i.HiddenMethod == "Physical"))
			{
				zone2.SchemaVersion = "2.0.0";
				schemaUpdated = true;
			}
		}
		if (schemaUpdated)
		{
			SaveZonesNow();
		}
	}

	private static void LogStartupError(string context, Exception ex)
	{
		LogHelper.Append(Path.Combine(Path.GetTempPath(), "DesktopTool_Crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MainViewModel.{context} 失败: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
	}

	private static void LogStartupDiagnostic(string message)
	{
		LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_startup.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
	}

	private static void LogExplorerSettings()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced");
			if (key != null)
			{
				object hidden = key.GetValue("Hidden");
				object superHidden = key.GetValue("ShowSuperHidden");
				bool showHidden = hidden != null && (int)hidden == 1;
				bool hideProtectedOS = superHidden != null && (int)superHidden == 1;
				LogStartupDiagnostic($"[ExplorerSettings] Hidden={hidden ?? "null"} (1=Show,2=Hide), ShowSuperHidden={superHidden ?? "null"} (0=Show,1=Hide), ShowHiddenFiles={showHidden}, HideProtectedOSFiles={hideProtectedOS}");
				if (showHidden)
				{
					LogStartupDiagnostic("[ExplorerSettings] ★ WARNING: 'Show hidden files' is ON! Attribute-hidden icons will still be VISIBLE on desktop. User needs to change Explorer setting to 'Don't show hidden files' (Hidden=2)");
				}
				if (!hideProtectedOS)
				{
					LogStartupDiagnostic("[ExplorerSettings] NOTE: 'Hide protected OS files' is OFF. System-file icons may still be visible.");
				}
			}
		}
		catch (Exception ex)
		{
			LogStartupDiagnostic("[ExplorerSettings] Failed to read registry: " + ex.Message);
		}
	}

	private void MigrateTodoSortOrders()
	{
		bool migrated = false;
		for (int i = 0; i < TodoItems.Count; i++)
		{
			if (TodoItems[i].SortOrder == 0)
			{
				TodoItems[i].SortOrder = i + 1;
				migrated = true;
			}
		}
		if (migrated)
		{
			try
			{
				_dataService.SaveTodos(TodoItems.ToList());
			}
			catch
			{
			}
		}
	}

	private void RestoreAllZoneWindows()
	{
		_ = Zones.Count;
		_ = 1;
		foreach (IconZone zone in Zones)
		{
			OpenExistingZone(zone);
		}
		LogStartupDiagnostic($"[RestoreMapping] 共 {Mappings.Count} 个映射区待恢复");
		foreach (MappingZone mapping in Mappings)
		{
			OpenExistingMapping(mapping);
		}
	}

	private void RestoreTodoWindowOnStartup()
	{
		if (_dataService.WasTodoWindowOpen())
		{
			CreateTodoWindow(null, autoOpen: true);
		}
	}

	private void CreateZone(object? _)
	{
		double defaultWidth = 360.0;
		double defaultHeight = 420.0;
		(double Left, double Top) primaryScreenCenter = DesktopEmbedService.GetPrimaryScreenCenter(defaultWidth, defaultHeight);
		double x = primaryScreenCenter.Left;
		double y = primaryScreenCenter.Top;
		IconZone zone = new IconZone
		{
			Name = "新建收纳区",
			X = x,
			Y = y,
			Width = defaultWidth,
			Height = defaultHeight,
			IconSizeLevel = _settings.DefaultIconSizeLevel,
			SchemaVersion = "2.0.0"
		};
		Zones.Add(zone);
		SaveZones();
		OpenExistingZone(zone);
	}

	public void OpenExistingZone(IconZone zone)
	{
		ZoneWindow existing = _openZoneWindows.FirstOrDefault((ZoneWindow w) => w.ZoneId == zone.Id);
		if (existing != null)
		{
			existing.WindowState = WindowState.Normal;
			existing.Activate();
			return;
		}
		ZoneWindow window = new ZoneWindow(zone, this);
		window.Show();
		Application.Current.Dispatcher.BeginInvoke((Action)delegate
		{
			window.BringToTop();
		}, DispatcherPriority.Background);
		window.Closed += delegate
		{
			_openZoneWindows.Remove(window);
			SaveZones();
		};
		_openZoneWindows.Add(window);
	}

	private void CreateMappingZone(object? _)
	{
		double defaultWidth = 360.0;
		double defaultHeight = 420.0;
		(double Left, double Top) primaryScreenCenter = DesktopEmbedService.GetPrimaryScreenCenter(defaultWidth, defaultHeight);
		double x = primaryScreenCenter.Left;
		double y = primaryScreenCenter.Top;
		MappingZone mapping = new MappingZone
		{
			Name = "新建映射区",
			X = x,
			Y = y,
			Width = defaultWidth,
			Height = defaultHeight,
			IconSizeLevel = _settings.DefaultIconSizeLevel
		};
		Mappings.Add(mapping);
		SaveMappings();
		OpenExistingMapping(mapping);
	}

	public void OpenExistingMapping(MappingZone mapping)
	{
		MappingWindow existing = _openMappingWindows.FirstOrDefault((MappingWindow w) => w.ZoneId == mapping.Id);
		if (existing != null)
		{
			existing.WindowState = WindowState.Normal;
			existing.Activate();
			return;
		}
		LogStartupDiagnostic($"[OpenMapping] 正在打开映射区 id={mapping.Id.Substring(0, 8)} name={mapping.Name} x={mapping.X:F0} y={mapping.Y:F0} w={mapping.Width:F0} h={mapping.Height:F0}");
		MappingWindow window = new MappingWindow(mapping, this);
		LogStartupDiagnostic("[OpenMapping] MappingWindow 创建成功 id=" + mapping.Id.Substring(0, 8));
		window.Show();
		window.Closed += delegate
		{
			_openMappingWindows.Remove(window);
			SaveMappings();
		};
		_openMappingWindows.Add(window);
	}

	public void DeleteMappingZone(MappingZone mapping)
	{
		Mappings.Remove(mapping);
		SaveMappings();
	}

	public MappingWindow? GetOpenMappingWindow(string zoneId)
	{
		return _openMappingWindows.FirstOrDefault((MappingWindow w) => w.ZoneId == zoneId);
	}

	public void ResetMappingPosition(MappingZone mapping)
	{
		MappingWindow mappingWindow = _openMappingWindows.FirstOrDefault((MappingWindow mw) => mw.ZoneId == mapping.Id);
		if (mappingWindow != null)
		{
			mappingWindow.ResetToScreenCenter();
			return;
		}
		double w = ((mapping.Width > 100.0) ? mapping.Width : 360.0);
		double h = ((mapping.Height > 100.0) ? mapping.Height : 420.0);
		var (left, top) = DesktopEmbedService.GetPrimaryScreenCenter(w, h);
		mapping.X = left;
		mapping.Y = top;
		mapping.Width = w;
		mapping.Height = h;
		SaveMappings();
	}

	private void CreateTodoWindow(object? parameter, bool autoOpen = false)
	{
		if (_todoWindow != null)
		{
			try
			{
				_todoWindow.Activate();
				if (new WindowInteropHelper(_todoWindow).Handle != IntPtr.Zero)
				{
					return;
				}
			}
			catch
			{
			}
			_todoWindow = null;
			OnPropertyChanged("HasTodoWindow");
			((RelayCommand)CreateTodoCommand).RaiseCanExecuteChanged();
		}
		TodoWindow window = new TodoWindow(TodoItems, this);
		_todoWindow = window;
		OnPropertyChanged("HasTodoWindow");
		((RelayCommand)CreateTodoCommand).RaiseCanExecuteChanged();
		window.Show();
		Application.Current.Dispatcher.BeginInvoke((Action)delegate
		{
			window.PushToBottom();
		}, DispatcherPriority.Loaded);
		window.Closed += delegate
		{
			if (!DesktopEmbedService.IsAppExiting)
			{
				_todoWindow = null;
			}
			SaveTodos();
			if (!DesktopEmbedService.IsAppExiting)
			{
				_dataService.SaveTodoWindowOpen(isOpen: false);
			}
			OnPropertyChanged("HasTodoWindow");
			((RelayCommand)CreateTodoCommand).RaiseCanExecuteChanged();
		};
		_dataService.SaveTodoWindowOpen(isOpen: true);
	}

	public void DeleteZone(IconZone zone)
	{
		Zones.Remove(zone);
		SaveZones();
	}

	public void ResetZonePosition(IconZone zone)
	{
		ZoneWindow existing = _openZoneWindows.FirstOrDefault((ZoneWindow w) => w.ZoneId == zone.Id);
		if (existing != null)
		{
			existing.ResetToScreenCenter();
			zone.X = existing.Left;
			zone.Y = existing.Top;
			zone.Width = existing.Width;
			zone.Height = existing.Height;
			SaveZones();
		}
		else
		{
			double defaultWidth = 360.0;
			double defaultHeight = 420.0;
			var (x, y) = DesktopEmbedService.GetPrimaryScreenCenter(defaultWidth, defaultHeight);
			zone.X = x;
			zone.Y = y;
			zone.Width = defaultWidth;
			zone.Height = defaultHeight;
			SaveZones();
		}
	}

	public void ResetTodoPosition()
	{
		double defaultWidth = 320.0;
		double defaultHeight = 400.0;
		if (_todoWindow != null)
		{
			_todoWindow.ResetToScreenCenter();
			_dataService.SaveTodoWindowState(_todoWindow.Left, _todoWindow.Top, _todoWindow.Width, _todoWindow.Height);
		}
		else
		{
			var (x, y) = DesktopEmbedService.GetPrimaryScreenCenter(defaultWidth, defaultHeight);
			_dataService.SaveTodoWindowState(x, y, defaultWidth, defaultHeight);
		}
	}

	public void RestoreTodoWindow()
	{
		CreateTodoWindow(null, autoOpen: true);
	}

	public void ActivateOpenWindowsOnly()
	{
		foreach (ZoneWindow w in _openZoneWindows.ToList())
		{
			try
			{
				if (w.IsVisible)
				{
					w.ForceTopMost();
				}
			}
			catch
			{
			}
		}
		foreach (MappingWindow w2 in _openMappingWindows.ToList())
		{
			try
			{
				w2.ForceTopMost();
			}
			catch
			{
			}
		}
		if (_todoWindow == null)
		{
			return;
		}
		try
		{
			if (_todoWindow.IsVisible)
			{
				_todoWindow.ForceTopMost();
			}
		}
		catch
		{
		}
	}

	public void ReloadWindowsWithNewStyle()
	{
		SaveZonesNow();
		bool hadTodo = _todoWindow != null;
		if (_todoWindow != null)
		{
			try
			{
				_todoWindow.SaveWindowState();
			}
			catch
			{
			}
			try
			{
				_dataService.SaveTodoWindowOpen(isOpen: true);
			}
			catch
			{
			}
		}
		List<string> zoneIds = _openZoneWindows.Select((ZoneWindow w) => w.ZoneId).ToList();
		foreach (ZoneWindow window in _openZoneWindows.ToList())
		{
			try
			{
				window.Close();
			}
			catch
			{
			}
		}
		_openZoneWindows.Clear();
		if (_todoWindow != null)
		{
			try
			{
				_todoWindow.Close();
			}
			catch
			{
			}
			_todoWindow = null;
			OnPropertyChanged("HasTodoWindow");
			((RelayCommand)CreateTodoCommand).RaiseCanExecuteChanged();
		}
		foreach (string id in zoneIds)
		{
			IconZone zone = Zones.FirstOrDefault((IconZone z) => z.Id == id);
			if (zone != null)
			{
				OpenExistingZone(zone);
			}
		}
		if (hadTodo)
		{
			CreateTodoWindow(null, autoOpen: true);
		}
	}

	public bool RestoreAllHiddenIcons()
	{
		foreach (IconZone zone in Zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons.Where((ZoneIcon i) => i.IsHidden).ToList())
				{
					try
					{
						if (DesktopIconService.RestoreFile(icon.FilePath, icon.HiddenMethod, icon.HiddenPath))
						{
							icon.HiddenMethod = null;
							icon.HiddenPath = null;
							DesktopIconService.SyncIconFilePath(icon, "MainViewModel.RestoreAllHiddenIcons");
						}
					}
					catch
					{
					}
				}
			}
		}
		SaveZonesNow();
		DesktopIconService.VerifyAndLogIconPaths(Zones, "RestoreAllHiddenIcons");
		return true;
	}

	public async Task<bool> RestoreAllHiddenIconsAsync(Action<string?> reportProgress)
	{
		List<(ZoneIcon Icon, string Name)> allHidden = new List<(ZoneIcon, string)>();
		foreach (IconZone zone in Zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (icon.IsHidden)
					{
						allHidden.Add((icon, icon.DisplayName ?? Path.GetFileName(icon.FilePath)));
					}
				}
			}
		}
		await Task.Run(delegate
		{
			int num = 0;
			int count = allHidden.Count;
			foreach (var item3 in allHidden)
			{
				ZoneIcon item = item3.Icon;
				string item2 = item3.Name;
				num++;
				reportProgress($"正在移动：{item2} ({num}/{count})");
				if (DesktopIconService.RestoreFile(item.FilePath, item.HiddenMethod, item.HiddenPath))
				{
					item.HiddenMethod = null;
					item.HiddenPath = null;
					DesktopIconService.SyncIconFilePath(item, "MainViewModel.RestoreAllHiddenIconsAsync");
				}
			}
		});
		SaveZonesNow();
		DesktopIconService.VerifyAndLogIconPaths(Zones, "RestoreAllHiddenIconsAsync");
		return true;
	}

	public void RestoreZoneIcons(IconZone zone)
	{
		if (zone == null)
		{
			return;
		}
		foreach (IconGroup group in zone.Groups)
		{
			foreach (ZoneIcon icon in group.Icons.Where((ZoneIcon i) => i.IsHidden).ToList())
			{
				try
				{
					bool shouldSkip = false;
					if (icon.HiddenMethod == "Attributes")
					{
						if (!File.Exists(icon.FilePath) && !Directory.Exists(icon.FilePath) && icon.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
						{
							shouldSkip = true;
						}
					}
					else if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath) && !File.Exists(icon.HiddenPath) && !Directory.Exists(icon.HiddenPath))
					{
						shouldSkip = true;
					}
					if (shouldSkip)
					{
						icon.HiddenMethod = null;
						icon.HiddenPath = null;
					}
					else if (DesktopIconService.RestoreFile(icon.FilePath, icon.HiddenMethod, icon.HiddenPath))
					{
						icon.HiddenMethod = null;
						icon.HiddenPath = null;
						DesktopIconService.SyncIconFilePath(icon, "MainViewModel.RestoreZoneIcons");
					}
				}
				catch
				{
				}
			}
		}
		DesktopIconService.VerifyAndLogIconPaths(new List<IconZone> { zone }, "RestoreZoneIcons");
	}

	private static void CollectZoneLocks(IconZone zone, List<string> allLocks)
	{
	}

	public HashSet<string> GetAllTrackedHiddenPaths()
	{
		HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (IconZone zone in Zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (!string.IsNullOrEmpty(icon.HiddenPath))
					{
						try
						{
							set.Add(Path.GetFullPath(icon.HiddenPath));
						}
						catch
						{
						}
					}
				}
			}
		}
		return set;
	}

	public HashSet<string> GetAllTrackedOriginalPaths()
	{
		HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (IconZone zone in Zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (!string.IsNullOrEmpty(icon.FilePath))
					{
						set.Add(Path.GetFullPath(icon.FilePath));
					}
				}
			}
		}
		return set;
	}

	public void PerformStartupIntegrityCheck()
	{
		bool changed = false;
		List<(IconZone, IconGroup, ZoneIcon)> zombieIcons = new List<(IconZone, IconGroup, ZoneIcon)>();
		try
		{
			int repaired = DesktopIconService.RepairCrossContaminatedFilePaths(Zones);
			if (repaired > 0)
			{
				changed = true;
				LogStartupDiagnostic($"[IntegrityCheck] 已自动修复 {repaired} 个串味图标");
			}
		}
		catch (Exception ex)
		{
			LogStartupError("RepairCrossContam", ex);
		}
		Dictionary<string, List<ZoneIcon>> v2IconPaths = new Dictionary<string, List<ZoneIcon>>(StringComparer.OrdinalIgnoreCase);
		foreach (IconZone zone in Zones)
		{
			if (zone.SchemaVersion == "2.0.0")
			{
				new List<ZoneIcon>();
				foreach (IconGroup group in zone.Groups)
				{
					foreach (ZoneIcon icon in group.Icons)
					{
						try
						{
							if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath))
							{
								if (File.Exists(icon.FilePath) || Directory.Exists(icon.FilePath))
								{
									icon.HiddenMethod = null;
									icon.HiddenPath = null;
									changed = true;
								}
								else if (!File.Exists(icon.HiddenPath) && !Directory.Exists(icon.HiddenPath))
								{
									zombieIcons.Add((zone, group, icon));
									changed = true;
								}
							}
							if (!string.IsNullOrEmpty(icon.FilePath))
							{
								try
								{
									string normalizedPath = Path.GetFullPath(icon.FilePath).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
									if (!v2IconPaths.ContainsKey(normalizedPath))
									{
										v2IconPaths[normalizedPath] = new List<ZoneIcon>();
									}
									v2IconPaths[normalizedPath].Add(icon);
								}
								catch
								{
								}
							}
							int num;
							int num2;
							if (!string.IsNullOrEmpty(icon.FilePath) && !File.Exists(icon.FilePath))
							{
								num = ((!Directory.Exists(icon.FilePath)) ? 1 : 0);
								if (num != 0 && icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath) && !File.Exists(icon.HiddenPath))
								{
									num2 = ((!Directory.Exists(icon.HiddenPath)) ? 1 : 0);
									goto IL_023b;
								}
							}
							else
							{
								num = 0;
							}
							num2 = 0;
							goto IL_023b;
							IL_023b:
							bool isOrphanedPhysical = (byte)num2 != 0;
							bool isOrphanedNonPhysical = num != 0 && (icon.HiddenMethod != "Physical" || string.IsNullOrEmpty(icon.HiddenPath));
							if (isOrphanedPhysical || isOrphanedNonPhysical)
							{
								if (isOrphanedNonPhysical && !isOrphanedPhysical && TryRecoverFromHiddenDir(icon, group, zone) != null)
								{
									changed = true;
									LogStartupDiagnostic("[IntegrityCheck] Recovered orphaned file from hidden dir: " + icon.FilePath);
									continue;
								}
								zombieIcons.Add((zone, group, icon));
								changed = true;
								LogStartupDiagnostic($"[IntegrityCheck] Marked zombie: {icon.FilePath} (Method={icon.HiddenMethod ?? "null"}, HiddenPath exists={File.Exists(icon.HiddenPath ?? "")})");
							}
						}
						catch (Exception ex2)
						{
							LogStartupDiagnostic($"[IntegrityCheck] Per-icon error (skipped): {icon.FilePath}, {ex2.GetType().Name}: {ex2.Message}");
						}
					}
				}
				continue;
			}
			foreach (IconGroup group2 in zone.Groups)
			{
				foreach (ZoneIcon icon2 in group2.Icons)
				{
					if (icon2.HiddenMethod == "Attributes" && !File.Exists(icon2.FilePath) && !Directory.Exists(icon2.FilePath))
					{
						icon2.HiddenMethod = null;
						changed = true;
					}
					if (icon2.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon2.HiddenPath))
					{
						string full = Path.GetFullPath(icon2.HiddenPath);
						if (!File.Exists(full) && !Directory.Exists(full))
						{
							icon2.HiddenMethod = null;
							icon2.HiddenPath = null;
							changed = true;
						}
					}
					if (string.IsNullOrEmpty(icon2.HiddenMethod) && !string.IsNullOrEmpty(icon2.HiddenPath))
					{
						icon2.HiddenMethod = "Physical";
						changed = true;
					}
					if (string.IsNullOrEmpty(icon2.HiddenMethod) && string.IsNullOrEmpty(icon2.HiddenPath) && !string.IsNullOrEmpty(icon2.FilePath) && !File.Exists(icon2.FilePath) && !Directory.Exists(icon2.FilePath))
					{
						zombieIcons.Add((zone, group2, icon2));
						changed = true;
					}
				}
			}
		}
		foreach (var item in zombieIcons)
		{
			IconGroup group3 = item.Item2;
			ZoneIcon icon3 = item.Item3;
			try
			{
				group3.Icons.Remove(icon3);
				LogStartupDiagnostic("[IntegrityCheck] Removed zombie icon: " + icon3.FilePath);
			}
			catch (Exception ex3)
			{
				LogStartupDiagnostic("[IntegrityCheck] Failed to remove zombie: " + icon3.FilePath + ", " + ex3.Message);
			}
		}
		if (zombieIcons.Count > 0)
		{
			changed = true;
		}
		foreach (KeyValuePair<string, List<ZoneIcon>> kvp in v2IconPaths)
		{
			if (kvp.Value.Count <= 1)
			{
				continue;
			}
			foreach (ZoneIcon icon4 in (from i in kvp.Value
				group i by i.DisplayName ?? string.Empty into g
				where g.Count() > 1
				select g).SelectMany((IGrouping<string, ZoneIcon> g) => g.Skip(1)))
			{
				foreach (IconZone zone2 in Zones)
				{
					foreach (IconGroup group4 in zone2.Groups)
					{
						if (group4.Icons.Remove(icon4))
						{
							changed = true;
							break;
						}
					}
				}
			}
		}
		try
		{
			HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (IconZone zone3 in Zones)
			{
				foreach (IconGroup group5 in zone3.Groups)
				{
					foreach (ZoneIcon icon5 in group5.Icons)
					{
						if (!string.IsNullOrEmpty(icon5.FilePath))
						{
							try
							{
								known.Add(Path.GetFullPath(icon5.FilePath).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
							}
							catch
							{
								known.Add(icon5.FilePath);
							}
						}
					}
				}
			}
			List<string> orphans = DesktopIconService.FindOrphanAttributeHiddenFiles(known);
			if (orphans.Count > 0)
			{
				List<string> restored = DesktopIconService.RestoreOrphanHiddenFiles(orphans);
				DesktopIconService.ForceDesktopViewRefresh();
				if (restored.Count > 0)
				{
					LogStartupDiagnostic($"[OrphanRecovery] 已恢复 {restored.Count} 个孤立隐藏文件: {string.Join(", ", restored)}");
					string restoredList = string.Join("\n", restored.Select((string n) => "• " + n));
					string restoreMsg = $"检测到 {restored.Count} 个被隐藏但不在收纳区的文件（收纳记录可能已丢失）：\n\n{restoredList}\n\n已自动取消其隐藏属性并显示到桌面，请重新将其拖入收纳区，或手动删除。";
					Thread thread = new Thread((ThreadStart)delegate
					{
						try
						{
							MessageBox.Show(restoreMsg, "桌面工具箱 — 已恢复丢失的隐藏文件", MessageBoxButton.OK, MessageBoxImage.Asterisk);
						}
						catch
						{
						}
					});
					thread.SetApartmentState(ApartmentState.STA);
					thread.Start();
				}
			}
		}
		catch (Exception ex4)
		{
			LogStartupError("OrphanRecovery", ex4);
		}
		if (changed)
		{
			SaveZonesNow();
			LogStartupDiagnostic($"[IntegrityCheck] Changes saved (zombies={zombieIcons.Count}, dupes detected)");
		}
	}

	private static string? TryRecoverFromHiddenDir(ZoneIcon icon, IconGroup group, IconZone zone)
	{
		try
		{
			string hiddenDir = DesktopIconService.HiddenDir;
			if (!Directory.Exists(hiddenDir))
			{
				return null;
			}
			string fileName = Path.GetFileName(icon.FilePath);
			string candidatePath = Path.Combine(hiddenDir, fileName);
			if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
			{
				return null;
			}
			if (File.Exists(icon.FilePath) || Directory.Exists(icon.FilePath))
			{
				LogStartupDiagnostic("[TryRecoverFromHiddenDir] Target already exists on desktop, skip: " + icon.FilePath);
				return null;
			}
			try
			{
				if ((File.GetAttributes(candidatePath) & FileAttributes.Directory) != FileAttributes.None)
				{
					Directory.Move(candidatePath, icon.FilePath);
				}
				else
				{
					File.Move(candidatePath, icon.FilePath);
				}
				LogStartupDiagnostic("[TryRecoverFromHiddenDir] ✅ Recovered: " + candidatePath + " → " + icon.FilePath);
				return icon.FilePath;
			}
			catch (Exception ex)
			{
				LogStartupDiagnostic($"[TryRecoverFromHiddenDir] Move failed: {candidatePath} → {icon.FilePath}, {ex.Message}");
				return null;
			}
		}
		catch (Exception ex2)
		{
			LogStartupDiagnostic("[TryRecoverFromHiddenDir] Unexpected error: " + ex2.Message);
			return null;
		}
	}

	private void ReapplyHiddenAttributes()
	{
		if (SkipHidingForStartup)
		{
			return;
		}
		if (DesktopIconService.UserSelectedMode != "Physical")
		{
			try
			{
				DesktopIconService.ConfigureExplorerForAttributeMode();
			}
			catch (Exception ex)
			{
				LogStartupError("ConfigureExplorerForAttributeMode", ex);
			}
		}
		int reapplied = 0;
		int alreadyHidden = 0;
		int missingFile = 0;
		int failedHide = 0;
		List<string> affectedPaths = new List<string>();
		foreach (IconZone zone in Zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (string.IsNullOrEmpty(icon.FilePath))
					{
						continue;
					}
					if (!File.Exists(icon.FilePath) && !Directory.Exists(icon.FilePath))
					{
						if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath) && (File.Exists(icon.HiddenPath) || Directory.Exists(icon.HiddenPath)))
						{
							alreadyHidden++;
							continue;
						}
						if (TryRecoverFromHiddenDir(icon, group, zone) == null)
						{
							missingFile++;
							AddZombieForCleanup(icon, group);
							continue;
						}
						reapplied++;
						affectedPaths.Add(icon.FilePath);
						icon.HiddenMethod = null;
						icon.HiddenPath = null;
					}
					bool num = DesktopIconService.UserSelectedMode == "Physical";
					bool isActuallyHidden = DesktopIconService.IsAttributeHidden(icon.FilePath);
					if (num)
					{
						if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath) && (File.Exists(icon.HiddenPath) || Directory.Exists(icon.HiddenPath)))
						{
							alreadyHidden++;
						}
						else if (isActuallyHidden)
						{
							DesktopIconService.TryAttributeRestore(icon.FilePath);
							string hiddenPath = DesktopIconService.MoveToHidden(icon.FilePath);
							if (hiddenPath != null)
							{
								icon.HiddenMethod = "Physical";
								icon.HiddenPath = hiddenPath;
								reapplied++;
								affectedPaths.Add(icon.FilePath);
							}
							else
							{
								TryHideWithRetry(icon.FilePath);
								icon.HiddenMethod = "Attributes";
								icon.HiddenPath = null;
								failedHide++;
							}
						}
						else
						{
							string hiddenPath2 = DesktopIconService.MoveToHidden(icon.FilePath);
							if (hiddenPath2 != null)
							{
								icon.HiddenMethod = "Physical";
								icon.HiddenPath = hiddenPath2;
								reapplied++;
								affectedPaths.Add(icon.FilePath);
							}
							else
							{
								failedHide++;
							}
						}
						continue;
					}
					if (isActuallyHidden)
					{
						bool num2 = DesktopIconService.IsFullyAttributeHidden(icon.FilePath);
						bool isAlreadyAttributes = icon.HiddenMethod == "Attributes";
						if (!num2)
						{
							if (TryHideWithRetry(icon.FilePath))
							{
								reapplied++;
								affectedPaths.Add(icon.FilePath);
								icon.HiddenMethod = "Attributes";
								icon.HiddenPath = null;
								continue;
							}
							string hiddenPath3 = DesktopIconService.MoveToHidden(icon.FilePath);
							if (hiddenPath3 != null)
							{
								icon.HiddenMethod = "Physical";
								icon.HiddenPath = hiddenPath3;
								reapplied++;
								affectedPaths.Add(icon.FilePath);
							}
							else
							{
								failedHide++;
							}
						}
						else if (!isAlreadyAttributes)
						{
							_ = icon.HiddenMethod;
							icon.HiddenMethod = "Attributes";
							icon.HiddenPath = null;
							alreadyHidden++;
						}
						else
						{
							alreadyHidden++;
						}
						continue;
					}
					if (TryHideWithRetry(icon.FilePath))
					{
						_ = icon.HiddenMethod;
						icon.HiddenMethod = "Attributes";
						icon.HiddenPath = null;
						reapplied++;
						affectedPaths.Add(icon.FilePath);
						continue;
					}
					string hiddenPath4 = DesktopIconService.MoveToHidden(icon.FilePath);
					if (hiddenPath4 != null)
					{
						icon.HiddenMethod = "Physical";
						icon.HiddenPath = hiddenPath4;
						reapplied++;
						affectedPaths.Add(icon.FilePath);
						continue;
					}
					failedHide++;
					int win32Error = 0;
					try
					{
						win32Error = Marshal.GetLastWin32Error();
					}
					catch
					{
					}
					string oldMethod = icon.HiddenMethod;
					LogStartupDiagnostic($"[ReapplyHiddenAttributes] ATTRIBUTE_HIDE_FAILED File={icon.FilePath}, OldMethod={oldMethod ?? "null"}, Win32Err={win32Error}" + ((win32Error == 5) ? " (ACCESS_DENIED, file likely locked)" : "") + $", Exists={File.Exists(icon.FilePath)}, IsDir={Directory.Exists(icon.FilePath)}" + ", PhysicalFallback=FAILED");
				}
			}
		}
		foreach (string item in affectedPaths)
		{
			DesktopIconService.NotifyDesktopChange(item);
		}
		LogStartupDiagnostic($"[ReapplyHiddenAttributes] SUMMARY Reapplied={reapplied}, AlreadyHidden={alreadyHidden}, MissingFile={missingFile}, FailedHide={failedHide}");
		if (failedHide > 0)
		{
			LogStartupDiagnostic($"[ReapplyHiddenAttributes] WARNING: {failedHide} icons could NOT be attribute-hidden!");
		}
		int ghostCleaned = SanitizeHOnlyGhosts();
		int zombiesFlushed = FlushZombieCleanup();
		if (reapplied > 0 || missingFile > 0 || failedHide > 0 || ghostCleaned > 0 || zombiesFlushed > 0)
		{
			SaveZonesNow();
		}
		foreach (IconZone zone2 in Zones)
		{
			if (!(zone2.SchemaVersion == "2.0.0") && !zone2.Groups.SelectMany((IconGroup g) => g.Icons).Any((ZoneIcon i) => i.HiddenMethod == "Physical"))
			{
				zone2.SchemaVersion = "2.0.0";
			}
		}
		static bool TryHideWithRetry(string p)
		{
			for (int i = 0; i < 4; i++)
			{
				if (DesktopIconService.TryAttributeHide(p))
				{
					return true;
				}
				if (i < 3)
				{
					Thread.Sleep(80);
				}
			}
			return false;
		}
	}

	private int SanitizeHOnlyGhosts()
	{
		if (DesktopIconService.UserSelectedMode == "Physical")
		{
			return 0;
		}
		int cleaned = 0;
		foreach (IconZone zone in Zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (!string.IsNullOrEmpty(icon.FilePath) && (File.Exists(icon.FilePath) || Directory.Exists(icon.FilePath)) && !(icon.HiddenMethod != "Attributes") && DesktopIconService.IsAttributeHidden(icon.FilePath) && !DesktopIconService.IsFullyAttributeHidden(icon.FilePath))
					{
						string hiddenPath = DesktopIconService.MoveToHidden(icon.FilePath);
						if (hiddenPath != null)
						{
							icon.HiddenMethod = "Physical";
							icon.HiddenPath = hiddenPath;
							cleaned++;
						}
						else if (DesktopIconService.TryAttributeRestore(icon.FilePath))
						{
							icon.HiddenMethod = null;
							icon.HiddenPath = null;
							cleaned++;
							LogStartupDiagnostic("[SanitizeHOnlyGhosts] Cleared H-only to visible: " + icon.FilePath);
						}
						else
						{
							LogStartupDiagnostic("[SanitizeHOnlyGhosts] ❌ UNRECOVERABLE: " + icon.FilePath + " — stuck H-only!");
						}
					}
				}
			}
		}
		if (cleaned > 0)
		{
			LogStartupDiagnostic($"[SanitizeHOnlyGhosts] Cleaned {cleaned} semi-transparent ghost icons");
		}
		return cleaned;
	}

	public void RefreshAllZoneIconSizes()
	{
		foreach (ZoneWindow item in _openZoneWindows.ToList())
		{
			item.RefreshIconSizes();
		}
	}

	public void RefreshAllMappingIconSizes()
	{
		foreach (MappingWindow item in _openMappingWindows.ToList())
		{
			item.RefreshAll();
		}
	}

	public void RefreshAllWindowStyles()
	{
		foreach (ZoneWindow win in _openZoneWindows.ToList())
		{
			win.Dispatcher.Invoke(delegate
			{
				win.RefreshVisualStyle();
			});
		}
		foreach (MappingWindow win2 in _openMappingWindows.ToList())
		{
			win2.Dispatcher.Invoke(delegate
			{
				win2.RefreshVisualStyle();
			});
		}
		_todoWindow?.Dispatcher.Invoke(delegate
		{
			_todoWindow.RefreshVisualStyle();
		});
	}

	public void RefreshAllZoneWindows()
	{
		foreach (ZoneWindow win in _openZoneWindows.ToList())
		{
			win.Dispatcher.Invoke(delegate
			{
				win.RefreshAllContent();
			});
		}
	}

	public void SetAllZoneWindowsSuppressWatcher(bool suppress)
	{
		foreach (ZoneWindow win in _openZoneWindows.ToList())
		{
			win.Dispatcher.Invoke(delegate
			{
				win.SetSuppressDesktopWatcher(suppress);
			});
		}
	}

	public void SaveZones()
	{
		if (App.IsRestoringBackup)
		{
			return;
		}
		_saveZonesCts?.Cancel();
		_saveZonesCts?.Dispose();
		_saveZonesCts = new CancellationTokenSource();
		CancellationToken token = _saveZonesCts.Token;
		Task.Delay(500, token).ContinueWith(delegate
		{
			if (!token.IsCancellationRequested)
			{
				Application.Current.Dispatcher.Invoke(delegate
				{
					if (!token.IsCancellationRequested)
					{
						_dataService.SaveZones(Zones.ToList());
					}
				});
			}
		}, TaskContinuationOptions.NotOnCanceled);
	}

	public void SaveZonesNow()
	{
		if (!App.IsRestoringBackup)
		{
			_saveZonesCts?.Cancel();
			_saveZonesCts?.Dispose();
			_saveZonesCts = null;
			_dataService.SaveZones(Zones.ToList());
			_dataService.SaveMappings(Mappings.ToList());
		}
	}

	public void SaveMappings()
	{
		if (!App.IsRestoringBackup)
		{
			_dataService.SaveMappings(Mappings.ToList());
		}
	}

	public void SaveTodos()
	{
		if (!App.IsRestoringBackup)
		{
			_dataService.SaveTodos(TodoItems.ToList());
		}
	}

	public void ResortTodosByDate(bool ascending)
	{
		List<TodoItem> ordered = (ascending ? (from t in TodoItems
			orderby t.IsCompleted ? 1 : 0, t.DateRef
			select t).ToList() : (from t in TodoItems
			orderby t.IsCompleted ? 1 : 0, t.DateRef descending
			select t).ToList());
		for (int i = 0; i < ordered.Count; i++)
		{
			ordered[i].SortOrder = i + 1;
		}
		try
		{
			SaveTodos();
		}
		catch
		{
		}
		this.TodoResorted?.Invoke(this, EventArgs.Empty);
	}

	public void SaveRecurringTasks()
	{
		if (!App.IsRestoringBackup)
		{
			_dataService.SaveRecurringTasks(RecurringTasks.ToList());
		}
	}

	public void SaveTodoWindowStateBeforeShutdown()
	{
		if (!App.IsRestoringBackup)
		{
			_todoWindow?.SaveWindowState();
			bool wasOpen = _todoWindow != null;
			_dataService.SaveTodoWindowOpen(wasOpen);
		}
	}

	public void SaveAllData()
	{
		SaveZonesNow();
		SaveTodos();
		SaveRecurringTasks();
		_dataService.SaveSettings(_settings);
		SaveTodoWindowStateBeforeShutdown();
	}

	public void ClearAllHiddenStates()
	{
		SaveZonesNow();
	}

	private void AddZombieForCleanup(ZoneIcon icon, IconGroup group)
	{
		if (_pendingZombies == null)
		{
			_pendingZombies = new List<(IconGroup, ZoneIcon)>();
		}
		_pendingZombies.Add((group, icon));
		LogStartupDiagnostic("[ReapplyHiddenAttributes] Marked zombie for cleanup: " + icon.FilePath);
	}

	private int FlushZombieCleanup()
	{
		if (_pendingZombies == null || _pendingZombies.Count == 0)
		{
			return 0;
		}
		int count = 0;
		foreach (var (group, icon) in _pendingZombies)
		{
			try
			{
				group.Icons.Remove(icon);
				count++;
			}
			catch (Exception ex)
			{
				LogStartupDiagnostic("[ReapplyHiddenAttributes] Failed to remove zombie: " + icon.FilePath + ", " + ex.Message);
			}
		}
		_pendingZombies.Clear();
		_pendingZombies = null;
		LogStartupDiagnostic($"[ReapplyHiddenAttributes] Flushed {count} zombie icons");
		return count;
	}
}
