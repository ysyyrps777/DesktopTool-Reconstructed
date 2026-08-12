using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Timers;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopTool.ViewModels;
using DesktopTool.Views;

namespace DesktopTool.Services;

public class TrayService : IDisposable
{
	private readonly MainViewModel _mainVm;

	private readonly Window _mainWindow;

	private readonly DataService _dataService;

	private NotifyIcon? _notifyIcon;

	private volatile bool _isExiting;

	private System.Timers.Timer? _exitRequestTimer;

	public bool IsSilentStartup { get; set; }

	public static TrayService? Instance { get; private set; }

	private static string ExitRequestPath => Path.Combine(App.GetAppDataDir(), ".exit_request");

	public TrayService(MainViewModel mainVm, Window mainWindow, DataService dataService)
	{
		Instance = this;
		_mainVm = mainVm;
		_mainWindow = mainWindow;
		_dataService = dataService;
		_mainWindow.Closing += OnMainWindowClosing;
	}

	public void Initialize()
	{
		try
		{
			if (!ProbeSystemDrawingAvailable())
			{
				LogError("TrayService 预探测失败：System.Drawing 不可用", null);
				_notifyIcon = null;
				try
				{
					StartExitRequestMonitor();
					return;
				}
				catch
				{
					return;
				}
			}
			InitializeInternal();
		}
		catch (Exception ex)
		{
			LogError("TrayService.Initialize 整体失败（托盘图标不可用）", ex);
			_notifyIcon = null;
			try
			{
				StartExitRequestMonitor();
			}
			catch
			{
			}
		}
	}

	private static bool ProbeSystemDrawingAvailable()
	{
		try
		{
			typeof(Icon).Assembly.GetExportedTypes();
			return true;
		}
		catch
		{
			return false;
		}
	}

	private void InitializeInternal()
	{
		Icon trayIcon;
		try
		{
			trayIcon = CreateDefaultIcon();
		}
		catch (Exception ex)
		{
			LogError("托盘图标 - 第1层(提取exe图标) 失败", ex);
			try
			{
				using Bitmap fallbackBmp = new Bitmap(16, 16);
				for (int x = 0; x < 16; x++)
				{
					for (int y = 0; y < 16; y++)
					{
						fallbackBmp.SetPixel(x, y, Color.FromArgb(24, 118, 210));
					}
				}
				trayIcon = Icon.FromHandle(fallbackBmp.GetHicon());
			}
			catch
			{
				try
				{
					trayIcon = SystemIcons.Application;
					goto end_IL_0075;
				}
				catch
				{
					trayIcon = CreateEmbeddedFallbackIcon();
					goto end_IL_0075;
				}
				end_IL_0075:;
			}
		}
		_notifyIcon = new NotifyIcon
		{
			Icon = trayIcon,
			Text = "桌面工具箱",
			Visible = true
		};
		StartExitRequestMonitor();
		BuildTrayMenuAsync(_notifyIcon);
		if (_notifyIcon != null)
		{
			_notifyIcon.MouseDoubleClick += delegate(object? s, MouseEventArgs e)
			{
				if (e.Button == MouseButtons.Left)
				{
					ShowMainWindow();
				}
			};
		}
		bool isFirstLaunch = _mainVm.Zones.Count == 0 && _mainVm.TodoItems.Count == 0;
		if (IsSilentStartup)
		{
			_mainWindow.Opacity = 0.0;
		}
		else if (isFirstLaunch)
		{
			_mainWindow.Opacity = 1.0;
			_mainWindow.Loaded += delegate
			{
				_mainWindow.ShowInTaskbar = true;
				_mainWindow.Activate();
			};
			_mainWindow.Show();
			AppPanelTracker.Register(new WindowInteropHelper(_mainWindow).Handle);
		}
		else
		{
			_mainWindow.Opacity = 0.0;
		}
		WinDHookService.Start();
	}

