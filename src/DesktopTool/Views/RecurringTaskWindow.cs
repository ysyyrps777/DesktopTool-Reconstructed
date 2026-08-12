using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using DesktopTool.Models;
using DesktopTool.ViewModels;

namespace DesktopTool.Views;

public class RecurringTaskWindow : Window, IComponentConnector, IStyleConnector
{
	private readonly MainViewModel _vm;

	private RecurringTask? _editing;

	private bool _timeSync;

	private bool _initialized;

	private DateTime _yearlyDate = DateTime.Today;

	private int _pickerYear;

	private int _pickerMonth;

	internal ListBox LstRecurring;

	internal Button AddRecurring;

	internal Border EditorPanel;

	internal TextBox TxtName;

	internal Grid RecurrenceTabs;

	internal RadioButton RbDaily;

	internal RadioButton RbWeekly;

	internal RadioButton RbMonthly;

	internal RadioButton RbYearly;

	internal StackPanel DailyPanel;

	internal TextBox TxtDailyTime;

	internal ListBox LstRecDailyHour;

	internal ListBox LstRecDailyMinute;

	internal StackPanel WeeklyPanel;

	internal ListBox LstWeekday;

	internal StackPanel MonthlyPanel;

	internal ListBox LstMonthDay;

	internal StackPanel YearlyPanel;

	internal Button BtnYearlyDate;

	internal Border DatePickerOverlay;

	internal Border DatePickerPanel;

	internal TextBlock TxtMonthYear;

	internal UniformGrid DayGrid;

	private bool _contentLoaded;

	public RecurringTaskWindow(MainViewModel vm)
	{
		_vm = vm;
		base.DataContext = vm;
		InitializeComponent();
		FillCombos();
		_initialized = true;
		if (TryParseTime(TxtDailyTime.Text, out var hh, out var mm))
		{
			LstRecDailyHour.SelectedIndex = Math.Clamp(hh, 0, 23);
			LstRecDailyMinute.SelectedIndex = Math.Clamp(mm, 0, 59);
		}
		UpdateYearlyDateText();
		CenterOnPrimaryScreen();
	}

