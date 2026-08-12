using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DesktopTool.Models;

public class MappingGroup : INotifyPropertyChanged
{
	private string _name = "新分组";

	[JsonIgnore]
	internal DateTime _lastKnownDirWriteTime;

	public string Id { get; set; } = Guid.NewGuid().ToString("N");

	public string Name
	{
		get
		{
			return _name;
		}
		set
		{
			_name = value;
			OnPropertyChanged("Name");
		}
	}

	public string FolderPath { get; set; } = "";

	[JsonIgnore]
	public ObservableCollection<MappingFileItem> Files { get; set; } = new ObservableCollection<MappingFileItem>();

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
