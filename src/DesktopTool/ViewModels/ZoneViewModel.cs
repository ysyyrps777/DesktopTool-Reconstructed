using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopTool.Models;
using DesktopTool.Services;
using DesktopTool.Views;

namespace DesktopTool.ViewModels;

public class ZoneViewModel : ViewModelBase
{
	private readonly IconZone _zone;

	private readonly MainViewModel _mainVm;

	private bool _isLocked;

	private int _activeGroupIndex;

	private int _iconSizeLevel;

	private bool _isCollapsed;

	private bool _isHoverExpanded;

	private const string LockClosedGeometry = "M4,10 L20,10 C21.1,10 22,10.9 22,12 L22,22 C22,23.1 21.1,24 20,24 L4,24 C2.9,24 2,23.1 2,22 L2,12 C2,10.9 2.9,10 4,10 Z M8,10 L8,6 C8,3.79 9.79,2 12,2 C14.21,2 16,3.79 16,6 L16,10 L14,10 L14,6 C14,4.9 13.1,4 12,4 C10.9,4 10,4.9 10,6 L10,10 Z";

	private const string LockOpenGeometry = "M4,10 L20,10 C21.1,10 22,10.9 22,12 L22,22 C22,23.1 21.1,24 20,24 L4,24 C2.9,24 2,23.1 2,22 L2,12 C2,10.9 2.9,10 4,10 Z M18,10 L18,6 C18,2.69 15.31,0 12,0 C8.69,0 6,2.69 6,6 L6,7 L8,7 L8,6 C8,3.79 9.79,2 12,2 C14.21,2 16,3.79 16,6 L16,10 Z";

	private ObservableCollection<ZoneIcon> _icons = new ObservableCollection<ZoneIcon>();

	private string _searchText = "";

	private int _iconSortMode;

	private bool _iconSortAscending = true;

	private bool _isNarrow;

	public MainViewModel MainVm => _mainVm;

	public string Id => _zone.Id;

	public string Name
	{
		get
		{
			return _zone.Name;
		}
		set
		{
			string v = value ?? "";
			if (_zone.Name != v)
			{
				_zone.Name = v;
				OnPropertyChanged("Name");
				_mainVm.SaveZones();
			}
		}
	}

	public double WindowLeft
	{
		get
		{
			return _zone.X;
		}
		set
		{
			if (Math.Abs(_zone.X - value) > 0.01)
			{
				_zone.X = value;
				_zone.PosPhysical = true;
				OnPropertyChanged("WindowLeft");
				_mainVm.SaveZones();
			}
		}
	}

	public double WindowTop
	{
		get
		{
			return _zone.Y;
		}
		set
		{
			if (Math.Abs(_zone.Y - value) > 0.01)
			{
				_zone.Y = value;
				_zone.PosPhysical = true;
				OnPropertyChanged("WindowTop");
				_mainVm.SaveZones();
			}
		}
	}

	public double ZoneWidth
	{
		get
		{
			return _zone.Width;
		}
		set
		{
			if (Math.Abs(_zone.Width - value) > 0.01)
			{
				_zone.Width = value;
				_zone.PosPhysical = true;
				OnPropertyChanged("ZoneWidth");
				_mainVm.SaveZones();
			}
		}
	}

