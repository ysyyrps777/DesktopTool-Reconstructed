using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopTool.Models;
using DesktopTool.Services;
using DesktopTool.ViewModels;

namespace DesktopTool.Views;

public class MainWindow : Window, IComponentConnector, IStyleConnector
{
	private class ManifestCountDto
	{
		public List<object>? Entries { get; set; }
	}

	private readonly MainViewModel _viewModel;

	private readonly DataService _dataService;

	private AppSettings _settings;

	private readonly ObservableCollection<ToolboxTool> _toolboxTools = new ObservableCollection<ToolboxTool>();

	private readonly ObservableCollection<DataService.BackupInfo> _backups = new ObservableCollection<DataService.BackupInfo>();

	private bool _settingsInitialized;

	private string _originalMode = "Attributes";

	private const int HotkeyId_WakeMainWindow = 1;

	private bool _hotkeyRegistered;

	private const int HotkeyId_OpenSearch = 2;

	private bool _searchHotkeyRegistered;

	private bool _searchHotkeyConflict;

	private SearchWindow? _searchWindow;

	private DesktopSearchWidget? _desktopSearchWidget;

	private bool _isTogglingAutoStart;

	private const double PanelColorSat = 0.65;

	private const double PanelColorLum = 0.45;

	private const string DefaultPanelColorHex = "#1A1A1A";

	private bool _panelColorSuppress;

	private bool _aboutInitialized;

	internal System.Windows.Controls.Button BtnZones;

	internal System.Windows.Controls.Button BtnTodos;

	internal System.Windows.Controls.Button BtnMappings;

	internal System.Windows.Controls.Button BtnToolbox;

	internal System.Windows.Controls.Button BtnSettings;

	internal System.Windows.Controls.Button BtnBackup;

	internal System.Windows.Controls.Button BtnAbout;

	internal System.Windows.Controls.CheckBox ChkAutoStart;

	internal System.Windows.Controls.CheckBox ChkTempShowHidden;

	internal TextBlock TxtPageTitle;

	internal System.Windows.Controls.Button BtnClosePanel;

	internal ScrollViewer ZonesContent;

	internal ItemsControl ZonesList;

	internal Border ZonesEmptyHint;

	internal ScrollViewer TodosContent;

	internal ScrollViewer MappingsContent;

	internal ItemsControl MappingsList;

	internal Border MappingsEmptyHint;

	internal ScrollViewer SettingsContent;

	internal TextBlock OpacityLabel;

	internal Slider OpacitySlider;

	internal Slider IconSpacingSlider;

	internal TextBlock IconSpacingLabel;

	internal Slider PanelColorSlider;

	internal System.Windows.Controls.Button PanelColorResetButton;

	internal Border PanelColorPreview;

	internal TextBlock PanelColorHex;

	internal System.Windows.Controls.TextBox PanelColorInput;

	internal System.Windows.Controls.RadioButton RbStyleSimple;

	internal System.Windows.Controls.RadioButton RbStyleGlass;

	internal System.Windows.Controls.RadioButton RbFontSmall;

	internal System.Windows.Controls.RadioButton RbFontMedium;

	internal System.Windows.Controls.RadioButton RbFontLarge;

	internal System.Windows.Controls.RadioButton RbFontXLarge;

	internal System.Windows.Controls.RadioButton RbTodoFaded;

	internal System.Windows.Controls.RadioButton RbTodoStrikethrough;

	internal TextBlock TxtHotkeyDisplay;

	internal System.Windows.Controls.Button BtnChangeHotkey;

	internal System.Windows.Controls.Button BtnResetHotkey;

	internal System.Windows.Controls.RadioButton RbModeAttribute;

	internal System.Windows.Controls.RadioButton RbModePhysical;

	internal System.Windows.Controls.Button SwitchModeButton;

	internal TextBlock CurrentHiddenPathText;

	internal System.Windows.Controls.TextBox CustomPathTextBox;

	internal System.Windows.Controls.Button BtnRestoreAll;

	internal ScrollViewer ToolboxContent;

	internal ItemsControl ToolboxList;

	internal ScrollViewer AboutContent;

	internal TextBlock AboutVersionText;

	internal StackPanel AboutUpdatePanel;

	internal TextBlock AboutUpdateLabel;

	internal Border AboutDonateSeparator;

	internal StackPanel AboutDonatePanel;

	internal ScrollViewer BackupContent;

	internal System.Windows.Controls.Button BtnCreateBackup;

	internal TextBlock TxtBackupHint;

	internal ItemsControl BackupList;

	internal TextBlock TxtBackupEmpty;

	private bool _contentLoaded;

	public bool SearchHotkeyBlocked => _searchHotkeyConflict;

	public MainViewModel ViewModel => _viewModel;

