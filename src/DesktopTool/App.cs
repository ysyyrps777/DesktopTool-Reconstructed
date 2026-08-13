using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopTool.Services;
using DesktopTool.ViewModels;
using DesktopTool.Views;

namespace DesktopTool;

public class App : System.Windows.Application
{
	private static readonly Mutex _mutex = CreateSingleInstanceMutex();

	private static readonly string CrashLogPath = Path.Combine(Path.GetTempPath(), "DesktopTool_Crash.log");

	// Do not call GetAppDataDir() here: AppDataDir is initialized later in the
	// generated field order and would still be null during the type initializer.
	private static readonly string StartupHistoryPath = Path.Combine(ResolveAppDataDir(), "debug_startup_history.log");

	private static readonly string DebugStartupLogPath = Path.Combine(ResolveAppDataDir(), "debug_startup.log");

	private MainWindow? _mainWindow;

	private MainViewModel? _mainViewModel;

	private TrayService? _trayService;

	private TodoSchedulerService? _todoScheduler;

	private BrowserFileDialogMonitor? _browserFileDialogMonitor;

	private HwndSource? _ipcWindow;

	public static volatile bool IsInternalDrag;

	public const string InternalDragFormat = "DesktopToolInternalDrag";

	private bool _isSystemShuttingDown;

	private bool _isSilentStartup;

	private const int WM_CLOSE = 16;

	private static readonly string AppDataDir = ResolveAppDataDir();

	private static readonly string UninstallingSentinel = Path.Combine(AppDataDir, ".uninstalling");

	private static readonly DateTime _appStartTimeUtc = DateTime.UtcNow;

	private const int StartupGraceSecondsForShellRefresh = 60;

	private bool _contentLoaded;

	public static bool IsRestoringBackup { get; set; } = false;

	private static Mutex CreateSingleInstanceMutex()
	{
		string[] array = new string[2] { "Global\\DesktopTool_Reconstructed_SingleInstanceMutex", "DesktopTool_Reconstructed_SingleInstanceMutex" };
		foreach (string name in array)
		{
			try
			{
				return new Mutex(initiallyOwned: false, name);
			}
			catch (UnauthorizedAccessException)
			{
			}
			catch (IOException)
			{
			}
		}
		return new Mutex(initiallyOwned: false, "DesktopTool_Reconstructed_SingleInstanceMutex");
	}

