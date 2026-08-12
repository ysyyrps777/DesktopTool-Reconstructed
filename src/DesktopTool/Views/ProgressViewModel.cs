using System.ComponentModel;

namespace DesktopTool.Views;

public class ProgressViewModel : INotifyPropertyChanged
{
	private string _title = "正在处理文件...";

	private string _currentFile = "";

	private double _progressValue;

	private double _progressMax;

	private string _progressText = "";

	public string Title
	{
		get
		{
			return _title;
		}
		set
		{
			_title = value;
			OnPropertyChanged("Title");
		}
	}

	public string CurrentFile
	{
		get
		{
			return _currentFile;
		}
		set
		{
			_currentFile = value;
			OnPropertyChanged("CurrentFile");
		}
	}

	public double ProgressValue
	{
		get
		{
			return _progressValue;
		}
		set
		{
			_progressValue = value;
			OnPropertyChanged("ProgressValue");
		}
	}

	public double ProgressMax
	{
		get
		{
			return _progressMax;
		}
		set
		{
			_progressMax = value;
			OnPropertyChanged("ProgressMax");
		}
	}

	public string ProgressText
	{
		get
		{
			return _progressText;
		}
		set
		{
			_progressText = value;
			OnPropertyChanged("ProgressText");
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private void OnPropertyChanged(string propName)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
	}
}
