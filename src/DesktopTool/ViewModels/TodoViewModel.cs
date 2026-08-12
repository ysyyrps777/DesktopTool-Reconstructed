using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClosedXML.Excel;
using DesktopTool.Models;
using DesktopTool.Services;
using DesktopTool.Views;
using Microsoft.Win32;

namespace DesktopTool.ViewModels;

public class TodoViewModel : ViewModelBase
{
	private readonly ObservableCollection<TodoItem> _allTodos;

	private readonly MainViewModel _mainVm;

	private readonly DataService _dataService;

	private string _newTodoText = "";

	private string _searchText = "";

	private TodoFilter _currentFilter;

	private TodoDateGroup _currentDateGroup;

	private double _windowLeft;

	private double _windowTop;

	private double _todoWidth = 320.0;

	private double _todoHeight = 400.0;

	private bool _isCollapsed;

	private bool _isHoverExpanded;

	private bool _isLocked;

	private bool _isColorFilterCollapsed;

	private bool _isFilterAreaCollapsed;

	private bool _sortAscending;

	public static readonly (string Name, string Hex)[] TodoColors = new(string, string)[8]
	{
		("无标记", ""),
		("红色", "#FF6B6B"),
		("橙色", "#FFA94D"),
		("黄色", "#FFD93D"),
		("绿色", "#6BCB77"),
		("蓝色", "#4D96FF"),
		("紫色", "#9B59B6"),
		("粉色", "#FF6B9D")
	};

	private string? _currentColorFilter;

	private DateTime? _customDateFrom;

	private DateTime? _customDateTo;

	private const string LockClosedGeometry = "M4,10 L20,10 C21.1,10 22,10.9 22,12 L22,22 C22,23.1 21.1,24 20,24 L4,24 C2.9,24 2,23.1 2,22 L2,12 C2,10.9 2.9,10 4,10 Z M8,10 L8,6 C8,3.79 9.79,2 12,2 C14.21,2 16,3.79 16,6 L16,10 L14,10 L14,6 C14,4.9 13.1,4 12,4 C10.9,4 10,4.9 10,6 L10,10 Z";

	private const string LockOpenGeometry = "M4,10 L20,10 C21.1,10 22,10.9 22,12 L22,22 C22,23.1 21.1,24 20,24 L4,24 C2.9,24 2,23.1 2,22 L2,12 C2,10.9 2.9,10 4,10 Z M18,10 L18,6 C18,2.69 15.31,0 12,0 C8.69,0 6,2.69 6,6 L6,7 L8,7 L8,6 C8,3.79 9.79,2 12,2 C14.21,2 16,3.79 16,6 L16,10 Z";

	private bool _pendingFilterRefresh;

	public MainViewModel MainVm => _mainVm;

	public ObservableCollection<TodoItem> FilteredTodos { get; } = new ObservableCollection<TodoItem>();

	public string NewTodoText
	{
		get
		{
			return _newTodoText;
		}
		set
		{
			if (SetProperty(ref _newTodoText, value, "NewTodoText"))
			{
				CommandManager.InvalidateRequerySuggested();
			}
		}
	}

	public string SearchText
	{
		get
		{
			return _searchText;
		}
		set
		{
			if (SetProperty(ref _searchText, value, "SearchText"))
			{
				ApplyFilter();
			}
		}
	}

	public TodoFilter CurrentFilter
	{
		get
		{
			return _currentFilter;
		}
		set
		{
			if (SetProperty(ref _currentFilter, value, "CurrentFilter"))
			{
				ApplyFilter();
			}
		}
	}

	public TodoDateGroup CurrentDateGroup
	{
		get
		{
			return _currentDateGroup;
		}
		set
		{
			if (SetProperty(ref _currentDateGroup, value, "CurrentDateGroup"))
			{
				OnPropertyChanged("IsCustomDateRangeActive");
				ApplyFilter();
			}
		}
	}

	public double WindowLeft
	{
		get
		{
			return _windowLeft;
		}
		set
		{
			SetProperty(ref _windowLeft, value, "WindowLeft");
		}
	}

