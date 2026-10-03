namespace v2rayN.Views;

public partial class ColituMainWindow
{
    /// <summary>The Windows app is published under GPL-3.0; linked from About.</summary>
    private const string SourceCodeUrl = "https://github.com/cyberlexs/colitu-windows";

    private void SourceCode_Click(object sender, RoutedEventArgs e) => OpenUrl(SourceCodeUrl);
}
