using System.ComponentModel;
using System.Windows.Media;

namespace DesktopTool.ViewModels;

public class DesktopSearchWidgetViewModel : INotifyPropertyChanged
{
	private readonly MainViewModel _mainVm;

	public Brush PanelColorBrush => _mainVm.PanelColorBrush;

	public double BackgroundOpacity => _mainVm.GlobalOpacity;

	public event PropertyChangedEventHandler? PropertyChanged;

	public DesktopSearchWidgetViewModel(MainViewModel mainVm)
	{
		_mainVm = mainVm;
		_mainVm.PropertyChanged += delegate(object? _, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == "PanelColorBrush" || e.PropertyName == "GlobalOpacity")
			{
				OnPropertyChanged("PanelColorBrush");
				OnPropertyChanged("BackgroundOpacity");
			}
		};
	}

	private void OnPropertyChanged(string name)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