	public double WindowTop
	{
		get
		{
			return _windowTop;
		}
		set
		{
			SetProperty(ref _windowTop, value, "WindowTop");
		}
	}

	public double TodoWidth
	{
		get
		{
			return _todoWidth;
		}
		set
		{
			SetProperty(ref _todoWidth, value, "TodoWidth");
		}
	}

	public double TodoHeight
	{
		get
		{
			return _todoHeight;
		}
		set
		{
			SetProperty(ref _todoHeight, value, "TodoHeight");
		}
	}

	public ICommand AddTodoCommand { get; }

	public ICommand ToggleTodoCommand { get; }

	public ICommand DeleteTodoCommand { get; }

	public ICommand SetFilterCommand { get; }

	public ICommand SetDateGroupCommand { get; }

	public ICommand ToggleCollapseCommand { get; }

	public ICommand ToggleLockCommand { get; }

	public ICommand ToggleSortDirectionCommand { get; }

	public ICommand SetTodoColorCommand { get; }

	public ICommand SetColorFilterCommand { get; }

	public ICommand ToggleColorFilterCommand { get; }

	public ICommand ToggleFilterAreaCommand { get; }

	public ICommand SetTodoDateCommand { get; }

	public ICommand ClearTodoDateCommand { get; }

	public ICommand ApplyCustomDateRangeCommand { get; }

	public ICommand ExportToExcelCommand { get; }

	public ICommand ExportToTxtCommand { get; }

	public IEnumerable<TodoFilter> FilterOptions => Enum.GetValues<TodoFilter>();

	public IEnumerable<TodoDateGroup> DateGroupOptions => Enum.GetValues<TodoDateGroup>();

	public int TotalCount => _allTodos.Count;

	public int ActiveCount => _allTodos.Count((TodoItem t) => !t.IsCompleted);

	public int CompletedCount => _allTodos.Count((TodoItem t) => t.IsCompleted);

	public double BackgroundOpacity
	{
		get
		{
			return _mainVm.GlobalOpacity;
		}
		set
		{
			_mainVm.GlobalOpacity = value;
		}
	}

	public Brush PanelColorBrush => _mainVm.PanelColorBrush;

	public double TodoTitleFontSize => 14.0 * _mainVm.DesktopFontRatio * _mainVm.FontScaleMultiplier;

	public double TodoContentFontSize => 12.0 * _mainVm.DesktopFontRatio * _mainVm.FontScaleMultiplier;

	public double TodoSmallFontSize => 11.0 * _mainVm.DesktopFontRatio * _mainVm.FontScaleMultiplier;

	public double TodoLargeFontSize => 18.0 * _mainVm.DesktopFontRatio * _mainVm.FontScaleMultiplier;

