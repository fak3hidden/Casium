using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Casium.Views
{
    public partial class LoginWindow : Window
    {
        // TODO: replace with a real authentication service.
        private const string DemoUsername = "admin";
        private const string DemoPassword = "casium123";

        private static readonly Geometry EyeOff = Geometry.Parse("M12,3 A9,9 0 1,0 12,21 A9,9 0 1,0 12,3 Z M5.64,5.64 L18.36,18.36");
        private static readonly Geometry EyeOn = Geometry.Parse("M1.5,12 C4,7.2 7.8,4.8 12,4.8 C16.2,4.8 20,7.2 22.5,12 C20,16.8 16.2,19.2 12,19.2 C7.8,19.2 4,16.8 1.5,12 Z M12,15.2 A3.2,3.2 0 1,0 12,8.8 A3.2,3.2 0 1,0 12,15.2 Z");

        private bool _isPasswordVisible;
        private bool _hasError;

        public LoginWindow()
        {
            InitializeComponent();
            Loaded += LoginWindow_Loaded;
            StateChanged += (s, e) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        private void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string remembered = Properties.Settings.Default.RememberedUsername;
                if (!string.IsNullOrWhiteSpace(remembered))
                {
                    UsernameInput.Text = remembered;
                }
            }
            catch { }

            if (UsernameInput.Text.Length > 0)
            {
                PasswordInput.Focus();
            }
            else
            {
                UsernameInput.Focus();
            }
            UpdateState();
        }

        // ---- window chrome ------------------------------------------------------------

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeButton_Click(sender, e);
                return;
            }
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ---- inputs -----------------------------------------------------------------------

        private string CurrentPassword
        {
            get { return _isPasswordVisible ? VisiblePasswordInput.Text : PasswordInput.Password; }
        }

        private void UpdateState()
        {
            UsernameHint.Visibility = UsernameInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool hasPassword = CurrentPassword.Length > 0;
            PasswordHint.Visibility = hasPassword ? Visibility.Collapsed : Visibility.Visible;
            ShowPasswordButton.Visibility = hasPassword ? Visibility.Visible : Visibility.Collapsed;
            LoginButton.IsEnabled = UsernameInput.Text.Trim().Length > 0 && hasPassword;
            PaintBorders();
        }

        private void PaintBorders()
        {
            UsernameBorder.BorderBrush = _hasError ? Brush("L.Error")
                : UsernameInput.IsKeyboardFocusWithin ? Brush("L.Focus") : Brush("L.InputBorder");
            PasswordBorder.BorderBrush = _hasError ? Brush("L.Error")
                : (PasswordInput.IsKeyboardFocusWithin || VisiblePasswordInput.IsKeyboardFocusWithin) ? Brush("L.Focus") : Brush("L.InputBorder");
        }

        private System.Windows.Media.Brush Brush(string key)
        {
            return (System.Windows.Media.Brush)FindResource(key);
        }

        private void Input_GotFocus(object sender, RoutedEventArgs e)
        {
            PaintBorders();
            UpdateCapsLockHint();
        }

        private void Input_LostFocus(object sender, RoutedEventArgs e)
        {
            PaintBorders();
            CapsLockHint.Visibility = Visibility.Collapsed;
        }

        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            ClearError();
            UpdateState();
        }

        private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isPasswordVisible)
            {
                return;
            }
            ClearError();
            UpdateState();
        }

        private void VisiblePasswordInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isPasswordVisible)
            {
                return;
            }
            ClearError();
            UpdateState();
        }

        private void ShowPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            _isPasswordVisible = !_isPasswordVisible;
            if (_isPasswordVisible)
            {
                VisiblePasswordInput.Text = PasswordInput.Password;
                VisiblePasswordInput.Visibility = Visibility.Visible;
                PasswordInput.Visibility = Visibility.Collapsed;
                VisiblePasswordInput.Focus();
                VisiblePasswordInput.CaretIndex = VisiblePasswordInput.Text.Length;
                EyeIcon.Data = EyeOn;
                ShowPasswordButton.ToolTip = "Hide password";
            }
            else
            {
                PasswordInput.Password = VisiblePasswordInput.Text;
                PasswordInput.Visibility = Visibility.Visible;
                VisiblePasswordInput.Visibility = Visibility.Collapsed;
                PasswordInput.Focus();
                EyeIcon.Data = EyeOff;
                ShowPasswordButton.ToolTip = "Show password";
            }
            UpdateState();
        }

        private void UsernameInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (_isPasswordVisible) VisiblePasswordInput.Focus(); else PasswordInput.Focus();
            }
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && LoginButton.IsEnabled)
            {
                e.Handled = true;
                LoginButton_Click(sender, e);
            }
        }

        private void PasswordInput_KeyUp(object sender, KeyEventArgs e)
        {
            UpdateCapsLockHint();
        }

        private void UpdateCapsLockHint()
        {
            bool focused = PasswordInput.IsKeyboardFocusWithin || VisiblePasswordInput.IsKeyboardFocusWithin;
            CapsLockHint.Visibility = focused && Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- sign in ----------------------------------------------------------------------

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string username = UsernameInput.Text.Trim();
            string password = CurrentPassword;
            if (username.Length == 0 || password.Length == 0)
            {
                return;
            }

            SetBusy(true);
            try
            {
                await Task.Delay(600);

                if (!TryAuthenticate(username, password))
                {
                    ShowError("Incorrect username or password.");
                    return;
                }

                try
                {
                    Properties.Settings.Default.RememberedUsername = username;
                    Properties.Settings.Default.Save();
                }
                catch { }

                var main = new MainMenu(username);
                main.Show();
                Close();
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static bool TryAuthenticate(string username, string password)
        {
            return string.Equals(username, DemoUsername, StringComparison.OrdinalIgnoreCase)
                && string.Equals(password, DemoPassword, StringComparison.Ordinal);
        }

        private void SetBusy(bool busy)
        {
            LoginButton.Content = busy ? "Signing in…" : "Sign in";
            UsernameInput.IsEnabled = !busy;
            PasswordInput.IsEnabled = !busy;
            VisiblePasswordInput.IsEnabled = !busy;
            ShowPasswordButton.IsEnabled = !busy;
            if (busy)
            {
                LoginButton.IsEnabled = false;
            }
            else
            {
                UpdateState();
            }
        }

        private void ShowError(string message)
        {
            _hasError = true;
            ErrorText.Text = message;
            ErrorBox.Visibility = Visibility.Visible;
            PaintBorders();
        }

        private void ClearError()
        {
            if (!_hasError)
            {
                return;
            }
            _hasError = false;
            ErrorBox.Visibility = Visibility.Collapsed;
            PaintBorders();
        }
    }
}