	private void BuildTrayMenuAsync(NotifyIcon? notifyIcon)
	{
		if (notifyIcon == null)
		{
			return;
		}
		Dispatcher uiDispatcher = _mainWindow.Dispatcher;
		Thread thread = new Thread((ThreadStart)delegate
		{
			try
			{
				ContextMenuStrip menu = new ContextMenuStrip();
				ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem("\ud83d\udcc2 显示主面板");
				toolStripMenuItem.Click += delegate
				{
					ShowMainWindow();
				};
				ToolStripMenuItem toolStripMenuItem2 = new ToolStripMenuItem("\ud83d\udcc1 新建收纳区");
				toolStripMenuItem2.Click += delegate
				{
					_mainVm.CreateZoneCommand.Execute(null);
				};
				ToolStripMenuItem toolStripMenuItem3 = new ToolStripMenuItem("\ud83d\udcc2 新建映射区");
				toolStripMenuItem3.Click += delegate
				{
					_mainVm.CreateMappingCommand.Execute(null);
				};
				ToolStripMenuItem toolStripMenuItem4 = new ToolStripMenuItem("\ud83d\udccb 新建待办区");
				toolStripMenuItem4.Click += delegate
				{
					_mainVm.CreateTodoCommand.Execute(null);
				};
				ToolStripMenuItem toolStripMenuItem5 = new ToolStripMenuItem("❌ 退出应用");
				toolStripMenuItem5.Click += delegate
				{
					if (System.Windows.MessageBox.Show("退出工具箱，图标【不会恢复】，可能会影响您的使用哦！", "退出应用", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
					{
						ExitApplication();
					}
				};
				ToolStripMenuItem toolStripMenuItem6 = new ToolStripMenuItem("ℹ 关于软件");
				toolStripMenuItem6.Click += delegate
				{
					ShowAboutDialog();
				};
				ToolStripMenuItem toolStripMenuItem7 = new ToolStripMenuItem("⚙ 系统设置");
				toolStripMenuItem7.Click += delegate
				{
					OpenSystemSettingsWindow();
				};
				ToolStripMenuItem toolStripMenuItem8 = new ToolStripMenuItem("\ud83d\udd0d 文件搜索");
				toolStripMenuItem8.Click += delegate
				{
					if (_mainWindow is MainWindow mainWindow)
					{
						mainWindow.OpenSearchWindow();
					}
				};
				menu.Items.Add(toolStripMenuItem);
				menu.Items.Add(toolStripMenuItem8);
				menu.Items.Add(new ToolStripSeparator());
				menu.Items.Add(toolStripMenuItem2);
				menu.Items.Add(toolStripMenuItem3);
				menu.Items.Add(toolStripMenuItem4);
				menu.Items.Add(new ToolStripSeparator());
				menu.Items.Add(toolStripMenuItem7);
				menu.Items.Add(toolStripMenuItem6);
				menu.Items.Add(new ToolStripSeparator());
				menu.Items.Add(toolStripMenuItem5);
				uiDispatcher.BeginInvoke((Action)delegate
				{
					if (_isExiting)
					{
						menu.Dispose();
					}
					else
					{
						notifyIcon.ContextMenuStrip = menu;
					}
				});
			}
			catch (Exception ex)
			{
				LogError("托盘右键菜单后台构建失败（降级为无右键菜单）", ex);
			}
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.IsBackground = true;
		thread.Start();
	}

	public void ShowNotification(string title, string message, string? time = null)
	{
		try
		{
			ToastNotifier.Show(title, message, time);
		}
		catch
		{
			try
			{
				_notifyIcon?.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
			}
			catch
			{
			}
		}
	}

	public void ShowMainWindow()
	{
		if (_isExiting)
		{
			return;
		}
		_mainWindow.Dispatcher.Invoke(delegate
		{
			_mainWindow.Opacity = 1.0;
			_mainWindow.Show();
			_mainWindow.WindowState = WindowState.Normal;
			_mainWindow.ShowInTaskbar = true;
			_mainWindow.Activate();
			try
			{
				nint handle = new WindowInteropHelper(_mainWindow).Handle;
				if (handle != IntPtr.Zero)
				{
					NativeMethods.BringToFront(handle);
				}
			}
			catch
			{
			}
			AppPanelTracker.Register(new WindowInteropHelper(_mainWindow).Handle);
		});
	}

	public void HideMainWindow()
	{
		if (!_isExiting)
		{
			_mainWindow.Dispatcher.Invoke(delegate
			{
				_mainWindow.Hide();
				_mainWindow.ShowInTaskbar = false;
				AppPanelTracker.Unregister(new WindowInteropHelper(_mainWindow).Handle);
			});
		}
	}

	public void ExitApplication()
	{
		_isExiting = true;
		DesktopEmbedService.IsAppExiting = true;
		try
		{
			_mainVm.SaveZonesNow();
		}
		catch
		{
		}
		try
		{
			_mainVm.SaveTodoWindowStateBeforeShutdown();
		}
		catch
		{
		}
		if (File.Exists(Path.Combine(App.GetAppDataDir(), ".uninstalling")))
		{
			LogError("ExitApplication", new Exception("检测到 .uninstalling 哨兵，卸载器正在运行，跳过图标恢复"));
			if (_notifyIcon != null)
			{
				_notifyIcon.Visible = false;
			}
			System.Windows.Application.Current.Shutdown();
		}
		else
		{
			if (_notifyIcon != null)
			{
				_notifyIcon.Visible = false;
			}
			WinDHookService.Stop();
			System.Windows.Application.Current.Shutdown();
		}
	}

	private void OpenSystemSettingsWindow()
	{
		if (_isExiting)
		{
			return;
		}
		_mainWindow.Dispatcher.Invoke(delegate
		{
			try
			{
				if (_mainWindow is MainWindow mainWindow)
				{
					mainWindow.ShowSettingsPanel();
				}
			}
			catch (Exception ex)
			{
				LogError("打开系统设置失败", ex);
			}
		});
	}

	private void ShowAboutDialog()
	{
		if (_isExiting)
		{
			return;
		}
		_mainWindow.Dispatcher.Invoke(delegate
		{
			if (_mainWindow is MainWindow mainWindow)
			{
				mainWindow.ShowAboutPanel();
			}
		});
	}

	private void OnMainWindowClosing(object? sender, CancelEventArgs e)
	{
		if (!_isExiting)
		{
			e.Cancel = true;
			HideMainWindow();
		}
	}

	private void StartExitRequestMonitor()
	{
		_exitRequestTimer = new System.Timers.Timer(1000.0);
		_exitRequestTimer.Elapsed += delegate
		{
			if (_isExiting)
			{
				_exitRequestTimer?.Stop();
			}
			else if (File.Exists(ExitRequestPath))
			{
				try
				{
					File.Delete(ExitRequestPath);
				}
				catch
				{
				}
				_exitRequestTimer.Stop();
				_mainWindow.Dispatcher.InvokeAsync(HandleInstallExitRequest);
			}
		};
		_exitRequestTimer.AutoReset = true;
		_exitRequestTimer.Start();
	}

	private async void HandleInstallExitRequest()
	{
		if (_isExiting)
		{
			return;
		}
		_isExiting = true;
		try
		{
			try
			{
				string path = Path.Combine(App.GetAppDataDir(), ".exit_acknowledged");
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllText(path, DateTime.Now.ToString("O"));
			}
			catch
			{
			}
			await ProgressWindow.RunWithProgressAsync(null, "正在为安装准备，恢复桌面图标…", async delegate(Action<string?> report)
			{
				await _mainVm.RestoreAllHiddenIconsAsync(report);
			});
		}
		catch (Exception)
		{
		}
		finally
		{
			if (_notifyIcon != null)
			{
				_notifyIcon.Visible = false;
			}
			WinDHookService.Stop();
			DesktopEmbedService.IsAppExiting = true;
			_mainVm.SaveTodoWindowStateBeforeShutdown();
			CrashRecoveryService.MarkCleanExit();
			System.Windows.Application.Current.Shutdown();
		}
	}

	private static Icon CreateEmbeddedFallbackIcon()
	{
		byte[] obj = new byte[62]
		{
			0, 0, 1, 0, 1, 0, 16, 0, 16, 0,
			0, 0, 0, 0, 1, 0, 32, 0, 104, 4,
			0, 0, 40, 0, 0, 0, 16, 0, 0, 0,
			32, 0, 0, 0, 1, 0, 32, 0, 0, 0,
			0, 0, 0, 4, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0
		};
		byte[] fullIco = new byte[1118];
		obj.CopyTo(fullIco, 0);
		for (int y = 0; y < 16; y++)
		{
			for (int x = 0; x < 16; x++)
			{
				int offset = 62 + (y * 16 + x) * 4;
				fullIco[offset] = 210;
				fullIco[offset + 1] = 118;
				fullIco[offset + 2] = 24;
				fullIco[offset + 3] = byte.MaxValue;
			}
		}
		using MemoryStream ms = new MemoryStream(fullIco);
		return new Icon(ms);
	}

	private static Icon CreateDefaultIcon()
	{
		try
		{
			return AppIcon.GetTrayIcon();
		}
		catch
		{
		}
		try
		{
			string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
			if (File.Exists(iconPath))
			{
				return new Icon(iconPath);
			}
		}
		catch
		{
		}
		try
		{
			string path = Environment.ProcessPath;
			if (!string.IsNullOrEmpty(path) && File.Exists(path))
			{
				Icon icon = Icon.ExtractAssociatedIcon(path);
				if (icon != null)
				{
					return icon;
				}
			}
		}
		catch
		{
		}
		using Bitmap bmp = new Bitmap(32, 32);
		using Graphics g = Graphics.FromImage(bmp);
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.InterpolationMode = InterpolationMode.HighQualityBicubic;
		using (GraphicsPath path2 = MakeRoundedRect(1f, 1f, 30f, 30f, 6f))
		{
			using LinearGradientBrush brush = new LinearGradientBrush(new System.Drawing.Point(0, 0), new System.Drawing.Point(30, 30), Color.FromArgb(24, 118, 210), Color.FromArgb(13, 71, 161));
			g.FillPath(brush, path2);
		}
		using (Pen pen = new Pen(Color.White, 1.8f)
		{
			StartCap = LineCap.Round,
			EndCap = LineCap.Round
		})
		{
			g.DrawLine(pen, 10, 25, 22, 25);
			g.DrawLine(pen, 16, 20, 16, 25);
			g.DrawRectangle(pen, 8, 7, 16, 12);
			g.DrawLine(pen, 10, 11, 22, 11);
		}
		using (SolidBrush brush2 = new SolidBrush(Color.White))
		{
			g.FillEllipse(brush2, 19, 9, 3, 3);
		}
		return Icon.FromHandle(bmp.GetHicon());
	}

	private static GraphicsPath MakeRoundedRect(float x, float y, float w, float h, float r)
	{
		GraphicsPath graphicsPath = new GraphicsPath();
		graphicsPath.AddArc(x, y, r * 2f, r * 2f, 180f, 90f);
		graphicsPath.AddArc(x + w - r * 2f, y, r * 2f, r * 2f, 270f, 90f);
		graphicsPath.AddArc(x + w - r * 2f, y + h - r * 2f, r * 2f, r * 2f, 0f, 90f);
		graphicsPath.AddArc(x, y + h - r * 2f, r * 2f, r * 2f, 90f, 90f);
		graphicsPath.CloseFigure();
		return graphicsPath;
	}

	private static void LogError(string context, Exception ex)
	{
		LogHelper.Append(Path.Combine(Path.GetTempPath(), "DesktopTool_Crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [TRAY] {context}: {ex}\n");
	}

	public void Dispose()
	{
		Instance = null;
		_exitRequestTimer?.Stop();
		_exitRequestTimer?.Dispose();
		_notifyIcon?.Dispose();
	}
}
