using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace DesktopTool.Models;

public class IconGroup : INotifyPropertyChanged
{
	private bool _isActive;

	public string Id { get; set; } = Guid.NewGuid().ToString("N");

	public string Name { get; set; } = "新分组";

	public List<ZoneIcon> Icons { get; set; } = new List<ZoneIcon>();

	[JsonIgnore]
	public bool IsActive
	{
		get
		{
			return _isActive;
		}
		set
		{
			if (_isActive != value)
			{
				_isActive = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsActive"));
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;
}
