using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopTool.Models;
using DesktopTool.Services;

namespace DesktopTool.Views;

public class SearchWindow : Window, IComponentConnector, IStyleConnector
{
	private struct MINMAXINFO
	{
		public NativeMethods.POINT ptReserved;

		public NativeMethods.POINT ptMaxSize;

		public NativeMethods.POINT ptMaxPosition;

		public NativeMethods.POINT ptMinTrackSize;

		public NativeMethods.POINT ptMaxTrackSize;
	}

	private bool _engineReady;

	private bool _searching;

	private bool _checking;

	private bool _authorizing;

	private Storyboard? _searchSpinStoryboard;

	private MainWindow? _owner;

	private AppSettings? _settings;

	private DesktopDragHost? _dragHost;

	private WidgetDragAlignController? _align;

	private string? _scopePath;

	private DispatcherTimer? _autoSearchTimer;

	private nint _panelHwnd;

	private string? _fileKind;

	private List<EverythingSearchService.SearchResult>? _allResults;

	private string? _sortColumn;

	private bool _sortAscending;

	private static readonly Dictionary<string, string> _sortColumnLabels = new Dictionary<string, string>
	{
		["FileName"] = "名称",
		["FullPath"] = "路径",
		["Size"] = "大小",
		["Modified"] = "修改时间"
	};

	private static readonly Dictionary<string, string> KindFilters = new Dictionary<string, string>
	{
		{ "all", "" },
		{ "folder", "folder:" },
		{ "doc", "ext:doc;docx;xls;xlsx;ppt;pptx;pdf;txt;rtf;odt;ods;odp;csv;html;htm;md;wps;wpd;pages;key;numbers;tex;epub;mobi;azw;azw3;djvu;chm;pps;ppsx;pot;potx;dot;dotx;xps" },
		{ "picture", "ext:jpg;jpeg;png;gif;bmp;tiff;tif;webp;ico;svg;heic;heif;raw;cr2;nef;arw;psd;ai;eps;pbm;pgm;ppm;xbm;xpm;tga;pcx;wmf;emf;dds;exr" },
		{ "video", "ext:mp4;mkv;avi;mov;wmv;flv;mpg;mpeg;m4v;webm;vob;ogv;m2ts;ts;3gp;3g2;rm;rmvb;asf;divx;mpe;m1v;m2v;swf;f4v" },
		{ "audio", "ext:mp3;wav;wma;flac;aac;ogg;oga;m4a;aiff;aif;mid;midi;ac3;dts;ape;opus;ra;ram;au;cda;wv" },
		{ "exe", "ext:exe;msi;bat;cmd;com;scr;ps1;vbs;js;jar;cpl;pyw;rb;sh;bin;app;gadget" },
		{ "zip", "ext:zip;rar;7z;tar;gz;bz2;bz;xz;lz;lzma;z;tgz;tbz;tbz2;iso;cab;arj;lzh;ace;wim;esd;deb;rpm" }
	};

	private static readonly Dictionary<string, string> KindLabels = new Dictionary<string, string>
	{
		{ "all", "全部" },
		{ "doc", "文档" },
		{ "folder", "文件夹" },
		{ "picture", "图片" },
		{ "video", "视频" },
		{ "audio", "音频" },
		{ "exe", "可执行文件" },
		{ "zip", "压缩文件" }
	};

	private Point? _dragStartPoint;

	private EverythingSearchService.SearchResult? _dragItem;

	private const int WM_NCHITTEST = 132;

	private const int WM_ACTIVATE = 6;

	private const int WA_INACTIVE = 0;

	private const int HTLEFT = 10;

	private const int HTRIGHT = 11;

	private const int HTTOP = 12;

	private const int HTTOPLEFT = 13;

	private const int HTTOPRIGHT = 14;

	private const int HTBOTTOM = 15;

	private const int HTBOTTOMLEFT = 16;

	private const int HTBOTTOMRIGHT = 17;

	private const int WM_GETMINMAXINFO = 36;

	internal Border RootBorder;

	internal ToggleButton DesktopWidgetToggle;

	internal TextBlock HotkeyDisplay;

	internal Button BtnChangeHotkey;

	internal Button BtnResetHotkey;

	internal TextBlock MaxRestoreGlyph;

	internal TextBox SearchBox;

	internal Button SearchButton;

	internal TextBlock SearchBtnLabel;

	internal StackPanel SearchBtnLoading;

	internal RotateTransform SearchSpin;

	internal ToggleButton ScopeToggle;

	internal Popup ScopePopup;

	internal StackPanel ScopeItemsPanel;

	internal RadioButton FilterAll;

	internal RadioButton FilterDoc;

	internal RadioButton FilterFolder;

	internal RadioButton FilterPicture;

	internal RadioButton FilterVideo;

	internal RadioButton FilterAudio;

	internal RadioButton FilterExe;

	internal RadioButton FilterZip;

	internal Border InstallBanner;

	internal TextBlock BannerTitle;

	internal TextBlock BannerDesc;

	internal Button InstallButton;

	internal ListView ResultList;

	internal TextBlock StatusText;

	private bool _contentLoaded;

