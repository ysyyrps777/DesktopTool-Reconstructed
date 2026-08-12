using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DesktopTool.Models;

public class IconZone : INotifyPropertyChanged
{
	private string _name = "新建收纳区";

	public string? SchemaVersion { get; set; }

	public string Id { get; set; } = Guid.NewGuid().ToString("N");

	public string Name
	{
		get
		{
			return _name;
		}
		set
		{
			if (_name != value)
			{
				_name = value;
				OnPropertyChanged("Name");
			}
		}
	}

	public double X { get; set; } = 100.0;

	public double Y { get; set; } = 100.0;

	public double Width { get; set; } = 320.0;

	public double Height { get; set; } = 220.0;

	public bool PosPhysical { get; set; }

	public bool IsLocked { get; set; }

	public int IconSizeLevel { get; set; } = 1;

	public int IconSortMode { get; set; }

	public bool IconSortAscending { get; set; } = true;

	[JsonIgnore]
	public bool IsLargeIcon
	{
		get
		{
			return IconSizeLevel >= 1;
		}
		set
		{
			if (value && IconSizeLevel < 1)
			{
				IconSizeLevel = 1;
			}
			else if (!value)
			{
				IconSizeLevel = 0;
			}
		}
	}

	public bool IsCollapsed { get; set; }

	public bool AreTabsCollapsed { get; set; }

	public List<IconGroup> Groups { get; set; } = new List<IconGroup>();

	public int ActiveGroupIndex { get; set; }

	public List<ZoneIcon> Icons { get; set; } = new List<ZoneIcon>();

	[JsonIgnore]
	public int TotalIconCount => Groups.Sum((IconGroup g) => g.Icons.Count);

	public event PropertyChangedEventHandler? PropertyChanged;

	public void NotifyTotalIconCountChanged()
	{
		OnPropertyChanged("TotalIconCount");
	}

	protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
