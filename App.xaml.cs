using Microsoft.UI.Xaml;


// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OcrApp
{

    public partial class App
    {
        private Window? _window;
        public static Window? CurrentWindow { get; private set; }


        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            CurrentWindow = _window;
            _window.Activate();
        }
    }
}
