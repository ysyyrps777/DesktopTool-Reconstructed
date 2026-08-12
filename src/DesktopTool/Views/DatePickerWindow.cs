using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using DesktopTool.Services;

namespace DesktopTool.Views;

public class DatePickerWindow : Window, IComponentConnector
{
	private nint _hwnd;

	private readonly nint _ownerHwnd;

	internal Calendar CalendarControl;

	private bool _contentLoaded;

	public event Action<DateTime>? DateSelected;

	public DatePickerWindow(nint ownerHwnd, DateTime defaultDate)
	{
		InitializeComponent();
		_ownerHwnd = ownerHwnd;
		CalendarControl.SelectedDate = defaultDate;
		CalendarControl.DisplayDate = defaultDate;
		ApplyDarkTheme(CalendarControl);
		base.SourceInitialized += OnSourceInitialized;
		base.Loaded += OnLoaded;
	}

	private void OnSourceInitialized(object? sender, EventArgs e)
	{
		_hwnd = new WindowInteropHelper(this).Handle;
		NativeMethods.SetWindowPos(_hwnd, _ownerHwnd, 0, 0, 0, 0, 19u);
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		CalendarControl.Focus();
	}

	private void Calendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (CalendarControl.SelectedDate.HasValue)
		{
			this.DateSelected?.Invoke(CalendarControl.SelectedDate.Value);
			Close();
		}
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 1)
		{
			DragMove();
		}
	}

	public void ShowAtPosition(double screenX, double screenY)
	{
		base.WindowStartupLocation = WindowStartupLocation.Manual;
		base.Left = screenX;
		base.Top = screenY;
		ShowDialog();
	}

	private static void ApplyDarkTheme(Calendar cal)
	{
		SolidColorBrush bgBrush = new SolidColorBrush(Color.FromRgb(40, 40, 48));
		cal.Resources[SystemColors.WindowBrushKey] = bgBrush;
		cal.Resources[SystemColors.WindowTextBrushKey] = Brushes.White;
		cal.Resources[SystemColors.ControlTextBrushKey] = Brushes.White;
		cal.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(80, 110, 197));
		cal.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
		cal.Resources["CalendarTextBrush"] = Brushes.White;
		cal.Resources["CalendarTextBrush!"] = new SolidColorBrush(Color.FromRgb(200, 200, 216));
		cal.Resources[SystemColors.ActiveCaptionBrushKey] = new SolidColorBrush(Color.FromRgb(60, 84, 149));
		cal.Resources[SystemColors.ActiveCaptionTextBrushKey] = Brushes.White;
		cal.Resources["CalendarNavigationButtonBackground"] = new SolidColorBrush(Color.FromArgb(48, byte.MaxValue, byte.MaxValue, byte.MaxValue));
		cal.Resources["CalendarNavigationButtonBorderBrush"] = Brushes.Transparent;
		cal.Resources["CalendarNavigationButtonForeground"] = Brushes.White;
		cal.Resources["CalendarItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(58, 58, 69));
		cal.Resources[SystemColors.GrayTextBrushKey] = new SolidColorBrush(Color.FromRgb(176, 176, 192));
		cal.Resources["CalendarDayTitleTextStyle"] = new Style(typeof(TextBlock))
		{
			Setters = 
			{
				(SetterBase)new Setter(TextBlock.ForegroundProperty, Brushes.White),
				(SetterBase)new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold),
				(SetterBase)new Setter(TextBlock.FontSizeProperty, 11.0)
			}
		};
		Style dayBtnStyle = new Style(typeof(CalendarButton));
		dayBtnStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
		dayBtnStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
		dayBtnStyle.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse("\r\n<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'\r\n                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'\r\n                 TargetType='CalendarButton'>\r\n    <Grid>\r\n        <ContentPresenter x:Name='Content'\r\n                          Content='{TemplateBinding Content}'\r\n                          ContentTemplate='{TemplateBinding ContentTemplate}'\r\n                          Margin='2'\r\n                          HorizontalAlignment='Center'\r\n                          VerticalAlignment='Center'/>\r\n    </Grid>\r\n    <ControlTemplate.Triggers>\r\n        <Trigger Property='IsFocused' Value='True'>\r\n            <Setter TargetName='Content' Property='Margin' Value='1'/>\r\n        </Trigger>\r\n    </ControlTemplate.Triggers>\r\n</ControlTemplate>")));
		cal.Resources["CalendarButtonStyle"] = dayBtnStyle;
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/views/datepickerwindow.xaml", UriKind.Relative);
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
			((Border)target).MouseLeftButtonDown += TitleBar_MouseLeftButtonDown;
			break;
		case 2:
			((Button)target).Click += CloseButton_Click;
			break;
		case 3:
			CalendarControl = (Calendar)target;
			CalendarControl.SelectedDatesChanged += Calendar_SelectedDatesChanged;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
