using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace Uninstaller;

public class UninstallWindow : Window, IComponentConnector
{
	internal TextBlock StatusTitle;

	internal TextBlock StatusDetail;

	internal ProgressBar ProgressBar;

	internal StackPanel ConfirmPanel;

	internal StackPanel WorkingPanel;

	internal TextBlock WorkingText;

	internal StackPanel DonePanel;

	internal TextBlock DoneText;

	private bool _contentLoaded;

	public UninstallWindow()
	{
		InitializeComponent();
	}

	private async void ConfirmYesButton_Click(object sender, RoutedEventArgs e)
	{
		ConfirmPanel.Visibility = Visibility.Collapsed;
		WorkingPanel.Visibility = Visibility.Visible;
		UpdateStatus("退出程序", "正在关闭正在运行的桌面工具箱...");
		SetProgress(5);
		await Task.Run((Action)Program.KillDesktopToolProcess);
		await Task.Delay(600);
		UpdateStatus("恢复桌面图标", "正在将所有隐藏的图标恢复到桌面...");
		SetProgress(15);
		int num = await Task.Run((Func<int>)Program.RestoreAllWithFallback);
		if (num >= 0)
		{
			UpdateStatus("图标已恢复", $"已成功恢复 {num} 个桌面图标");
		}
		else
		{
			UpdateStatus("图标已恢复", "未找到需要恢复的图标数据");
		}
		bool hasLeftoverFiles = false;
		await Task.Run(delegate
		{
			string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopTool", "hidden");
			if (Directory.Exists(text))
			{
				string b = Path.Combine(text, "recovery_manifest.json");
				foreach (string item in Directory.EnumerateFileSystemEntries(text))
				{
					if (!string.Equals(item, b, StringComparison.OrdinalIgnoreCase))
					{
						hasLeftoverFiles = true;
						break;
					}
				}
			}
		});
		if (hasLeftoverFiles)
		{
			UpdateStatus("⚠ 警告", "部分文件未能恢复，将保留在数据目录中");
		}
		SetProgress(30);
		UpdateStatus("清理注册表", "正在移除自启动和卸载信息...");
		SetProgress(50);
		await Task.Run((Action)Program.CleanRegistry);
		UpdateStatus("清理快捷方式", "正在移除桌面和启动栏快捷方式...");
		SetProgress(65);
		await Task.Run((Action)Program.CleanShortcuts);
		UpdateStatus("清理临时数据", "保留用户配置文件，清理缓存和恢复相关文件...");
		SetProgress(80);
		await Task.Run(delegate
		{
			Program.CleanAppDataDir(keepUserFiles: true, hasLeftoverFiles);
			Program.CleanScheduledTask();
		});
		UpdateStatus("清理安装目录", "准备完成卸载...");
		SetProgress(95);
		await Task.Run(delegate
		{
			string directoryName = Path.GetDirectoryName(Environment.ProcessPath);
			if (!string.IsNullOrEmpty(directoryName) && Directory.Exists(directoryName))
			{
				Program.ScheduleDirectoryDelete(directoryName);
			}
		});
		await Task.Run((Action)Program.CleanUninstallingSentinel);
		await Task.Delay(400);
		WorkingPanel.Visibility = Visibility.Collapsed;
		DonePanel.Visibility = Visibility.Visible;
		DoneText.Text = "卸载完成！\n所有隐藏的桌面图标已恢复，用户数据已保留。";
		SetProgress(100);
	}

	private void UpdateStatus(string title, string detail)
	{
		base.Dispatcher.Invoke(delegate
		{
			StatusTitle.Text = title;
			StatusDetail.Text = detail;
		});
	}

	private void SetProgress(int value)
	{
		base.Dispatcher.Invoke(() => ProgressBar.Value = value);
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Application.Current.Shutdown();
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 1)
		{
			DragMove();
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.9.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/Uninstaller;component/uninstallwindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.9.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			((Grid)target).MouseLeftButtonDown += TitleBar_MouseLeftButtonDown;
			break;
		case 2:
			((Button)target).Click += CloseButton_Click;
			break;
		case 3:
			StatusTitle = (TextBlock)target;
			break;
		case 4:
			StatusDetail = (TextBlock)target;
			break;
		case 5:
			ProgressBar = (ProgressBar)target;
			break;
		case 6:
			ConfirmPanel = (StackPanel)target;
			break;
		case 7:
			((Button)target).Click += ConfirmYesButton_Click;
			break;
		case 8:
			((Button)target).Click += CloseButton_Click;
			break;
		case 9:
			WorkingPanel = (StackPanel)target;
			break;
		case 10:
			WorkingText = (TextBlock)target;
			break;
		case 11:
			DonePanel = (StackPanel)target;
			break;
		case 12:
			DoneText = (TextBlock)target;
			break;
		case 13:
			((Button)target).Click += CloseButton_Click;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
