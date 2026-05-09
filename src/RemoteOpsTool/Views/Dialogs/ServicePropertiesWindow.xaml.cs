namespace RemoteOpsTool.Views.Dialogs;

public partial class ServicePropertiesWindow : System.Windows.Window
{
    public ServicePropertiesWindow()
    {
        InitializeComponent();
    }

    public void SetConfigText(string config, string serviceName)
    {
        TitleBlock.Text = $"服务属性 - {serviceName}";
        ConfigText.Text = config;
    }
}
