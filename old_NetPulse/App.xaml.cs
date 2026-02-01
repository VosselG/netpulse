using System.Windows;

namespace NetPulse
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            try
            {
                var mainWindow = new MainWindow();
                this.MainWindow = mainWindow;
                mainWindow.Show();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Error Starting NetPulse");
                Shutdown();
            }
        }
    }
}