	public bool IsCollapsed
	{
		get
		{
			return _isCollapsed;
		}
		set
		{
			if (SetProperty(ref _isCollapsed, value, "IsCollapsed"))
			{
				_isHoverExpanded = false;
				OnPropertyChanged("IsHoverExpanded");
				_dataService.SaveTodoCollapsed(value);
				OnPropertyChanged("ContentVisibility");
				OnPropertyChanged("CollapseLabel");
				this.CollapsedChanged?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	public bool IsHoverExpanded
	{
		get
		{
			return _isHoverExpanded;
		}
		set
		{
			if (SetProperty(ref _isHoverExpanded, value, "IsHoverExpanded"))
			{
				OnPropertyChanged("ContentVisibility");
			}
		}
	}

	public Visibility ContentVisibility
	{
		get
		{
			if (!IsCollapsed || IsHoverExpanded)
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public string CollapseLabel
	{
		get
		{
			if (!_isCollapsed)
			{
				return "▲";
			}
			return "▼";
		}
	}

	public bool IsColorFilterCollapsed
	{
		get
		{
			return _isColorFilterCollapsed;
		}
		set
		{
			if (SetProperty(ref _isColorFilterCollapsed, value, "IsColorFilterCollapsed"))
			{
				_dataService.SaveTodoColorFilterCollapsed(value);
				OnPropertyChanged("ColorFilterVisibility");
				OnPropertyChanged("ColorFilterCollapseLabel");
			}
		}
	}

	public Visibility ColorFilterVisibility
	{
		get
		{
			if (!_isColorFilterCollapsed)
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public string ColorFilterCollapseLabel
	{
		get
		{
			if (!_isColorFilterCollapsed)
			{
				return "▲";
			}
			return "▼";
		}
	}

	public bool IsFilterAreaCollapsed
	{
		get
		{
			return _isFilterAreaCollapsed;
		}
		set
		{
			if (SetProperty(ref _isFilterAreaCollapsed, value, "IsFilterAreaCollapsed"))
			{
				_dataService.SaveTodoFilterAreaCollapsed(value);
				IsColorFilterCollapsed = value;
				OnPropertyChanged("FilterAreaVisibility");
				OnPropertyChanged("FilterAreaCollapseLabel");
			}
		}
	}

	public Visibility FilterAreaVisibility
	{
		get
		{
			if (!_isFilterAreaCollapsed)
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public string FilterAreaCollapseLabel
	{
		get
		{
			if (!_isFilterAreaCollapsed)
			{
				return "▲";
			}
			return "▼";
		}
	}

	public bool SortAscending
	{
		get
		{
			return _sortAscending;
		}
		set
		{
			if (SetProperty(ref _sortAscending, value, "SortAscending"))
			{
				ResetSortOrderByDateRef();
				ApplyFilter();
				OnPropertyChanged("SortLabel");
			}
		}
	}

	public string SortLabel
	{
		get
		{
			if (!_sortAscending)
			{
				return "▼ 时间";
			}
			return "▲ 时间";
		}
	}

	public string? CurrentColorFilter
	{
		get
		{
			return _currentColorFilter;
		}
		set
		{
			if (SetProperty(ref _currentColorFilter, value, "CurrentColorFilter"))
			{
				ApplyFilter();
			}
		}
	}

	public bool ColorFilterActive => _currentColorFilter != null;

	public DateTime? CustomDateFrom
	{
		get
		{
			return _customDateFrom;
		}
		set
		{
			SetProperty(ref _customDateFrom, value, "CustomDateFrom");
			OnPropertyChanged("CustomDateFromText");
		}
	}

	public DateTime? CustomDateTo
	{
		get
		{
			return _customDateTo;
		}
		set
		{
			SetProperty(ref _customDateTo, value, "CustomDateTo");
			OnPropertyChanged("CustomDateToText");
		}
	}

	public string CustomDateFromText => _customDateFrom?.ToString("yyyy-MM-dd") ?? "";

	public string CustomDateToText => _customDateTo?.ToString("yyyy-MM-dd") ?? "";

	public bool IsCustomDateRangeActive => _currentDateGroup == TodoDateGroup.CustomRange;

	public string CustomRangeSummary
	{
		get
		{
			if (!IsCustomDateRangeActive)
			{
				return "";
			}
			return "从 " + CustomDateFromText + " 到 " + CustomDateToText;
		}
	}

	public bool IsLocked
	{
		get
		{
			return _isLocked;
		}
		set
		{
			if (SetProperty(ref _isLocked, value, "IsLocked"))
			{
				OnPropertyChanged("LockIcon");
				_dataService.SaveTodoLocked(value);
			}
		}
	}

	public string LockIcon
	{
		get
		{
			if (!_isLocked)
			{
				return "M4,10 L20,10 C21.1,10 22,10.9 22,12 L22,22 C22,23.1 21.1,24 20,24 L4,24 C2.9,24 2,23.1 2,22 L2,12 C2,10.9 2.9,10 4,10 Z M18,10 L18,6 C18,2.69 15.31,0 12,0 C8.69,0 6,2.69 6,6 L6,7 L8,7 L8,6 C8,3.79 9.79,2 12,2 C14.21,2 16,3.79 16,6 L16,10 Z";
			}
			return "M4,10 L20,10 C21.1,10 22,10.9 22,12 L22,22 C22,23.1 21.1,24 20,24 L4,24 C2.9,24 2,23.1 2,22 L2,12 C2,10.9 2.9,10 4,10 Z M8,10 L8,6 C8,3.79 9.79,2 12,2 C14.21,2 16,3.79 16,6 L16,10 L14,10 L14,6 C14,4.9 13.1,4 12,4 C10.9,4 10,4.9 10,6 L10,10 Z";
		}
	}

	public string CompletedTodoStyle => _mainVm.CompletedTodoStyle;

	public event EventHandler? CollapsedChanged;

	private void ResetSortOrderByDateRef()
	{
		IOrderedEnumerable<TodoItem> obj = (_sortAscending ? (from t in _allTodos
			orderby t.IsCompleted ? 1 : 0, t.DateRef
			select t) : (from t in _allTodos
			orderby t.IsCompleted ? 1 : 0, t.DateRef descending
			select t));
		int i = 1;
		foreach (TodoItem item in obj)
		{
			item.SortOrder = i++;
		}
	}

	public void RefreshSortView()
	{
		_sortAscending = false;
		ApplyFilter();
	}

	public TodoViewModel(ObservableCollection<TodoItem> allTodos, MainViewModel mainVm)
	{
		_allTodos = allTodos;
		_mainVm = mainVm;
		_dataService = mainVm.DataService;
		_mainVm.PropertyChanged += delegate(object? s, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == "GlobalOpacity")
			{
				OnPropertyChanged("BackgroundOpacity");
			}
			else if (e.PropertyName == "FontSizeLevel")
			{
				RefreshFontSizes();
			}
			if (e.PropertyName == "PanelColorBrush")
			{
				OnPropertyChanged("PanelColorBrush");
			}
		};
		LoadWindowState();
		_isCollapsed = _dataService.LoadTodoCollapsed();
		_isLocked = _dataService.LoadTodoLocked();
		_isColorFilterCollapsed = _dataService.LoadTodoColorFilterCollapsed();
		_isFilterAreaCollapsed = _dataService.LoadTodoFilterAreaCollapsed();
		OnPropertyChanged("FilterAreaVisibility");
		OnPropertyChanged("FilterAreaCollapseLabel");
		_allTodos.CollectionChanged += OnAllTodosChanged;
		AddTodoCommand = new RelayCommand(delegate
		{
			AddTodo(null);
		}, () => !string.IsNullOrWhiteSpace(NewTodoText));
		ToggleTodoCommand = new RelayCommand(ToggleTodo);
		DeleteTodoCommand = new RelayCommand(DeleteTodo);
		SetFilterCommand = new RelayCommand(delegate(object? param)
		{
			if (param is string value && Enum.TryParse<TodoFilter>(value, out var result))
			{
				CurrentFilter = result;
			}
			else if (param is TodoFilter currentFilter)
			{
				CurrentFilter = currentFilter;
			}
		});
		SetDateGroupCommand = new RelayCommand(delegate(object? param)
		{
			TodoDateGroup todoDateGroup = TodoDateGroup.All;
			if (param is string value && Enum.TryParse<TodoDateGroup>(value, out var result))
			{
				todoDateGroup = result;
			}
			else if (param is TodoDateGroup todoDateGroup2)
			{
				todoDateGroup = todoDateGroup2;
			}
			if (todoDateGroup == TodoDateGroup.CustomRange)
			{
				DateTime today = DateTime.Today;
				int num = (int)(today.DayOfWeek - 1);
				if (num < 0)
				{
					num = 6;
				}
				CustomDateFrom = today.AddDays(-num);
				CustomDateTo = today.AddDays(6 - num);
			}
			CurrentDateGroup = todoDateGroup;
		});
		ToggleCollapseCommand = new RelayCommand((Action<object?>)delegate
		{
			IsCollapsed = !IsCollapsed;
		}, (Func<object?, bool>?)null);
		ToggleLockCommand = new RelayCommand((Action<object?>)delegate
		{
			IsLocked = !IsLocked;
		}, (Func<object?, bool>?)null);
		ToggleSortDirectionCommand = new RelayCommand((Action<object?>)delegate
		{
			SortAscending = !SortAscending;
		}, (Func<object?, bool>?)null);
		SetTodoColorCommand = new RelayCommand(SetTodoColor);
		SetColorFilterCommand = new RelayCommand(delegate(object? param)
		{
			string text = param as string;
			if (text == "All")
			{
				CurrentColorFilter = null;
			}
			else if (text == "None")
			{
				CurrentColorFilter = "";
			}
			else
			{
				CurrentColorFilter = text;
			}
		});
		ToggleColorFilterCommand = new RelayCommand((Action<object?>)delegate
		{
			IsFilterAreaCollapsed = !IsFilterAreaCollapsed;
		}, (Func<object?, bool>?)null);
		ToggleFilterAreaCommand = new RelayCommand((Action<object?>)delegate
		{
			IsFilterAreaCollapsed = !IsFilterAreaCollapsed;
		}, (Func<object?, bool>?)null);
		SetTodoDateCommand = new RelayCommand(SetTodoDate);
		ClearTodoDateCommand = new RelayCommand(ClearTodoDate);
		ApplyCustomDateRangeCommand = new RelayCommand((Action<object?>)delegate
		{
			ApplyFilter();
		}, (Func<object?, bool>?)null);
		ExportToExcelCommand = new RelayCommand(ExportToExcel);
		ExportToTxtCommand = new RelayCommand(ExportToTxt);
		ApplyFilter();
	}

	private void LoadWindowState()
	{
		(double, double, double, double)? state = _dataService.LoadTodoWindowState();
		if (state.HasValue)
		{
			_windowLeft = state.Value.Item1;
			_windowTop = state.Value.Item2;
			_todoWidth = state.Value.Item3;
			_todoHeight = state.Value.Item4;
		}
		else
		{
			(double Left, double Top) primaryScreenCenter = DesktopEmbedService.GetPrimaryScreenCenter(_todoWidth, _todoHeight);
			double cx = primaryScreenCenter.Left;
			double cy = primaryScreenCenter.Top;
			_windowLeft = cx;
			_windowTop = cy;
		}
	}

	public void SaveWindowState()
	{
		_dataService.SaveTodoWindowState(WindowLeft, WindowTop, TodoWidth, TodoHeight);
	}

	private void AddTodo(object? _)
	{
		if (string.IsNullOrWhiteSpace(NewTodoText))
		{
			return;
		}
		foreach (TodoItem allTodo in _allTodos)
		{
			allTodo.SortOrder++;
		}
		_allTodos.Add(new TodoItem
		{
			Content = NewTodoText.Trim(),
			CreatedAt = DateTime.Now,
			SortOrder = 1
		});
		NewTodoText = "";
		_mainVm.SaveTodos();
		ApplyFilter();
	}

	private void ToggleTodo(object? parameter)
	{
		if (parameter is TodoItem t)
		{
			t.IsCompleted = !t.IsCompleted;
			t.CompletedAt = (t.IsCompleted ? new DateTime?(DateTime.Now) : ((DateTime?)null));
			_mainVm.SaveTodos();
			ApplyFilter();
			OnCountsChanged();
		}
	}

	public void SetTodoCompleted(TodoItem t, bool completed)
	{
		if ((!completed || !t.IsCompleted || !t.CompletedAt.HasValue) && (completed || t.IsCompleted || t.CompletedAt.HasValue))
		{
			t.IsCompleted = completed;
			t.CompletedAt = (completed ? new DateTime?(t.CompletedAt ?? DateTime.Now) : ((DateTime?)null));
			_mainVm.SaveTodos();
			ApplyFilter();
			OnCountsChanged();
		}
	}

	private void DeleteTodo(object? parameter)
	{
		if (parameter is TodoItem t)
		{
			_allTodos.Remove(t);
			_mainVm.SaveTodos();
			ApplyFilter();
			OnCountsChanged();
		}
	}

	public void MoveTodo(TodoItem dragged, TodoItem target)
	{
		List<TodoItem> ordered = _allTodos.OrderBy((TodoItem t) => t.SortOrder).ToList();
		int oldIndex = ordered.IndexOf(dragged);
		int newIndex = ordered.IndexOf(target);
		if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
		{
			ordered.RemoveAt(oldIndex);
			int insertIndex = ((oldIndex < newIndex) ? (newIndex - 1) : newIndex);
			ordered.Insert(insertIndex, dragged);
			for (int i = 0; i < ordered.Count; i++)
			{
				ordered[i].SortOrder = i + 1;
			}
			_mainVm.SaveTodos();
			ApplyFilter();
		}
	}

	private void OnAllTodosChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (!_pendingFilterRefresh)
		{
			_pendingFilterRefresh = true;
			Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate
			{
				_pendingFilterRefresh = false;
				ApplyFilter();
			});
		}
	}

	public void ApplyFilter()
	{
		IEnumerable<TodoItem> items = _allTodos;
		items = _currentFilter switch
		{
			TodoFilter.Active => items.Where((TodoItem t) => !t.IsCompleted), 
			TodoFilter.Completed => items.Where((TodoItem t) => t.IsCompleted), 
			_ => items, 
		};
		DateTime now = DateTime.Now;
		items = _currentDateGroup switch
		{
			TodoDateGroup.Today => items.Where((TodoItem t) => t.DateRef.Date == now.Date), 
			TodoDateGroup.Yesterday => items.Where((TodoItem t) => t.DateRef.Date == now.Date.AddDays(-1.0)), 
			TodoDateGroup.ThisWeek => items.Where(delegate(TodoItem t)
			{
				DateTime dateTime = now.Date.AddDays(0 - ((now.DayOfWeek == DayOfWeek.Sunday) ? ((DayOfWeek)7) : now.DayOfWeek) + 1);
				DateTime dateTime2 = dateTime.AddDays(6.0);
				return t.DateRef.Date >= dateTime && t.DateRef.Date <= dateTime2;
			}), 
			TodoDateGroup.LastWeek => items.Where(delegate(TodoItem t)
			{
				DateTime dateTime = now.Date.AddDays(0 - ((now.DayOfWeek == DayOfWeek.Sunday) ? ((DayOfWeek)7) : now.DayOfWeek) + 1);
				DateTime dateTime2 = dateTime.AddDays(-7.0);
				DateTime dateTime3 = dateTime.AddDays(-1.0);
				return t.DateRef.Date >= dateTime2 && t.DateRef.Date <= dateTime3;
			}), 
			TodoDateGroup.ThisMonth => items.Where((TodoItem t) => t.DateRef.Year == now.Year && t.DateRef.Month == now.Month), 
			TodoDateGroup.LastMonth => items.Where(delegate(TodoItem t)
			{
				DateTime dateTime = now.AddMonths(-1);
				return t.DateRef.Year == dateTime.Year && t.DateRef.Month == dateTime.Month;
			}), 
			TodoDateGroup.CustomRange => items.Where(delegate(TodoItem t)
			{
				bool num = !_customDateFrom.HasValue || t.DateRef.Date >= _customDateFrom.Value.Date;
				bool flag = !_customDateTo.HasValue || t.DateRef.Date <= _customDateTo.Value.Date;
				return num && flag;
			}), 
			_ => items, 
		};
		if (_currentColorFilter != null)
		{
			if (_currentColorFilter == "")
			{
				items = items.Where((TodoItem t) => string.IsNullOrEmpty(t.Color));
			}
			else
			{
				string target = NormalizeColor(_currentColorFilter);
				items = items.Where((TodoItem t) => NormalizeColor(t.Color) == target);
			}
		}
		if (!string.IsNullOrWhiteSpace(_searchText))
		{
			string kw = _searchText.Trim();
			items = items.Where((TodoItem t) => t.Content != null && t.Content.Contains(kw, StringComparison.OrdinalIgnoreCase));
		}
		items = ((!_sortAscending) ? (from t in items
			orderby t.IsCompleted ? 1 : 0, t.SortOrder, t.DateRef descending
			select t) : (from t in items
			orderby t.IsCompleted ? 1 : 0, t.SortOrder, t.DateRef
			select t));
		List<TodoItem> sortedList = items.ToList();
		Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate
		{
			FilteredTodos.Clear();
			foreach (TodoItem current in sortedList)
			{
				FilteredTodos.Add(current);
			}
		});
		static string NormalizeColor(string c)
		{
			if (string.IsNullOrEmpty(c))
			{
				return c;
			}
			string s = (c ?? string.Empty).Trim().TrimStart('#');
			if (s.Length == 8)
			{
				string text = s;
				s = text.Substring(2, text.Length - 2);
			}
			else if (s.Length == 3)
			{
				s = string.Concat(s.Select((char x) => $"{x}{x}"));
			}
			else if (s.Length != 6)
			{
				return c;
			}
			return "#" + s.ToUpperInvariant();
		}
	}

	private void SetTodoColor(object? parameter)
	{
		if (parameter is object[] args && args.Length >= 2 && args[0] is TodoItem todo && args[1] is string color)
		{
			todo.Color = (string.IsNullOrEmpty(color) ? null : color);
			_mainVm.SaveTodos();
		}
	}

	private void SetTodoDate(object? parameter)
	{
		if (parameter is object[] args && args.Length >= 2 && args[0] is TodoItem todo && args[1] is DateTime date)
		{
			todo.DueDate = date;
			if (_currentDateGroup != TodoDateGroup.All)
			{
				SetProperty(ref _currentDateGroup, TodoDateGroup.All, "CurrentDateGroup");
			}
			_mainVm.SaveTodos();
			ApplyFilter();
		}
	}

	private void ClearTodoDate(object? parameter)
	{
		if (parameter is TodoItem todo)
		{
			todo.DueDate = null;
			if (_currentDateGroup != TodoDateGroup.All)
			{
				SetProperty(ref _currentDateGroup, TodoDateGroup.All, "CurrentDateGroup");
			}
			_mainVm.SaveTodos();
			ApplyFilter();
		}
	}

	private void OnCountsChanged()
	{
		OnPropertyChanged("TotalCount");
		OnPropertyChanged("ActiveCount");
		OnPropertyChanged("CompletedCount");
	}

	public void RefreshCompletedStyle()
	{
		OnPropertyChanged("CompletedTodoStyle");
	}

	public void RefreshFontSizes()
	{
		OnPropertyChanged("TodoTitleFontSize");
		OnPropertyChanged("TodoContentFontSize");
		OnPropertyChanged("TodoSmallFontSize");
		OnPropertyChanged("TodoLargeFontSize");
	}

	private static string EnsureFileWritable(string originalPath)
	{
		if (!File.Exists(originalPath))
		{
			return originalPath;
		}
		try
		{
			using (File.Open(originalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
			{
				return originalPath;
			}
		}
		catch
		{
			string dir = Path.GetDirectoryName(originalPath) ?? "";
			string nameNoExt = Path.GetFileNameWithoutExtension(originalPath);
			string ext = Path.GetExtension(originalPath);
			string newPath = Path.Combine(dir, $"{nameNoExt}_{DateTime.Now:HHmmss}{ext}");
			int n = 1;
			while (File.Exists(newPath) && n < 1000)
			{
				newPath = Path.Combine(dir, $"{nameNoExt}_{DateTime.Now:HHmmss}_{n}{ext}");
				n++;
			}
			return newPath;
		}
	}

	private List<TodoItem> GetExportOrderedTodos()
	{
		IOrderedEnumerable<TodoItem> first = from t in _allTodos
			where !t.IsCompleted
			orderby t.DateRef descending
			select t;
		IOrderedEnumerable<TodoItem> completed = from t in _allTodos
			where t.IsCompleted
			orderby t.CompletedAt ?? DateTime.MinValue descending
			select t;
		return first.Concat(completed).ToList();
	}

	private void ExportToExcel(object? _)
	{
		SaveFileDialog dialog = new SaveFileDialog
		{
			Filter = "Excel 文件|*.xlsx",
			FileName = $"导出待办{DateTime.Now:yyyyMMdd}.xlsx",
			Title = "导出待办到 Excel"
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			using XLWorkbook wb = new XLWorkbook();
			IXLWorksheet ws = wb.Worksheets.Add("待办事项");
			IXLStyle style = wb.Style;
			style.Font.Bold = true;
			style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
			style.Font.FontColor = XLColor.White;
			string[] headers = new string[6] { "序号", "待办内容", "创建日期", "截止日期", "完成状态", "完成时间" };
			for (int i = 0; i < headers.Length; i++)
			{
				IXLCell iXLCell = ws.Cell(1, i + 1);
				iXLCell.Value = headers[i];
				iXLCell.Style.Font.Bold = true;
				iXLCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
				iXLCell.Style.Font.FontColor = XLColor.White;
				iXLCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
			}
			List<TodoItem> todos = GetExportOrderedTodos();
			for (int row = 0; row < todos.Count; row++)
			{
				TodoItem t = todos[row];
				int r = row + 2;
				ws.Cell(r, 1).Value = row + 1;
				ws.Cell(r, 2).Value = t.Content;
				ws.Cell(r, 3).Value = t.CreatedAt.ToString("yyyy-MM-dd HH:mm");
				ws.Cell(r, 4).Value = t.DueDate?.ToString("yyyy-MM-dd") ?? "-";
				ws.Cell(r, 5).Value = (t.IsCompleted ? "✓ 已完成" : "进行中");
				ws.Cell(r, 6).Value = t.CompletedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-";
				if (t.IsCompleted)
				{
					for (int c = 1; c <= headers.Length; c++)
					{
						ws.Cell(r, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2EFDA");
					}
				}
			}
			ws.Columns().AdjustToContents(5, 40);
			ws.Column(5).Width = Math.Max(ws.Column(5).Width, 12.0);
			string savePath = EnsureFileWritable(dialog.FileName);
			wb.SaveAs(savePath);
			if (ConfirmDialog.Show(null, "导出成功", $"已成功导出 {todos.Count} 条待办到：\n{savePath}\n\n是否打开导出文件？", "打开", "取消"))
			{
				Process.Start(new ProcessStartInfo(savePath)
				{
					UseShellExecute = true
				});
			}
		}
		catch (Exception ex)
		{
			ConfirmDialog.Show(null, "导出错误", "导出 Excel 失败：" + ex.Message, "确定", "关闭");
		}
	}

	private void ExportToTxt(object? _)
	{
		SaveFileDialog dialog = new SaveFileDialog
		{
			Filter = "文本文件|*.txt",
			FileName = $"导出待办{DateTime.Now:yyyyMMdd}.txt",
			Title = "导出待办到 TXT"
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			StringBuilder sb = new StringBuilder();
			List<TodoItem> todos = GetExportOrderedTodos();
			foreach (TodoItem t in todos)
			{
				if (t.IsCompleted && t.CompletedAt.HasValue)
				{
					StringBuilder stringBuilder = sb;
					StringBuilder stringBuilder2 = stringBuilder;
					StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(5, 2, stringBuilder);
					handler.AppendLiteral("【");
					handler.AppendFormatted(t.CompletedAt.Value, "yyyy-MM-dd HH:mm");
					handler.AppendLiteral(" 完成】");
					handler.AppendFormatted(t.Content);
					stringBuilder2.AppendLine(ref handler);
				}
				else
				{
					StringBuilder stringBuilder = sb;
					StringBuilder stringBuilder3 = stringBuilder;
					StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(5, 1, stringBuilder);
					handler.AppendLiteral("【未完成】");
					handler.AppendFormatted(t.Content);
					stringBuilder3.AppendLine(ref handler);
				}
			}
			string savePath = EnsureFileWritable(dialog.FileName);
			File.WriteAllText(savePath, sb.ToString(), Encoding.UTF8);
			if (ConfirmDialog.Show(null, "导出成功", $"已成功导出 {todos.Count} 条待办到：\n{savePath}\n\n是否打开导出文件？", "打开", "取消"))
			{
				Process.Start(new ProcessStartInfo(savePath)
				{
					UseShellExecute = true
				});
			}
		}
		catch (Exception ex)
		{
			ConfirmDialog.Show(null, "导出错误", "导出 TXT 失败：" + ex.Message, "确定", "关闭");
		}
	}
}
