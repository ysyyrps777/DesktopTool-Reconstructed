using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace DesktopTool.Views;

public class ConfirmDialog : Window, IComponentConnector
{
	internal TextBlock TitleText;

	internal TextBlock MessageText;

	internal Button YesButton;

	internal Button NoButton;

	private bool _contentLoaded;

	public bool Result { get; private set; }

	public ConfirmDialog(string title, string message, string yesText = "是", string noText = "否")
	{
		InitializeComponent();
		TitleText.Text = title;
		MessageText.Text = message;
		YesButton.Content = yesText;
		NoButton.Content = noText;
	}

	private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.LeftButton == MouseButtonState.Pressed)
		{
			DragMove();
		}
	}

	private void Yes_Click(object sender, RoutedEventArgs e)
	{
		Result = true;
		base.DialogResult = true;
	}

	private void No_Click(object sender, RoutedEventArgs e)
	{
		Result = false;
		base.DialogResult = false;
	}

	public static bool Show(Window? owner, string title, string message, string yesText = "是", string noText = "否")
	{
		ConfirmDialog dlg = new ConfirmDialog(title, message, yesText, noText);
		if (owner != null)
		{
			dlg.Owner = owner;
			dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
		}
		else
		{
			dlg.WindowStartupLocation = WindowStartupLocation.Manual;
			dlg.Loaded += delegate
			{
				Rect workArea = SystemParameters.WorkArea;
				dlg.Left = workArea.Left + (workArea.Width - dlg.ActualWidth) / 2.0;
				dlg.Top = workArea.Top + (workArea.Height - dlg.ActualHeight) / 2.0;
			};
		}
		if (dlg.ShowDialog() == true)
		{
			return dlg.Result;
		}
		return false;
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/views/confirmdialog.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocater);
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
			((ConfirmDialog)target).MouseLeftButtonDown += Window_MouseLeftButtonDown;
			break;
		case 2:
			TitleText = (TextBlock)target;
			break;
		case 3:
			MessageText = (TextBlock)target;
			break;
		case 4:
			YesButton = (Button)target;
			YesButton.Click += Yes_Click;
			break;
		case 5:
			NoButton = (Button)target;
			NoButton.Click += No_Click;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