	public static void RestartApplication()
	{
		try
		{
			string exe = Process.GetCurrentProcess().MainModule?.FileName;
			if (string.IsNullOrWhiteSpace(exe))
			{
				System.Windows.MessageBox.Show("无法获取当前程序路径，请手动重启软件以应用恢复结果。", "重启失败", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
			else if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
			{
				System.Windows.Application.Current.Dispatcher.Invoke(delegate
				{
					RestartApplicationCore(exe);
				});
			}
			else
			{
				RestartApplicationCore(exe);
			}
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("自动重启失败：" + ex.Message + "\n请手动重启软件以应用恢复结果。", "重启失败", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private static void RestartApplicationCore(string exe)
	{
		while (true)
		{
			try
			{
				_mutex.ReleaseMutex();
			}
			catch
			{
				break;
			}
		}
		DesktopEmbedService.IsAppExiting = true;
		Process.Start(new ProcessStartInfo(exe)
		{
			UseShellExecute = true
		});
		System.Windows.Application.Current?.Shutdown();
	}

	private void CreateIpcWindow()
	{
		try
		{
			HwndSourceParameters hwndSourceParameters = new HwndSourceParameters("DesktopTool_IPC");
			hwndSourceParameters.Width = 0;
			hwndSourceParameters.Height = 0;
			hwndSourceParameters.WindowStyle = 0;
			hwndSourceParameters.HwndSourceHook = IpcWndProc;
			HwndSourceParameters ps = hwndSourceParameters;
			ps.SetPosition(0, 0);
			_ipcWindow = new HwndSource(ps);
			EmergencyLog("IPCWindow: created");
			try
			{
				if (_ipcWindow.Handle != IntPtr.Zero && NativeMethods.WTSRegisterSessionNotification(_ipcWindow.Handle, 0))
				{
					EmergencyLog("IPCWindow: WTSRegisterSessionNotification OK（锁屏/解锁监听已启用）");
				}
				else
				{
					EmergencyLog("IPCWindow: WTSRegisterSessionNotification 失败（锁屏恢复将不可用）");
				}
			}
			catch (Exception ex)
			{
				EmergencyLog("IPCWindow: WTSRegisterSessionNotification 异常: " + ex.Message);
			}
		}
		catch (Exception ex2)
		{
			EmergencyLog("IPCWindow create failed: " + ex2.Message);
		}
	}

	private nint IpcWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (msg == (int)NativeMethods.WM_DESKTOOL_SHOWMAIN)
		{
			TrayService.Instance?.ShowMainWindow();
			handled = true;
		}
		else if (msg == 689)
		{
			int evt = (int)wParam;
			EmergencyLog($"WTSSESSION_CHANGE: event={evt}");
			if (evt == 8 || evt == 5 || evt == 1 || evt == 3)
			{
				OnSessionResumeRecover(evt);
			}
			handled = true;
		}
		else if (msg == (int)NativeMethods.WM_TASKBARCREATED)
		{
			DesktopEmbedService.HandleTaskbarRecreated();
			handled = true;
		}
		else if (msg == 536)
		{
			int pbt = (int)wParam;
			EmergencyLog($"WM_POWERBROADCAST pbt=0x{pbt:X4}");
			if (pbt == 7 || pbt == 18)
			{
				EmergencyLog("休眠/睡眠恢复 → 重建桌面保护(复用 OnSessionResumeRecover)");
				OnSessionResumeRecover(pbt);
			}
			handled = true;
		}
		return IntPtr.Zero;
	}

	private void OnSessionResumeRecover(int evt)
	{
		RecoverOnce("0s");
		int[] array = new int[3] { 1, 3, 6 };
		foreach (int sec in array)
		{
			int captured = sec;
			Task.Delay(TimeSpan.FromSeconds(captured)).ContinueWith(delegate
			{
				Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher;
				if (dispatcher != null)
				{
					dispatcher.BeginInvoke((Action)delegate
					{
						RecoverOnce($"{captured}s");
					});
				}
				else
				{
					RecoverOnce($"{captured}s");
				}
			}, TaskScheduler.Default);
		}
		void RecoverOnce(string phase)
		{
			try
			{
				DesktopEmbedService.ResetDesktopOwnerCache();
				int restored = DesktopEmbedService.ReapplyAllProtectedWindows();
				EmergencyLog($"SessionResume[{phase}] event={evt}: 已重建桌面保护（恢复所有者 {restored} 个）");
			}
			catch (Exception ex)
			{
				EmergencyLog("SessionResume[" + phase + "] 恢复异常: " + ex.Message);
			}
		}
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		try
		{
			if (File.Exists(CrashLogPath))
			{
				string archived = CrashLogPath + $".prev_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
				File.Move(CrashLogPath, archived);
			}
		}
		catch
		{
		}
		EmergencyLog("========== 启动尝试 ==========");
		EmergencyLog("APP_ENTER_OnStartup");
		try
		{
			string ad = AppDataDir;
			StringBuilder sb = new StringBuilder();
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(25, 1, stringBuilder);
			handler.AppendLiteral("[DataDirDiag] AppDataDir=");
			handler.AppendFormatted(ad);
			stringBuilder2.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(19, 1, stringBuilder);
			handler.AppendLiteral("[DataDirDiag] 目录存在=");
			handler.AppendFormatted(Directory.Exists(ad));
			stringBuilder3.AppendLine(ref handler);
			if (Directory.Exists(ad))
			{
				string[] fileSystemEntries = Directory.GetFileSystemEntries(ad);
				foreach (string f in fileSystemEntries)
				{
					stringBuilder = sb;
					StringBuilder stringBuilder4 = stringBuilder;
					handler = new StringBuilder.AppendInterpolatedStringHandler(19, 1, stringBuilder);
					handler.AppendLiteral("[DataDirDiag]   条目=");
					handler.AppendFormatted(Path.GetFileName(f));
					stringBuilder4.AppendLine(ref handler);
				}
				fileSystemEntries = new string[7] { "zones.json", "todos.json", "settings.json", "recovery_manifest.json", "backups", "hidden", "mappings" };
				foreach (string key in fileSystemEntries)
				{
					string p = Path.Combine(ad, key);
					if (File.Exists(p))
					{
						stringBuilder = sb;
						StringBuilder stringBuilder5 = stringBuilder;
						handler = new StringBuilder.AppendInterpolatedStringHandler(26, 2, stringBuilder);
						handler.AppendLiteral("[DataDirDiag]   >>> ");
						handler.AppendFormatted(key);
						handler.AppendLiteral(" 文件大小=");
						handler.AppendFormatted(new FileInfo(p).Length);
						stringBuilder5.AppendLine(ref handler);
					}
					else if (Directory.Exists(p))
					{
						stringBuilder = sb;
						StringBuilder stringBuilder6 = stringBuilder;
						handler = new StringBuilder.AppendInterpolatedStringHandler(28, 2, stringBuilder);
						handler.AppendLiteral("[DataDirDiag]   >>> ");
						handler.AppendFormatted(key);
						handler.AppendLiteral(" 目录 子项数=");
						handler.AppendFormatted(Directory.GetFileSystemEntries(p).Length);
						stringBuilder6.AppendLine(ref handler);
					}
					else
					{
						stringBuilder = sb;
						StringBuilder stringBuilder7 = stringBuilder;
						handler = new StringBuilder.AppendInterpolatedStringHandler(23, 1, stringBuilder);
						handler.AppendLiteral("[DataDirDiag]   >>> ");
						handler.AppendFormatted(key);
						handler.AppendLiteral(" 缺失");
						stringBuilder7.AppendLine(ref handler);
					}
				}
			}
			LogHelper.Append(DebugStartupLogPath, sb.ToString());
		}
		catch (Exception ex)
		{
			EmergencyLog("[DataDirDiag] 异常: " + ex.Message);
		}
		try
		{
			string appDir = Path.GetDirectoryName(Environment.ProcessPath);
			if (!string.IsNullOrEmpty(appDir) && Directory.Exists(appDir))
			{
				Environment.CurrentDirectory = appDir;
			}
		}
		catch
		{
		}
		NativeCrashTracer.Install();
		UiHangWatchdog.Start();
		base.DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
		try
		{
			System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
			System.Windows.Forms.Application.ThreadException += OnWinFormsThreadException;
			EmergencyLog("WinForms ThreadException 处理器已安装（不再弹系统异常框）");
		}
		catch (Exception ex2)
		{
			EmergencyLog("WinForms ThreadException 处理器安装失败: " + ex2.Message);
		}
		AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
		try
		{
			EmergencyLog("BEFORE_BaseOnStartup");
			base.OnStartup(e);
			EmergencyLog("AFTER_BaseOnStartup");
			WarmUpWpfVisualPipeline();
			if (ElevationHelper.EnsureNormalIntegrity(e.Args, EmergencyLog))
			{
				EmergencyLog("DE_ELEVATE_RelaunchedAsMediumIntegrity");
				Shutdown(0);
				return;
			}
			EmergencyLog("BEFORE_MutexCheck");
			try
			{
				NativeMethods.RegisterDesktopToolMessage();
			}
			catch
			{
			}
			try
			{
				if (!_mutex.WaitOne(0, exitContext: false))
				{
					EmergencyLog("MutexCheck_AnotherInstance");
					try
					{
						Process current = Process.GetCurrentProcess();
						Process other = null;
						Process[] processesByName = Process.GetProcessesByName(current.ProcessName);
						foreach (Process proc in processesByName)
						{
							if (proc.Id != current.Id)
							{
								other = proc;
								break;
							}
						}
						if (other != null)
						{
							bool delivered = false;
							for (int j = 0; j < 15; j++)
							{
								if (delivered)
								{
									break;
								}
								try
								{
									delivered = NativeMethods.SendShowMainMessageToProcess(other.Id);
								}
								catch
								{
								}
								if (!delivered)
								{
									Thread.Sleep(100);
								}
							}
						}
					}
					catch
					{
					}
					Shutdown(0);
					return;
				}
				EmergencyLog("AFTER_MutexCheck");
			}
			catch (AbandonedMutexException)
			{
				EmergencyLog("MutexCheck_AbandonedTakenOver");
			}
			CreateIpcWindow();
			_isSilentStartup = DetectSilentStartup(e.Args);
			base.ShutdownMode = ShutdownMode.OnExplicitShutdown;
			EmergencyLog("Step1: Creating MainWindow...");
			_mainWindow = new MainWindow();
			EmergencyLog("Step1: MainWindow created OK");
			_mainViewModel = _mainWindow.ViewModel;
			EmergencyLog("Step2: ViewModel retrieved OK");
			EmergencyLog("Step3: Creating TrayService...");
			_trayService = new TrayService(_mainViewModel, _mainWindow, _mainViewModel.DataService);
			_trayService.IsSilentStartup = _isSilentStartup;
			EmergencyLog($"Step3: TrayService created OK (SilentStartup={_isSilentStartup})");
			EmergencyLog("Step4: Initializing TrayService (deferred to Loaded)...");
			try
			{
				System.Windows.Application.Current.Dispatcher.BeginInvoke((Action)delegate
				{
					try
					{
						_trayService?.Initialize();
						EmergencyLog("Step4: TrayService initialized OK");
					}
					catch (Exception ex9)
					{
						EmergencyLog("Step4: TrayService FAILED (app continues without tray): " + ex9.GetType().Name + ": " + ex9.Message);
						DiagnosticLog(ex9.ToString());
					}
				}, DispatcherPriority.Loaded);
			}
			catch (Exception ex4)
			{
				EmergencyLog("Step4: TrayService FAILED (app continues without tray): " + ex4.GetType().Name + ": " + ex4.Message);
				DiagnosticLog(ex4.ToString());
			}
			EmergencyLog("STARTUP_SUCCESS");
			try
			{
				if (_mainViewModel != null && _trayService != null)
				{
					ToastNotifier.OnActivate = delegate
					{
						try
						{
							_mainViewModel.RestoreTodoWindow();
						}
						catch
						{
						}
					};
					_todoScheduler = new TodoSchedulerService(_mainViewModel.TodoItems, _mainViewModel, _trayService);
					_todoScheduler.Start();
				}
			}
			catch (Exception ex5)
			{
				EmergencyLog("TodoScheduler start failed: " + ex5.Message);
			}
			try
			{
				DesktopIconService.EnsureDesktopAutoRefresh();
			}
			catch
			{
			}
			try
			{
				_browserFileDialogMonitor = new BrowserFileDialogMonitor();
				_browserFileDialogMonitor.VisibilityChanged += delegate(bool _)
				{
					_mainWindow?.SetTemporaryHiddenVisibilityUiState(DesktopIconService.IsExplorerShowingHiddenFiles());
				};
				_browserFileDialogMonitor.Start();
			}
			catch (Exception ex)
			{
				EmergencyLog("BrowserFileDialogMonitor start failed: " + ex.Message);
			}
			try
			{
				IconHelper.InitIconCacheMaintenance();
			}
			catch
			{
			}
			try
			{
				DataService.InitBackupMaintenance();
			}
			catch
			{
			}
			try
			{
				DispatcherTimer dispatcherTimer = new DispatcherTimer(DispatcherPriority.Background, System.Windows.Application.Current?.Dispatcher ?? base.Dispatcher)
				{
					Interval = TimeSpan.FromSeconds(3.0)
				};
				dispatcherTimer.Tick += delegate
				{
					dispatcherTimer.Stop();
					try
					{
						_mainWindow?.ReRegisterSearchHotkey();
					}
					catch
					{
					}
				};
				dispatcherTimer.Start();
			}
			catch
			{
			}
			EmergencyLog("Step5: CrashRecovery detect...");
			try
			{
				if (CrashRecoveryService.DetectAbnormalExit(_mainViewModel))
				{
					EmergencyLog("CrashRecovery: 检测到上次异常退出，将在后台恢复图标");
					CrashRecoveryService.RecoveryTask = Task.Run(delegate
					{
						try
						{
							int value = CrashRecoveryService.PerformRecovery(_mainViewModel);
							EmergencyLog($"CrashRecovery: 后台恢复完成，恢复了 {value} 个文件");
						}
						catch (Exception ex9)
						{
							EmergencyLog("CrashRecovery background recovery failed: " + ex9.Message);
						}
					});
				}
			}
			catch (Exception ex6)
			{
				EmergencyLog("CrashRecovery detect failed: " + ex6.Message);
			}
			EmergencyLog("Step5: CrashRecovery detect done");
			// Safety change for the reconstructed build: do not silently close Explorer
			// windows or mutate the privately installed build's startup/task entries.
			// User-requested startup changes remain available from the settings UI.
			RefreshShellIcon();
			try
			{
				System.Windows.Application.Current.SessionEnding += OnSessionEnding;
			}
			catch (Exception ex7)
			{
				EmergencyLog("SessionEnding register failed: " + ex7.Message);
			}
			EmergencyLog("========== 启动成功 ==========");
		}
		catch (Exception ex8)
		{
			EmergencyLog("CRASH_OnStartup: " + ex8.GetType().Name + ": " + ex8.Message);
			LogCrash("OnStartup", ex8);
			try
			{
				System.Windows.MessageBox.Show($"应用启动失败：\n{ex8.Message}\n\n详细信息已写入：\n{CrashLogPath}\n\n请将此文件发送给开发者以协助排查。", "桌面工具箱 - 启动错误", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
			catch
			{
			}
			Shutdown(1);
		}
	}

	private static void BootDiagLog(string message)
	{
		try
		{
			LogHelper.Append(DebugStartupLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	private static void EmergencyLog(string message)
	{
		string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
		try
		{
			LogHelper.Append(CrashLogPath, line + Environment.NewLine);
		}
		catch
		{
			try
			{
				EventLog.WriteEntry("DesktopTool", line, EventLogEntryType.Information, 1001);
			}
			catch
			{
			}
		}
		LogHelper.Append(StartupHistoryPath, line + Environment.NewLine);
	}

	private static void DiagnosticLog(string message)
	{
		EmergencyLog("[DIAG] " + message);
	}

	private static bool DetectSilentStartup(string[] args)
	{
		if (args != null && args.Any((string a) => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)))
		{
			EmergencyLog("[SilentStartup] 检测到 --silent 命令行参数 → 静默启动");
			return true;
		}
		try
		{
			TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
			if (uptime.TotalMinutes < 3.0)
			{
				EmergencyLog($"[SilentStartup] 系统运行时间={uptime.TotalMinutes:F1}min < 3min → 判定为开机自动启动（静默模式）");
				return true;
			}
		}
		catch (Exception ex)
		{
			EmergencyLog("[SilentStartup] 系统运行时间检测失败: " + ex.Message + "，保守按非静默处理");
		}
		return false;
	}

	private static void LogSystemBootDiagnostics()
	{
		try
		{
			TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
			EmergencyLog($"[BootDiag] SystemUptime={uptime.TotalMinutes:F1}min ({uptime.TotalHours:F1}h)");
			try
			{
				Process[] explorers = Process.GetProcessesByName("explorer");
				Process[] array = explorers;
				foreach (Process proc in array)
				{
					try
					{
						TimeSpan explorerAge = DateTime.Now - proc.StartTime;
						EmergencyLog($"[BootDiag] Explorer PID={proc.Id} Age={explorerAge.TotalMinutes:F1}min StartedAt={proc.StartTime:HH:mm:ss.fff} MainWindow=0x{proc.MainWindowHandle:X8}");
					}
					catch (Exception ex)
					{
						EmergencyLog($"[BootDiag] Explorer PID={proc.Id} (无法获取详情: {ex.Message})");
					}
				}
				if (explorers.Length == 0)
				{
					EmergencyLog("[BootDiag] ⚠ 未找到 explorer.exe 进程！");
				}
			}
			catch (Exception ex2)
			{
				EmergencyLog("[BootDiag] Explorer查询失败: " + ex2.Message);
			}
			try
			{
				EmergencyLog($"[BootDiag] OS={Environment.OSVersion} 64bit={Environment.Is64BitOperatingSystem} ProcessorCount={Environment.ProcessorCount}");
			}
			catch
			{
			}
			try
			{
				EnumerateAndCloseDesktopExplorerWindows("启动0s复检");
			}
			catch
			{
			}
			int[] array2 = new int[4] { 15, 30, 45, 60 };
			foreach (int sec in array2)
			{
				try
				{
					int captured = sec;
					Task.Delay(TimeSpan.FromSeconds(captured)).ContinueWith(delegate
					{
						try
						{
							EnumerateAndCloseDesktopExplorerWindows($"延迟{captured}s复检");
						}
						catch
						{
						}
					}, TaskScheduler.Default);
				}
				catch
				{
				}
			}
			try
			{
				StartupService.LogScheduledTaskDiagnostics();
			}
			catch
			{
			}
			try
			{
				bool autoStart = StartupService.IsAutoStartEnabled();
				(bool, bool, string, string) diag = StartupService.GetAutoStartDiagnostics();
				EmergencyLog($"[BootDiag] AutoStart: Enabled={autoStart} Shortcut={diag.Item1} RegKey={diag.Item2}");
				if (diag.Item1)
				{
					EmergencyLog("[BootDiag] StartupShortcut: " + diag.Item3);
				}
				if (diag.Item2)
				{
					EmergencyLog("[BootDiag] RegistryRun: " + diag.Item4);
				}
				string approvedDiag = StartupService.GetStartupApprovedDiagnostics();
				if (!string.IsNullOrEmpty(approvedDiag))
				{
					EmergencyLog("[BootDiag] StartupApproved: " + approvedDiag);
					LogHelper.Append(DebugStartupLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [BootDiag] StartupApproved: {approvedDiag}{Environment.NewLine}");
				}
				StartupService.LogAllStartupItems();
			}
			catch
			{
			}
		}
		catch (Exception ex3)
		{
			EmergencyLog("[BootDiag] ERROR: " + ex3.Message);
		}
	}

	private static void EnumerateAndCloseDesktopExplorerWindows(string source = "未指定")
	{
		try
		{
			string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
			BootDiagLog("[BootDiag][" + source + "] 桌面路径=" + desktopPath);
			try
			{
				HashSet<uint> explorerPids = new HashSet<uint>();
				Process[] processesByName = Process.GetProcessesByName("explorer");
				foreach (Process p in processesByName)
				{
					explorerPids.Add((uint)p.Id);
				}
				BootDiagLog($"[BootDiag][{source}] 诊断: 发现 {explorerPids.Count} 个 explorer 进程");
				NativeMethods.EnumWindows(delegate(nint num2, nint _)
				{
					try
					{
						NativeMethods.GetWindowThreadProcessId(num2, out var lpdwProcessId);
						if (lpdwProcessId == 0 || !explorerPids.Contains(lpdwProcessId))
						{
							return true;
						}
						StringBuilder stringBuilder = new StringBuilder(256);
						NativeMethods.GetClassName(num2, stringBuilder, stringBuilder.Capacity);
						StringBuilder stringBuilder2 = new StringBuilder(256);
						NativeMethods.GetWindowTextW(num2, stringBuilder2, stringBuilder2.Capacity);
						bool value = NativeMethods.IsWindowVisible(num2);
						BootDiagLog($"[BootDiag][{source}] [Explorer窗] PID={lpdwProcessId} 类=\"{stringBuilder}\" 标题=\"{stringBuilder2}\" 可见={value} HWND=0x{num2:X}");
					}
					catch
					{
					}
					return true;
				}, IntPtr.Zero);
			}
			catch (Exception ex)
			{
				BootDiagLog("[BootDiag][" + source + "] 诊断枚举异常: " + ex.Message);
			}
			Type shellAppType = Type.GetTypeFromProgID("Shell.Application");
			if (shellAppType == null)
			{
				BootDiagLog("[BootDiag][" + source + "] Shell.Application ProgID 不可用，跳过 Shell 路径匹配");
				return;
			}
			dynamic shell = Activator.CreateInstance(shellAppType);
			dynamic windows = shell.Windows();
			int totalCount = windows.Count;
			BootDiagLog($"[BootDiag][{source}] ShellWindows: 共 {totalCount} 个 Shell 窗口");
			int closedCount = 0;
			for (int i2 = 0; i2 < totalCount; i2++)
			{
				try
				{
					dynamic win = windows.Item(i2);
					string locationUrl = (win.LocationURL ?? "").ToString();
					string locationName = (win.LocationName ?? "").ToString();
					if (string.IsNullOrEmpty(locationUrl) && string.IsNullOrEmpty(locationName))
					{
						continue;
					}
					bool isDesktopFolder = false;
					if (locationUrl.StartsWith("file:///"))
					{
						isDesktopFolder = string.Equals(Uri.UnescapeDataString(locationUrl.Replace("file:///", "")).Replace('/', '\\').TrimEnd('\\'), desktopPath, StringComparison.OrdinalIgnoreCase);
					}
					int hwnd = 0;
					try
					{
						hwnd = (int)win.HWND;
					}
					catch
					{
					}
					string cls = "";
					try
					{
						StringBuilder clsSb = new StringBuilder(256);
						NativeMethods.GetClassName(hwnd, clsSb, clsSb.Capacity);
						cls = clsSb.ToString();
					}
					catch
					{
					}
					bool isDesktopByTitle = (cls == "CabinetWClass" || cls == "ExploreWClass") && string.Equals(locationName.Trim(), "桌面", StringComparison.OrdinalIgnoreCase);
					bool num = isDesktopFolder || isDesktopByTitle;
					BootDiagLog($"[BootDiag][{source}] ShellWindows[{i2}]: 类=\"{cls}\" 名称=\"{locationName}\" URL=\"{locationUrl}\" IsDesktop={isDesktopFolder} 标题命中={isDesktopByTitle}");
					if (!num)
					{
						continue;
					}
					string procInfo = "未知";
					try
					{
						if (hwnd != 0)
						{
							NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
							if (pid != 0)
							{
								Process p2 = Process.GetProcessById((int)pid);
								procInfo = $"PID={pid} 进程={p2.ProcessName}";
							}
						}
					}
					catch
					{
					}
					BootDiagLog($"[BootDiag][{source}] ★★★ 检测到桌面文件夹窗口被意外打开！正在关闭… ({procInfo})");
					bool closed = false;
					if (hwnd != 0)
					{
						try
						{
							NativeMethods.PostMessage(hwnd, 16u, IntPtr.Zero, IntPtr.Zero);
							closed = true;
							BootDiagLog($"[BootDiag][{source}] 已投递 WM_CLOSE 到 HWND=0x{hwnd:X}");
						}
						catch (Exception ex2)
						{
							BootDiagLog("[BootDiag][" + source + "] PostMessage 关闭失败: " + ex2.Message);
						}
					}
					if (!closed)
					{
						try
						{
							win.Quit();
							closed = true;
						}
						catch (Exception ex3)
						{
							BootDiagLog("[BootDiag][" + source + "] Shell.Quit 关闭失败: " + ex3.Message);
						}
					}
					if (closed)
					{
						closedCount++;
						BootDiagLog($"[BootDiag][{source}] 桌面文件夹窗口已关闭 (#{closedCount}) ({procInfo})");
					}
				}
				catch (Exception ex4)
				{
					BootDiagLog($"[BootDiag][{source}] ShellWindows[{i2}] 枚举异常: {ex4.Message}");
				}
			}
			if (closedCount > 0)
			{
				BootDiagLog($"[BootDiag][{source}] ★ 共关闭 {closedCount} 个桌面文件夹窗口");
			}
			else
			{
				BootDiagLog("[BootDiag][" + source + "] ShellWindows: 未发现桌面文件夹窗口（正常）");
			}
		}
		catch (Exception ex5)
		{
			BootDiagLog("[BootDiag][" + source + "] ShellWindows 枚举失败: " + ex5.Message);
		}
	}

	private static void WarmUpWpfVisualPipeline()
	{
		for (int attempt = 1; attempt <= 2; attempt++)
		{
			try
			{
				System.Windows.Controls.ToolTip toolTip = new System.Windows.Controls.ToolTip();
				toolTip.Content = string.Empty;
				toolTip.Placement = PlacementMode.Absolute;
				toolTip.HorizontalOffset = -32000.0;
				toolTip.VerticalOffset = -32000.0;
				toolTip.IsOpen = true;
				toolTip.IsOpen = false;
				EmergencyLog($"WarmUp: WPF 渲染管线预热成功(第{attempt}次)");
				break;
			}
			catch (Exception ex)
			{
				EmergencyLog($"WarmUp: 预热失败(第{attempt}次) {ex.GetType().Name}: {ex.Message}");
			}
		}
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		EmergencyLog("DispatcherException: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
		LogCrash("UI线程异常", e.Exception);
		e.Handled = true;
	}

	[HandleProcessCorruptedStateExceptions]
	[SecurityCritical]
	private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception ex)
		{
			EmergencyLog("AppDomainException: " + ex.GetType().Name + ": " + ex.Message);
			LogCrash("未处理异常(AppDomain)", ex);
		}
	}

	private void OnWinFormsThreadException(object? sender, ThreadExceptionEventArgs e)
	{
		EmergencyLog("WinFormsThreadException: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
		LogCrash("WinForms线程异常(已拦截弹框)", e.Exception);
	}

	private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
		EmergencyLog("UnobservedTaskException: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
		LogCrash("未观察到的Task异常", e.Exception);
		e.SetObserved();
	}

	private static void LogCrash(string context, Exception ex)
	{
		try
		{
			StringBuilder sb = new StringBuilder();
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(25, 2, stringBuilder);
			handler.AppendLiteral("========== [");
			handler.AppendFormatted(DateTime.Now, "yyyy-MM-dd HH:mm:ss");
			handler.AppendLiteral("] ");
			handler.AppendFormatted(context);
			handler.AppendLiteral(" ==========");
			stringBuilder2.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder);
			handler.AppendLiteral("Message: ");
			handler.AppendFormatted(ex.Message);
			stringBuilder3.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder4 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(6, 1, stringBuilder);
			handler.AppendLiteral("Type: ");
			handler.AppendFormatted(ex.GetType().FullName);
			stringBuilder4.AppendLine(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder5 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
			handler.AppendLiteral("Stack: ");
			handler.AppendFormatted(ex.StackTrace);
			stringBuilder5.AppendLine(ref handler);
			if (ex.InnerException != null)
			{
				stringBuilder = sb;
				StringBuilder stringBuilder6 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder);
				handler.AppendLiteral("Inner: ");
				handler.AppendFormatted(ex.InnerException.Message);
				stringBuilder6.AppendLine(ref handler);
				stringBuilder = sb;
				StringBuilder stringBuilder7 = stringBuilder;
				handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder);
				handler.AppendLiteral("Inner Stack: ");
				handler.AppendFormatted(ex.InnerException.StackTrace);
				stringBuilder7.AppendLine(ref handler);
			}
			sb.AppendLine();
			LogHelper.Append(CrashLogPath, sb.ToString());
		}
		catch
		{
		}
	}

	private static string ResolveAppDataDir()
	{
		bool portable = false;
		try
		{
			if (File.Exists(Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "portable.txt")))
			{
				portable = true;
			}
		}
		catch
		{
		}
		if (portable)
		{
			string dataDir = Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "Data");
			try
			{
				Directory.CreateDirectory(dataDir);
			}
			catch
			{
			}
			return dataDir;
		}
		// Keep reconstructed-build state completely separate from the installed app.
		// This prevents a test run from changing its settings, history or recovery data.
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopTool-Reconstructed");
	}

	public static string GetAppDataDir()
	{
		return AppDataDir;
	}

	protected override void OnExit(ExitEventArgs e)
	{
		DesktopEmbedService.IsAppExiting = true;
		try
		{
			_browserFileDialogMonitor?.Dispose();
			_browserFileDialogMonitor = null;
		}
		catch (Exception ex)
		{
			EmergencyLog("BrowserFileDialogMonitor dispose failed: " + ex.Message);
		}
		if (_isSystemShuttingDown)
		{
			EmergencyLog("OnExit: 系统正在关机/重启，跳过图标恢复");
			try
			{
				_trayService?.Dispose();
			}
			catch
			{
			}
			try
			{
				_todoScheduler?.Dispose();
			}
			catch
			{
			}
			CrashRecoveryService.MarkCleanExit();
			base.OnExit(e);
			return;
		}
		if (File.Exists(UninstallingSentinel))
		{
			EmergencyLog("OnExit: 检测到 .uninstalling 哨兵，卸载器正在运行，跳过图标恢复");
			try
			{
				_trayService?.Dispose();
			}
			catch
			{
			}
			try
			{
				_todoScheduler?.Dispose();
			}
			catch
			{
			}
			CrashRecoveryService.MarkCleanExit();
			base.OnExit(e);
			return;
		}
		try
		{
			DesktopIconService.RestoreTempShowHiddenIfActiveOnExit();
		}
		catch (Exception ex)
		{
			EmergencyLog("OnExit_TempShowRestoreFailed: " + ex.GetType().Name + ": " + ex.Message);
		}
		if (!DesktopEmbedService.IsAppExiting && !IsRestoringBackup)
		{
			try
			{
				_mainViewModel?.SaveZonesNow();
			}
			catch (Exception ex2)
			{
				EmergencyLog("OnExit_SaveFailed: " + ex2.GetType().Name + ": " + ex2.Message);
			}
			try
			{
				_mainViewModel?.SaveTodoWindowStateBeforeShutdown();
			}
			catch (Exception ex3)
			{
				EmergencyLog("OnExit_SaveTodoWindowStateFailed: " + ex3.Message);
			}
		}
		else
		{
			EmergencyLog("OnExit: IsAppExiting=true，状态已在 Shutdown 前保存，跳过重复保存");
		}
		try
		{
			_trayService?.Dispose();
		}
		catch (Exception ex4)
		{
			EmergencyLog("OnExit_TrayDisposeFailed: " + ex4.GetType().Name + ": " + ex4.Message);
		}
		try
		{
			_todoScheduler?.Dispose();
		}
		catch (Exception ex5)
		{
			EmergencyLog("OnExit_TodoSchedulerDisposeFailed: " + ex5.Message);
		}
		try
		{
			nint? num = _ipcWindow?.Handle;
			if (num.HasValue)
			{
				nint h = num.GetValueOrDefault();
				if (h != IntPtr.Zero)
				{
					NativeMethods.WTSUnRegisterSessionNotification(h);
				}
			}
		}
		catch (Exception ex6)
		{
			EmergencyLog("OnExit_WTSUnRegisterFailed: " + ex6.Message);
		}
		CrashRecoveryService.MarkCleanExit();
		EmergencyLog("========== 正常退出 ==========");
		base.OnExit(e);
	}

	private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
	{
		DesktopEmbedService.IsAppExiting = true;
		EmergencyLog("SESSION_ENDING: 系统正在关机/重启 — 跳过图标恢复，保存窗口位置…");
		_isSystemShuttingDown = true;
		try
		{
			_mainViewModel?.SaveZonesNow();
			_mainViewModel?.SaveTodos();
			try
			{
				_mainViewModel?.SaveTodoWindowStateBeforeShutdown();
			}
			catch (Exception ex)
			{
				EmergencyLog("SESSION_ENDING SaveTodoWindowState failed: " + ex.Message);
			}
		}
		catch (Exception ex2)
		{
			EmergencyLog("SessionEnding save failed: " + ex2.GetType().Name + ": " + ex2.Message);
		}
		finally
		{
			CrashRecoveryService.MarkCleanExit();
		}
		EmergencyLog("SESSION_ENDING: 状态已保存，锁文件已清除 — 图标保持隐藏");
	}

	private void OnProcessExit(object? sender, EventArgs e)
	{
		try
		{
			if (_isSystemShuttingDown)
			{
				EmergencyLog("PROCESS_EXIT: 系统关机中，跳过图标恢复");
				CrashRecoveryService.MarkCleanExit();
				return;
			}
			if (File.Exists(UninstallingSentinel))
			{
				EmergencyLog("PROCESS_EXIT: 检测到 .uninstalling 哨兵，卸载器负责恢复，跳过");
				CrashRecoveryService.MarkCleanExit();
				return;
			}
			EmergencyLog("PROCESS_EXIT: 进程终止 — 跳过图标恢复（由 OnExit / CrashRecovery 兜底）");
			if (!DesktopEmbedService.IsAppExiting)
			{
				try
				{
					_mainViewModel?.SaveZonesNow();
				}
				catch (Exception ex)
				{
					EmergencyLog("PROCESS_EXIT SaveZones failed: " + ex.GetType().Name + ": " + ex.Message);
				}
			}
			CrashRecoveryService.MarkCleanExit();
		}
		catch (Exception ex2)
		{
			EmergencyLog("PROCESS_EXIT failed: " + ex2.GetType().Name + ": " + ex2.Message);
		}
	}

	private static void RefreshShellIcon()
	{
		try
		{
			string exePath = Environment.ProcessPath;
			if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
			{
				BootDiagLog("RefreshShellIcon: 无法获取 EXE 路径，跳过");
				return;
			}
			EmergencyLog("RefreshShellIcon: 刷新 " + exePath + " 的图标缓存");
			nint pathPtr = Marshal.StringToCoTaskMemUni(exePath);
			try
			{
				NativeMethods.SHChangeNotify(8192u, 5u, pathPtr, 0);
			}
			finally
			{
				Marshal.FreeCoTaskMem(pathPtr);
			}
			Task.Run(async delegate
			{
				try
				{
					await Task.Delay(500);
					double elapsed = (DateTime.UtcNow - _appStartTimeUtc).TotalSeconds;
					if (elapsed < 60.0)
					{
						EmergencyLog($"RefreshShellIcon: 跳过全局 SHCNE_ASSOCCHANGED（启动宽限期，剩余 {60.0 - elapsed:F0}s，避免开机弹桌面文件夹）");
					}
					else
					{
						NativeMethods.SHChangeNotify(134217728u, 0u, 0, 0);
						BootDiagLog("RefreshShellIcon: SHCNE_ASSOCCHANGED 已发送");
					}
				}
				catch (Exception ex2)
				{
					EmergencyLog("RefreshShellIcon: 延迟刷新失败: " + ex2.Message);
				}
			});
			BootDiagLog("RefreshShellIcon: 图标刷新已调度 ✓");
		}
		catch (Exception ex)
		{
			EmergencyLog("RefreshShellIcon: 异常（不影响主流程）: " + ex.Message);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/app.xaml", UriKind.Relative);
			System.Windows.Application.LoadComponent(this, resourceLocater);
		}
	}

	[STAThread]
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public static void Main()
	{
		App app = new App();
		app.InitializeComponent();
		app.Run();
	}
}
