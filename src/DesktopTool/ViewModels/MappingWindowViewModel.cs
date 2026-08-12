using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopTool.Models;
using DesktopTool.Services;

namespace DesktopTool.ViewModels;

public class MappingWindowViewModel : INotifyPropertyChanged
{
	private sealed class MappingRefreshPlanItem
	{
		public string FilePath = "";

		public string Name = "";

		public bool IsDirectory;

		public string Extension = "";

		public DateTime LastWriteTime;

		public long FileSize;

		public BitmapSource? Icon;

		public bool NeedIcon;

		public bool NeedRepair;
	}

	private readonly MappingZone _zone;

	private readonly MainViewModel _mainVm;

	private bool _isCollapsed;

	private bool _isHoverExpanded;

	private bool _isLocked;

	private int _iconSizeLevel;

	private bool _iconSizeDirty;

	private int _iconSortMode;

	private MappingGroup? _activeGroup;

	private readonly object _refreshLock = new object();

	private readonly HashSet<string> _refreshingGroupIds = new HashSet<string>();

	private const string LockClosedGeometry = "M4,10 L20,10 C21.1,10 22,10.9 22,12 L22,22 C22,23.1 21.1,24 20,24 L4,24 C2.9,24 2,23.1 2,22 L2,12 C2,10.9 2.9,10 4,10 Z M8,10 L8,6 C8,3.79 9.79,2 12,2 C14.21,2 16,3.79 16,6 L16,10 L14,10 L14,6 C14,4.9 13.1,4 12,4 C10.9,4 10,4.9 10,6 L10,10 Z";

	private const string LockOpenGeometry = "M4,10 L20,10 C21.1,10 22,10.9 22,12 L22,22 C22,23.1 21.1,24 20,24 L4,24 C2.9,24 2,23.1 2,22 L2,12 C2,10.9 2.9,10 4,10 Z M18,10 L18,6 C18,2.69 15.31,0 12,0 C8.69,0 6,2.69 6,6 L6,7 L8,7 L8,6 C8,3.79 9.79,2 12,2 C14.21,2 16,3.79 16,6 L16,10 Z";

	private bool _iconSortAscending = true;

	private bool _iconSortUseFolder;

	private ShellFolderSortHelper.FolderSortInfo _resolvedFolderSort;

	private bool _isNarrow;

	private string _searchText = "";

	public bool LastRefreshHadChanges { get; private set; } = true;

	public string Name
	{
		get
		{
			return _zone.Name;
		}
		set
		{
			_zone.Name = value;
			OnPropertyChanged("Name");
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
			_zone.X = value;
			OnPropertyChanged("WindowLeft");
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
			_zone.Y = value;
			OnPropertyChanged("WindowTop");
		}
	}

	public double WindowWidth
	{
		get
		{
			return _zone.Width;
		}
		set
		{
			_zone.Width = value;
			OnPropertyChanged("WindowWidth");
		}
	}

	public double WindowHeight
	{
		get
		{
			return _zone.Height;
		}
		set
		{
			_zone.Height = value;
			OnPropertyChanged("WindowHeight");
		}
	}

	public double BackgroundOpacity => _mainVm.GlobalOpacity;

	public Brush PanelColorBrush => _mainVm.PanelColorBrush;

	public Thickness IconSpacingThickness => _mainVm.IconSpacingThickness;

	public MainViewModel MainVm => _mainVm;

	public bool IsCollapsed
	{
		get
		{
			return _isCollapsed;
		}
		set
		{
			if (SetProp(ref _isCollapsed, value, "IsCollapsed"))
			{
				_isHoverExpanded = false;
				OnPropertyChanged("IsHoverExpanded");
				_zone.IsCollapsed = value;
				this.CollapsedChanged?.Invoke(this, EventArgs.Empty);
				OnPropertyChanged("CollapseLabel");
				OnPropertyChanged("ContentVisibility");
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
			if (SetProp(ref _isLocked, value, "IsLocked"))
			{
				_zone.IsLocked = value;
				OnPropertyChanged("LockIcon");
				OnPropertyChanged("LockMenuLabel");
			}
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
				OnPropertyChanged("IconSortMode");
				SaveState();
				ApplySortToCurrent();
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
				OnPropertyChanged("IconSortAscending");
				OnPropertyChanged("IconSortAscendingHighlight");
				OnPropertyChanged("IconSortDescendingHighlight");
				SaveState();
				ApplySortToCurrent();
			}
		}
	}