	private void CenterOnPrimaryScreen()
	{
		Rect wa = SystemParameters.WorkArea;
		base.Left = wa.Left + (wa.Width - base.Width) / 2.0;
		base.Top = wa.Top + (wa.Height - base.Height) / 2.0;
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ButtonState == MouseButtonState.Pressed)
		{
			try
			{
				DragMove();
			}
			catch
			{
			}
		}
	}

	private void FillCombos()
	{
		if (LstWeekday.Items.Count == 0)
		{
			string[] array = new string[7] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
			foreach (string w in array)
			{
				LstWeekday.Items.Add(w);
			}
		}
		if (LstMonthDay.Items.Count == 0)
		{
			for (int d = 1; d <= 31; d++)
			{
				LstMonthDay.Items.Add(d.ToString());
			}
		}
		if (LstRecDailyHour.Items.Count == 0)
		{
			for (int h = 0; h < 24; h++)
			{
				LstRecDailyHour.Items.Add(h.ToString("D2"));
			}
		}
		if (LstRecDailyMinute.Items.Count == 0)
		{
			for (int m = 0; m < 60; m++)
			{
				LstRecDailyMinute.Items.Add(m.ToString("D2"));
			}
		}
	}

	private void TxtDailyTime_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (!_initialized || _timeSync || !TryParseTime(TxtDailyTime.Text, out var hh, out var mm))
		{
			return;
		}
		_timeSync = true;
		try
		{
			hh = Math.Clamp(hh, 0, 23);
			mm = Math.Clamp(mm, 0, 59);
			if (LstRecDailyHour.SelectedIndex != hh)
			{
				LstRecDailyHour.SelectedIndex = hh;
			}
			if (LstRecDailyMinute.SelectedIndex != mm)
			{
				LstRecDailyMinute.SelectedIndex = mm;
			}
		}
		finally
		{
			_timeSync = false;
		}
	}

	private void LstRecDailyHour_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		SyncTimeBoxFromLists();
	}

	private void LstRecDailyMinute_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		SyncTimeBoxFromLists();
	}

	private void SyncTimeBoxFromLists()
	{
		if (_timeSync)
		{
			return;
		}
		_timeSync = true;
		try
		{
			int hh = ((LstRecDailyHour.SelectedIndex >= 0) ? LstRecDailyHour.SelectedIndex : 0);
			int mm = ((LstRecDailyMinute.SelectedIndex >= 0) ? LstRecDailyMinute.SelectedIndex : 0);
			TxtDailyTime.Text = $"{hh:D2}:{mm:D2}";
		}
		finally
		{
			_timeSync = false;
		}
	}

	private static bool TryParseTime(string s, out int hh, out int mm)
	{
		hh = 0;
		mm = 0;
		if (string.IsNullOrWhiteSpace(s))
		{
			return false;
		}
		s = s.Replace("：", ":");
		string[] parts = s.Split(':');
		if (parts.Length == 2)
		{
			if (int.TryParse(parts[0], out hh))
			{
				return int.TryParse(parts[1], out mm);
			}
			return false;
		}
		if (parts.Length == 1 && s.Length >= 3 && s.Length <= 4 && int.TryParse(s, out var v))
		{
			hh = v / 100;
			mm = v % 100;
			if (hh >= 0 && hh <= 23 && mm >= 0)
			{
				return mm <= 59;
			}
			return false;
		}
		return false;
	}

	private void AddRecurring_Click(object sender, RoutedEventArgs e)
	{
		_editing = null;
		ClearEditor();
		ShowEditor();
	}

	private void EditRecurring_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is RecurringTask def)
		{
			_editing = def;
			LoadEditor(def);
			ShowEditor();
		}
	}

	private void DeleteRecurring_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is RecurringTask def && MessageBox.Show("确定删除循环任务「" + def.Name + "」？\n（已生成的待办不受影响）", "删除循环任务", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
		{
			_vm.RecurringTasks.Remove(def);
			_vm.SaveRecurringTasks();
		}
	}

	private void RecEnabled_Changed(object sender, RoutedEventArgs e)
	{
		_vm.SaveRecurringTasks();
	}

	private void RecurrenceRadio_Changed(object sender, RoutedEventArgs e)
	{
		DailyPanel.Visibility = ((RbDaily.IsChecked != true) ? Visibility.Collapsed : Visibility.Visible);
		WeeklyPanel.Visibility = ((RbWeekly.IsChecked != true) ? Visibility.Collapsed : Visibility.Visible);
		MonthlyPanel.Visibility = ((RbMonthly.IsChecked != true) ? Visibility.Collapsed : Visibility.Visible);
		YearlyPanel.Visibility = ((RbYearly.IsChecked != true) ? Visibility.Collapsed : Visibility.Visible);
	}

	private void Editor_OK(object sender, RoutedEventArgs e)
	{
		string name = TxtName.Text.Trim();
		if (string.IsNullOrEmpty(name))
		{
			MessageBox.Show("请输入任务名称", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		TodoRecurrenceType type = GetSelectedType();
		RecurringTask def = _editing ?? new RecurringTask();
		def.Name = name;
		def.RecurrenceType = type;
		switch (type)
		{
		case TodoRecurrenceType.Weekly:
			def.RecurrenceWeekday = ((LstWeekday.SelectedIndex < 0) ? DayOfWeek.Monday : ((DayOfWeek)LstWeekday.SelectedIndex));
			break;
		case TodoRecurrenceType.Monthly:
		{
			int md = LstMonthDay.SelectedIndex + 1;
			def.RecurrenceMonthDay = ((md < 1 || md > 31) ? 1 : md);
			break;
		}
		case TodoRecurrenceType.Daily:
		{
			int hh = LstRecDailyHour.SelectedIndex;
			int mm = LstRecDailyMinute.SelectedIndex;
			if (hh < 0 || hh > 23)
			{
				hh = 9;
			}
			if (mm < 0 || mm > 59)
			{
				mm = 0;
			}
			def.RecurrenceTime = new TimeSpan(hh, mm, 0);
			break;
		}
		case TodoRecurrenceType.Yearly:
			def.RecurrenceDate = _yearlyDate;
			break;
		}
		if (_editing == null)
		{
			def.Enabled = true;
			_vm.RecurringTasks.Add(def);
		}
		_vm.SaveRecurringTasks();
		HideEditor();
	}

	private void Editor_Cancel(object sender, RoutedEventArgs e)
	{
		HideEditor();
	}

	private void Close_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private TodoRecurrenceType GetSelectedType()
	{
		if (RbDaily.IsChecked == true)
		{
			return TodoRecurrenceType.Daily;
		}
		if (RbWeekly.IsChecked == true)
		{
			return TodoRecurrenceType.Weekly;
		}
		if (RbMonthly.IsChecked == true)
		{
			return TodoRecurrenceType.Monthly;
		}
		if (RbYearly.IsChecked == true)
		{
			return TodoRecurrenceType.Yearly;
		}
		return TodoRecurrenceType.Daily;
	}

	private void LoadEditor(RecurringTask def)
	{
		TxtName.Text = def.Name;
		RbDaily.IsChecked = def.RecurrenceType == TodoRecurrenceType.Daily;
		RbWeekly.IsChecked = def.RecurrenceType == TodoRecurrenceType.Weekly;
		RbMonthly.IsChecked = def.RecurrenceType == TodoRecurrenceType.Monthly;
		RbYearly.IsChecked = def.RecurrenceType == TodoRecurrenceType.Yearly;
		if (!RbDaily.IsChecked.Value && !RbWeekly.IsChecked.Value && !RbMonthly.IsChecked.Value && !RbYearly.IsChecked.Value)
		{
			RbDaily.IsChecked = true;
		}
		LstWeekday.SelectedIndex = Math.Clamp((int)def.RecurrenceWeekday, 0, 6);
		LstMonthDay.SelectedIndex = Math.Clamp(def.RecurrenceMonthDay - 1, 0, 30);
		LstRecDailyHour.SelectedIndex = def.RecurrenceTime.Hours;
		LstRecDailyMinute.SelectedIndex = def.RecurrenceTime.Minutes;
		_yearlyDate = ((def.RecurrenceDate.Date == default(DateTime)) ? DateTime.Today : def.RecurrenceDate.Date);
		UpdateYearlyDateText();
		RecurrenceRadio_Changed(null, null);
	}

	private void ClearEditor()
	{
		TxtName.Text = string.Empty;
		RbDaily.IsChecked = true;
		LstWeekday.SelectedIndex = 1;
		LstMonthDay.SelectedIndex = 0;
		LstRecDailyHour.SelectedIndex = 9;
		LstRecDailyMinute.SelectedIndex = 0;
		_yearlyDate = DateTime.Today;
		UpdateYearlyDateText();
	}

	private void UpdateYearlyDateText()
	{
		BtnYearlyDate.Content = _yearlyDate.ToString("yyyy年M月d日");
	}

	private void YearlyDate_Click(object sender, RoutedEventArgs e)
	{
		_pickerYear = _yearlyDate.Year;
		_pickerMonth = _yearlyDate.Month;
		BuildDayGrid();
		DatePickerOverlay.Visibility = Visibility.Visible;
	}

	private void BuildDayGrid()
	{
		DayGrid.Children.Clear();
		TxtMonthYear.Text = $"{_pickerYear}年{_pickerMonth}月";
		DateTime firstDay = new DateTime(_pickerYear, _pickerMonth, 1);
		int startDayOfWeek = (int)firstDay.DayOfWeek;
		int daysInMonth = DateTime.DaysInMonth(_pickerYear, _pickerMonth);
		DateTime today = DateTime.Today;
		if (startDayOfWeek > 0)
		{
			DateTime prevMonth = firstDay.AddMonths(-1);
			int prevDays = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);
			for (int i = startDayOfWeek - 1; i >= 0; i--)
			{
				int day = prevDays - i;
				DayGrid.Children.Add(CreateDayButton(day, isCurrentMonth: false, isToday: false, isSelected: false, new DateTime(prevMonth.Year, prevMonth.Month, day)));
			}
		}
		for (int d = 1; d <= daysInMonth; d++)
		{
			DateTime date = new DateTime(_pickerYear, _pickerMonth, d);
			DayGrid.Children.Add(CreateDayButton(d, isCurrentMonth: true, date == today, date == _yearlyDate.Date, date));
		}
		int remaining = 42 - DayGrid.Children.Count;
		DateTime nextMonth = firstDay.AddMonths(1);
		for (int j = 1; j <= remaining; j++)
		{
			DayGrid.Children.Add(CreateDayButton(j, isCurrentMonth: false, isToday: false, isSelected: false, new DateTime(nextMonth.Year, nextMonth.Month, j)));
		}
	}

	private Button CreateDayButton(int day, bool isCurrentMonth, bool isToday, bool isSelected, DateTime date)
	{
		Button obj = new Button
		{
			Content = day.ToString(),
			Width = 28.0,
			Height = 28.0,
			FontSize = 12.0,
			Cursor = Cursors.Hand,
			Tag = date,
			ToolTip = date.ToString("yyyy-MM-dd")
		};
		Color bg;
		Color fg;
		if (isSelected)
		{
			bg = Color.FromRgb(51, 153, byte.MaxValue);
			fg = Colors.White;
		}
		else if (isToday)
		{
			bg = Color.FromRgb(85, 85, 101);
			fg = Colors.White;
		}
		else if (isCurrentMonth)
		{
			bg = Color.FromRgb(58, 58, 68);
			fg = Colors.White;
		}
		else
		{
			bg = Color.FromRgb(42, 42, 50);
			fg = Color.FromRgb(102, 102, 119);
		}
		obj.Background = new SolidColorBrush(bg);
		obj.Foreground = new SolidColorBrush(fg);
		obj.BorderThickness = new Thickness(0.0);
		ControlTemplate template = new ControlTemplate(typeof(Button));
		FrameworkElementFactory borderFactory = new FrameworkElementFactory(typeof(Border));
		borderFactory.SetBinding(Border.BackgroundProperty, new Binding("Background")
		{
			RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
		});
		borderFactory.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness")
		{
			RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
		});
		borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4.0));
		borderFactory.SetValue(Border.PaddingProperty, new Thickness(2.0));
		FrameworkElementFactory contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
		contentPresenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
		contentPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		borderFactory.AppendChild(contentPresenter);
		template.VisualTree = borderFactory;
		Trigger hoverTrigger = new Trigger
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(85, 85, 101))));
		template.Triggers.Add(hoverTrigger);
		obj.Template = template;
		obj.Click += DayButton_Click;
		return obj;
	}

	private void DayButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: var tag } && tag is DateTime selectedDate)
		{
			_yearlyDate = selectedDate.Date;
			UpdateYearlyDateText();
		}
		DatePickerOverlay.Visibility = Visibility.Collapsed;
	}

	private void DatePicker_PrevMonth(object sender, RoutedEventArgs e)
	{
		if (_pickerMonth == 1)
		{
			_pickerMonth = 12;
			_pickerYear--;
		}
		else
		{
			_pickerMonth--;
		}
		BuildDayGrid();
	}

	private void DatePicker_NextMonth(object sender, RoutedEventArgs e)
	{
		if (_pickerMonth == 12)
		{
			_pickerMonth = 1;
			_pickerYear++;
		}
		else
		{
			_pickerMonth++;
		}
		BuildDayGrid();
	}

	private void DatePickerOverlay_BackgroundClick(object sender, MouseButtonEventArgs e)
	{
		DatePickerOverlay.Visibility = Visibility.Collapsed;
	}

	private void DatePickerPanel_PreventClose(object sender, MouseButtonEventArgs e)
	{
		e.Handled = true;
	}

	private void DatePickerCloseButton_Click(object sender, RoutedEventArgs e)
	{
		DatePickerOverlay.Visibility = Visibility.Collapsed;
	}

	private void ShowEditor()
	{
		EditorPanel.Visibility = Visibility.Visible;
	}

	private void HideEditor()
	{
		EditorPanel.Visibility = Visibility.Collapsed;
		_editing = null;
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/views/recurringtaskwindow.xaml", UriKind.Relative);
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
			((RecurringTaskWindow)target).Loaded += Window_Loaded;
			break;
		case 2:
			((Grid)target).MouseLeftButtonDown += TitleBar_MouseLeftButtonDown;
			break;
		case 3:
			((Button)target).Click += Close_Click;
			break;
		case 4:
			LstRecurring = (ListBox)target;
			break;
		case 8:
			AddRecurring = (Button)target;
			AddRecurring.Click += AddRecurring_Click;
			break;
		case 9:
			EditorPanel = (Border)target;
			break;
		case 10:
			TxtName = (TextBox)target;
			break;
		case 11:
			RecurrenceTabs = (Grid)target;
			break;
		case 12:
			RbDaily = (RadioButton)target;
			RbDaily.Checked += RecurrenceRadio_Changed;
			break;
		case 13:
			RbWeekly = (RadioButton)target;
			RbWeekly.Checked += RecurrenceRadio_Changed;
			break;
		case 14:
			RbMonthly = (RadioButton)target;
			RbMonthly.Checked += RecurrenceRadio_Changed;
			break;
		case 15:
			RbYearly = (RadioButton)target;
			RbYearly.Checked += RecurrenceRadio_Changed;
			break;
		case 16:
			DailyPanel = (StackPanel)target;
			break;
		case 17:
			TxtDailyTime = (TextBox)target;
			TxtDailyTime.TextChanged += TxtDailyTime_TextChanged;
			break;
		case 18:
			LstRecDailyHour = (ListBox)target;
			LstRecDailyHour.SelectionChanged += LstRecDailyHour_SelectionChanged;
			break;
		case 19:
			LstRecDailyMinute = (ListBox)target;
			LstRecDailyMinute.SelectionChanged += LstRecDailyMinute_SelectionChanged;
			break;
		case 20:
			WeeklyPanel = (StackPanel)target;
			break;
		case 21:
			LstWeekday = (ListBox)target;
			break;
		case 22:
			MonthlyPanel = (StackPanel)target;
			break;
		case 23:
			LstMonthDay = (ListBox)target;
			break;
		case 24:
			YearlyPanel = (StackPanel)target;
			break;
		case 25:
			BtnYearlyDate = (Button)target;
			BtnYearlyDate.Click += YearlyDate_Click;
			break;
		case 26:
			((Button)target).Click += Editor_OK;
			break;
		case 27:
			((Button)target).Click += Editor_Cancel;
			break;
		case 28:
			DatePickerOverlay = (Border)target;
			DatePickerOverlay.MouseLeftButtonDown += DatePickerOverlay_BackgroundClick;
			break;
		case 29:
			DatePickerPanel = (Border)target;
			DatePickerPanel.MouseLeftButtonDown += DatePickerPanel_PreventClose;
			break;
		case 30:
			((Button)target).Click += DatePickerCloseButton_Click;
			break;
		case 31:
			((Button)target).Click += DatePicker_PrevMonth;
			break;
		case 32:
			TxtMonthYear = (TextBlock)target;
			break;
		case 33:
			((Button)target).Click += DatePicker_NextMonth;
			break;
		case 34:
			DayGrid = (UniformGrid)target;
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
		case 5:
			((CheckBox)target).Checked += RecEnabled_Changed;
			((CheckBox)target).Unchecked += RecEnabled_Changed;
			break;
		case 6:
			((Button)target).Click += EditRecurring_Click;
			break;
		case 7:
			((Button)target).Click += DeleteRecurring_Click;
			break;
		}
	}
}
