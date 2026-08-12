using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DesktopTool.Models;

public class MappingFileItem : INotifyPropertyChanged
{
	private string _filePath = "";

	private string _fileName = "";

	private string _displayName = "";

	private ImageSource? _iconImage;

	private bool _isDirectory;

	private long _fileSize;

	private DateTime _lastWriteTime;

	private string _extension = "";

	public string FilePath
	{
		get
		{
			return _filePath;
		}
		set
		{
			SetField(ref _filePath, value, "FilePath");
		}
	}

	public string FileName
	{
		get
		{
			return _fileName;
		}
		set
		{
			SetField(ref _fileName, value, "FileName");
		}
	}

	public string DisplayName
	{
		get
		{
			return _displayName;
		}
		set
		{
			SetField(ref _displayName, value, "DisplayName");
		}
	}

	public ImageSource? IconImage
	{
		get
		{
			return _iconImage;
		}
		set
		{
			SetField(ref _iconImage, value, "IconImage");
		}
	}

	public bool IsDirectory
	{
		get
		{
			return _isDirectory;
		}
		set
		{
			SetField(ref _isDirectory, value, "IsDirectory");
		}
	}

	public long FileSize
	{
		get
		{
			return _fileSize;
		}
		set
		{
			SetField(ref _fileSize, value, "FileSize");
		}
	}

	public DateTime LastWriteTime
	{
		get
		{
			return _lastWriteTime;
		}
		set
		{
			SetField(ref _lastWriteTime, value, "LastWriteTime");
		}
	}

	public string Extension
	{
		get
		{
			return _extension;
		}
		set
		{
			SetField(ref _extension, value, "Extension");
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
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