	public double ZoneHeight
	{
		get
		{
			return _zone.Height;
		}
		set
		{
			if (Math.Abs(_zone.Height - value) > 0.01)
			{
				_zone.Height = value;
				_zone.PosPhysical = true;
				OnPropertyChanged("ZoneHeight");
				_mainVm.SaveZones();
			}
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
				_zone.IsLocked = value;
				OnPropertyChanged("LockIcon");
				OnPropertyChanged("LockMenuLabel");
				_mainVm.SaveZones();
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

	public Thickness IconSpacingThickness => _mainVm.IconSpacingThickness;

	public ObservableCollection<IconGroup> Groups { get; }

	public int ActiveGroupIndex
	{
		get
		{
			return _activeGroupIndex;
		}
		set
		{
			if (SetProperty(ref _activeGroupIndex, value, "ActiveGroupIndex"))
			{
				_zone.ActiveGroupIndex = value;
				SyncActiveIcons();
				UpdateGroupActiveState();
				_mainVm.SaveZones();
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
			if (!(_searchText == value))
			{
				_searchText = value;
				OnPropertyChanged("SearchText");
				OnPropertyChanged("Icons");
			}
		}
	}

	public ObservableCollection<ZoneIcon> Icons
	{
		get
		{
			if (string.IsNullOrWhiteSpace(_searchText))
			{
				return _icons;
			}
			string q = _searchText.Trim();
			return new ObservableCollection<ZoneIcon>(_icons.Where((ZoneIcon i) => NameMatch(i.DisplayName, q)));
		}
		private set
		{
			SetProperty(ref _icons, value, "Icons");
		}
	}

	public bool IsLargeIcon
	{
		get
		{
			return _iconSizeLevel >= 1;
		}
		set
		{
			int newLevel = (value ? Math.Max(_iconSizeLevel, 1) : 0);
			SetIconSizeLevel(newLevel);
		}
	}

	public int IconSizeLevel
	{
		get
		{
			return _iconSizeLevel;
		}
		set
		{
			SetIconSizeLevel(value);
		}
	}

	public double IconContainerWidth => GetScaledValue(_iconSizeLevel switch
	{
		0 => 64.0, 
		1 => 78.0, 
		2 => 96.0, 
		_ => double.NaN, 
	});

	public double IconContainerHeight => GetScaledValue(_iconSizeLevel switch
	{
		0 => 78.0, 
		1 => 90.0, 
		2 => 108.0, 
		_ => 32.0, 
	});

	public double IconImageSize => GetScaledValue(_iconSizeLevel switch
	{
		0 => 32.0, 
		1 => 40.0, 
		2 => 50.0, 
		_ => 20.0, 
	});

	public double IconTextHeight => GetScaledValue(_iconSizeLevel switch
	{
		0 => 30.0, 
		1 => 28.0, 
		2 => 34.0, 
		_ => 20.0, 
	});

	public double IconTextWrapWidth => GetScaledFont(64.0);

	public double IconFontSize => GetScaledFont(_iconSizeLevel switch
	{
		0 => 9.0, 
		1 => 10.0, 
		2 => 12.0, 
		_ => 10.0, 
	});

	public double ListItemWidth => GetScaledValue(280.0);

	public string IconViewModeLabel => _iconSizeLevel switch
	{
		0 => "⊟", 
		1 => "⊞", 
		2 => "⊡", 
		_ => "≡", 
	};

	public bool IsListView => _iconSizeLevel == 3;

	public int IconSortMode
	{
		get
		{
			return _iconSortMode;
		}
		set
		{
			if (_iconSortMode != value)
			{
				if (value == 1 && _iconSortMode != 1)
				{
					IconSortAscending = false;
				}
				_iconSortMode = value;
				_zone.IconSortMode = value;
				_mainVm.SaveZones();
				OnPropertyChanged("IconSortMode");
				ApplySort();
			}
		}
	}

	public bool IconSortAscending
	{
		get
		{
			return _iconSortAscending;
		}
		set
		{
			if (_iconSortAscending != value)
			{
				_iconSortAscending = value;
				_zone.IconSortAscending = value;
				_mainVm.SaveZones();
				OnPropertyChanged("IconSortAscending");
				ApplySort();
			}
		}
	}

	public ICommand SetIconSizeCommand { get; }

	public ICommand SetSortModeCommand { get; }

	public ICommand SetSortDirectionCommand { get; }

	public string LockMenuLabel
	{
		get
		{
			if (!_isLocked)
			{
				return "锁定";
			}
			return "解锁";
		}
	}

	public bool IsNarrow
	{
		get
		{
			return _isNarrow;
		}
		set
		{
			SetProperty(ref _isNarrow, value, "IsNarrow");
		}
	}

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
				_zone.IsCollapsed = value;
				_mainVm.SaveZones();
				OnPropertyChanged("TabsVisibility");
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
				OnPropertyChanged("TabsVisibility");
			}
		}
	}