	public MainWindow()
	{
		try
		{
			InitializeComponent();
			BackupList.ItemsSource = _backups;
			base.SourceInitialized += delegate
			{
				HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
				RegisterCurrentHotkey();
				RegisterSearchHotkey();
			};
			SetWindowIcon();
			_dataService = new DataService();
			_viewModel = new MainViewModel(_dataService);
			base.DataContext = _viewModel;
			_toolboxTools.Add(new ToolboxTool
			{
				Id = "FileSearch",
				Name = "文件搜索",
				Icon = "\ud83d\udd0d",
				IconPath = "M11 11 m-8 0 a8 8 0 1 0 16 0 a8 8 0 1 0 -16 0 M21 21 l-4.3 -4.3",
				IconImage = "pack://application:,,,/Images/Nav/filesearch2.png",
				IconContainerless = true,
				Description = "毫秒级全盘极速搜索"
			});
			ToolboxList.ItemsSource = _toolboxTools;
			_settings = _viewModel.Settings;
			_ = new WindowInteropHelper(this).Handle;
			try
			{
				AppSettings settings = _viewModel.Settings;
				if (settings != null && settings.IntentAutoStartEnabled)
				{
					StartupService.TryRestoreAutoStartFromIntent(userIntent: true);
				}
				ChkAutoStart.IsChecked = StartupService.IsAutoStartEnabled();
			}
			catch (Exception)
			{
				try
				{
					ChkAutoStart.IsChecked = StartupService.IsAutoStartEnabled();
				}
				catch
				{
				}
			}
			try
			{
				DesktopIconService.ReconcileTempShowHiddenOnStartup();
			}
			catch (Exception)
			{
			}
			NavigateTo("Backup");
			if (_settings.DesktopSearchWidgetEnabled)
			{
				base.Dispatcher.BeginInvoke(new Action(ShowDesktopSearchWidget), DispatcherPriority.Loaded);
			}
			_viewModel.Zones.CollectionChanged += delegate
			{
				UpdateZonesEmptyHint();
			};
			_viewModel.Mappings.CollectionChanged += delegate
			{
				UpdateMappingsEmptyHint();
			};
			base.Closed += delegate
			{
				UnregisterCurrentHotkey();
				UnregisterSearchHotkey();
				_viewModel.SaveZones();
				_viewModel.SaveTodos();
				_viewModel.SaveMappings();
			};
		}
		catch (Exception value)
		{
			LogHelper.Append(Path.Combine(Path.GetTempPath(), "DesktopTool_Crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MainWindow构造失败: {value}\n");
			throw;
		}
	}

	private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ButtonState == MouseButtonState.Pressed)
		{
			DragMove();
		}
	}

	private void NavButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { Tag: string tag })
		{
			NavigateTo(tag);
		}
	}

	private void NavigateTo(string page)
	{
		ResetNavButtonStyle(BtnZones);
		ResetNavButtonStyle(BtnMappings);
		ResetNavButtonStyle(BtnTodos);
		ResetNavButtonStyle(BtnToolbox);
		ResetNavButtonStyle(BtnSettings);
		ResetNavButtonStyle(BtnAbout);
		ResetNavButtonStyle(BtnBackup);
		ZonesContent.Visibility = Visibility.Collapsed;
		MappingsContent.Visibility = Visibility.Collapsed;
		TodosContent.Visibility = Visibility.Collapsed;
		ToolboxContent.Visibility = Visibility.Collapsed;
		SettingsContent.Visibility = Visibility.Collapsed;
		AboutContent.Visibility = Visibility.Collapsed;
		BackupContent.Visibility = Visibility.Collapsed;
		if (page == null)
		{
			return;
		}
		switch (page.Length)
		{
		case 5:
			switch (page[0])
			{
			case 'Z':
				if (page == "Zones")
				{
					BtnZones.Style = (Style)FindResource("SidebarButtonActive");
					TxtPageTitle.Text = "收纳区管理";
					ZonesContent.Visibility = Visibility.Visible;
					UpdateZonesEmptyHint();
				}
				break;
			case 'T':
				if (page == "Todos")
				{
					BtnTodos.Style = (Style)FindResource("SidebarButtonActive");
					TxtPageTitle.Text = "待办区管理";
					TodosContent.Visibility = Visibility.Visible;
				}
				break;
			case 'A':
				if (page == "About")
				{
					BtnAbout.Style = (Style)FindResource("SidebarButtonActive");
					TxtPageTitle.Text = "关于软件";
					InitAboutPanel();
					AboutContent.Visibility = Visibility.Visible;
				}
				break;
			}
			break;
		case 8:
			switch (page[0])
			{
			case 'M':
				if (page == "Mappings")
				{
					BtnMappings.Style = (Style)FindResource("SidebarButtonActive");
					TxtPageTitle.Text = "映射区管理";
					MappingsContent.Visibility = Visibility.Visible;
					UpdateMappingsEmptyHint();
					_viewModel.RefreshMappingFileCounts();
				}
				break;
			case 'S':
				if (page == "Settings")
				{
					BtnSettings.Style = (Style)FindResource("SidebarButtonActive");
					TxtPageTitle.Text = "系统设置";
					InitSettingsPanel();
					SettingsContent.Visibility = Visibility.Visible;
				}
				break;
			}
			break;
		case 6:
			if (page == "Backup")
			{
				BtnBackup.Style = (Style)FindResource("SidebarButtonActive");
				TxtPageTitle.Text = "备份与恢复";
				LoadBackups();
				BackupContent.Visibility = Visibility.Visible;
			}
			break;
		case 7:
			if (page == "Toolbox")
			{
				BtnToolbox.Style = (Style)FindResource("SidebarButtonActive");
				TxtPageTitle.Text = "工具箱";
				ToolboxContent.Visibility = Visibility.Visible;
			}
			break;
		}
	}

	private static void ResetNavButtonStyle(System.Windows.Controls.Button btn)
	{
		btn.Style = (Style)btn.FindResource("SidebarButton");
	}

	private void LoadBackups()
	{
		_backups.Clear();
		foreach (DataService.BackupInfo b in DataService.GetManualBackups())
		{
			_backups.Add(b);
		}
		TxtBackupEmpty.Visibility = ((_backups.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
		TxtBackupHint.Visibility = ((_backups.Count <= 50) ? Visibility.Collapsed : Visibility.Visible);
		TxtBackupHint.Text = ((_backups.Count > 50) ? $"备份数量已达 {_backups.Count} 个，建议删除不再需要的旧备份以节省磁盘空间。" : "");
	}

	private void BtnCreateBackup_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			_viewModel.SaveAllData();
			if (string.IsNullOrEmpty(DataService.CreateManualBackup()))
			{
				System.Windows.MessageBox.Show("创建备份失败，请查看日志。", "备份失败", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			LoadBackups();
			TxtBackupHint.Text = ((_backups.Count > 50) ? $"已创建备份。备份数量已达 {_backups.Count} 个，建议删除不再需要的旧备份以节省磁盘空间。" : "已创建备份。");
			TxtBackupHint.Visibility = Visibility.Visible;
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("创建备份异常：" + ex.Message, "备份失败", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private void BtnRestoreBackup_Click(object sender, RoutedEventArgs e)
	{
		if (!(sender is System.Windows.Controls.Button { DataContext: DataService.BackupInfo info }) || System.Windows.MessageBox.Show("确定要恢复到「" + info.Name + "」的备份吗？\n\n当前配置将被覆盖，软件会自动重启以应用结果。", "恢复备份", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
		{
			return;
		}
		try
		{
			_viewModel.SaveAllData();
			if (!DataService.RestoreManualBackup(info.Id))
			{
				System.Windows.MessageBox.Show("恢复备份失败，请查看日志。", "恢复失败", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			try
			{
				AppSettings restored = DataService.LoadSettingsStatic();
				if (restored != null)
				{
					StartupService.SetAutoStart(restored.IntentAutoStartEnabled);
				}
			}
			catch
			{
			}
			App.IsRestoringBackup = true;
			App.RestartApplication();
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("恢复备份异常：" + ex.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private void BtnDeleteBackup_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { DataContext: DataService.BackupInfo info } && System.Windows.MessageBox.Show("确定删除备份「" + info.Name + "」吗？此操作不可撤销。", "删除备份", MessageBoxButton.OKCancel, MessageBoxImage.Exclamation) == MessageBoxResult.OK)
		{
			DataService.DeleteManualBackup(info.Id);
			LoadBackups();
		}
	}

	public void OpenSearchWindow(string? initialQuery = null)
	{
		try
		{
			if (_searchWindow != null && _searchWindow.IsLoaded)
			{
				_searchWindow.Activate();
				if (initialQuery != null)
				{
					_searchWindow.ApplyExternalQuery(initialQuery);
				}
				return;
			}
			_searchWindow = new SearchWindow(this, _settings);
			_searchWindow.Closed += delegate
			{
				_searchWindow = null;
			};
			_searchWindow.Show();
			if (initialQuery != null)
			{
				_searchWindow.ApplyExternalQuery(initialQuery);
			}
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("打开文件搜索失败：" + ex.Message, "文件搜索", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	public void ShowDesktopSearchWidget()
	{
		try
		{
			LogHelper.Append(Path.Combine(App.GetAppDataDir(), "debug_desktopwidget.log"), $"{DateTime.Now:HH:mm:ss.fff} [Main] ShowDesktopSearchWidget 调用，现有实例={((_desktopSearchWidget == null) ? "无" : "有")}{Environment.NewLine}");
		}
		catch
		{
		}
		if (_desktopSearchWidget != null && _desktopSearchWidget.IsLoaded)
		{
			_desktopSearchWidget.Activate();
			return;
		}
		_desktopSearchWidget = new DesktopSearchWidget(this, _settings);
		_desktopSearchWidget.Closed += delegate
		{
			_desktopSearchWidget = null;
		};
		_desktopSearchWidget.Show();
	}

	public void HideDesktopSearchWidget()
	{
		if (_desktopSearchWidget != null)
		{
			DesktopSearchWidget w = _desktopSearchWidget;
			_desktopSearchWidget = null;
			try
			{
				w.Close();
			}
			catch
			{
			}
		}
		_settings.DesktopSearchWidgetEnabled = false;
		_settings.SearchWidgetLeft = -1.0;
		_settings.SearchWidgetTop = -1.0;
		try
		{
			_dataService.SaveSettings(_settings);
		}
		catch
		{
		}
	}

	public void SaveAppSettings()
	{
		_dataService.SaveSettings(_settings);
	}

	private void ToolboxTool_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { Tag: string id } && id == "FileSearch")
		{
			OpenSearchWindow();
		}
	}

	private void UpdateZonesEmptyHint()
	{
		ZonesEmptyHint.Visibility = ((_viewModel.Zones.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
	}

	private void UpdateMappingsEmptyHint()
	{
		MappingsEmptyHint.Visibility = ((_viewModel.Mappings.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
	}

	private void ZoneItem_Click(object sender, MouseButtonEventArgs e)
	{
		if (sender is Border { DataContext: IconZone zone })
		{
			_viewModel.OpenExistingZone(zone);
		}
	}

	private void MappingItem_Click(object sender, MouseButtonEventArgs e)
	{
		if (sender is Border { DataContext: MappingZone mapping })
		{
			_viewModel.OpenExistingMapping(mapping);
		}
	}

	private void ResetMappingPosition_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { Tag: MappingZone mapping })
		{
			_viewModel.ResetMappingPosition(mapping);
		}
	}

	private void SetMappingFolder_Click(object sender, RoutedEventArgs e)
	{
		if (!(sender is System.Windows.Controls.Button { Tag: MappingZone mapping }))
		{
			return;
		}
		FolderBrowserDialog dialog = new FolderBrowserDialog
		{
			Description = "选择要映射的文件夹",
			ShowNewFolderButton = false
		};
		if (!string.IsNullOrEmpty(mapping.FolderPath) && Directory.Exists(mapping.FolderPath))
		{
			dialog.SelectedPath = mapping.FolderPath;
		}
		if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			mapping.FolderPath = dialog.SelectedPath;
			string folderName = Path.GetFileName(dialog.SelectedPath.TrimEnd('\\'));
			if (!string.IsNullOrEmpty(folderName))
			{
				mapping.Name = folderName;
			}
			_viewModel.SaveMappings();
			_viewModel.OpenExistingMapping(mapping);
		}
	}

	private void DeleteZone_Click(object sender, RoutedEventArgs e)
	{
		if (!(sender is System.Windows.Controls.Button { Tag: IconZone zone }) || System.Windows.MessageBox.Show("确定要解散收纳区「" + zone.Name + "」吗？\n收纳区内的图标将回归桌面原位置。", "解散收纳区", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		List<string> allLocks = new List<string>();
		foreach (IconGroup group in zone.Groups)
		{
			foreach (ZoneIcon icon in group.Icons)
			{
				if (!icon.IsHidden || string.IsNullOrEmpty(icon.HiddenPath))
				{
					continue;
				}
				try
				{
					foreach (var (fileName, procName) in DesktopIconService.CheckFileLocks(icon.HiddenPath))
					{
						allLocks.Add($"• {fileName} — 被 {procName} 占用");
					}
				}
				catch
				{
					allLocks.Add("• " + (icon.DisplayName ?? Path.GetFileName(icon.FilePath)) + " — 检测异常，可能被占用");
				}
			}
		}
		if (allLocks.Count > 0)
		{
			List<string> distinctLocks = allLocks.Distinct().Take(8).ToList();
			System.Windows.MessageBox.Show("收纳区「" + zone.Name + "」中有文件正在被其他应用占用：\n\n" + string.Join("\n", distinctLocks) + ((allLocks.Distinct().Count() > 8) ? $"\n... 等共 {allLocks.Distinct().Count()} 处占用" : "") + "\n\n关闭相关应用后再次尝试解散。", "文件被占用", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		_viewModel.DeleteZone(zone);
		_viewModel.RestoreZoneIcons(zone);
		foreach (object window in System.Windows.Application.Current.Windows)
		{
			if (window is ZoneWindow zw && zw.ZoneId == zone.Id)
			{
				zw.Close();
			}
		}
	}

	private void DismissZoneFromList_Click(object sender, RoutedEventArgs e)
	{
		if (!(sender is System.Windows.Controls.Button { Tag: var tag }))
		{
			return;
		}
		IconZone zone = tag as IconZone;
		if (zone != null)
		{
			if (System.Windows.Application.Current.Windows.OfType<ZoneWindow>().FirstOrDefault((ZoneWindow w) => w.ZoneId == zone.Id)?.DataContext is ZoneViewModel zvm && zvm.DismissZoneCommand.CanExecute(null))
			{
				zvm.DismissZoneCommand.Execute(null);
				return;
			}
			_viewModel.RestoreZoneIcons(zone);
			_viewModel.DeleteZone(zone);
		}
	}

	private void DismissMappingFromList_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { Tag: MappingZone mapping } && System.Windows.MessageBox.Show("确定要解散映射区「" + mapping.Name + "」吗？\n\n解散后可从主面板「映射区管理」中重新打开。", "解散映射区", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			MappingWindow mw = _viewModel.GetOpenMappingWindow(mapping.Id);
			if (mw != null)
			{
				mw.CloseMappingZone();
			}
			else
			{
				_viewModel.DeleteMappingZone(mapping);
			}
		}
	}

	private void ResetZonePosition_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button { Tag: IconZone zone })
		{
			_viewModel.ResetZonePosition(zone);
		}
	}

	private void TodoItem_Click(object sender, MouseButtonEventArgs e)
	{
		_viewModel.CreateTodoCommand.Execute(null);
	}

	private void ResetTodoPosition_Click(object sender, RoutedEventArgs e)
	{
		_viewModel.ResetTodoPosition();
	}

	private void RestoreTodoWindow_Click(object sender, RoutedEventArgs e)
	{
		_viewModel.RestoreTodoWindow();
	}

	private void OpenRecurringTasks_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			new RecurringTaskWindow(_viewModel).Show();
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("打开循环任务窗口失败：\n" + ex.Message + "\n\n" + ex.StackTrace, "循环任务", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private async void ChkAutoStart_Click(object sender, RoutedEventArgs e)
	{
		if (_isTogglingAutoStart)
		{
			return;
		}
		_isTogglingAutoStart = true;
		bool desired = ChkAutoStart.IsChecked == true;
		ChkAutoStart.IsEnabled = false;
		Window loadingWindow = new Window
		{
			Title = "自启动设置 — 请稍候",
			Width = 360.0,
			Height = 130.0,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			WindowStyle = WindowStyle.None,
			AllowsTransparency = true,
			Background = Brushes.Transparent,
			ResizeMode = ResizeMode.NoResize,
			ShowInTaskbar = false,
			Topmost = true,
			Content = new Border
			{
				Background = new SolidColorBrush(Color.FromRgb(30, 30, 46)),
				CornerRadius = new CornerRadius(8.0),
				BorderBrush = new SolidColorBrush(Color.FromRgb(68, 68, 102)),
				BorderThickness = new Thickness(1.0),
				Padding = new Thickness(20.0),
				Child = new StackPanel
				{
					VerticalAlignment = VerticalAlignment.Center,
					Children = 
					{
						(UIElement)new TextBlock
						{
							Text = (desired ? "正在开启开机自启动，请稍候..." : "正在关闭开机自启动，请稍候..."),
							HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
							FontSize = 13.0,
							Margin = new Thickness(0.0, 0.0, 0.0, 16.0),
							Foreground = new SolidColorBrush(Color.FromRgb(224, 224, 240))
						},
						(UIElement)new System.Windows.Controls.ProgressBar
						{
							IsIndeterminate = true,
							Height = 20.0,
							Width = 280.0,
							HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
							Foreground = new SolidColorBrush(Color.FromRgb(74, 144, 217)),
							Background = new SolidColorBrush(Color.FromRgb(42, 42, 58))
						}
					}
				}
			}
		};
		(bool actual, string errorDetails) result = default((bool, string));
		DateTime startTime = DateTime.Now;
		bool showLoading = true;
		if (showLoading)
		{
			loadingWindow.Show();
		}
		try
		{
			result = await Task.Run(() => StartupService.SetAutoStart(desired));
			if (showLoading)
			{
				double elapsed = (DateTime.Now - startTime).TotalMilliseconds;
				if (elapsed < 600.0)
				{
					await Task.Delay((int)(600.0 - elapsed));
				}
			}
		}
		finally
		{
			if (showLoading)
			{
				loadingWindow.Close();
			}
			ChkAutoStart.IsEnabled = true;
		}
		var (actual, errorDetails) = result;
		try
		{
			actual = StartupService.IsAutoStartEnabled();
		}
		catch
		{
		}
		try
		{
			if (_viewModel.Settings != null)
			{
				_viewModel.Settings.IntentAutoStartEnabled = actual;
				_dataService.SaveSettings(_viewModel.Settings);
			}
		}
		catch (Exception)
		{
		}
		if (actual != desired)
		{
			ChkAutoStart.IsChecked = actual;
			string msg = (actual ? "开机自启动无法关闭，可能被安全软件拦截。\n请尝试手动处理。" : (string.IsNullOrEmpty(errorDetails) ? "开机自启动无法开启，可能被安全软件拦截。\n请尝试手动处理。" : ("开机自启动设置失败，原因如下：\n\n" + errorDetails + "\n\n建议：\n• 检查是否有安全软件（杀毒/防火墙）拦截\n• 尝试以管理员身份运行桌面工具箱\n• 重启电脑后再试")));
			System.Windows.MessageBox.Show(msg, "桌面工具箱 — 自启动设置失败", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		_isTogglingAutoStart = false;
	}

	private async void ChkTempShowHidden_Click(object sender, RoutedEventArgs e)
	{
		bool desired = ChkTempShowHidden.IsChecked == true;
		ChkTempShowHidden.IsEnabled = false;
		try
		{
			bool success = await Task.Run(() => DesktopIconService.SetTemporaryShowHiddenFiles(desired));
			ChkTempShowHidden.IsChecked = success
				? DesktopIconService.IsExplorerShowingHiddenFiles()
				: !desired;

			if (!success)
			{
				System.Windows.MessageBox.Show(
					"无法修改 Windows 隐藏文件显示设置，请稍后重试。",
					"操作失败",
					MessageBoxButton.OK,
					MessageBoxImage.Warning);
			}
		}
		finally
		{
			ChkTempShowHidden.IsEnabled = true;
		}
	}

	public void SetTemporaryHiddenVisibilityUiState(bool showing)
	{
		if (!base.Dispatcher.CheckAccess())
		{
			base.Dispatcher.BeginInvoke(new Action(() => SetTemporaryHiddenVisibilityUiState(showing)));
			return;
		}

		if (ChkTempShowHidden != null)
		{
			ChkTempShowHidden.IsChecked = showing;
		}
	}

	public void ShowSettingsPanel()
	{
		base.Opacity = 1.0;
		Show();
		Activate();
		NavigateTo("Settings");
	}

	private void InitSettingsPanel()
	{
		if (!_settingsInitialized)
		{
			_settingsInitialized = true;
			_settings = _viewModel.Settings;
			OpacitySlider.Value = _viewModel.GlobalOpacity;
			UpdateSettingsOpacityLabel(OpacitySlider.Value);
			IconSpacingSlider.Value = _viewModel.IconSpacing;
			UpdateIconSpacingLabel(_viewModel.IconSpacing);
			InitPanelColorFromSettings();
			int fontLevel = Math.Clamp(_settings.FontSizeLevel, 0, 3);
			System.Windows.Controls.RadioButton[] fontRadios = new System.Windows.Controls.RadioButton[4] { RbFontSmall, RbFontMedium, RbFontLarge, RbFontXLarge };
			if (fontLevel >= 0 && fontLevel < fontRadios.Length)
			{
				fontRadios[fontLevel].IsChecked = true;
			}
			if (_settings.WindowStyle == "Glass")
			{
				RbStyleGlass.IsChecked = true;
			}
			else
			{
				RbStyleSimple.IsChecked = true;
			}
			_originalMode = _settings.HidingMode ?? "Attributes";
			if (_originalMode == "Physical")
			{
				RbModePhysical.IsChecked = true;
			}
			else
			{
				RbModeAttribute.IsChecked = true;
			}
			string currentPath = DesktopIconService.CustomHiddenDir ?? DesktopIconService.HiddenDir;
			CurrentHiddenPathText.Text = "当前路径：" + currentPath;
			CustomPathTextBox.Text = _settings.CustomHiddenPath ?? "";
			ChkTempShowHidden.IsChecked = DesktopIconService.IsExplorerShowingHiddenFiles();
			if (_settings.CompletedTodoStyle == "Strikethrough")
			{
				RbTodoStrikethrough.IsChecked = true;
			}
			else
			{
				RbTodoFaded.IsChecked = true;
			}
			TxtHotkeyDisplay.Text = FormatHotkeyDisplay(_settings.HotkeyModifiers, _settings.HotkeyKey);
			RegisterCurrentHotkey();
		}
	}

	private void UpdateHideModeInfo()
	{
	}

	private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_settingsInitialized)
		{
			double v = Math.Round(e.NewValue, 2);
			UpdateSettingsOpacityLabel(v);
			_viewModel.GlobalOpacity = v;
		}
	}

	private void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is Slider slider && (!(e.OriginalSource is DependencyObject dep) || !IsThumbOrChild(dep)))
		{
			double mouseX = e.GetPosition(slider).X;
			double sliderWidth = slider.ActualWidth;
			double trackRatio = Math.Max(0.0, Math.Min(1.0, (mouseX - 8.0) / (sliderWidth - 16.0)));
			double newValue = slider.Minimum + trackRatio * (slider.Maximum - slider.Minimum);
			double tick = slider.TickFrequency;
			newValue = Math.Round(newValue / tick) * tick;
			newValue = Math.Max(slider.Minimum, Math.Min(slider.Maximum, newValue));
			slider.Value = newValue;
			e.Handled = true;
		}
	}

	private void UpdateSettingsOpacityLabel(double opacity)
	{
		OpacityLabel.Text = $"标题栏和背景透明度 {opacity * 100.0:F0}%";
	}

	private void IconSpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_settingsInitialized)
		{
			int v = (int)Math.Round(e.NewValue);
			UpdateIconSpacingLabel(v);
			_viewModel.IconSpacing = v;
		}
	}

	private void UpdateIconSpacingLabel(int pct)
	{
		IconSpacingLabel.Text = $"{pct}%";
	}

	private void PanelColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_panelColorSuppress && _settingsInitialized)
		{
			UpdatePanelColorFromHue(e.NewValue);
		}
	}

	private void PanelColorResetButton_Click(object sender, RoutedEventArgs e)
	{
		(int r, int g, int b) tuple = ParseHexColor("#1A1A1A");
		int r = tuple.r;
		int g = tuple.g;
		int b = tuple.b;
		_panelColorSuppress = true;
		PanelColorSlider.Value = RgbToHue(r, g, b);
		_panelColorSuppress = false;
		PanelColorHex.Text = "#1A1A1A";
		PanelColorInput.Text = "#1A1A1A";
		PanelColorPreview.Background = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
		_viewModel.PanelColor = "#1A1A1A";
	}

	private void UpdatePanelColorFromHue(double hue)
	{
		(int r, int g, int b) tuple = HslToRgb(hue, 0.65, 0.45);
		int r = tuple.r;
		int g = tuple.g;
		int b = tuple.b;
		string hex = $"#{r:X2}{g:X2}{b:X2}";
		PanelColorHex.Text = hex;
		PanelColorInput.Text = hex;
		PanelColorPreview.Background = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
		_viewModel.PanelColor = hex;
	}

	private void InitPanelColorFromSettings()
	{
		(int r, int g, int b) tuple = ParseHexColor(_viewModel.PanelColor);
		int r = tuple.r;
		int g = tuple.g;
		int b = tuple.b;
		_panelColorSuppress = true;
		PanelColorSlider.Value = RgbToHue(r, g, b);
		_panelColorSuppress = false;
		PanelColorHex.Text = _viewModel.PanelColor;
		PanelColorPreview.Background = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
		PanelColorInput.Text = _viewModel.PanelColor;
	}

	private void PanelColorInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key == Key.Return)
		{
			ApplyPanelColorHex(PanelColorInput.Text);
			PanelColorInput.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			PanelColorInput.Text = _viewModel.PanelColor;
			PanelColorInput.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
			e.Handled = true;
		}
	}

	private void PanelColorInput_LostFocus(object sender, RoutedEventArgs e)
	{
		ApplyPanelColorHex(PanelColorInput.Text);
	}

	private void ApplyPanelColorHex(string input)
	{
		string hex = (input ?? string.Empty).Trim();
		if (hex.Length > 0 && hex[0] != '#')
		{
			hex = "#" + hex;
		}
		if (!TryParseHexColor(hex, out (int, int, int) rgb))
		{
			PanelColorInput.Text = _viewModel.PanelColor;
			return;
		}
		string normalized = $"#{rgb.Item1:X2}{rgb.Item2:X2}{rgb.Item3:X2}";
		PanelColorHex.Text = normalized;
		PanelColorPreview.Background = new SolidColorBrush(Color.FromRgb((byte)rgb.Item1, (byte)rgb.Item2, (byte)rgb.Item3));
		_viewModel.PanelColor = normalized;
	}

	private static bool TryParseHexColor(string hex, out (int r, int g, int b) result)
	{
		result = (r: 26, g: 26, b: 26);
		try
		{
			if (string.IsNullOrEmpty(hex) || hex[0] != '#')
			{
				return false;
			}
			string h = hex.Substring(1);
			if (h.Length == 3)
			{
				h = new string(new char[6]
				{
					h[0],
					h[0],
					h[1],
					h[1],
					h[2],
					h[2]
				});
			}
			if (h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, null, out var c))
			{
				result = (r: (c >> 16) & 0xFF, g: (c >> 8) & 0xFF, b: c & 0xFF);
				return true;
			}
			if (h.Length == 8 && int.TryParse(h, NumberStyles.HexNumber, null, out var c8))
			{
				result = (r: (c8 >> 16) & 0xFF, g: (c8 >> 8) & 0xFF, b: c8 & 0xFF);
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	private static (int r, int g, int b) HslToRgb(double hue, double s, double l)
	{
		double hn = (hue % 360.0 + 360.0) % 360.0 / 360.0;
		double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
		double x = c * (1.0 - Math.Abs(hn * 6.0 % 2.0 - 1.0));
		double m = l - c / 2.0;
		double r1;
		double g1;
		double b1;
		switch ((int)(hn * 6.0) % 6)
		{
		case 0:
			r1 = c;
			g1 = x;
			b1 = 0.0;
			break;
		case 1:
			r1 = x;
			g1 = c;
			b1 = 0.0;
			break;
		case 2:
			r1 = 0.0;
			g1 = c;
			b1 = x;
			break;
		case 3:
			r1 = 0.0;
			g1 = x;
			b1 = c;
			break;
		case 4:
			r1 = x;
			g1 = 0.0;
			b1 = c;
			break;
		default:
			r1 = c;
			g1 = 0.0;
			b1 = x;
			break;
		}
		return (r: ClampByte((r1 + m) * 255.0), g: ClampByte((g1 + m) * 255.0), b: ClampByte((b1 + m) * 255.0));
	}

	private static double RgbToHue(int r, int g, int b)
	{
		double rn = (double)r / 255.0;
		double gn = (double)g / 255.0;
		double bn = (double)b / 255.0;
		double max = Math.Max(rn, Math.Max(gn, bn));
		double min = Math.Min(rn, Math.Min(gn, bn));
		double delta = max - min;
		if (delta < 0.04)
		{
			return 210.0;
		}
		double h = ((max == rn) ? (60.0 * ((gn - bn) / delta % 6.0)) : ((max != gn) ? (60.0 * ((rn - gn) / delta + 4.0)) : (60.0 * ((bn - rn) / delta + 2.0))));
		if (h < 0.0)
		{
			h += 360.0;
		}
		return h;
	}

	private static int ClampByte(double v)
	{
		return (int)Math.Max(0.0, Math.Min(255.0, Math.Round(v)));
	}

	private static (int r, int g, int b) ParseHexColor(string hex)
	{
		try
		{
			if (!string.IsNullOrEmpty(hex) && hex[0] == '#')
			{
				string h = hex.Substring(1);
				if (h.Length == 3)
				{
					h = new string(new char[6]
					{
						h[0],
						h[0],
						h[1],
						h[1],
						h[2],
						h[2]
					});
				}
				if (h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, null, out var c))
				{
					return (r: (c >> 16) & 0xFF, g: (c >> 8) & 0xFF, b: c & 0xFF);
				}
				if (h.Length == 8 && int.TryParse(h, NumberStyles.HexNumber, null, out var c8))
				{
					return (r: (c8 >> 16) & 0xFF, g: (c8 >> 8) & 0xFF, b: c8 & 0xFF);
				}
			}
		}
		catch
		{
		}
		return (r: 26, g: 26, b: 26);
	}

	private static bool IsThumbOrChild(DependencyObject? element)
	{
		while (element != null)
		{
			if (element is Thumb)
			{
				return true;
			}
			element = VisualTreeHelper.GetParent(element);
		}
		return false;
	}

	private void FontSizeRadio_Click(object sender, RoutedEventArgs e)
	{
		if (_settingsInitialized && sender is System.Windows.Controls.RadioButton { Tag: var tag } && int.TryParse(tag?.ToString(), out var level))
		{
			_viewModel.SetFontSizeLevel(level);
		}
	}

	private void CompletedTodoStyle_Checked(object sender, RoutedEventArgs e)
	{
		if (_settingsInitialized)
		{
			_settings.CompletedTodoStyle = ((RbTodoStrikethrough.IsChecked == true) ? "Strikethrough" : "Faded");
			_dataService.SaveSettings(_settings);
			_viewModel.CompletedTodoStyle = _settings.CompletedTodoStyle;
		}
	}

	private void WindowStyleReload_Click(object sender, RoutedEventArgs e)
	{
		if (_settingsInitialized)
		{
			string style = ((RbStyleGlass.IsChecked == true) ? "Glass" : "Simple");
			_viewModel.WindowStyle = style;
			_viewModel.ReloadWindowsWithNewStyle();
		}
	}

	private void BrowsePath_Click(object sender, RoutedEventArgs e)
	{
		using FolderBrowserDialog dialog = new FolderBrowserDialog
		{
			Description = "选择隐藏文件存储目录",
			ShowNewFolderButton = true,
			SelectedPath = CustomPathTextBox.Text
		};
		if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
		{
			CustomPathTextBox.Text = dialog.SelectedPath.Trim();
		}
	}

	private void CurrentPath_Click(object sender, MouseButtonEventArgs e)
	{
		try
		{
			string dir = DesktopIconService.HiddenDir;
			if (Directory.Exists(dir))
			{
				Process.Start("explorer.exe", dir);
			}
		}
		catch
		{
		}
	}

	private void HidingMode_Checked(object sender, RoutedEventArgs e)
	{
		if (_settingsInitialized)
		{
			string selectedMode = ((RbModePhysical.IsChecked == true) ? "Physical" : "Attributes");
			bool changed = !string.Equals(selectedMode, _originalMode, StringComparison.OrdinalIgnoreCase);
			SwitchModeButton.IsEnabled = changed;
			if (changed)
			{
				SwitchModeButton.Content = ((selectedMode == "Physical") ? "切换到路径隐藏（物理收纳）" : "切换到属性隐藏（视觉收纳）");
				SwitchModeButton.Background = ((selectedMode == "Physical") ? new SolidColorBrush(Color.FromRgb(232, 141, 60)) : new SolidColorBrush(Color.FromRgb(74, 144, 217)));
			}
			else
			{
				SwitchModeButton.Content = "✔ 已是当前模式";
				SwitchModeButton.Background = new SolidColorBrush(Color.FromRgb(85, 85, 101));
			}
		}
	}

	private async void SwitchModeButton_Click(object sender, RoutedEventArgs e)
	{
		string targetMode = ((RbModePhysical.IsChecked == true) ? "Physical" : "Attributes");
		string targetLabel = ((targetMode == "Physical") ? "路径隐藏（物理收纳）" : "属性隐藏（视觉收纳）");
		int needMigration = DesktopIconService.CountModeMigrationNeeded(_viewModel.Zones, targetMode);
		int manifestOrphans = 0;
		if (targetMode == "Attributes")
		{
			manifestOrphans = DesktopIconService.CountManifestOrphans(_viewModel.Zones);
			_ = 0;
		}
		string message = ((needMigration == 0 && manifestOrphans == 0) ? ("当前没有需要迁移的图标。\n\n将收纳模式切换为「" + targetLabel + "」后，新收纳的图标将直接使用此模式。") : ((needMigration > 0 && manifestOrphans > 0) ? $"将收纳模式切换为「{targetLabel}」\n\n• {needMigration} 个已收纳图标需要迁移\n• 额外检测到 {manifestOrphans} 个未跟踪的物理隐藏文件将被修复\n• 迁移过程受进度保护，不会丢失文件\n\n确定继续？" : ((needMigration != 0 || manifestOrphans <= 0) ? $"将收纳模式切换为「{targetLabel}」\n\n• 有 {needMigration} 个已收纳图标需要迁移到新模式\n• 迁移过程受进度保护，不会丢失文件\n• 单个文件失败不影响其他文件\n\n确定继续？" : $"将收纳模式切换为「{targetLabel}」\n\n• 检测到 {manifestOrphans} 个物理隐藏文件未被当前数据跟踪\n• 将通过恢复清单自动修复并迁移到新模式\n• 迁移过程受进度保护，不会丢失文件\n\n确定继续？")));
		if (System.Windows.MessageBox.Show(message, "切换收纳模式", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		if (targetMode == "Attributes")
		{
			(bool, string) ensureResult = DesktopIconService.EnsureShowSuperHiddenForAttributeMode();
			if (!ensureResult.Item1)
			{
				System.Windows.MessageBox.Show(ensureResult.Item2, "无法切换收纳模式", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				HidingMode_Checked(sender, e);
				return;
			}
		}
		SwitchModeButton.IsEnabled = false;
		SwitchModeButton.Content = "正在切换…";
		RbModeAttribute.IsEnabled = false;
		RbModePhysical.IsEnabled = false;
		DesktopIconService.ModeSwitchResult result = new DesktopIconService.ModeSwitchResult();
		Exception error = null;
		try
		{
			_viewModel.SetAllZoneWindowsSuppressWatcher(suppress: true);
			await ProgressWindow.RunWithProgressAsync(this, "正在切换收纳模式…", async delegate(Action<string?> reportFile, Action<int, int> reportProgress)
			{
				await Task.Run(delegate
				{
					if (targetMode == "Physical")
					{
						result = DesktopIconService.BatchSwitchToPhysical(_viewModel.Zones, reportFile, reportProgress);
					}
					else
					{
						result = DesktopIconService.BatchSwitchToAttribute(_viewModel.Zones, reportFile, reportProgress);
					}
					DesktopIconService.UserSelectedMode = targetMode;
				});
			});
		}
		catch (Exception ex)
		{
			error = ex;
		}
		_settings.HidingMode = targetMode;
		_originalMode = targetMode;
		_dataService.SaveSettings(_settings);
		_viewModel.SetAllZoneWindowsSuppressWatcher(suppress: false);
		_viewModel.SaveZonesNow();
		_viewModel.RefreshAllZoneWindows();
		RbModeAttribute.IsEnabled = true;
		RbModePhysical.IsEnabled = true;
		string failureDetail = string.Empty;
		if (result.Failures.Count > 0)
		{
			List<string> lines = new List<string>();
			foreach (DesktopIconService.ModeSwitchFailure f in result.Failures.Take(20))
			{
				lines.Add("• " + (f.Name ?? "(未知文件名)") + "\n    " + (f.Reason ?? "未知原因"));
			}
			failureDetail = "\n\n切换失败的文件（仍保留在桌面可见，不会被丢弃）：\n" + string.Join("\n", lines);
			if (result.Failures.Count > 20)
			{
				failureDetail += $"\n… 其余 {result.Failures.Count - 20} 个详见日志";
			}
			failureDetail += "\n\n建议：关闭可能占用这些文件的程序（如资源管理器预览窗格、杀毒软件、Windows 搜索索引）后，重新执行收纳；或在下次启动本软件时自动重试隐藏。";
		}
		if (error != null)
		{
			System.Windows.MessageBox.Show($"模式切换异常：{error.Message}\n\n成功迁移 {result.Success} 个文件，失败 {result.Fail} 个。\n收纳模式已保存为「{targetLabel}」。{failureDetail}", "切换异常", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else if (result.Fail > 0)
		{
			System.Windows.MessageBox.Show($"模式切换完成！\n成功迁移 {result.Success} 个文件，失败 {result.Fail} 个。\n收纳模式已保存为「{targetLabel}」。{failureDetail}", "部分完成", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else
		{
			System.Windows.MessageBox.Show("模式切换成功！\n当前收纳模式：" + targetLabel + "\n\n新收纳的图标将自动使用此模式。", "切换成功", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		HidingMode_Checked(sender, e);
	}

	private async void SaveHiddenPath_Click(object sender, RoutedEventArgs e)
	{
		string newPath = CustomPathTextBox.Text.Trim();
		if (!string.IsNullOrEmpty(newPath))
		{
			try
			{
				newPath = Path.GetFullPath(newPath);
			}
			catch
			{
				System.Windows.MessageBox.Show("路径格式无效，请检查输入。", "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
				return;
			}
			newPath = Path.Combine(newPath, "DesktopTool_hidden");
		}
		if (string.Equals(_settings.CustomHiddenPath ?? "", newPath, StringComparison.OrdinalIgnoreCase))
		{
			System.Windows.MessageBox.Show("路径未发生变化。", "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		string oldDir = DesktopIconService.HiddenDir;
		_settings.CustomHiddenPath = ((!string.IsNullOrEmpty(newPath)) ? newPath : null);
		DesktopIconService.CustomHiddenDir = _settings.CustomHiddenPath;
		_dataService.SaveSettings(_settings);
		CurrentHiddenPathText.Text = "当前路径：" + DesktopIconService.HiddenDir;
		if (Directory.Exists(oldDir) && EnumerateFilesRecursiveSafe(oldDir).Any() && System.Windows.MessageBox.Show("检测到旧存储路径中仍有隐藏文件。\n是否将所有隐藏文件迁移到新路径？\n\n建议选择\"是\"以确保数据完整。", "迁移确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			string targetDir = DesktopIconService.HiddenDir;
			int? migratedCount = null;
			Exception migrationError = null;
			try
			{
				await ProgressWindow.RunWithProgressAsync(this, "正在迁移隐藏文件…", async delegate(Action<string?> reportProgress)
				{
					await Task.Run(delegate
					{
						migratedCount = MigrateHiddenFiles(oldDir, targetDir, reportProgress);
						UpdateIconPathsAfterMigration(oldDir, targetDir);
					});
				});
			}
			catch (Exception ex)
			{
				migrationError = ex;
			}
			if (migrationError != null)
			{
				System.Windows.MessageBox.Show("迁移过程中出现异常：" + migrationError.Message + "\n\n请确保目标路径有足够空间和写入权限。", "迁移失败", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
			else if (migratedCount.HasValue)
			{
				CurrentHiddenPathText.Text = "当前路径：" + DesktopIconService.HiddenDir;
				System.Windows.MessageBox.Show($"文件迁移完成！共迁移 {migratedCount.Value} 个文件。", "成功", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			}
		}
		else
		{
			System.Windows.MessageBox.Show("保存成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
	}

	private void UpdateIconPathsAfterMigration(string oldDir, string newDir)
	{
		foreach (IconZone zone in _viewModel.Zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				foreach (ZoneIcon icon in group.Icons)
				{
					if (icon.HiddenPath != null && icon.HiddenPath.StartsWith(oldDir, StringComparison.OrdinalIgnoreCase))
					{
						string hiddenPath = icon.HiddenPath;
						int length = oldDir.Length;
						string relative = hiddenPath.Substring(length, hiddenPath.Length - length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
						icon.HiddenPath = Path.Combine(newDir, relative);
					}
				}
			}
		}
		_viewModel.SaveZones();
	}

	private static IEnumerable<string> EnumerateFilesRecursiveSafe(string root)
	{
		if (!Directory.Exists(root))
		{
			yield break;
		}
		Stack<string> dirs = new Stack<string>();
		dirs.Push(root);
		while (dirs.Count > 0)
		{
			string current = dirs.Pop();
			IEnumerable<string> files;
			try
			{
				files = Directory.EnumerateFiles(current);
			}
			catch
			{
				continue;
			}
			foreach (string item in files)
			{
				yield return item;
			}
			IEnumerable<string> subDirs;
			try
			{
				subDirs = Directory.EnumerateDirectories(current);
			}
			catch
			{
				continue;
			}
			foreach (string d in subDirs)
			{
				try
				{
					if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != FileAttributes.None)
					{
						continue;
					}
				}
				catch
				{
					continue;
				}
				dirs.Push(d);
			}
		}
	}

	private static int MigrateHiddenFiles(string sourceDir, string targetDir, Action<string> reportProgress = null)
	{
		if (!Directory.Exists(sourceDir))
		{
			return 0;
		}
		Directory.CreateDirectory(targetDir);
		int moved = 0;
		foreach (string file in EnumerateFilesRecursiveSafe(sourceDir))
		{
			reportProgress?.Invoke(file);
			try
			{
				string text = file;
				int length = sourceDir.Length;
				string relativePath = text.Substring(length, text.Length - length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				string destFile = Path.Combine(targetDir, relativePath);
				string destDir = Path.GetDirectoryName(destFile);
				if (destDir != null)
				{
					Directory.CreateDirectory(destDir);
				}
				try
				{
					File.Move(file, destFile);
				}
				catch (IOException)
				{
					File.Copy(file, destFile, overwrite: true);
					try
					{
						File.Delete(file);
					}
					catch
					{
					}
				}
				moved++;
			}
			catch
			{
			}
		}
		TryDeleteEmptyDirs(sourceDir);
		return moved;
	}

	private static void TryDeleteEmptyDirs(string dir)
	{
		try
		{
			string[] directories = Directory.GetDirectories(dir);
			foreach (string sub in directories)
			{
				try
				{
					if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != FileAttributes.None)
					{
						continue;
					}
				}
				catch
				{
					continue;
				}
				TryDeleteEmptyDirs(sub);
			}
			if (!Directory.EnumerateFileSystemEntries(dir).Any())
			{
				Directory.Delete(dir);
			}
		}
		catch
		{
		}
	}

	private void CloseMainPanel_Click(object sender, RoutedEventArgs e)
	{
		TrayService.Instance?.HideMainWindow();
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
		if (msg == 786 && ((IntPtr)wParam).ToInt32() == 1)
		{
			RaiseDesktopWidgetsViaHotkey();
			handled = true;
		}
		else if (msg == 786 && ((IntPtr)wParam).ToInt32() == 2)
		{
			OpenSearchWindow();
			handled = true;
		}
		else if (msg == (int)NativeMethods.WM_DESKTOOL_SHOWMAIN)
		{
			TrayService.Instance?.ShowMainWindow();
			handled = true;
		}
		return IntPtr.Zero;
	}

	private void RaiseDesktopWidgetsViaHotkey()
	{
		_viewModel?.ActivateOpenWindowsOnly();
		if (_desktopSearchWidget != null && _desktopSearchWidget.IsVisible)
		{
			try
			{
				_desktopSearchWidget.ForceTopMost();
			}
			catch
			{
			}
		}
	}

	private void RegisterCurrentHotkey()
	{
		UnregisterCurrentHotkey();
		string modifiers = _settings?.HotkeyModifiers ?? "Win+Shift";
		string key = _settings?.HotkeyKey ?? "D";
		uint modMask = ParseModifiersMask(modifiers);
		uint vkCode = ParseKeyCode(key);
		if (vkCode == 0)
		{
			return;
		}
		nint hwnd = new WindowInteropHelper(this).Handle;
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		if (NativeMethods.RegisterHotKey(hwnd, 1, modMask, vkCode))
		{
			_hotkeyRegistered = true;
			DebugLog("热键注册成功：" + FormatHotkeyDisplay(modifiers, key));
			return;
		}
		int err = Marshal.GetLastWin32Error();
		DebugLog($"热键注册失败：{FormatHotkeyDisplay(modifiers, key)}，错误码={err}；将于 Loaded 后重试");
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			try
			{
				nint handle = new WindowInteropHelper(this).Handle;
				if (handle != IntPtr.Zero && NativeMethods.RegisterHotKey(handle, 1, modMask, vkCode))
				{
					_hotkeyRegistered = true;
					DebugLog("热键重试注册成功：" + FormatHotkeyDisplay(modifiers, key));
				}
				else
				{
					DebugLog($"热键重试仍失败，错误码={Marshal.GetLastWin32Error()}");
				}
			}
			catch (Exception ex)
			{
				DebugLog("热键重试异常：" + ex.Message);
			}
		}, DispatcherPriority.Loaded);
	}

	private void UnregisterCurrentHotkey()
	{
		if (!_hotkeyRegistered)
		{
			return;
		}
		try
		{
			nint hwnd = new WindowInteropHelper(this).Handle;
			if (hwnd != IntPtr.Zero)
			{
				NativeMethods.UnregisterHotKey(hwnd, 1);
			}
		}
		catch
		{
		}
		_hotkeyRegistered = false;
	}

	private void RegisterSearchHotkey()
	{
		UnregisterSearchHotkey();
		if (_settings == null)
		{
			return;
		}
		string modifiers = _settings.SearchHotkeyModifiers;
		string key = _settings.SearchHotkeyKey;
		if (string.IsNullOrWhiteSpace(modifiers))
		{
			modifiers = "Win+Shift";
		}
		if (string.IsNullOrWhiteSpace(key))
		{
			key = "F";
		}
		uint modMask = ParseModifiersMask(modifiers);
		uint vkCode = ParseKeyCode(key);
		nint hwnd = new WindowInteropHelper(this).Handle;
		if (hwnd != IntPtr.Zero)
		{
			if (NativeMethods.RegisterHotKey(hwnd, 2, modMask, vkCode))
			{
				_searchHotkeyRegistered = true;
				_searchHotkeyConflict = false;
				DebugLog("搜索快捷键注册成功：" + FormatHotkeyDisplay(modifiers, key));
				return;
			}
			int err = Marshal.GetLastWin32Error();
			_searchHotkeyConflict = err == 1409;
			string hint = (_searchHotkeyConflict ? "（错误码1409=该组合已被其它程序占用。请在该程序中解除全局热键，或在设置里更换组合；搜索窗口仍可通过托盘菜单/应用内按钮打开）" : "");
			DebugLog($"搜索快捷键注册失败：{FormatHotkeyDisplay(modifiers, key)}，错误码={err}{hint}");
		}
	}

	private void UnregisterSearchHotkey()
	{
		if (!_searchHotkeyRegistered)
		{
			return;
		}
		try
		{
			nint hwnd = new WindowInteropHelper(this).Handle;
			if (hwnd != IntPtr.Zero)
			{
				NativeMethods.UnregisterHotKey(hwnd, 2);
			}
		}
		catch
		{
		}
		_searchHotkeyRegistered = false;
	}

	public void SetSearchHotkey(string modifiers, string key)
	{
		if (_settings != null)
		{
			_settings.SearchHotkeyModifiers = modifiers;
			_settings.SearchHotkeyKey = key;
			try
			{
				_dataService.SaveSettings(_settings);
			}
			catch
			{
			}
			RegisterSearchHotkey();
			DebugLog("文件搜索快捷键已更新为：" + FormatHotkeyDisplay(modifiers, key));
		}
	}

	public void ResetSearchHotkey()
	{
		if (_settings != null)
		{
			_settings.SearchHotkeyModifiers = "Win+Shift";
			_settings.SearchHotkeyKey = "F";
			try
			{
				_dataService.SaveSettings(_settings);
			}
			catch
			{
			}
			RegisterSearchHotkey();
			DebugLog("文件搜索快捷键已重置为默认 Win+Shift+F");
		}
	}

	public void ReRegisterSearchHotkey()
	{
		if (!_searchHotkeyRegistered)
		{
			DebugLog("启动安全网：尝试重新注册搜索快捷键（首次可能静默失败）");
			RegisterSearchHotkey();
		}
	}

	private static uint ParseModifiersMask(string modifiers)
	{
		uint mask = 0u;
		string[] array = modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string p in array)
		{
			if (string.Equals(p, "Win", StringComparison.OrdinalIgnoreCase))
			{
				mask |= 8;
			}
			else if (string.Equals(p, "Ctrl", StringComparison.OrdinalIgnoreCase))
			{
				mask |= 2;
			}
			else if (string.Equals(p, "Alt", StringComparison.OrdinalIgnoreCase))
			{
				mask |= 1;
			}
			else if (string.Equals(p, "Shift", StringComparison.OrdinalIgnoreCase))
			{
				mask |= 4;
			}
		}
		return mask | 0x4000;
	}

	private static uint ParseKeyCode(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return 0u;
		}
		return key switch
		{
			"D" => 68u, 
			"F" => 70u, 
			"E" => 69u, 
			"R" => 82u, 
			"T" => 84u, 
			"G" => 71u, 
			"H" => 72u, 
			"S" => 83u, 
			"W" => 87u, 
			"Q" => 81u, 
			"A" => 65u, 
			"Z" => 90u, 
			"X" => 88u, 
			"C" => 67u, 
			"V" => 86u, 
			"B" => 66u, 
			"N" => 78u, 
			"M" => 77u, 
			"J" => 74u, 
			"K" => 75u, 
			"L" => 76u, 
			"U" => 85u, 
			"I" => 73u, 
			"O" => 79u, 
			"P" => 80u, 
			"Space" => 32u, 
			"F1" => 112u, 
			"F2" => 113u, 
			"F3" => 114u, 
			"F4" => 115u, 
			"F5" => 116u, 
			"F6" => 117u, 
			"F7" => 118u, 
			"F8" => 119u, 
			"F9" => 120u, 
			"F10" => 121u, 
			"F11" => 122u, 
			"F12" => 123u, 
			_ => 0u, 
		};
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
			Title = "桌面工具箱 — 设置快捷键",
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
			Text = "请按下新的快捷键组合（如 Win + Shift + D）...",
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			FontSize = 14.0,
			Margin = new Thickness(0.0, 20.0, 0.0, 12.0),
			Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51))
		};
		TextBlock txtCaptured = new TextBlock
		{
			Text = "等待按键...",
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
			FontSize = 20.0,
			FontWeight = FontWeights.Bold,
			Margin = new Thickness(0.0, 0.0, 0.0, 16.0),
			Foreground = new SolidColorBrush(Color.FromRgb(74, 144, 217))
		};
		StackPanel btnContainer = new StackPanel
		{
			Orientation = System.Windows.Controls.Orientation.Horizontal,
			HorizontalAlignment = System.Windows.HorizontalAlignment.Center
		};
		System.Windows.Controls.Button btnConfirm = new System.Windows.Controls.Button
		{
			Content = "确认",
			Width = 100.0,
			Height = 32.0,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
			IsEnabled = false,
			Background = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
			Foreground = Brushes.White,
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand
		};
		btnConfirm.Click += delegate
		{
			confirmed = true;
			captureWindow.Close();
		};
		System.Windows.Controls.Button btnCancel = new System.Windows.Controls.Button
		{
			Content = "取消",
			Width = 100.0,
			Height = 32.0,
			Background = new SolidColorBrush(Color.FromRgb(224, 224, 224)),
			Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51)),
			BorderThickness = new Thickness(0.0),
			Cursor = System.Windows.Input.Cursors.Hand
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
		captureWindow.PreviewKeyDown += delegate(object s, System.Windows.Input.KeyEventArgs args)
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
			_settings.HotkeyModifiers = capturedMods;
			_settings.HotkeyKey = capturedKey.ToString();
			_dataService.SaveSettings(_settings);
			TxtHotkeyDisplay.Text = FormatHotkeyDisplay(_settings.HotkeyModifiers, _settings.HotkeyKey);
			RegisterCurrentHotkey();
			DebugLog("快捷键已更新为：" + TxtHotkeyDisplay.Text);
		}
	}

	private void BtnResetHotkey_Click(object sender, RoutedEventArgs e)
	{
		if (string.Equals(_settings.HotkeyModifiers, "Win+Shift", StringComparison.OrdinalIgnoreCase) && string.Equals(_settings.HotkeyKey, "D", StringComparison.OrdinalIgnoreCase))
		{
			DebugLog("快捷键已是默认值 Win+Shift+D，无需重置");
			return;
		}
		_settings.HotkeyModifiers = "Win+Shift";
		_settings.HotkeyKey = "D";
		_dataService.SaveSettings(_settings);
		TxtHotkeyDisplay.Text = FormatHotkeyDisplay("Win+Shift", "D");
		RegisterCurrentHotkey();
		DebugLog("快捷键已重置为默认 Win+Shift+D");
	}

	private async void RestoreAllButton_Click(object sender, RoutedEventArgs e)
	{
		if (DesktopIconService.IsDangerousHiddenPath(DesktopIconService.HiddenDir, out string dangerReason))
		{
			System.Windows.MessageBox.Show("⚠ 安全警告\n\n" + dangerReason + "\n\n请先在「设置 → 隐藏路径」中修改存储路径到安全的文件夹后再试。", "无法执行一键复原", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		int hiddenCount = 0;
		foreach (IconZone zone in _viewModel.Zones)
		{
			foreach (IconGroup group in zone.Groups)
			{
				hiddenCount += group.Icons.Count((ZoneIcon i) => i.IsHidden);
			}
		}
		if (hiddenCount == 0)
		{
			int manifestCount = CountManifestEntries();
			if (manifestCount == 0)
			{
				System.Windows.MessageBox.Show("当前没有需要复原的隐藏文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}
			hiddenCount = manifestCount;
		}
		if (System.Windows.MessageBox.Show($"即将执行以下操作：\n\n• 将 {hiddenCount} 个隐藏/移动的图标恢复到桌面显示\n• 清空所有收纳区内的图标记录\n\n⚠ 此操作不可撤销，确定继续吗？", "一键复原确认", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
		{
			return;
		}
		System.Windows.Controls.Button btn = sender as System.Windows.Controls.Button;
		if (btn != null)
		{
			btn.IsEnabled = false;
			btn.Content = "正在复原中…";
		}
		int restoredCount = 0;
		int failedCount = 0;
		Exception error = null;
		try
		{
			await ProgressWindow.RunWithProgressAsync(this, "正在一键复原…", async delegate(Action<string?> reportProgress)
			{
				await Task.Run(delegate
				{
					foreach (IconZone zone2 in _viewModel.Zones)
					{
						foreach (IconGroup group2 in zone2.Groups)
						{
							foreach (ZoneIcon current in group2.Icons.Where((ZoneIcon i) => i.IsHidden).ToList())
							{
								try
								{
									string text = current.DisplayName ?? Path.GetFileName(current.FilePath);
									reportProgress("正在恢复：" + text);
									if (DesktopIconService.RestoreFile(current.FilePath, current.HiddenMethod, current.HiddenPath))
									{
										current.HiddenMethod = null;
										current.HiddenPath = null;
										DesktopIconService.SyncIconFilePath(current, "MainWindow.ReleaseAll");
										int num = restoredCount;
										restoredCount = num + 1;
									}
									else
									{
										int num = failedCount;
										failedCount = num + 1;
									}
								}
								catch
								{
									int num = failedCount;
									failedCount = num + 1;
								}
							}
						}
					}
					try
					{
						reportProgress("正在检查恢复清单…");
						int num2 = DesktopIconService.RestoreAllFromManifest();
						restoredCount += num2;
					}
					catch
					{
					}
					try
					{
						HashSet<string> allTrackedHiddenPaths = _viewModel.GetAllTrackedHiddenPaths();
						reportProgress("正在检查隐藏目录…");
						int num3 = DesktopIconService.RestoreUntrackedFiles(allTrackedHiddenPaths);
						restoredCount += num3;
					}
					catch
					{
					}
				});
			});
		}
		catch (Exception ex)
		{
			error = ex;
		}
		foreach (IconZone zone3 in _viewModel.Zones)
		{
			foreach (IconGroup group3 in zone3.Groups)
			{
				group3.Icons.Clear();
			}
		}
		_viewModel.SaveZonesNow();
		_viewModel.RefreshAllZoneWindows();
		if (btn != null)
		{
			btn.IsEnabled = true;
			btn.Content = "一键复原文件";
		}
		if (error != null)
		{
			System.Windows.MessageBox.Show($"复原过程中出现异常：{error.Message}\n\n已恢复 {restoredCount} 个文件，失败 {failedCount} 个。", "部分完成", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else if (failedCount > 0)
		{
			System.Windows.MessageBox.Show($"操作完成！\n成功恢复 {restoredCount} 个文件到桌面，清空了收纳区内容。\n{failedCount} 个文件恢复失败，可能是文件被占用，请关闭相关程序后重试。", "部分完成", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else
		{
			System.Windows.MessageBox.Show($"操作完成！\n已成功恢复 {restoredCount} 个文件到桌面，收纳区已清空。", "复原成功", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
	}

	private static int CountManifestEntries()
	{
		try
		{
			string manifestPath = Path.Combine(App.GetAppDataDir(), "recovery_manifest.json");
			if (!File.Exists(manifestPath))
			{
				return 0;
			}
			return (JsonSerializer.Deserialize<ManifestCountDto>(File.ReadAllText(manifestPath))?.Entries?.Count).GetValueOrDefault();
		}
		catch
		{
			return 0;
		}
	}

	public void ShowAboutPanel()
	{
		base.Opacity = 1.0;
		Show();
		Activate();
		NavigateTo("About");
	}

	private void InitAboutPanel()
	{
		if (!_aboutInitialized)
		{
			_aboutInitialized = true;
			Version ver = Assembly.GetExecutingAssembly().GetName().Version;
			AboutVersionText.Text = $"V{ver.Major}.{ver.Minor}.{ver.Build}";
			if (IsRunningAsMsix())
			{
				AboutUpdateLabel.Text = "官方网站：";
				AboutDonateSeparator.Visibility = Visibility.Collapsed;
				AboutDonatePanel.Visibility = Visibility.Collapsed;
			}
		}
	}

	private static bool IsRunningAsMsix()
	{
		try
		{
			string[] obj = new string[3] { "Windows.ApplicationModel.Package, Microsoft.Windows.SDK.NET", "Windows.ApplicationModel.Package", "Windows.ApplicationModel.Package, Windows" };
			Type pkgType = null;
			string[] array = obj;
			for (int i = 0; i < array.Length; i++)
			{
				pkgType = Type.GetType(array[i]);
				if (pkgType != null)
				{
					break;
				}
			}
			if (pkgType == null)
			{
				return false;
			}
			return pkgType.GetProperty("Current")?.GetValue(null) != null;
		}
		catch
		{
			return false;
		}
	}

	private void CopyableTextBox_GotFocus(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.TextBox tb)
		{
			tb.SelectAll();
		}
	}

	private void AboutUpdateLink_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Process.Start(new ProcessStartInfo("https://icaogu.cn/desktop/index/index.html")
			{
				UseShellExecute = true
			});
		}
		catch
		{
		}
	}

	private void AboutDonate_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			new DonationWindow().ShowDialog();
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show("收款码窗口打开失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private void SetWindowIcon()
	{
		try
		{
			BitmapSource wpfIcon = AppIcon.GetWpfIcon();
			if (wpfIcon != null)
			{
				base.Icon = wpfIcon;
			}
		}
		catch
		{
		}
	}

	private static void DebugLog(string context, Exception? ex = null)
	{
		try
		{
			string log = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MainWindow: {context}";
			if (ex != null)
			{
				log += $": {ex}";
			}
			log += "\n";
			LogHelper.Append(Path.Combine(Path.GetTempPath(), "DesktopTool_Crash.log"), log);
		}
		catch
		{
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/views/mainwindow.xaml", UriKind.Relative);
			System.Windows.Application.LoadComponent(this, resourceLocater);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			((Border)target).MouseLeftButtonDown += Window_MouseLeftButtonDown;
			break;
		case 2:
			BtnZones = (System.Windows.Controls.Button)target;
			BtnZones.Click += NavButton_Click;
			break;
		case 3:
			BtnTodos = (System.Windows.Controls.Button)target;
			BtnTodos.Click += NavButton_Click;
			break;
		case 4:
			BtnMappings = (System.Windows.Controls.Button)target;
			BtnMappings.Click += NavButton_Click;
			break;
		case 5:
			BtnToolbox = (System.Windows.Controls.Button)target;
			BtnToolbox.Click += NavButton_Click;
			break;
		case 6:
			BtnSettings = (System.Windows.Controls.Button)target;
			BtnSettings.Click += NavButton_Click;
			break;
		case 7:
			BtnBackup = (System.Windows.Controls.Button)target;
			BtnBackup.Click += NavButton_Click;
			break;
		case 8:
			BtnAbout = (System.Windows.Controls.Button)target;
			BtnAbout.Click += NavButton_Click;
			break;
		case 9:
			ChkAutoStart = (System.Windows.Controls.CheckBox)target;
			ChkAutoStart.Click += ChkAutoStart_Click;
			break;
		case 10:
			ChkTempShowHidden = (System.Windows.Controls.CheckBox)target;
			ChkTempShowHidden.Click += ChkTempShowHidden_Click;
			break;
		case 11:
			TxtPageTitle = (TextBlock)target;
			break;
		case 12:
			BtnClosePanel = (System.Windows.Controls.Button)target;
			BtnClosePanel.Click += CloseMainPanel_Click;
			break;
		case 13:
			ZonesContent = (ScrollViewer)target;
			break;
		case 14:
			ZonesList = (ItemsControl)target;
			break;
		case 18:
			ZonesEmptyHint = (Border)target;
			break;
		case 19:
			TodosContent = (ScrollViewer)target;
			break;
		case 20:
			((Border)target).MouseLeftButtonDown += TodoItem_Click;
			break;
		case 21:
			((System.Windows.Controls.Button)target).Click += OpenRecurringTasks_Click;
			break;
		case 22:
			((System.Windows.Controls.Button)target).Click += ResetTodoPosition_Click;
			break;
		case 23:
			((System.Windows.Controls.Button)target).Click += RestoreTodoWindow_Click;
			break;
		case 24:
			MappingsContent = (ScrollViewer)target;
			break;
		case 25:
			MappingsList = (ItemsControl)target;
			break;
		case 29:
			MappingsEmptyHint = (Border)target;
			break;
		case 30:
			SettingsContent = (ScrollViewer)target;
			break;
		case 31:
			OpacityLabel = (TextBlock)target;
			break;
		case 32:
			OpacitySlider = (Slider)target;
			OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;
			OpacitySlider.PreviewMouseLeftButtonDown += Slider_PreviewMouseLeftButtonDown;
			break;
		case 33:
			IconSpacingSlider = (Slider)target;
			IconSpacingSlider.ValueChanged += IconSpacingSlider_ValueChanged;
			IconSpacingSlider.PreviewMouseLeftButtonDown += Slider_PreviewMouseLeftButtonDown;
			break;
		case 34:
			IconSpacingLabel = (TextBlock)target;
			break;
		case 35:
			PanelColorSlider = (Slider)target;
			PanelColorSlider.ValueChanged += PanelColorSlider_ValueChanged;
			PanelColorSlider.PreviewMouseLeftButtonDown += Slider_PreviewMouseLeftButtonDown;
			break;
		case 36:
			PanelColorResetButton = (System.Windows.Controls.Button)target;
			PanelColorResetButton.Click += PanelColorResetButton_Click;
			break;
		case 37:
			PanelColorPreview = (Border)target;
			break;
		case 38:
			PanelColorHex = (TextBlock)target;
			break;
		case 39:
			PanelColorInput = (System.Windows.Controls.TextBox)target;
			PanelColorInput.KeyDown += PanelColorInput_KeyDown;
			PanelColorInput.LostFocus += PanelColorInput_LostFocus;
			break;
		case 40:
			RbStyleSimple = (System.Windows.Controls.RadioButton)target;
			break;
		case 41:
			RbStyleGlass = (System.Windows.Controls.RadioButton)target;
			break;
		case 42:
			((System.Windows.Controls.Button)target).Click += WindowStyleReload_Click;
			break;
		case 43:
			RbFontSmall = (System.Windows.Controls.RadioButton)target;
			RbFontSmall.Click += FontSizeRadio_Click;
			break;
		case 44:
			RbFontMedium = (System.Windows.Controls.RadioButton)target;
			RbFontMedium.Click += FontSizeRadio_Click;
			break;
		case 45:
			RbFontLarge = (System.Windows.Controls.RadioButton)target;
			RbFontLarge.Click += FontSizeRadio_Click;
			break;
		case 46:
			RbFontXLarge = (System.Windows.Controls.RadioButton)target;
			RbFontXLarge.Click += FontSizeRadio_Click;
			break;
		case 47:
			RbTodoFaded = (System.Windows.Controls.RadioButton)target;
			RbTodoFaded.Checked += CompletedTodoStyle_Checked;
			break;
		case 48:
			RbTodoStrikethrough = (System.Windows.Controls.RadioButton)target;
			RbTodoStrikethrough.Checked += CompletedTodoStyle_Checked;
			break;
		case 49:
			TxtHotkeyDisplay = (TextBlock)target;
			break;
		case 50:
			BtnChangeHotkey = (System.Windows.Controls.Button)target;
			BtnChangeHotkey.Click += BtnChangeHotkey_Click;
			break;
		case 51:
			BtnResetHotkey = (System.Windows.Controls.Button)target;
			BtnResetHotkey.Click += BtnResetHotkey_Click;
			break;
		case 52:
			RbModeAttribute = (System.Windows.Controls.RadioButton)target;
			RbModeAttribute.Checked += HidingMode_Checked;
			break;
		case 53:
			RbModePhysical = (System.Windows.Controls.RadioButton)target;
			RbModePhysical.Checked += HidingMode_Checked;
			break;
		case 54:
			SwitchModeButton = (System.Windows.Controls.Button)target;
			SwitchModeButton.Click += SwitchModeButton_Click;
			break;
		case 55:
			CurrentHiddenPathText = (TextBlock)target;
			CurrentHiddenPathText.MouseLeftButtonDown += CurrentPath_Click;
			break;
		case 56:
			CustomPathTextBox = (System.Windows.Controls.TextBox)target;
			break;
		case 57:
			((System.Windows.Controls.Button)target).Click += BrowsePath_Click;
			break;
		case 58:
			((System.Windows.Controls.Button)target).Click += SaveHiddenPath_Click;
			break;
		case 59:
			BtnRestoreAll = (System.Windows.Controls.Button)target;
			BtnRestoreAll.Click += RestoreAllButton_Click;
			break;
		case 60:
			ToolboxContent = (ScrollViewer)target;
			break;
		case 61:
			ToolboxList = (ItemsControl)target;
			break;
		case 63:
			AboutContent = (ScrollViewer)target;
			break;
		case 64:
			AboutVersionText = (TextBlock)target;
			break;
		case 65:
			AboutUpdatePanel = (StackPanel)target;
			break;
		case 66:
			AboutUpdateLabel = (TextBlock)target;
			break;
		case 67:
			((System.Windows.Controls.Button)target).Click += AboutUpdateLink_Click;
			break;
		case 68:
			((System.Windows.Controls.TextBox)target).GotFocus += CopyableTextBox_GotFocus;
			break;
		case 69:
			((System.Windows.Controls.TextBox)target).GotFocus += CopyableTextBox_GotFocus;
			break;
		case 70:
			((System.Windows.Controls.TextBox)target).GotFocus += CopyableTextBox_GotFocus;
			break;
		case 71:
			AboutDonateSeparator = (Border)target;
			break;
		case 72:
			AboutDonatePanel = (StackPanel)target;
			break;
		case 73:
			((System.Windows.Controls.Button)target).Click += AboutDonate_Click;
			break;
		case 74:
			BackupContent = (ScrollViewer)target;
			break;
		case 75:
			BtnCreateBackup = (System.Windows.Controls.Button)target;
			BtnCreateBackup.Click += BtnCreateBackup_Click;
			break;
		case 76:
			TxtBackupHint = (TextBlock)target;
			break;
		case 77:
			BackupList = (ItemsControl)target;
			break;
		case 80:
			TxtBackupEmpty = (TextBlock)target;
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
		switch (connectionId)
		{
		case 15:
			((Border)target).MouseLeftButtonDown += ZoneItem_Click;
			break;
		case 16:
			((System.Windows.Controls.Button)target).Click += ResetZonePosition_Click;
			break;
		case 17:
			((System.Windows.Controls.Button)target).Click += DismissZoneFromList_Click;
			break;
		case 26:
			((Border)target).MouseLeftButtonDown += MappingItem_Click;
			break;
		case 27:
			((System.Windows.Controls.Button)target).Click += ResetMappingPosition_Click;
			break;
		case 28:
			((System.Windows.Controls.Button)target).Click += DismissMappingFromList_Click;
			break;
		case 62:
			((System.Windows.Controls.Button)target).Click += ToolboxTool_Click;
			break;
		case 78:
			((System.Windows.Controls.Button)target).Click += BtnRestoreBackup_Click;
			break;
		case 79:
			((System.Windows.Controls.Button)target).Click += BtnDeleteBackup_Click;
			break;
		}
	}
}
