using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Titles;
using LocalizerModel = AgentSpeed.Presentation.Localization.Localizer;

namespace AgentSpeed.Linux;

public static class LinuxTitleText
{
    public static LocalizerModel Localizer { get; } = new(LocalizerModel.LoadEmbeddedTable(), "zh-Hans");
    public static string Name(TitleTheme theme) => theme.Name(Localizer);
    public static string Label(TitleTheme theme, int level) => theme.Text(level, Localizer.Get);
}
