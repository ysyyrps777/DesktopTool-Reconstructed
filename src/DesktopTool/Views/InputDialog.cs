using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace DesktopTool.Views;

public class InputDialog : Window, IComponentConnector
{
	internal TextBlock TitleText;

	internal TextBlock PromptText;

	internal TextBox InputTextBox;

	private bool _contentLoaded;

	public string ResponseText => InputTextBox.Text.Trim();

	public InputDialog(string prompt, string title = "??", string defaultValue = "")
	{
		InitializeComponent();
		FixRecoveredButtonText();
		PromptText.Text = prompt;
		TitleText.Text = title;
		InputTextBox.Text = defaultValue;
		base.KeyDown += delegate(object s, KeyEventArgs e)
		{
			if (e.Key == Key.Escape)
			{
				base.DialogResult = false;
				Close();
			}
		};
		base.Loaded += delegate
		{
			InputTextBox.Focus();
			InputTextBox.SelectAll();
		};
	}


	private void FixRecoveredButtonText()
	{
		List<Button> buttons = new List<Button>();
		CollectButtons(this, buttons);
		if (buttons.Count >= 3)
		{
			buttons[0].Content = "?";
			buttons[1].Content = "??";
			buttons[2].Content = "??";
		}
	}

	private static void CollectButtons(DependencyObject parent, List<Button> buttons)
	{
		for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
		{
			DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
			if (child is Button button)
			{
				buttons.Add(button);
			}
			CollectButtons(child, buttons);
		}
	}

	private void OK_Click(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(InputTextBox.Text))
		{
			MessageBox.Show("???????", "??", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		base.DialogResult = true;
		Close();
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = false;
		Close();
	}

	private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Return)
		{
			OK_Click(sender, e);
		}
	}

	private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ButtonState == MouseButtonState.Pressed)
		{
			DragMove();
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/views/inputdialog.xaml", UriKind.Relative);
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
			((InputDialog)target).MouseLeftButtonDown += Window_MouseLeftButtonDown;
			break;
		case 2:
			TitleText = (TextBlock)target;
			break;
		case 3:
			((Button)target).Click += Cancel_Click;
			break;
		case 4:
			PromptText = (TextBlock)target;
			break;
		case 5:
			InputTextBox = (TextBox)target;
			InputTextBox.KeyDown += InputTextBox_KeyDown;
			break;
		case 6:
			((Button)target).Click += OK_Click;
			break;
		case 7:
			((Button)target).Click += Cancel_Click;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
