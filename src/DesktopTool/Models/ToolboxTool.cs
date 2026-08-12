namespace DesktopTool.Models;

public class ToolboxTool
{
	public string Id { get; set; } = string.Empty;

	public string Name { get; set; } = string.Empty;

	public string Icon { get; set; } = "\ud83d\udd27";

	public string IconPath { get; set; } = string.Empty;

	public string IconImage { get; set; } = string.Empty;

	public bool IconContainerless { get; set; }

	public string Description { get; set; } = string.Empty;
}
