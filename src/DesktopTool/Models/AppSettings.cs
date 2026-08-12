namespace DesktopTool.Models;

public class AppSettings
{
	public double BackgroundOpacity { get; set; } = 0.8;

	public string PanelColor { get; set; } = "#1A1A1A";

	public int IconSpacing { get; set; } = 30;

	public string? CustomHiddenPath { get; set; }

	public int FontSizeLevel { get; set; } = 1;

	public int DefaultIconSizeLevel { get; set; } = 1;

	public string WindowStyle { get; set; } = "Simple";

	public string HidingMode { get; set; } = "Attributes";

	public bool AutoArrangeDesktop { get; set; }

	public bool IntentAutoStartEnabled { get; set; }

	public string CompletedTodoStyle { get; set; } = "Faded";

	public string HotkeyModifiers { get; set; } = "Win+Shift";

	public string HotkeyKey { get; set; } = "D";

	public string SearchHotkeyModifiers { get; set; } = "Win+Shift";

	public string SearchHotkeyKey { get; set; } = "F";

	public bool DesktopSearchWidgetEnabled { get; set; }

	public bool WinDZOrderImmune { get; set; }

	public double SearchWidgetLeft { get; set; } = -1.0;

	public double SearchWidgetTop { get; set; } = -1.0;

	public double SearchLeft { get; set; } = -1.0;

	public double SearchTop { get; set; } = -1.0;

	public double SearchWidth { get; set; } = -1.0;

	public double SearchHeight { get; set; } = -1.0;
}