	public Visibility ContentVisibility
	{
		get
		{
			if (!_isCollapsed || _isHoverExpanded)
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public Visibility TabsVisibility
	{
		get
		{
			if (!_isCollapsed || _isHoverExpanded)
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

	public ICommand ToggleLockCommand { get; }

	public ICommand DismissZoneCommand { get; }

	public ICommand DeleteIconCommand { get; }

	public ICommand ToggleIconSizeCommand { get; }

	public ICommand SwitchGroupCommand { get; }

	public ICommand AddGroupCommand { get; }

	public ICommand DeleteGroupCommand { get; }

	public ICommand RenameGroupCommand { get; }

	public ICommand ToggleCollapseCommand { get; }

	public bool IsSyncingIcons { get; private set; }

	public event EventHandler? CollapsedChanged;

	private static bool NameMatch(string? s, string q)
	{
		if (!string.IsNullOrEmpty(s))
		{
			return s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
		}
		return false;
	}

	private bool SetIconSizeLevel(int level)
	{
		if (level < 0)
		{
			level = 0;
		}
		if (level > 3)
		{
			level = 3;
		}
		if (_iconSizeLevel == level)
		{
			return false;
		}
		_iconSizeLevel = level;
		_zone.IconSizeLevel = level;
		_mainVm.SaveZones();
		OnPropertyChanged("IconContainerWidth");
		OnPropertyChanged("IconContainerHeight");
		OnPropertyChanged("IconImageSize");
		OnPropertyChanged("IconFontSize");
		OnPropertyChanged("IconTextHeight");
		OnPropertyChanged("IconTextWrapWidth");
		OnPropertyChanged("IconViewModeLabel");
		OnPropertyChanged("IsLargeIcon");
		OnPropertyChanged("IsListView");
		OnPropertyChanged("IconSizeLevel");
		RefreshAllIconBitmaps();
		return true;
	}

	private void ApplySort()
	{
		IconGroup group = Groups.ElementAtOrDefault(_activeGroupIndex);
		if (group == null)
		{
			return;
		}
		List<ZoneIcon> list = group.Icons.ToList();
		SortIconList(list);
		group.Icons.Clear();
		foreach (ZoneIcon it in list)
		{
			group.Icons.Add(it);
		}
		Icons = new ObservableCollection<ZoneIcon>(list);
		_mainVm.SaveZones();
	}

	private void SortIconList(List<ZoneIcon> list)
	{
		Func<ZoneIcon, ZoneIcon, int> cmp;
		switch (_iconSortMode)
		{
		case 1:
			cmp = (ZoneIcon a, ZoneIcon b) => GetTime(a).CompareTo(GetTime(b));
			break;
		case 2:
			cmp = (ZoneIcon a, ZoneIcon b) => string.Compare(GetExt(a), GetExt(b), StringComparison.OrdinalIgnoreCase);
			break;
		case 3:
			cmp = (ZoneIcon a, ZoneIcon b) => GetSize(a).CompareTo(GetSize(b));
			break;
		default:
			cmp = (ZoneIcon a, ZoneIcon b) => NativeMethods.NaturalCompare(a.DisplayName ?? "", b.DisplayName ?? "");
			break;
		}
		list.Sort(delegate(ZoneIcon a, ZoneIcon b)
		{
			int num = cmp(a, b);
			return (!_iconSortAscending) ? (-num) : num;
		});
	}

	private static DateTime GetTime(ZoneIcon i)
	{
		return i.LastWriteTime ?? DateTime.MinValue;
	}

	private static string GetExt(ZoneIcon i)
	{
		if (!string.IsNullOrEmpty(i.FilePath))
		{
			return Path.GetExtension(i.FilePath);
		}
		return "";
	}

	private static long GetSize(ZoneIcon i)
	{
		try
		{
			return (!string.IsNullOrEmpty(i.FilePath) && File.Exists(i.FilePath)) ? new FileInfo(i.FilePath).Length : 0;
		}
		catch
		{
			return 0L;
		}
	}

	private static int ToInt(object p)
	{
		if (!(p is int result))
		{
			if (p is string s && int.TryParse(s, out var v))
			{
				return v;
			}
			return 0;
		}
		return result;
	}

	private static bool ToBool(object p)
	{
		if (!(p is bool result))
		{
			if (p is string value && bool.TryParse(value, out var v))
			{
				return v;
			}
			return true;
		}
		return result;
	}

	private double GetScaledValue(double baseValue)
	{
		return baseValue * _mainVm.FontScaleMultiplier;
	}

	private double GetScaledFont(double baseSize)
	{
		return baseSize * _mainVm.DesktopFontRatio * _mainVm.FontScaleMultiplier;
	}

	public ZoneViewModel(IconZone zone, MainViewModel mainVm)
	{
		_zone = zone;
		_mainVm = mainVm;
		_isLocked = zone.IsLocked;
		_iconSizeLevel = zone.IconSizeLevel;
		_iconSortMode = zone.IconSortMode;
		_iconSortAscending = zone.IconSortAscending;
		_activeGroupIndex = zone.ActiveGroupIndex;
		_isCollapsed = zone.IsCollapsed;
		Groups = new ObservableCollection<IconGroup>(zone.Groups);
		if (Groups.Count == 0)
		{
			IconGroup g = new IconGroup
			{
				Name = "默认分组"
			};
			Groups.Add(g);
			zone.Groups.Add(g);
		}
		if (_activeGroupIndex < 0 || _activeGroupIndex >= Groups.Count)
		{
			_activeGroupIndex = 0;
		}
		SyncActiveIcons();
		UpdateGroupActiveState();
		Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(LoadIconImages));
		_mainVm.PropertyChanged += delegate(object? s, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == "GlobalOpacity")
			{
				OnPropertyChanged("BackgroundOpacity");
			}
			if (e.PropertyName == "PanelColorBrush" || e.PropertyName == "IconSpacingThickness")
			{
				OnPropertyChanged("PanelColorBrush");
				OnPropertyChanged("IconSpacingThickness");
			}
		};
		ToggleLockCommand = new RelayCommand((Action<object?>)delegate
		{
			ToggleLock();
		}, (Func<object?, bool>?)null);
		DismissZoneCommand = new RelayCommand((Action<object?>)delegate
		{
			DismissZone();
		}, (Func<object?, bool>?)null);
		DeleteIconCommand = new RelayCommand(DeleteIcon);
		ToggleIconSizeCommand = new RelayCommand((Action<object?>)delegate
		{
			ToggleIconSize();
		}, (Func<object?, bool>?)null);
		SetIconSizeCommand = new RelayCommand(delegate(object? p)
		{
			SetIconSizeLevel(ToInt(p));
		});
		SetSortModeCommand = new RelayCommand(delegate(object? p)
		{
			IconSortMode = ToInt(p);
		});
		SetSortDirectionCommand = new RelayCommand(delegate(object? p)
		{
			IconSortAscending = ToBool(p);
		});
		SwitchGroupCommand = new RelayCommand(SwitchGroup);
		AddGroupCommand = new RelayCommand((Action<object?>)delegate
		{
			AddGroup();
		}, (Func<object?, bool>?)null);
		DeleteGroupCommand = new RelayCommand(DeleteGroup);
		RenameGroupCommand = new RelayCommand(RenameGroup);
		ToggleCollapseCommand = new RelayCommand((Action<object?>)delegate
		{
			IsCollapsed = !IsCollapsed;
		}, (Func<object?, bool>?)null);
	}

	private void LoadIconImages()
	{
		foreach (IconGroup group in Groups)
		{
			foreach (ZoneIcon icon in group.Icons)
			{
				try
				{
					string source = icon.ResolvedPath;
					if (string.IsNullOrEmpty(source) || (!File.Exists(source) && !Directory.Exists(source)))
					{
						source = icon.FilePath;
					}
					if (!string.IsNullOrEmpty(source) && (File.Exists(source) || Directory.Exists(source)))
					{
						icon.IconImage = IconHelper.GetFileIconSafe(source, large: false, IconImageSize);
						BitmapSource bi = icon.IconImage;
						IconHelper.LogIconDiagnostic($"[LOAD] {Path.GetFileName(source)} IconImage={((bi == null) ? "NULL" : $"{bi.PixelWidth}x{bi.PixelHeight}@{bi.DpiX:0}dpi frozen={bi.IsFrozen} fmt={bi.Format}")} IconImageSize={IconImageSize}");
					}
					MonitorAndRepairWhiteIcon(icon);
					if (string.IsNullOrEmpty(icon.DisplayName) && !string.IsNullOrEmpty(icon.FilePath))
					{
						bool isDirectory = Directory.Exists(icon.FilePath) || (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath) && Directory.Exists(icon.HiddenPath));
						icon.DisplayName = (isDirectory ? Path.GetFileName(icon.FilePath) : Path.GetFileNameWithoutExtension(icon.FilePath));
					}
				}
				catch
				{
				}
			}
		}
	}

	internal void SyncActiveIcons()
	{
		IconGroup group = Groups.ElementAtOrDefault(_activeGroupIndex);
		IsSyncingIcons = true;
		try
		{
			if (group == null)
			{
				Icons = new ObservableCollection<ZoneIcon>();
			}
			else
			{
				Icons = new ObservableCollection<ZoneIcon>(group.Icons);
			}
		}
		finally
		{
			IsSyncingIcons = false;
		}
	}

	internal void UpdateGroupActiveState()
	{
		for (int i = 0; i < Groups.Count; i++)
		{
			Groups[i].IsActive = i == _activeGroupIndex;
		}
	}

	public void MoveIconToGroup(ZoneIcon icon, IconGroup targetGroup)
	{
		if (targetGroup == null)
		{
			return;
		}
		IconGroup sourceGroup = null;
		foreach (IconGroup g in Groups)
		{
			if (g.Icons.Contains(icon))
			{
				sourceGroup = g;
				break;
			}
		}
		if (sourceGroup != null && sourceGroup != targetGroup)
		{
			sourceGroup.Icons.Remove(icon);
			targetGroup.Icons.Add(icon);
			SyncActiveIcons();
			UpdateGroupActiveState();
			_zone.NotifyTotalIconCountChanged();
			_mainVm.SaveZonesNow();
		}
	}

	public void MoveGroup(int fromIndex, int toIndex)
	{
		if (fromIndex >= 0 && fromIndex < Groups.Count && toIndex >= 0 && toIndex < Groups.Count && fromIndex != toIndex)
		{
			Groups.Move(fromIndex, toIndex);
			IconGroup item = _zone.Groups[fromIndex];
			_zone.Groups.RemoveAt(fromIndex);
			_zone.Groups.Insert(toIndex, item);
			int newActiveIndex = _activeGroupIndex;
			if (_activeGroupIndex == fromIndex)
			{
				newActiveIndex = toIndex;
			}
			else if (fromIndex < _activeGroupIndex && toIndex >= _activeGroupIndex)
			{
				newActiveIndex = _activeGroupIndex - 1;
			}
			else if (fromIndex > _activeGroupIndex && toIndex <= _activeGroupIndex)
			{
				newActiveIndex = _activeGroupIndex + 1;
			}
			ActiveGroupIndex = newActiveIndex;
			_mainVm.SaveZonesNow();
		}
	}

	private void SwitchGroup(object? parameter)
	{
		if (parameter is IconGroup g)
		{
			int idx = Groups.IndexOf(g);
			if (idx >= 0)
			{
				ActiveGroupIndex = idx;
			}
		}
		else if (parameter is int i && i >= 0 && i < Groups.Count)
		{
			ActiveGroupIndex = i;
		}
	}

	private void AddGroup(string? name = null)
	{
		IconGroup g = new IconGroup
		{
			Name = (name ?? $"分组 {Groups.Count + 1}")
		};
		Groups.Add(g);
		_zone.Groups.Add(g);
		ActiveGroupIndex = Groups.Count - 1;
		_mainVm.SaveZones();
	}

	private async void DeleteGroup(object? parameter)
	{
		if (!(parameter is IconGroup g) || Groups.Count <= 1)
		{
			return;
		}
		List<ZoneIcon> hiddenIcons = g.Icons.Where((ZoneIcon i) => i.IsHidden).ToList();
		if (hiddenIcons.Count > 0)
		{
			if (MessageBox.Show($"确定要删除分组「{g.Name}」吗？\n该分组包含 {hiddenIcons.Count} 个隐藏的图标，将被恢复到桌面。", "删除分组", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				return;
			}
			List<string> failed = new List<string>();
			foreach (ZoneIcon icon in hiddenIcons)
			{
				if (DesktopIconService.RestoreFile(icon.FilePath, icon.HiddenMethod, icon.HiddenPath))
				{
					icon.HiddenMethod = null;
					icon.HiddenPath = null;
					DesktopIconService.SyncIconFilePath(icon, "DeleteGroup");
				}
				else
				{
					failed.Add(icon.DisplayName ?? Path.GetFileName(icon.FilePath));
				}
			}
			if (failed.Count > 0)
			{
				MessageBox.Show("以下图标恢复失败（可能被其他程序占用）：\n• " + string.Join("\n• ", failed.Take(5)), "恢复提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
		}
		Groups.Remove(g);
		_zone.Groups.Remove(g);
		_zone.NotifyTotalIconCountChanged();
		if (_activeGroupIndex >= Groups.Count)
		{
			_activeGroupIndex = Groups.Count - 1;
		}
		if (_activeGroupIndex < 0)
		{
			_activeGroupIndex = 0;
		}
		SyncActiveIcons();
		UpdateGroupActiveState();
		_mainVm.SaveZonesNow();
	}

	private void RenameGroup(object? parameter)
	{
		if (parameter is IconGroup)
		{
			_mainVm.SaveZones();
		}
	}

	public void AddIcon(string filePath, string displayName)
	{
		IconGroup group = Groups.ElementAtOrDefault(_activeGroupIndex);
		if (group != null && !group.Icons.Any((ZoneIcon i) => i.FilePath == filePath))
		{
			var (method, hiddenPath) = DesktopIconService.HideFile(filePath);
			if (method != null)
			{
				BitmapSource img = IconHelper.GetFileIconSafe(hiddenPath ?? filePath, large: false, IconImageSize);
				ZoneIcon icon = new ZoneIcon
				{
					FilePath = filePath,
					DisplayName = displayName,
					HiddenMethod = method,
					HiddenPath = hiddenPath,
					IconImage = img
				};
				group.Icons.Add(icon);
				Icons.Add(icon);
				_zone.NotifyTotalIconCountChanged();
				_mainVm.SaveZonesNow();
			}
		}
	}

	public void AddIconDirect(string filePath, string displayName, string? hiddenPath, BitmapSource? iconImage)
	{
		IconGroup group = Groups.ElementAtOrDefault(_activeGroupIndex);
		if (group != null && !group.Icons.Any((ZoneIcon i) => i.FilePath == filePath))
		{
			if (string.IsNullOrEmpty(hiddenPath) || !File.Exists(hiddenPath))
			{
				AddIcon(filePath, displayName);
				return;
			}
			ZoneIcon icon = new ZoneIcon
			{
				FilePath = filePath,
				DisplayName = displayName,
				HiddenMethod = "Physical",
				HiddenPath = hiddenPath,
				IconImage = (iconImage ?? IconHelper.GetFileIconSafe(hiddenPath, large: false, IconImageSize))
			};
			group.Icons.Add(icon);
			Icons.Add(icon);
			_zone.NotifyTotalIconCountChanged();
			_mainVm.SaveZonesNow();
		}
	}

	public void CommitIcon(ZoneIcon icon)
	{
		IconGroup group = Groups.ElementAtOrDefault(_activeGroupIndex);
		if (group != null && group.Icons.Any((ZoneIcon i) => i.FilePath == icon.FilePath))
		{
			IconHelper.LogIconDiagnostic($"[COMMIT-SKIP] duplicate FilePath={Path.GetFileName(icon.FilePath)} existing={group.Icons.Count((ZoneIcon i) => i.FilePath == icon.FilePath)}");
			return;
		}
		if (group == null)
		{
			IconHelper.LogIconDiagnostic($"[COMMIT-SKIP] group==null activeIndex={_activeGroupIndex}");
			return;
		}
		if (icon.IconImage == null && !string.IsNullOrEmpty(icon.HiddenPath) && (File.Exists(icon.HiddenPath) || Directory.Exists(icon.HiddenPath)))
		{
			icon.IconImage = IconHelper.GetFileIconSafe(icon.HiddenPath, large: false, IconImageSize);
		}
		string px = ((icon.IconImage == null) ? "NULL" : $"{icon.IconImage.PixelWidth}x{icon.IconImage.PixelHeight}");
		IconHelper.LogIconDiagnostic($"[COMMIT-ADD] {Path.GetFileName(icon.FilePath)} IconImage={px} IsDirectory={icon.IsDirectory} HiddenPath={icon.HiddenPath ?? "null"} ResolvedPath={icon.ResolvedPath}");
		group.Icons.Add(icon);
		Icons.Add(icon);
		_zone.NotifyTotalIconCountChanged();
		_mainVm.SaveZonesNow();
	}

	private void DeleteIcon(object? parameter)
	{
		if (!(parameter is ZoneIcon icon))
		{
			return;
		}
		IconGroup group = Groups.ElementAtOrDefault(_activeGroupIndex);
		if (group == null)
		{
			return;
		}
		if (icon.IsHidden)
		{
			bool num = !File.Exists(icon.FilePath) && !Directory.Exists(icon.FilePath);
			bool isShortcut = icon.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
			bool physicalHiddenMissing = icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath) && !File.Exists(icon.HiddenPath) && !Directory.Exists(icon.HiddenPath);
			bool steamUninstallCase = num && isShortcut && icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath) && (File.Exists(icon.HiddenPath) || Directory.Exists(icon.HiddenPath));
			if ((!num || !(isShortcut || physicalHiddenMissing)) && !steamUninstallCase && !DesktopIconService.RestoreFile(icon.FilePath, icon.HiddenMethod, icon.HiddenPath))
			{
				string detailedReason = DesktopIconService.LastRestoreFailReason ?? "未知原因";
				string hint = ((!File.Exists(icon.FilePath) && !Directory.Exists(icon.FilePath) && (!(icon.HiddenMethod == "Physical") || string.IsNullOrEmpty(icon.HiddenPath) || (!File.Exists(icon.HiddenPath) && !Directory.Exists(icon.HiddenPath)))) ? "文件可能已被删除或移动（如应用卸载后快捷方式被清理），\n请确认文件是否仍然需要。" : ("可能正被其他程序占用，请关闭占用程序后重试。\n\n详细信息：\n" + detailedReason));
				MessageBox.Show("无法移除「" + icon.DisplayName + "」：文件还原失败。\n" + hint, "移除失败", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			icon.HiddenMethod = null;
			icon.HiddenPath = null;
		}
		group.Icons.Remove(icon);
		Icons.Remove(icon);
		_zone.NotifyTotalIconCountChanged();
		_mainVm.SaveZonesNow();
	}

	public void RemoveIconFromZone(ZoneIcon icon)
	{
		Groups.FirstOrDefault((IconGroup g) => g.Icons.Contains(icon))?.Icons.Remove(icon);
		if (Icons.Contains(icon))
		{
			Icons.Remove(icon);
		}
		_zone.NotifyTotalIconCountChanged();
		try
		{
			_mainVm.SaveZonesNow();
		}
		catch
		{
		}
	}

	private async void DismissZone()
	{
		try
		{
			int hiddenCount = Groups.Sum((IconGroup g) => g.Icons.Count((ZoneIcon i) => i.IsHidden));
			if (MessageBox.Show((hiddenCount > 0) ? $"确定要解散收纳区吗？\n{hiddenCount} 个隐藏的图标将被恢复到桌面。" : "确定要解散收纳区吗？", "解散收纳区", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
			{
				return;
			}
			int restoredCount = 0;
			int failedCount = 0;
			List<string> restoredPaths = new List<string>();
			foreach (IconGroup group in Groups)
			{
				foreach (ZoneIcon icon in group.Icons.Where((ZoneIcon i) => i.IsHidden).ToList())
				{
					try
					{
						bool shouldSkip = false;
						string hiddenMethod = icon.HiddenMethod;
						string hiddenPath = icon.HiddenPath;
						string filePath = icon.FilePath;
						if (hiddenMethod == "Attributes")
						{
							if (!File.Exists(filePath) && !Directory.Exists(filePath) && filePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
							{
								shouldSkip = true;
							}
						}
						else if (hiddenMethod == "Physical" && !string.IsNullOrEmpty(hiddenPath) && !File.Exists(hiddenPath) && !Directory.Exists(hiddenPath))
						{
							shouldSkip = true;
						}
						if (shouldSkip)
						{
							continue;
						}
						if (DesktopIconService.RestoreFile(filePath, hiddenMethod, hiddenPath))
						{
							icon.HiddenMethod = null;
							icon.HiddenPath = null;
							restoredCount++;
							if (!string.IsNullOrEmpty(filePath))
							{
								restoredPaths.Add(filePath);
							}
							DesktopIconService.SyncIconFilePath(icon, "DismissZone");
						}
						else
						{
							failedCount++;
						}
					}
					catch (Exception)
					{
						failedCount++;
					}
				}
			}
			_mainVm.DeleteZone(_zone);
			Application.Current.Windows.OfType<ZoneWindow>().FirstOrDefault((ZoneWindow w) => w.ZoneId == _zone.Id)?.Close();
			if (restoredPaths.Count <= 0)
			{
				return;
			}
			double size = IconImageSize;
			Task.Run(delegate
			{
				try
				{
					IconHelper.RepairWhiteIcons(restoredPaths, size);
				}
				catch
				{
				}
			});
		}
		catch (Exception ex2)
		{
			MessageBox.Show("解散收纳区时出错：" + ex2.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	public bool RestoreHiddenIcons()
	{
		List<string> failed = new List<string>();
		foreach (IconGroup group in Groups)
		{
			foreach (ZoneIcon icon in group.Icons.Where((ZoneIcon i) => i.IsHidden).ToList())
			{
				try
				{
					bool shouldSkip = false;
					if (icon.HiddenMethod == "Attributes")
					{
						if (!File.Exists(icon.FilePath) && !Directory.Exists(icon.FilePath) && icon.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
						{
							shouldSkip = true;
						}
					}
					else if (icon.HiddenMethod == "Physical" && !string.IsNullOrEmpty(icon.HiddenPath) && !File.Exists(icon.HiddenPath) && !Directory.Exists(icon.HiddenPath))
					{
						shouldSkip = true;
					}
					if (shouldSkip)
					{
						icon.HiddenMethod = null;
						icon.HiddenPath = null;
					}
					else if (DesktopIconService.RestoreFile(icon.FilePath, icon.HiddenMethod, icon.HiddenPath))
					{
						icon.HiddenMethod = null;
						icon.HiddenPath = null;
						DesktopIconService.SyncIconFilePath(icon, "RestoreHiddenIcons");
					}
					else
					{
						failed.Add(icon.DisplayName ?? Path.GetFileName(icon.FilePath));
					}
				}
				catch (Exception)
				{
					failed.Add(icon.DisplayName ?? Path.GetFileName(icon.FilePath));
				}
			}
		}
		if (failed.Count > 0)
		{
			MessageBox.Show("以下图标恢复失败：\n• " + string.Join("\n• ", failed.Take(5)), "恢复提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return false;
		}
		return true;
	}

	public void SaveState()
	{
		_zone.ActiveGroupIndex = _activeGroupIndex;
		_mainVm.SaveZones();
	}

	public void RefreshIconSizes()
	{
		OnPropertyChanged("IconContainerWidth");
		OnPropertyChanged("IconContainerHeight");
		OnPropertyChanged("IconImageSize");
		OnPropertyChanged("IconFontSize");
		OnPropertyChanged("IconTextHeight");
		OnPropertyChanged("IconTextWrapWidth");
		OnPropertyChanged("IconViewModeLabel");
		OnPropertyChanged("IsListView");
		OnPropertyChanged("ListItemWidth");
		RefreshAllIconBitmaps();
	}

	private void RefreshAllIconBitmaps()
	{
		try
		{
			foreach (ZoneIcon icon in Icons)
			{
				if (icon != null && icon.ResolvedPath != null)
				{
					BitmapSource newIcon = IconHelper.GetFileIconSafe(icon.ResolvedPath, large: false, IconImageSize);
					if (newIcon != null)
					{
						icon.IconImage = newIcon;
					}
					MonitorAndRepairWhiteIcon(icon);
				}
			}
		}
		catch
		{
		}
	}

	private void MonitorAndRepairWhiteIcon(ZoneIcon? icon)
	{
		if (icon == null || !IconHelper.IsWhiteIcon(icon.IconImage))
		{
			return;
		}
		string p = icon.ResolvedPath;
		if (string.IsNullOrEmpty(p) || (!File.Exists(p) && !Directory.Exists(p)))
		{
			return;
		}
		Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			try
			{
				BitmapSource bitmapSource = IconHelper.RepairWhiteIcon(p, IconImageSize);
				if (bitmapSource != null && !IconHelper.IsWhiteIcon(bitmapSource))
				{
					icon.IconImage = bitmapSource;
				}
			}
			catch
			{
			}
		});
	}

	public int RepairWhiteIconsInZone()
	{
		List<string> whitePaths = new List<string>();
		foreach (IconGroup group in Groups)
		{
			foreach (ZoneIcon icon in group.Icons)
			{
				if (IconHelper.IsWhiteIcon(icon.IconImage))
				{
					string p = icon.ResolvedPath;
					if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
					{
						whitePaths.Add(p);
					}
				}
			}
		}
		if (whitePaths.Count == 0)
		{
			return 0;
		}
		return IconHelper.RepairWhiteIcons(whitePaths, IconImageSize);
	}

	public void RefreshAllContent()
	{
		SyncActiveIcons();
		UpdateGroupActiveState();
		_zone.NotifyTotalIconCountChanged();
	}

	private void ToggleIconSize()
	{
		int nextLevel = (_iconSizeLevel + 1) % 4;
		SetIconSizeLevel(nextLevel);
	}

	private void ToggleLock()
	{
		IsLocked = !IsLocked;
	}
}