	public SearchWindow(MainWindow owner, AppSettings settings)
	{
		InitializeComponent();
		_owner = owner;
		_settings = settings;
		try
		{
			base.Icon = AppIcon.GetWpfIcon();
		}
		catch
		{
		}
		base.SourceInitialized += delegate
		{
			HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
		};
		base.KeyDown += delegate(object s, KeyEventArgs e)
		{
			if (e.Key == Key.Escape)
			{
				Close();
			}
		};
		base.Loaded += delegate
		{
			UpdateHotkeyDisplay();
			SearchBox.Focus();
			AdjustResultColumns();
			CheckEngineAsync(autoStart: true);
			DesktopWidgetToggle.Checked -= DesktopWidgetToggle_Checked;
			DesktopWidgetToggle.Unchecked -= DesktopWidgetToggle_Unchecked;
			DesktopWidgetToggle.IsChecked = _settings.DesktopSearchWidgetEnabled;
			DesktopWidgetToggle.Checked += DesktopWidgetToggle_Checked;
			DesktopWidgetToggle.Unchecked += DesktopWidgetToggle_Unchecked;
		};
		base.Loaded += delegate
		{
			_panelHwnd = new WindowInteropHelper(this).Handle;
			AppPanelTracker.Register(_panelHwnd);
		};
		base.Activated += delegate
		{
			_panelHwnd = new WindowInteropHelper(this).Handle;
			AppPanelTracker.Register(_panelHwnd);
		};
		base.Closed += delegate
		{
			AppPanelTracker.Unregister(_panelHwnd);
		};
		_autoSearchTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(250.0)
		};
		_autoSearchTimer.Tick += delegate
		{
			_autoSearchTimer.Stop();
			DoSearch();
		};
		base.Loaded += delegate
		{
			PopulateScopeCombo();
		};
		if (_settings != null)
		{
			if (_settings.SearchLeft >= 0.0 && _settings.SearchTop >= 0.0)
			{
				base.Left = _settings.SearchLeft;
				base.Top = _settings.SearchTop;
			}
			if (_settings.SearchWidth >= 0.0)
			{
				base.Width = _settings.SearchWidth;
			}
			if (_settings.SearchHeight >= 0.0)
			{
				base.Height = _settings.SearchHeight;
			}
		}
		nint hwnd = new WindowInteropHelper(this).Handle;
		_dragHost = new DesktopDragHost(() => hwnd, () => DesktopEmbedService.GetOwnerHwnd(hwnd), () => base.MinWidth, () => base.MinHeight, this, delegate
		{
		}, delegate
		{
		}, SetPosition, delegate(int _)
		{
			_align?.OnDragCommitted(_);
		});
		_align = new WidgetDragAlignController(new WidgetDragAlignController.Config
		{
			Hwnd = hwnd,
			GlowTarget = this,
			GetBoundsDip = () => NativeDragHelper.GetDipRect(hwnd),
			SetBoundsDip = delegate(Rect r)
			{
				base.Left = r.X;
				base.Top = r.Y;
				base.Width = r.Width;
				base.Height = r.Height;
			},
			ApplyDragPosition = delegate(int x, int y, int w, int h)
			{
				if (w != 0 && h != 0)
				{
					base.Left = x;
					base.Top = y;
					base.Width = w;
					base.Height = h;
				}
				else
				{
					base.Left = x;
					base.Top = y;
				}
			},
			MinWidth = () => base.MinWidth,
			MinHeight = () => base.MinHeight,
			IsLocked = () => base.WindowState == WindowState.Maximized,
			Save = OnDragCommittedSave
		}, base.Dispatcher);
		base.Closed += delegate
		{
			WindowSnapService.Unregister(hwnd);
			SaveBounds();
			_align?.Dispose();
			_align = null;
			_dragHost = null;
		};
	}

	private void PopulateScopeCombo()
	{
		try
		{
			ScopeItemsPanel.Children.Clear();
			AddScopeItem("全盘", null);
			DriveInfo[] drives = DriveInfo.GetDrives();
			foreach (DriveInfo d in drives)
			{
				try
				{
					string root = d.RootDirectory.FullName;
					string label = d.Name.TrimEnd('\\') + "盘";
					AddScopeItem(label, root);
				}
				catch
				{
				}
			}
			ScopeToggle.Content = "全盘";
			_scopePath = null;
		}
		catch
		{
		}
	}

	private void AddScopeItem(string label, string? root)
	{
		Button btn = new Button
		{
			Content = label,
			Tag = root,
			Style = (Style)FindResource("ScopeDropItem"),
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		btn.Click += ScopeItem_Click;
		ScopeItemsPanel.Children.Add(btn);
	}

	private void ScopeItem_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button btn)
		{
			_scopePath = btn.Tag as string;
			ScopeToggle.Content = btn.Content;
			ScopeToggle.IsChecked = false;
			if (!string.IsNullOrWhiteSpace(SearchBox.Text))
			{
				DoSearch();
			}
		}
	}

	private void FilterTab_Checked(object sender, RoutedEventArgs e)
	{
		if (sender is RadioButton rb)
		{
			_fileKind = rb.Tag as string;
		}
		if (!string.IsNullOrWhiteSpace(SearchBox.Text))
		{
			DoSearch();
		}
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_engineReady && !string.IsNullOrWhiteSpace(SearchBox.Text))
		{
			_autoSearchTimer?.Stop();
			_autoSearchTimer?.Start();
		}
	}

	private void UpdateHotkeyDisplay()
	{
		if (_settings != null)
		{
			string text = FormatHotkeyDisplay(_settings.SearchHotkeyModifiers, _settings.SearchHotkeyKey);
			MainWindow? owner = _owner;
			if (owner != null && owner.SearchHotkeyBlocked)
			{
				text += "（已被其它程序占用）";
			}
			HotkeyDisplay.Text = text.Replace(" + ", "+");
		}
	}

	private static string FormatHotkeyDisplay(string modifiers, string key)
	{
		string[] parts = modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return string.Join(" + ", parts) + " + " + key;
	}

	private void BtnChangeHotkey_Click(object sender, RoutedEventArgs e)
	{
		string capturedMods = "";
		Key capturedKey = Key.None;
		bool confirmed = false;
		Window captureWindow = new Window
		{
			Title = "文件搜索 — 设置快捷键",
			Width = 420.0,
			Height = 200.0,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			WindowStyle = WindowStyle.SingleBorderWindow,
			ResizeMode = ResizeMode.NoResize,
			ShowInTaskbar = false,
			Topmost = true
		};
		try
		{
			InputMethod.SetIsInputMethodEnabled(captureWindow, value: false);
		}
		catch
		{
		}
		TextBlock txtInstruction = new TextBlock
		{
			Text = "请按下新的快捷键组合（如 Win + Shift + F）...",
			HorizontalAlignment = HorizontalAlignment.Center,
			FontSize = 14.0,
			Margin = new Thickness(0.0, 20.0, 0.0, 12.0),
			Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51))
		};
		TextBlock txtCaptured = new TextBlock
		{
			Text = "等待按键...",
			HorizontalAlignment = HorizontalAlignment.Center,
			FontSize = 20.0,
			FontWeight = FontWeights.Bold,
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0),
			Foreground = new SolidColorBrush(Color.FromRgb(74, 144, 217))
		};
		StackPanel btnContainer = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		Button btnConfirm = new Button
		{
			Content = "确认",
			Width = 100.0,
			Height = 32.0,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
			IsEnabled = false,
			Background = new SolidColorBrush(Color.FromRgb(74, 144, 217)),
			Foreground = Brushes.White,
			BorderThickness = new Thickness(0.0),
			Cursor = Cursors.Hand
		};
		btnConfirm.Click += delegate
		{
			confirmed = true;
			captureWindow.Close();
		};
		Button btnCancel = new Button
		{
			Content = "取消",
			Width = 100.0,
			Height = 32.0,
			Background = new SolidColorBrush(Color.FromRgb(224, 224, 224)),
			Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
			BorderThickness = new Thickness(0.0),
			Cursor = Cursors.Hand
		};
		btnCancel.Click += delegate
		{
			confirmed = false;
			captureWindow.Close();
		};
		btnContainer.Children.Add(btnConfirm);
		btnContainer.Children.Add(btnCancel);
		StackPanel panel = new StackPanel();
		panel.Children.Add(txtInstruction);
		panel.Children.Add(txtCaptured);
		panel.Children.Add(btnContainer);
		captureWindow.Content = panel;
		captureWindow.PreviewKeyDown += delegate(object s, KeyEventArgs args)
		{
			args.Handled = true;
			List<string> list = new List<string>();
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
			{
				list.Add("Win");
			}
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
			{
				list.Add("Ctrl");
			}
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
			{
				list.Add("Alt");
			}
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
			{
				list.Add("Shift");
			}
			Key key = args.Key;
			Key key2 = key switch
			{
				Key.ImeProcessed => args.ImeProcessedKey, 
				Key.DeadCharProcessed => args.DeadCharProcessedKey, 
				Key.System => args.SystemKey, 
				_ => key, 
			};
			if (key2 != Key.LeftCtrl && key2 != Key.RightCtrl && key2 != Key.LeftAlt && key2 != Key.RightAlt && key2 != Key.LeftShift && key2 != Key.RightShift && key2 != Key.LWin && key2 != Key.RWin)
			{
				capturedMods = string.Join("+", list);
				capturedKey = key2;
				if (string.IsNullOrEmpty(capturedMods))
				{
					txtCaptured.Text = $"请至少使用一个修饰键（Ctrl/Alt/Shift/Win）\n当前按下：{capturedKey}";
				}
				else
				{
					txtCaptured.Text = FormatHotkeyDisplay(capturedMods, capturedKey.ToString());
					btnConfirm.IsEnabled = true;
				}
			}
		};
		captureWindow.ShowDialog();
		if (confirmed && !string.IsNullOrEmpty(capturedMods) && capturedKey != Key.None)
		{
			_owner?.SetSearchHotkey(capturedMods, capturedKey.ToString());
			UpdateHotkeyDisplay();
		}
	}

	private void BtnResetHotkey_Click(object sender, RoutedEventArgs e)
	{
		_owner?.ResetSearchHotkey();
		UpdateHotkeyDisplay();
	}

	private void DesktopWidgetToggle_Checked(object sender, RoutedEventArgs e)
	{
		SetDesktopWidget(on: true);
	}

	private void DesktopWidgetToggle_Unchecked(object sender, RoutedEventArgs e)
	{
		SetDesktopWidget(on: false);
	}

	private void SetDesktopWidget(bool on)
	{
		try
		{
			LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_desktopwidget.log"), $"{DateTime.Now:HH:mm:ss.fff} [Toggle] SetDesktopWidget(on={on}) _owner={((_owner == null) ? "NULL" : "ok")} _settings={((_settings == null) ? "NULL" : "ok")}{Environment.NewLine}");
		}
		catch
		{
		}
		if (_settings != null)
		{
			_settings.DesktopSearchWidgetEnabled = on;
			_owner?.SaveAppSettings();
		}
		if (on)
		{
			_owner?.ShowDesktopSearchWidget();
		}
		else
		{
			_owner?.HideDesktopSearchWidget();
		}
	}

	private async Task CheckEngineAsync(bool autoStart = false)
	{
		if (_checking)
		{
			return;
		}
		_checking = true;
		try
		{
			EverythingSearchService.LogEverythingDiag($"[CheckEngineAsync] 快速探测 | autoStart={autoStart} | hasSetup={EverythingSearchService.FindBundledSetup() != null}");
			StatusText.Text = "正在检测文件搜索服务…";
			bool ready = await Task.Run(() => EverythingSearchService.IsEngineAvailable());
			_engineReady = ready;
			if (ready)
			{
				InstallBanner.Visibility = Visibility.Collapsed;
				StatusText.Text = "文件搜索服务已就绪（Everything 已在后台隐藏运行，无托盘、无窗口），输入关键词后回车即可全盘搜索";
			}
			else if (autoStart && EverythingSearchService.FindEverythingExe() != null)
			{
				AuthorizeAndStartEngine();
			}
			else
			{
				ShowNotReadyBanner();
			}
		}
		finally
		{
			_checking = false;
		}
	}

	private void ShowNotReadyBanner()
	{
		string engineExe = EverythingSearchService.FindEverythingExe();
		string setup = EverythingSearchService.FindBundledSetup();
		InstallBanner.Visibility = Visibility.Visible;
		if (engineExe != null && !EverythingSearchService.IsCompatibleBitness(engineExe))
		{
			BannerTitle.Text = "Everything 版本不兼容";
			BannerDesc.Text = "已找到的 Everything 是 32 位，与本程序(64 位)无法通信。请将 64 位 Everything（Everything-x64-setup.exe 或便携版 Everything64.exe）放入 Tools\\Everything 目录后重装。";
			InstallButton.Content = "需要 64 位版";
			InstallButton.IsEnabled = false;
			StatusText.Text = "文件搜索服务不可用，请先启动搜索服务";
		}
		else if (engineExe != null)
		{
			BannerTitle.Text = "需要启用文件搜索服务";
			BannerDesc.Text = "全盘极速搜索依赖 Everything 便携版后台索引服务。点击「启动搜索服务」将以隐藏模式启动它，若弹出授权面板，请选择「使用管理员权限运行」，之后无窗口、无托盘后台运行。";
			InstallButton.Content = "启动搜索服务";
			InstallButton.IsEnabled = true;
			StatusText.Text = "文件搜索服务不可用，请先启动搜索服务";
		}
		else if (setup != null)
		{
			if (Path.GetFileName(setup).Contains("setup", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(setup).EndsWith("-install.exe", StringComparison.OrdinalIgnoreCase))
			{
				BannerTitle.Text = "需要安装文件搜索服务组件";
				BannerDesc.Text = "全盘极速搜索依赖后台索引服务，点击右侧按钮完成安装。若弹出授权面板，请选择「使用管理员权限运行」。";
				InstallButton.Content = "安装组件";
			}
			else
			{
				BannerTitle.Text = "需要启用文件搜索服务";
				BannerDesc.Text = "全盘极速搜索依赖 Everything 便携版后台索引服务。点击「启动搜索服务」将以隐藏模式启动它，若弹出授权面板，请选择「使用管理员权限运行」，之后无窗口、无托盘后台运行。";
				InstallButton.Content = "启动搜索服务";
			}
			InstallButton.IsEnabled = true;
			StatusText.Text = "文件搜索服务不可用，请先启动搜索服务";
		}
		else
		{
			BannerTitle.Text = "未找到文件搜索服务安装组件";
			BannerDesc.Text = "安装包未随软件一起分发，请前往 voidtools.com 下载 Everything(64 位) 并保持后台运行后重试。";
			InstallButton.Content = "重新检测";
			InstallButton.IsEnabled = true;
			StatusText.Text = "文件搜索服务不可用，请先启动搜索服务";
		}
	}

	private async void InstallButton_Click(object sender, RoutedEventArgs e)
	{
		if (_authorizing)
		{
			return;
		}
		_authorizing = true;
		try
		{
			EverythingSearchService.LogEverythingDiag("[InstallButton_Click] 用户点击安装组件");
			string setup = EverythingSearchService.FindBundledSetup();
			string engineExe = EverythingSearchService.FindEverythingExe();
			if (setup == null && engineExe != null)
			{
				AuthorizeAndStartEngine();
				return;
			}
			if (setup == null)
			{
				await CheckEngineAsync();
				return;
			}
			if (!EverythingSearchService.IsCompatibleBitness(setup))
			{
				InstallButton.IsEnabled = true;
				InstallButton.Content = "需要 64 位版";
				StatusText.Text = "已捆绑的 Everything 安装包为 32 位，与本程序(64 位)不兼容，IPC 永远无法连通。请将 64 位 Everything 安装包（Everything-x64-setup.exe 或便携版 Everything64.exe）放入 Tools\\Everything 目录后重试。";
				return;
			}
			if (EverythingSearchService.IsInstalledEverythingCompatible())
			{
				AuthorizeAndStartEngine();
				return;
			}
			bool num = Path.GetFileName(setup).Contains("setup", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(setup).EndsWith("-install.exe", StringComparison.OrdinalIgnoreCase);
			InstallButton.IsEnabled = false;
			if (!num)
			{
				AuthorizeAndStartEngine();
				return;
			}
			EverythingSearchService.PrepareInstallConfig();
			InstallBanner.Visibility = Visibility.Collapsed;
			InstallButton.Content = "安装中…";
			StatusText.Text = "正在启动安装程序，请在系统授权询问中点击「是」…";
			bool launchedSetup = await Task.Run(() => EverythingSearchService.LaunchInstaller());
			if (!launchedSetup)
			{
				InstallButton.IsEnabled = true;
				InstallButton.Content = "安装组件";
				StatusText.Text = "安装未开始（可能已取消授权或安装包缺失），可稍后重试";
				return;
			}
			StatusText.Text = "正在等待安装完成…";
			bool installed = await Task.Run(() => EverythingSearchService.WaitForInstall(60000));
			StatusText.Text = "正在准备搜索组件…";
			bool prepared = await Task.Run(() => EverythingSearchService.PrepareAfterInstall());
			bool flag = prepared;
			if (flag)
			{
				flag = await Task.Run(() => EverythingSearchService.IsEngineAvailable());
			}
			bool okSetup;
			if (flag)
			{
				StatusText.Text = "正在隐藏搜索服务窗口…";
				EverythingSearchService.HideRunningEngineWindow();
				okSetup = true;
			}
			else
			{
				StatusText.Text = "正在以隐藏模式重启搜索服务…";
				okSetup = await Task.Run(() => EverythingSearchService.RestartEngine());
			}
			if (!okSetup)
			{
				StatusText.Text = "正在启动搜索服务…";
				okSetup = await Task.Run(() => EverythingSearchService.WaitForEngine(20000));
			}
			bool indexed = okSetup && EverythingSearchService.IsEngineHealthy();
			await Task.Run(delegate
			{
				CleanupEverythingDesktopShortcut();
			});
			EverythingSearchService.LogEverythingDiag($"[InstallButton_Click] launched={launchedSetup} installed={installed} prepared={prepared} ok={okSetup} indexed={indexed} dllReason={EverythingSearchService.LastDllReason ?? "null"}");
			InstallButton.IsEnabled = true;
			InstallButton.Content = "安装组件";
			if (okSetup)
			{
				_engineReady = true;
				InstallBanner.Visibility = Visibility.Collapsed;
				StatusText.Text = (indexed ? "文件搜索服务已就绪（Everything 已在后台隐藏运行，无托盘、无窗口），输入关键词后回车即可全盘搜索" : "文件搜索服务已就绪（Everything 正在后台建立索引，可先输入关键词搜索，结果会逐步补全）");
				SearchBox.Focus();
			}
			else
			{
				string reason = ((!installed) ? "安装程序似乎未成功完成（未检测到 Everything.exe）。请确认授权弹窗已点击「是」，或手动运行安装包后再试。" : ((!okSetup) ? (EverythingSearchService.LastDllReason ?? "引擎已安装但未响应查询，请稍候重试。") : "引擎已启动但未能建立磁盘索引（搜索会为空）。请确认安装/启动时的系统授权窗口已点击「允许」，让 Everything 以管理员读取磁盘索引。"));
				ShowNotReadyBanner();
				StatusText.Text = "服务尚未就绪：" + reason;
			}
		}
		finally
		{
			_authorizing = false;
		}
	}

	private void AuthorizeAndStartEngine()
	{
		InstallButton.IsEnabled = false;
		InstallButton.Content = "正在启动…";
		StatusText.Text = "正在启动文件搜索服务（若弹出授权面板，请选择「使用管理员权限运行」以读取磁盘索引）…";
		SetSearchButtonLoading(loading: true);
		SearchBox.Focus();
		Task.Run(delegate
		{
			bool flag = EverythingSearchService.RestartEngine();
			bool ok = flag || EverythingSearchService.WaitForEngine(15000);
			CleanupEverythingDesktopShortcut();
			bool healthy = ok && EverythingSearchService.IsEngineHealthy();
			base.Dispatcher.Invoke(delegate
			{
				_authorizing = false;
				SetSearchButtonLoading(loading: false);
				if (healthy)
				{
					_engineReady = true;
					InstallBanner.Visibility = Visibility.Collapsed;
					StatusText.Text = "文件搜索服务已就绪（Everything 已在后台隐藏运行，无托盘、无窗口），输入关键词后回车即可全盘搜索";
					SearchBox.Focus();
				}
				else if (ok)
				{
					InstallButton.IsEnabled = true;
					InstallButton.Content = "重新获取授权";
					StatusText.Text = "组件已启动，但未能建立磁盘索引（搜索会为空）。请确认已在授权面板选择「使用管理员权限运行」；若已取消，请点击「重新获取授权」再次以管理员身份启动。";
				}
				else
				{
					InstallButton.IsEnabled = true;
					InstallButton.Content = "重新获取授权";
					ShowNotReadyBanner();
					StatusText.Text = "服务尚未就绪：" + (EverythingSearchService.LastDllReason ?? "引擎未响应，请稍候重试。");
				}
			});
		});
	}

	private static void CleanupEverythingDesktopShortcut()
	{
		try
		{
			List<string> desks = new List<string>();
			string userDesk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
			if (!string.IsNullOrEmpty(userDesk) && Directory.Exists(userDesk))
			{
				desks.Add(userDesk);
			}
			string commonDesk = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
			if (!string.IsNullOrEmpty(commonDesk) && Directory.Exists(commonDesk))
			{
				desks.Add(commonDesk);
			}
			foreach (string item in desks)
			{
				string[] files = Directory.GetFiles(item, "Everything*.lnk");
				foreach (string f in files)
				{
					try
					{
						File.Delete(f);
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
	}

	private void SearchBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Return)
		{
			DoSearch();
		}
	}

	private void SearchButton_Click(object sender, RoutedEventArgs e)
	{
		DoSearch();
	}

	private void SetSearchButtonLoading(bool loading)
	{
		if (loading)
		{
			SearchBtnLabel.Visibility = Visibility.Collapsed;
			SearchBtnLoading.Visibility = Visibility.Visible;
			SearchButton.IsEnabled = false;
			if (_searchSpinStoryboard == null)
			{
				DoubleAnimation da = new DoubleAnimation(0.0, 360.0, new Duration(TimeSpan.FromSeconds(1.0)))
				{
					RepeatBehavior = RepeatBehavior.Forever
				};
				Storyboard.SetTarget(da, SearchSpin);
				Storyboard.SetTargetProperty(da, new PropertyPath(RotateTransform.AngleProperty));
				_searchSpinStoryboard = new Storyboard();
				_searchSpinStoryboard.Children.Add(da);
			}
			_searchSpinStoryboard.Begin();
		}
		else
		{
			_searchSpinStoryboard?.Stop();
			SearchBtnLoading.Visibility = Visibility.Collapsed;
			SearchBtnLabel.Visibility = Visibility.Visible;
			SearchButton.IsEnabled = true;
		}
	}

	private async void DoSearch()
	{
		if (_searching)
		{
			return;
		}
		string keyword = SearchBox.Text.Trim();
		if (string.IsNullOrEmpty(keyword))
		{
			StatusText.Text = "请输入搜索关键词";
			return;
		}
		_searching = true;
		try
		{
			bool flag = !_engineReady;
			if (flag)
			{
				flag = !(await Task.Run(() => EverythingSearchService.IsEngineAvailable()));
			}
			if (flag)
			{
				_engineReady = false;
				ShowNotReadyBanner();
				StatusText.Text = "搜索服务未就绪，请先点击「启动搜索服务」启动后台服务";
				return;
			}
			_engineReady = true;
			StatusText.Text = "正在搜索…";
			Stopwatch sw = Stopwatch.StartNew();
			string tf;
			string typeFilter = (KindFilters.TryGetValue(_fileKind ?? "all", out tf) ? tf : "");
			List<EverythingSearchService.SearchResult> results = await Task.Run(() => EverythingSearchService.Search(keyword, 500u, _scopePath, typeFilter));
			sw.Stop();
			string scopeLabel = (string.IsNullOrWhiteSpace(_scopePath) ? "全盘" : (_scopePath.TrimEnd('\\') + "盘"));
			string kl;
			string kindLabel = (KindLabels.TryGetValue(_fileKind ?? "all", out kl) ? kl : "全部");
			if (results == null)
			{
				_engineReady = false;
				_allResults = null;
				ResultList.ItemsSource = null;
				StatusText.Text = "搜索服务不可用，请先安装或启动组件";
				await CheckEngineAsync();
				return;
			}
			_allResults = results;
			ApplyResultSort();
			UpdateColumnHeaderIndicators();
			StatusText.Text = ((results.Count == 0) ? $"[{scopeLabel} · {kindLabel}] 未找到匹配「{keyword}」的文件（耗时 {sw.ElapsedMilliseconds} ms）" : $"[{scopeLabel} · {kindLabel}] 找到 {results.Count} 项，耗时 {sw.ElapsedMilliseconds} ms（最多显示 500 项）");
		}
		finally
		{
			_searching = false;
		}
	}

	private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		OpenSelected();
	}

	private void ResultList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		_dragStartPoint = e.GetPosition(ResultList);
		_dragItem = GetResultItemAt(_dragStartPoint.Value);
		if (_dragItem != null && ResultList.SelectedItems.Contains(_dragItem) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
		{
			e.Handled = true;
		}
	}

	private void ResultList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		_dragStartPoint = null;
		_dragItem = null;
	}

	private void ResultList_MouseMove(object sender, MouseEventArgs e)
	{
		if (e.LeftButton != MouseButtonState.Pressed || !_dragStartPoint.HasValue || _dragItem == null)
		{
			return;
		}
		Point pos = e.GetPosition(ResultList);
		if (!(Math.Abs(pos.X - _dragStartPoint.Value.X) < SystemParameters.MinimumHorizontalDragDistance) || !(Math.Abs(pos.Y - _dragStartPoint.Value.Y) < SystemParameters.MinimumVerticalDragDistance))
		{
			IEnumerable<string> paths = ((ResultList.SelectedItems.Count <= 1 || _dragItem == null || !ResultList.SelectedItems.Contains(_dragItem)) ? new string[1] { _dragItem.FullPath } : (from EverythingSearchService.SearchResult r in ResultList.SelectedItems
				select r.FullPath));
			StartFileDrag(paths);
			_dragStartPoint = null;
			_dragItem = null;
		}
	}

	private void StartFileDrag(IEnumerable<string> paths)
	{
		List<string> list = paths.Where((string p) => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p))).ToList();
		if (list.Count == 0)
		{
			return;
		}
		DataObject data = new DataObject();
		data.SetData(DataFormats.FileDrop, list.ToArray());
		if (list.Count == 1)
		{
			data.SetData("FileNameW", list[0]);
		}
		try
		{
			DragDrop.DoDragDrop(ResultList, data, DragDropEffects.Copy | DragDropEffects.Move);
		}
		catch
		{
		}
	}

	private EverythingSearchService.SearchResult? GetResultItemAt(Point p)
	{
		for (DependencyObject hit = ResultList.InputHitTest(p) as DependencyObject; hit != null; hit = VisualTreeHelper.GetParent(hit))
		{
			if (hit is ListViewItem { DataContext: EverythingSearchService.SearchResult r })
			{
				return r;
			}
		}
		return null;
	}

	private void ResultList_ColumnHeader_Click(object sender, RoutedEventArgs e)
	{
		if (!(sender is GridViewColumnHeader { Column: not null } header))
		{
			return;
		}
		string tag = GridViewSort.GetSortKey(header.Column);
		if (!string.IsNullOrEmpty(tag) && _allResults != null && _allResults.Count != 0)
		{
			if (_sortColumn == tag)
			{
				_sortAscending = !_sortAscending;
			}
			else
			{
				_sortColumn = tag;
				_sortAscending = false;
			}
			ApplyResultSort();
			UpdateColumnHeaderIndicators();
		}
	}

	private void ApplyResultSort()
	{
		if (_allResults == null)
		{
			ResultList.ItemsSource = null;
			return;
		}
		if (_sortColumn == null)
		{
			ResultList.ItemsSource = _allResults.ToList();
			return;
		}
		List<EverythingSearchService.SearchResult> sorted = _sortColumn switch
		{
			"FileName" => _sortAscending ? _allResults.OrderBy<EverythingSearchService.SearchResult, string>((EverythingSearchService.SearchResult x) => x.FileName, StringComparer.OrdinalIgnoreCase).ToList() : _allResults.OrderByDescending<EverythingSearchService.SearchResult, string>((EverythingSearchService.SearchResult x) => x.FileName, StringComparer.OrdinalIgnoreCase).ToList(), 
			"FullPath" => _sortAscending ? _allResults.OrderBy<EverythingSearchService.SearchResult, string>((EverythingSearchService.SearchResult x) => x.FullPath, StringComparer.OrdinalIgnoreCase).ToList() : _allResults.OrderByDescending<EverythingSearchService.SearchResult, string>((EverythingSearchService.SearchResult x) => x.FullPath, StringComparer.OrdinalIgnoreCase).ToList(), 
			"Size" => _sortAscending ? _allResults.OrderBy((EverythingSearchService.SearchResult x) => x.Size).ToList() : _allResults.OrderByDescending((EverythingSearchService.SearchResult x) => x.Size).ToList(), 
			"Modified" => _sortAscending ? _allResults.OrderBy((EverythingSearchService.SearchResult x) => ((!x.Modified.HasValue) ? 1 : 0, x.Modified ?? DateTime.MinValue)).ToList() : _allResults.OrderByDescending((EverythingSearchService.SearchResult x) => ((!x.Modified.HasValue) ? 1 : 0, x.Modified ?? DateTime.MinValue)).ToList(), 
			_ => _allResults.ToList(), 
		};
		ResultList.ItemsSource = sorted;
	}

	private void UpdateColumnHeaderIndicators()
	{
		if (!(ResultList.View is GridView grid))
		{
			return;
		}
		foreach (GridViewColumn col in grid.Columns)
		{
			string tag = GridViewSort.GetSortKey(col);
			if (!string.IsNullOrEmpty(tag))
			{
				string l;
				string label = (_sortColumnLabels.TryGetValue(tag, out l) ? l : tag);
				col.Header = ((tag == _sortColumn) ? (label + (_sortAscending ? "  ▲" : "  ▼")) : label);
			}
		}
	}

	private void MenuOpen_Click(object sender, RoutedEventArgs e)
	{
		OpenSelected();
	}

	private void OpenSelected()
	{
		if (!(ResultList.SelectedItem is EverythingSearchService.SearchResult item))
		{
			return;
		}
		if (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath))
		{
			MessageBox.Show("该文件已不存在或已被移动。", "文件搜索", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		try
		{
			NativeMethods.ShellExecuteOpenAsync(item.FullPath, IntPtr.Zero);
		}
		catch (Exception ex)
		{
			MessageBox.Show("打开失败：" + ex.Message, "文件搜索", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	public void ApplyExternalQuery(string query)
	{
		SearchBox.Text = query;
		DoSearch();
	}

	private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
	{
		if (!(ResultList.SelectedItem is EverythingSearchService.SearchResult item))
		{
			return;
		}
		try
		{
			if (File.Exists(item.FullPath))
			{
				NativeMethods.ShellExecuteOpenAsync("explorer.exe", IntPtr.Zero, "open", "/select,\"" + item.FullPath + "\"");
			}
			else if (Directory.Exists(item.FullPath))
			{
				NativeMethods.ShellExecuteOpenAsync("explorer.exe", IntPtr.Zero, "open", "\"" + item.FullPath + "\"");
			}
		}
		catch
		{
		}
	}

	private void MenuCopyFile_Click(object sender, RoutedEventArgs e)
	{
		List<string> paths = (from EverythingSearchService.SearchResult r in ResultList.SelectedItems
			select r.FullPath into p
			where !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p))
			select p).ToList();
		if (paths.Count == 0)
		{
			return;
		}
		try
		{
			StringCollection stringCollection = new StringCollection();
			stringCollection.AddRange(paths.ToArray());
			Clipboard.SetFileDropList(stringCollection);
		}
		catch (Exception)
		{
		}
	}

	private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
	{
		List<string> paths = (from EverythingSearchService.SearchResult r in ResultList.SelectedItems
			select r.FullPath into p
			where !string.IsNullOrEmpty(p)
			select p).ToList();
		if (paths.Count == 0)
		{
			return;
		}
		try
		{
			Clipboard.SetText(string.Join(Environment.NewLine, paths));
		}
		catch
		{
		}
	}

	private void MenuDelete_Click(object sender, RoutedEventArgs e)
	{
		DeleteSelectedItems();
	}

	private void ResultList_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Delete)
		{
			e.Handled = true;
			DeleteSelectedItems();
		}
	}

	private void ResultList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
	{
		Point pt = Mouse.GetPosition(ResultList);
		DependencyObject hit = ResultList.InputHitTest(pt) as DependencyObject;
		EverythingSearchService.SearchResult item = null;
		while (hit != null)
		{
			if (hit is ListViewItem { DataContext: EverythingSearchService.SearchResult r })
			{
				item = r;
				break;
			}
			hit = VisualTreeHelper.GetParent(hit);
		}
		if (item != null && !ResultList.SelectedItems.Contains(item))
		{
			ResultList.SelectedItems.Clear();
			ResultList.SelectedItems.Add(item);
		}
	}

	private void DeleteSelectedItems()
	{
		List<EverythingSearchService.SearchResult> items = ResultList.SelectedItems.Cast<EverythingSearchService.SearchResult>().ToList();
		if (items.Count == 0 || MessageBox.Show((items.Count == 1) ? ("确定要将文件「" + items[0].FileName + "」移入回收站吗？\n\n移入后可从回收站还原。") : $"确定要将选中的 {items.Count} 个文件/文件夹移入回收站吗？\n\n移入后可从回收站还原。", "移入回收站", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
		{
			return;
		}
		List<string> validPaths = (from it in items
			select it.FullPath into p
			where !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p))
			select p).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		if (validPaths.Count == 0)
		{
			return;
		}
		int ok = 0;
		int fail = 0;
		try
		{
			SendToRecycleBin(validPaths);
			ok = validPaths.Count;
		}
		catch
		{
			fail = validPaths.Count;
		}
		if (ok > 0)
		{
			HashSet<string> delSet = new HashSet<string>(validPaths, StringComparer.OrdinalIgnoreCase);
			_allResults.RemoveAll((EverythingSearchService.SearchResult r) => r.FullPath != null && delSet.Contains(r.FullPath));
		}
		ResultList.UnselectAll();
		ApplyResultSort();
		if (ok > 0)
		{
			StatusText.Text = ((fail > 0) ? $"已将 {ok} 项移入回收站，{fail} 项删除失败。" : $"已将 {ok} 项移入回收站。");
		}
		else
		{
			StatusText.Text = "删除失败，文件可能正被占用或权限不足。";
		}
	}

	private static void SendToRecycleBin(IEnumerable<string> paths)
	{
		string pFrom = string.Join('\0', paths) + "\0\0";
		NativeMethods.SHFILEOPSTRUCT fileOp = new NativeMethods.SHFILEOPSTRUCT
		{
			hwnd = IntPtr.Zero,
			wFunc = 3u,
			pFrom = pFrom,
			fFlags = 80
		};
		int result = NativeMethods.SHFileOperation(ref fileOp);
		if (result != 0)
		{
			throw new InvalidOperationException($"移入回收站失败（错误码：{result}）");
		}
	}

	private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (msg == 6)
		{
			if ((((IntPtr)wParam).ToInt32() & 0xFFFF) != 0)
			{
				AppPanelTracker.Register(hwnd);
			}
			else
			{
				AppPanelTracker.OnPanelDeactivated(hwnd);
			}
		}
		bool flag;
		switch (msg)
		{
		case 256:
		case 260:
		case 513:
		case 515:
		case 516:
		case 519:
		case 520:
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			WinDHookService.NotifyOwnWindowInteraction();
			if ((NativeMethods.GetWindowLong(hwnd, -20) & 0x8000000) != 0)
			{
				WinDHookService.KeepDesktopForeground();
			}
		}
		switch (msg)
		{
		case 36:
			HandleGetMinMaxInfo(hwnd, lParam, ref handled);
			return IntPtr.Zero;
		case 132:
			if (base.WindowState == WindowState.Normal)
			{
				int num = ((IntPtr)lParam).ToInt32();
				int x = (short)(num & 0xFFFF);
				int y = (short)(num >> 16);
				Point pt;
				try
				{
					pt = PointFromScreen(new Point(x, y));
				}
				catch
				{
					return IntPtr.Zero;
				}
				bool isLeft = pt.X <= 8.0;
				bool isRight = pt.X >= base.Width - 8.0;
				bool isTop = pt.Y <= 8.0;
				bool isBottom = pt.Y >= base.Height - 8.0;
				if (isLeft && isTop)
				{
					handled = true;
					return 13;
				}
				if (isLeft && isBottom)
				{
					handled = true;
					return 16;
				}
				if (isRight && isTop)
				{
					handled = true;
					return 14;
				}
				if (isRight && isBottom)
				{
					handled = true;
					return 17;
				}
				if (isLeft)
				{
					handled = true;
					return 10;
				}
				if (isRight)
				{
					handled = true;
					return 11;
				}
				if (isTop)
				{
					handled = true;
					return 12;
				}
				if (isBottom)
				{
					handled = true;
					return 15;
				}
			}
			break;
		}
		return IntPtr.Zero;
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 2)
		{
			ToggleMaximize();
		}
		else if (e.ButtonState == MouseButtonState.Pressed && base.WindowState == WindowState.Normal)
		{
			DragMove();
		}
	}

	private void SetPosition(int x, int y, int w, int h)
	{
		_align?.SetPosition(x, y, w, h);
	}

	private void OnDragCommittedSave(Rect bounds)
	{
		if (_settings != null)
		{
			_settings.SearchLeft = bounds.X;
			_settings.SearchTop = bounds.Y;
			_settings.SearchWidth = bounds.Width;
			_settings.SearchHeight = bounds.Height;
			_owner?.SaveAppSettings();
		}
	}

	private void SaveBounds()
	{
		if (_settings != null)
		{
			_settings.SearchLeft = base.Left;
			_settings.SearchTop = base.Top;
			_settings.SearchWidth = base.Width;
			_settings.SearchHeight = base.Height;
			_owner?.SaveAppSettings();
		}
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void MinimizeButton_Click(object sender, RoutedEventArgs e)
	{
		base.WindowState = WindowState.Minimized;
	}

	private void MaximizeButton_Click(object sender, RoutedEventArgs e)
	{
		ToggleMaximize();
	}

	private void ToggleMaximize()
	{
		base.WindowState = ((base.WindowState != WindowState.Maximized) ? WindowState.Maximized : WindowState.Normal);
	}

	protected override void OnStateChanged(EventArgs e)
	{
		base.OnStateChanged(e);
		try
		{
			bool max = base.WindowState == WindowState.Maximized;
			MaxRestoreGlyph.Text = (max ? "\ue923" : "\ue922");
			if (MaxRestoreGlyph.Parent is Button btn)
			{
				btn.ToolTip = (max ? "还原" : "最大化");
			}
			RootBorder.CornerRadius = (max ? new CornerRadius(0.0) : new CornerRadius(12.0));
		}
		catch
		{
		}
	}

	private void HandleGetMinMaxInfo(nint hwnd, nint lParam, ref bool handled)
	{
		try
		{
			nint hMon = NativeMethods.MonitorFromWindow(hwnd, 2u);
			if (hMon != IntPtr.Zero)
			{
				NativeMethods.MONITORINFO mi = new NativeMethods.MONITORINFO
				{
					cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
				};
				if (NativeMethods.GetMonitorInfo(hMon, ref mi))
				{
					MINMAXINFO mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
					mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
					mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
					mmi.ptMaxSize.X = mi.rcWork.Width;
					mmi.ptMaxSize.Y = mi.rcWork.Height;
					Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
					handled = true;
				}
			}
		}
		catch
		{
		}
	}

	private void SearchWindow_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		AdjustResultColumns();
	}

	private void AdjustResultColumns()
	{
		if (ResultList.View is GridView gv && gv.Columns.Count >= 2)
		{
			double avail = ResultList.ActualWidth - 514.0;
			if (avail < 160.0)
			{
				avail = 160.0;
			}
			gv.Columns[1].Width = avail;
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/views/searchwindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocater);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	internal Delegate _CreateDelegate(Type delegateType, string handler)
	{
		return Delegate.CreateDelegate(delegateType, this, handler);
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			((SearchWindow)target).SizeChanged += SearchWindow_SizeChanged;
			break;
		case 2:
			RootBorder = (Border)target;
			break;
		case 3:
			((Grid)target).MouseLeftButtonDown += TitleBar_MouseLeftButtonDown;
			break;
		case 4:
			DesktopWidgetToggle = (ToggleButton)target;
			DesktopWidgetToggle.Checked += DesktopWidgetToggle_Checked;
			DesktopWidgetToggle.Unchecked += DesktopWidgetToggle_Unchecked;
			break;
		case 5:
			HotkeyDisplay = (TextBlock)target;
			break;
		case 6:
			BtnChangeHotkey = (Button)target;
			BtnChangeHotkey.Click += BtnChangeHotkey_Click;
			break;
		case 7:
			BtnResetHotkey = (Button)target;
			BtnResetHotkey.Click += BtnResetHotkey_Click;
			break;
		case 8:
			((Button)target).Click += MinimizeButton_Click;
			break;
		case 9:
			((Button)target).Click += MaximizeButton_Click;
			break;
		case 10:
			MaxRestoreGlyph = (TextBlock)target;
			break;
		case 11:
			((Button)target).Click += CloseButton_Click;
			break;
		case 12:
			SearchBox = (TextBox)target;
			SearchBox.KeyDown += SearchBox_KeyDown;
			SearchBox.TextChanged += SearchBox_TextChanged;
			break;
		case 13:
			SearchButton = (Button)target;
			SearchButton.Click += SearchButton_Click;
			break;
		case 14:
			SearchBtnLabel = (TextBlock)target;
			break;
		case 15:
			SearchBtnLoading = (StackPanel)target;
			break;
		case 16:
			SearchSpin = (RotateTransform)target;
			break;
		case 17:
			ScopeToggle = (ToggleButton)target;
			break;
		case 18:
			ScopePopup = (Popup)target;
			break;
		case 19:
			ScopeItemsPanel = (StackPanel)target;
			break;
		case 20:
			FilterAll = (RadioButton)target;
			FilterAll.Checked += FilterTab_Checked;
			break;
		case 21:
			FilterDoc = (RadioButton)target;
			FilterDoc.Checked += FilterTab_Checked;
			break;
		case 22:
			FilterFolder = (RadioButton)target;
			FilterFolder.Checked += FilterTab_Checked;
			break;
		case 23:
			FilterPicture = (RadioButton)target;
			FilterPicture.Checked += FilterTab_Checked;
			break;
		case 24:
			FilterVideo = (RadioButton)target;
			FilterVideo.Checked += FilterTab_Checked;
			break;
		case 25:
			FilterAudio = (RadioButton)target;
			FilterAudio.Checked += FilterTab_Checked;
			break;
		case 26:
			FilterExe = (RadioButton)target;
			FilterExe.Checked += FilterTab_Checked;
			break;
		case 27:
			FilterZip = (RadioButton)target;
			FilterZip.Checked += FilterTab_Checked;
			break;
		case 28:
			InstallBanner = (Border)target;
			break;
		case 29:
			BannerTitle = (TextBlock)target;
			break;
		case 30:
			BannerDesc = (TextBlock)target;
			break;
		case 31:
			InstallButton = (Button)target;
			InstallButton.Click += InstallButton_Click;
			break;
		case 32:
			ResultList = (ListView)target;
			ResultList.MouseDoubleClick += ResultList_MouseDoubleClick;
			ResultList.PreviewMouseLeftButtonDown += ResultList_PreviewMouseLeftButtonDown;
			ResultList.PreviewMouseLeftButtonUp += ResultList_PreviewMouseLeftButtonUp;
			ResultList.MouseMove += ResultList_MouseMove;
			ResultList.KeyDown += ResultList_KeyDown;
			ResultList.ContextMenuOpening += ResultList_ContextMenuOpening;
			break;
		case 34:
			((MenuItem)target).Click += MenuOpen_Click;
			break;
		case 35:
			((MenuItem)target).Click += MenuOpenFolder_Click;
			break;
		case 36:
			((MenuItem)target).Click += MenuCopyFile_Click;
			break;
		case 37:
			((MenuItem)target).Click += MenuCopyPath_Click;
			break;
		case 38:
			((MenuItem)target).Click += MenuDelete_Click;
			break;
		case 39:
			StatusText = (TextBlock)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IStyleConnector.Connect(int connectionId, object target)
	{
		if (connectionId == 33)
		{
			EventSetter eventSetter = new EventSetter();
			eventSetter.Event = ButtonBase.ClickEvent;
			eventSetter.Handler = new RoutedEventHandler(ResultList_ColumnHeader_Click);
			((Style)target).Setters.Add(eventSetter);
		}
	}
}
