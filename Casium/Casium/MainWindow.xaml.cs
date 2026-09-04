using System.Windows;

namespace Casium
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public string CurrentUsername { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
        }

        public MainWindow(string username) : this()
        {
            CurrentUsername = username;
            WelcomeSubtitle.Text = string.Format("Signed in as {0}.", username);
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            new Views.LoginWindow().Show();
            Close();
        }
    }
}
