using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;

namespace DesktopTool.Views;

public class ProgressWindow : Window, IComponentConnector
{
	private readonly ProgressViewModel _vm;

	private bool _operationComplete;

	private readonly DispatcherTimer _safetyTimer;

	private const int SafetyTimeoutSeconds = 120;

	private bool _contentLoaded;

	public ProgressWindow(Window? owner = null)
	{
		InitializeComponent();
		_vm = new ProgressViewModel();
		base.DataContext = _vm;
		if (owner != null)
		{
			try
			{
				base.Owner = owner;
				base.WindowStartupLocation = WindowStartupLocation.CenterOwner;
			}
			catch (InvalidOperationException)
			{
				base.WindowStartupLocation = WindowStartupLocation.CenterScreen;
			}
		}
		base.MouseLeftButtonDown += delegate(object _, MouseButtonEventArgs e)
		{
			if (e.ButtonState == MouseButtonState.Pressed)
			{
				DragMove();
			}
		};
		_safetyTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(120.0)
		};
		_safetyTimer.Tick += delegate
		{
			_safetyTimer.Stop();
			_operationComplete = true;
			Close();
		};
		base.Closing += delegate(object? _, CancelEventArgs e)
		{
			if (!_operationComplete)
			{
				e.Cancel = true;
			}
		};
	}

	public void SetTitle(string title)
	{
		_vm.Title = title;
	}

	public void SetCurrentFile(string? filePath)
	{
		_vm.CurrentFile = filePath ?? "";
	}

	public void SetProgress(int current, int total)
	{
		if (base.Dispatcher.CheckAccess())
		{
			_vm.ProgressMax = total;
			_vm.ProgressValue = current;
			_vm.ProgressText = $"{current} / {total}";
			return;
		}
		base.Dispatcher.Invoke(delegate
		{
			_vm.ProgressMax = total;
			_vm.ProgressValue = current;
			_vm.ProgressText = $"{current} / {total}";
		});
	}

	public void ResetToIndeterminate()
	{
		_vm.ProgressMax = 0.0;
		_vm.ProgressValue = 0.0;
		_vm.ProgressText = "";
	}

	public void SafeClose()
	{
		if (base.Dispatcher.CheckAccess())
		{
			_safetyTimer.Stop();
			_operationComplete = true;
			Close();
			return;
		}
		base.Dispatcher.Invoke(delegate
		{
			_safetyTimer.Stop();
			_operationComplete = true;
			Close();
		});
	}

	public static (ProgressWindow Window, Action<string?> ReportProgress) ShowAndTrack(Window? owner, string title)
	{
		ProgressWindow win = new ProgressWindow(owner)
		{
			Title = title
		};
		_ = win._vm;
		win._vm.Title = title;
		win._safetyTimer.Start();
		win.Show();
		return (Window: win, ReportProgress: delegate(string? filePath)
		{
			win.SetCurrentFile(filePath);
		});
	}

	public static async Task RunWithProgressAsync(Window? owner, string title, Func<Action<string?>, Task> operation)
	{
		ProgressWindow win = new ProgressWindow(owner);
		win._vm.Title = title;
		if (owner != null)
		{
			owner.Dispatcher.Invoke(() => owner.IsEnabled = false);
		}
		win._safetyTimer.Start();
		win.Show();
		await Task.Yield();
		try
		{
			await operation(delegate(string? fileName)
			{
				win.Dispatcher.Invoke(delegate
				{
					ProgressViewModel vm = win._vm;
					string? obj = fileName ?? "";
					string result = obj;
					vm.CurrentFile = obj;
					return result;
				});
			});
		}
		finally
		{
			if (owner != null)
			{
				owner.Dispatcher.Invoke(() => owner.IsEnabled = true);
			}
			win.SafeClose();
		}
	}

	public static async Task RunWithProgressAsync(Window? owner, string title, Func<Action<string?>, Action<int, int>, Task> operation)
	{
		ProgressWindow win = new ProgressWindow(owner);
		win._vm.Title = title;
		if (owner != null)
		{
			owner.Dispatcher.Invoke(() => owner.IsEnabled = false);
		}
		win._safetyTimer.Start();
		win.Show();
		await Task.Yield();
		try
		{
			await operation(delegate(string? fileName)
			{
				win.Dispatcher.Invoke(delegate
				{
					ProgressViewModel vm = win._vm;
					string? obj = fileName ?? "";
					string result = obj;
					vm.CurrentFile = obj;
					return result;
				});
			}, delegate(int current, int total)
			{
				win.SetProgress(current, total);
			});
		}
		finally
		{
			if (owner != null)
			{
				owner.Dispatcher.Invoke(() => owner.IsEnabled = true);
			}
			win.SafeClose();
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/views/progresswindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocater);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		_contentLoaded = true;
	}
}