	public bool IconSortUseFolder
	{
		get
		{
			return _iconSortUseFolder;
		}
		set
		{
			if (_iconSortUseFolder != value)
			{
				_iconSortUseFolder = value;
				_zone.IconSortUseFolder = value;
				OnPropertyChanged("IconSortUseFolder");
				OnPropertyChanged("IconSortAscendingHighlight");
				OnPropertyChanged("IconSortDescendingHighlight");
				SaveState();
				ApplySortToCurrent();
			}
		}
	}

	public bool IconSortAscendingHighlight
	{
		get
		{
			if (!_iconSortUseFolder)
			{
				return _iconSortAscending;
			}
			return false;
		}
	}

	public bool IconSortDescendingHighlight
	{
		get
		{
			if (!_iconSortUseFolder)
			{
				return !_iconSortAscending;
			}
			return false;
		}
	}

	public ICommand SetIconSizeCommand { get; }

	public ICommand SetSortModeCommand { get; }

	public ICommand SetSortDirectionCommand { get; }

	public ICommand SetSortUseFolderCommand { get; }

	public ICommand RefreshCommand { get; }

	public bool IsHoverExpanded
	{
		get
		{
			return _isHoverExpanded;
		}
		set
		{
			if (SetProp(ref _isHoverExpanded, value, "IsHoverExpanded"))
			{
				OnPropertyChanged("ContentVisibility");
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

	public double IconFontSize => GetScaledFont(_iconSizeLevel switch
	{
		0 => 9.0, 
		1 => 10.0, 
		2 => 12.0, 
		_ => 10.0, 
	});

	public double IconTextHeight => GetScaledValue(_iconSizeLevel switch
	{
		0 => 30.0, 
		1 => 28.0, 
		2 => 34.0, 
		_ => 20.0, 
	});

	public double IconTextWrapWidth => GetScaledFont(64.0);

	public string IconViewModeLabel => _iconSizeLevel switch
	{
		0 => "⊟", 
		1 => "⊞", 
		2 => "⊡", 
		_ => "≡", 
	};

	public bool IsListView => _iconSizeLevel == 3;

	public bool IsNarrow
	{
		get
		{
			return _isNarrow;
		}
		set
		{
			SetProp(ref _isNarrow, value, "IsNarrow");
		}
	}

	public double TitleFontSize => GetScaledFont(14.0);

	public ObservableCollection<MappingGroup> Groups => _zone.Groups;

	public MappingGroup? ActiveGroup
	{
		get
		{
			return _activeGroup;
		}
		set
		{
			SetProp(ref _activeGroup, value, "ActiveGroup");
			OnPropertyChanged("VisibleFiles");
			OnPropertyChanged("HasMappingPath");
			OnPropertyChanged("ShowEmptyPrompt");
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
				OnPropertyChanged("VisibleFiles");
			}
		}
	}

	public ObservableCollection<MappingFileItem> VisibleFiles
	{
		get
		{
			ObservableCollection<MappingFileItem> baseList = _activeGroup?.Files ?? new ObservableCollection<MappingFileItem>();
			if (string.IsNullOrWhiteSpace(_searchText))
			{
				return baseList;
			}
			string q = _searchText.Trim();
			return new ObservableCollection<MappingFileItem>(baseList.Where((MappingFileItem f) => NameMatch(f.DisplayName, q) || NameMatch(f.FileName, q)));
		}
	}

	public ICommand ToggleCollapseCommand { get; }

	public ICommand ToggleLockCommand { get; }

	public ICommand ToggleIconSizeCommand { get; }

	public ICommand SwitchGroupCommand { get; }

	public ICommand RenameGroupCommand { get; }

	public ICommand DeleteGroupCommand { get; }

	public ICommand SetGroupPathCommand { get; }

	public ICommand SetCurrentGroupPathCommand { get; }

	public bool HasMappingPath
	{
		get
		{
			if (!string.IsNullOrEmpty(ActiveGroup?.FolderPath))
			{
				return Directory.Exists(ActiveGroup.FolderPath);
			}
			return false;
		}
	}

	public Visibility ShowEmptyPrompt
	{
		get
		{
			if (!HasMappingPath)
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public event EventHandler? CollapsedChanged;

	public event PropertyChangedEventHandler? PropertyChanged;

	private void ApplySortToCurrent()
	{
		MappingGroup g = ActiveGroup ?? Groups.FirstOrDefault();
		if (g == null || g.Files == null)
		{
			return;
		}
		List<MappingFileItem> sorted = g.Files.ToList();
		SortFileList(sorted);
		g.Files.Clear();
		foreach (MappingFileItem it in sorted)
		{
			g.Files.Add(it);
		}
		OnPropertyChanged("VisibleFiles");
	}

	private void SortFileList(List<MappingFileItem> list)
	{
		if (_iconSortUseFolder)
		{
			string folder = ActiveGroup?.FolderPath ?? _zone.FolderPath;
			ShellFolderSortHelper.FolderSortInfo info = ShellFolderSortHelper.GetFolderSortInfo(folder);
			_resolvedFolderSort = info;
			list.Sort((MappingFileItem a, MappingFileItem b) => CompareFiles(a, b, info.Mode, info.Ascending));
		}
		else
		{
			list.Sort((MappingFileItem a, MappingFileItem b) => CompareFiles(a, b, _iconSortMode, _iconSortAscending));
		}
	}

	private static int CompareFiles(MappingFileItem a, MappingFileItem b, int mode, bool ascending)
	{
		int dir = ((!a.IsDirectory) ? 1 : 0) - ((!b.IsDirectory) ? 1 : 0);
		if (dir != 0)
		{
			return dir;
		}
		int r = mode switch
		{
			1 => a.LastWriteTime.CompareTo(b.LastWriteTime), 
			2 => string.Compare(a.Extension ?? "", b.Extension ?? "", StringComparison.OrdinalIgnoreCase), 
			3 => a.FileSize.CompareTo(b.FileSize), 
			_ => NativeMethods.NaturalCompare(a.FileName ?? "", b.FileName ?? ""), 
		};
		if (!ascending)
		{
			return -r;
		}
		return r;
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

	private void SetIconSizeLevel(int level)
	{
		if (level < 0)
		{
			level = 0;
		}
		if (level > 3)
		{
			level = 3;
		}
		if (_iconSizeLevel != level)
		{
			_iconSizeLevel = level;
			_zone.IconSizeLevel = level;
			_iconSizeDirty = true;
			OnPropertyChanged("IconContainerWidth");
			OnPropertyChanged("IconContainerHeight");
			OnPropertyChanged("IconImageSize");
			OnPropertyChanged("IconFontSize");
			OnPropertyChanged("IconTextHeight");
			OnPropertyChanged("IconTextWrapWidth");
			OnPropertyChanged("IconViewModeLabel");
			OnPropertyChanged("IsListView");
			OnPropertyChanged("IconSizeLevel");
			SaveState();
			RefreshAllGroups();
		}
	}

	private double GetScaledValue(double baseValue)
	{
		return baseValue * _mainVm.FontScaleMultiplier;
	}

	private double GetScaledFont(double baseSize)
	{
		return baseSize * _mainVm.DesktopFontRatio * _mainVm.FontScaleMultiplier;
	}

	private static bool NameMatch(string? s, string q)
	{
		if (!string.IsNullOrEmpty(s))
		{
			return s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
		}
		return false;
	}

	public MappingWindowViewModel(MappingZone zone, MainViewModel mainVm)
	{
		_zone = zone;
		_mainVm = mainVm;
		_isCollapsed = zone.IsCollapsed;
		_isLocked = zone.IsLocked;
		_iconSizeLevel = Math.Clamp(zone.IconSizeLevel, 0, 3);
		_iconSortMode = zone.IconSortMode;
		_iconSortAscending = zone.IconSortAscending;
		_iconSortUseFolder = zone.IconSortUseFolder;
		ToggleCollapseCommand = new RelayCommand((Action<object?>)delegate
		{
			IsCollapsed = !IsCollapsed;
		}, (Func<object?, bool>?)null);
		ToggleLockCommand = new RelayCommand((Action<object?>)delegate
		{
			IsLocked = !IsLocked;
		}, (Func<object?, bool>?)null);
		ToggleIconSizeCommand = new RelayCommand((Action<object?>)delegate
		{
			IconSizeLevel = (_iconSizeLevel + 1) % 4;
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
			IconSortUseFolder = false;
			IconSortAscending = ToBool(p);
		});
		SetSortUseFolderCommand = new RelayCommand((Action<object?>)delegate
		{
			IconSortUseFolder = true;
		}, (Func<object?, bool>?)null);
		RefreshCommand = new RelayCommand((Action<object?>)delegate
		{
			RefreshFiles(force: true);
		}, (Func<object?, bool>?)null);
		SwitchGroupCommand = new RelayCommand(delegate(object? param)
		{
			if (param is MappingGroup mappingGroup)
			{
				ActiveGroup = mappingGroup;
				ApplySortToCurrent();
				RefreshGroupFilesAsync(mappingGroup, force: false);
				OnPropertyChanged("VisibleFiles");
				OnPropertyChanged("HasMappingPath");
				OnPropertyChanged("ShowEmptyPrompt");
			}
		});
		RenameGroupCommand = new RelayCommand(delegate(object? param)
		{
			if (param is MappingGroup mappingGroup)
			{
				RenameGroup(mappingGroup);
			}
		});
		DeleteGroupCommand = new RelayCommand(delegate(object? param)
		{
			if (param is MappingGroup mappingGroup)
			{
				DeleteGroup(mappingGroup);
			}
		});
		SetGroupPathCommand = new RelayCommand(delegate(object? param)
		{
			if (param is MappingGroup groupPath)
			{
				SetGroupPath(groupPath);
			}
		});
		SetCurrentGroupPathCommand = new RelayCommand((Action<object?>)delegate
		{
			if (ActiveGroup != null)
			{
				SetGroupPath(ActiveGroup);
			}
		}, (Func<object?, bool>?)null);
		if (_zone.Groups.Count == 0)
		{
			_zone.Groups.Add(new MappingGroup
			{
				Name = "默认分组"
			});
		}
		int idx = Math.Max(0, Math.Min(_zone.ActiveGroupIndex, _zone.Groups.Count - 1));
		ActiveGroup = _zone.Groups[idx];
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
	}

	public void AddGroup()
	{
		MappingGroup g = new MappingGroup
		{
			Name = $"分组{Groups.Count + 1}"
		};
		Groups.Add(g);
		ActiveGroup = g;
		SaveState();
	}

	public void RenameGroup(MappingGroup group)
	{
	}

	public void DeleteGroup(MappingGroup group)
	{
		if (Groups.Count > 1)
		{
			int idx = Groups.IndexOf(group);
			Groups.Remove(group);
			if (ActiveGroup == group)
			{
				ActiveGroup = Groups[Math.Min(idx, Groups.Count - 1)];
			}
			SaveState();
		}
	}

	public void MoveGroup(int fromIndex, int toIndex)
	{
		if (fromIndex >= 0 && fromIndex < Groups.Count && toIndex >= 0 && toIndex < Groups.Count && fromIndex != toIndex)
		{
			Groups.Move(fromIndex, toIndex);
			SaveState();
		}
	}

	public void SetGroupPath(MappingGroup group)
	{
		using FolderBrowserDialog dialog = new FolderBrowserDialog
		{
			Description = "为分组「" + group.Name + "」选择映射文件夹",
			ShowNewFolderButton = false
		};
		if (!string.IsNullOrEmpty(group.FolderPath) && Directory.Exists(group.FolderPath))
		{
			dialog.SelectedPath = group.FolderPath;
		}
		if (dialog.ShowDialog() == DialogResult.OK)
		{
			group.FolderPath = dialog.SelectedPath;
			string folderName = Path.GetFileName(dialog.SelectedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
			if (!string.IsNullOrEmpty(folderName))
			{
				group.Name = folderName;
			}
			SaveState();
			RefreshFiles();
		}
	}

	public void RefreshFiles(bool force = false)
	{
		MappingGroup activeGroup = ActiveGroup ?? Groups.FirstOrDefault();
		if (activeGroup != null)
		{
			RefreshGroupFilesAsync(activeGroup, force);
		}
	}

	private async void RefreshGroupFilesAsync(MappingGroup? group, bool force)
	{
		if (group == null)
		{
			return;
		}
		bool acquired;
		lock (_refreshLock)
		{
			acquired = _refreshingGroupIds.Add(group.Id);
		}
		if (!acquired)
		{
			return;
		}
		try
		{
			string path = group.FolderPath;
			if (string.IsNullOrEmpty(path))
			{
				path = _zone.FolderPath;
			}
			if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
			{
				group.Files.Clear();
				OnPropertyChanged("VisibleFiles");
				OnPropertyChanged("HasMappingPath");
				OnPropertyChanged("ShowEmptyPrompt");
				return;
			}
			if (!force && !_iconSizeDirty)
			{
				DateTime currentWriteTime = Directory.GetLastWriteTime(path);
				if (currentWriteTime == group._lastKnownDirWriteTime)
				{
					LastRefreshHadChanges = false;
					return;
				}
				group._lastKnownDirWriteTime = currentWriteTime;
			}
			else if (!force)
			{
				group._lastKnownDirWriteTime = Directory.GetLastWriteTime(path);
			}
			LastRefreshHadChanges = true;
			Dictionary<string, MappingFileItem> existingByPath = new Dictionary<string, MappingFileItem>(StringComparer.OrdinalIgnoreCase);
			foreach (MappingFileItem it in group.Files)
			{
				existingByPath[it.FilePath] = it;
			}
			double size = IconImageSize;
			bool effectiveForce = force || _iconSizeDirty;
			ApplyMappingRefreshPlan(group, await Task.Run(() => BuildMappingRefreshPlan(path, existingByPath, size, effectiveForce)), size);
			OnPropertyChanged("VisibleFiles");
			OnPropertyChanged("HasMappingPath");
			OnPropertyChanged("ShowEmptyPrompt");
		}
		catch (Exception)
		{
		}
		finally
		{
			lock (_refreshLock)
			{
				_refreshingGroupIds.Remove(group.Id);
			}
		}
	}

	private static List<MappingRefreshPlanItem> BuildMappingRefreshPlan(string path, Dictionary<string, MappingFileItem> existingByPath, double size, bool force)
	{
		return (from f in new DirectoryInfo(path).GetFileSystemInfos()
			where (f.Attributes & FileAttributes.Hidden) == 0
			select f).ToList().AsParallel().AsOrdered()
			.Select(delegate(FileSystemInfo fi)
			{
				bool flag = (fi.Attributes & FileAttributes.Directory) != 0;
				existingByPath.TryGetValue(fi.FullName, out var value);
				bool flag2;
				if (value == null)
				{
					flag2 = true;
				}
				else
				{
					bool flag3 = value.FileName != fi.Name;
					bool flag4 = value.LastWriteTime != fi.LastWriteTime;
					bool flag5 = value.IconImage is BitmapSource source && IconHelper.IsWhiteIcon(source);
					flag2 = force || flag3 || value.IconImage == null || flag4 || flag5;
				}
				BitmapSource bitmapSource = null;
				bool needRepair = false;
				if (flag2)
				{
					if (force)
					{
						IconHelper.InvalidateIconCache(fi.FullName);
					}
					bitmapSource = (flag ? IconHelper.GetFileIconSafe(fi.FullName, large: true, size) : IconHelper.GetFileIconSafe(fi.FullName, large: false, size));
					if (bitmapSource != null && IconHelper.IsWhiteIcon(bitmapSource))
					{
						needRepair = true;
					}
				}
				return new MappingRefreshPlanItem
				{
					FilePath = fi.FullName,
					Name = fi.Name,
					IsDirectory = flag,
					Extension = fi.Extension,
					LastWriteTime = fi.LastWriteTime,
					FileSize = ((fi is FileInfo fileInfo) ? fileInfo.Length : 0),
					Icon = bitmapSource,
					NeedIcon = flag2,
					NeedRepair = needRepair
				};
			})
			.ToList();
	}

	private void ApplyMappingRefreshPlan(MappingGroup group, List<MappingRefreshPlanItem> plan, double size)
	{
		Dictionary<string, MappingFileItem> existingByPath = new Dictionary<string, MappingFileItem>(StringComparer.OrdinalIgnoreCase);
		foreach (MappingFileItem it in group.Files)
		{
			existingByPath[it.FilePath] = it;
		}
		Dictionary<string, MappingRefreshPlanItem> planByPath = new Dictionary<string, MappingRefreshPlanItem>(StringComparer.OrdinalIgnoreCase);
		foreach (MappingRefreshPlanItem p in plan)
		{
			planByPath[p.FilePath] = p;
		}
		List<MappingFileItem> desired = new List<MappingFileItem>(plan.Count);
		foreach (MappingFileItem existing in group.Files)
		{
			if (!planByPath.TryGetValue(existing.FilePath, out var p2))
			{
				continue;
			}
			existing.FileName = p2.Name;
			existing.DisplayName = p2.Name;
			existing.IsDirectory = p2.IsDirectory;
			existing.Extension = p2.Extension;
			existing.LastWriteTime = p2.LastWriteTime;
			existing.FileSize = p2.FileSize;
			if (p2.NeedIcon)
			{
				BitmapSource finalIcon = p2.Icon;
				if (p2.NeedRepair)
				{
					BitmapSource repaired = IconHelper.RepairWhiteIcon(p2.FilePath, size);
					if (repaired != null)
					{
						finalIcon = repaired;
					}
				}
				if (finalIcon != null)
				{
					existing.IconImage = finalIcon;
				}
			}
			desired.Add(existing);
		}
		foreach (MappingRefreshPlanItem p3 in plan)
		{
			if (existingByPath.ContainsKey(p3.FilePath))
			{
				continue;
			}
			BitmapSource finalIcon2 = p3.Icon;
			if (p3.NeedRepair)
			{
				BitmapSource repaired2 = IconHelper.RepairWhiteIcon(p3.FilePath, size);
				if (repaired2 != null)
				{
					finalIcon2 = repaired2;
				}
			}
			desired.Add(new MappingFileItem
			{
				FilePath = p3.FilePath,
				FileName = p3.Name,
				DisplayName = p3.Name,
				IsDirectory = p3.IsDirectory,
				Extension = p3.Extension,
				LastWriteTime = p3.LastWriteTime,
				FileSize = p3.FileSize,
				IconImage = finalIcon2
			});
		}
		SyncFileItemsInPlace(group.Files, desired);
	}

	private static void SyncFileItemsInPlace(ObservableCollection<MappingFileItem> target, List<MappingFileItem> desired)
	{
		int i = 0;
		foreach (MappingFileItem want in desired)
		{
			if (i < target.Count && target[i] == want)
			{
				i++;
				continue;
			}
			int idx = target.IndexOf(want);
			if (idx == -1)
			{
				target.Insert(i, want);
			}
			else if (idx != i)
			{
				target.Move(idx, i);
			}
			i++;
		}
		while (target.Count > desired.Count)
		{
			target.RemoveAt(desired.Count);
		}
	}

	public void RefreshAllGroups()
	{
		bool sizeDirty = _iconSizeDirty;
		_iconSizeDirty = false;
		foreach (MappingGroup group in Groups)
		{
			RefreshGroupFilesAsync(group, sizeDirty);
		}
	}

	public void RefreshIconSizes()
	{
		OnPropertyChanged("IconContainerWidth");
		OnPropertyChanged("IconContainerHeight");
		OnPropertyChanged("IconImageSize");
		OnPropertyChanged("IconFontSize");
		OnPropertyChanged("IconTextHeight");
		OnPropertyChanged("IconTextWrapWidth");
		OnPropertyChanged("TitleFontSize");
	}

	public void SaveState()
	{
		_zone.ActiveGroupIndex = Groups.IndexOf(ActiveGroup ?? Groups.First());
		_mainVm.SaveMappings();
	}

	protected void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	protected bool SetProp<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (object.Equals(field, value))
		{
			return false;
		}
		field = value;
		OnPropertyChanged(name);
		return true;
	}
}
