using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Casium.Views
{
    /// <summary>
    /// Interaction logic for LoginWindow.xaml
    /// </summary>
    public partial class LoginWindow : Window
    {
        // Demo credentials — replace with a real auth service call.
        private const string DemoUsername = "admin";
        private const string DemoPassword = "casium123";

        private static readonly SolidColorBrush NormalBorder =
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0"));
        private static readonly SolidColorBrush FocusBorder =
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4F46E5"));
        private static readonly SolidColorBrush ErrorBorder =
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));

        private bool _isPasswordVisible;

        public LoginWindow()
        {
            InitializeComponent();
            Loaded += LoginWindow_Loaded;
        }

        private void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Pre-fill a remembered username, if one was saved.
            try
            {
                string remembered = Properties.Settings.Default.RememberedUsername;
                if (!string.IsNullOrWhiteSpace(remembered))
                {
                    UsernameInput.Text = remembered;
                    RememberMeCheckBox.IsChecked = true;
                    PasswordInput.Focus();
                    return;
                }
            }
            catch
            {
                // Settings store unavailable — just start with a blank form.
            }

            UsernameInput.Focus();
        }

        // ---- Password show / hide -------------------------------------------------

        private void ShowPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            _isPasswordVisible = !_isPasswordVisible;

            if (_isPasswordVisible)
            {
                VisiblePasswordInput.Text = PasswordInput.Password;
                PasswordInput.Visibility = Visibility.Collapsed;
                VisiblePasswordInput.Visibility = Visibility.Visible;
                VisiblePasswordInput.Focus();
                VisiblePasswordInput.CaretIndex = VisiblePasswordInput.Text.Length;
                ShowPasswordButton.Content = "Hide";
            }
            else
            {
                PasswordInput.Password = VisiblePasswordInput.Text;
                VisiblePasswordInput.Visibility = Visibility.Collapsed;
                PasswordInput.Visibility = Visibility.Visible;
                PasswordInput.Focus();
                ShowPasswordButton.Content = "Show";
            }
        }

        private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isPasswordVisible)
            {
                VisiblePasswordInput.Text = PasswordInput.Password;
            }
            ClearError();
            UpdateCapsLockHint();
        }

        private void VisiblePasswordInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isPasswordVisible)
            {
                PasswordInput.Password = VisiblePasswordInput.Text;
            }
            ClearError();
            UpdateCapsLockHint();
        }

        // ---- Input chrome: focus highlight, validation reset -----------------------

        private void Input_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender == UsernameInput)
            {
                UsernameBorder.BorderBrush = FocusBorder;
            }
            else
            {
                PasswordBorder.BorderBrush = FocusBorder;
            }
        }

        private void Input_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender == UsernameInput)
            {
                UsernameBorder.BorderBrush = NormalBorder;
            }
            else if (ErrorBox.Visibility != Visibility.Visible)
            {
                PasswordBorder.BorderBrush = NormalBorder;
            }

            UpdateCapsLockHint();
        }

        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            ClearError();
        }

        private void UsernameInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (_isPasswordVisible)
                {
                    VisiblePasswordInput.Focus();
                }
                else
                {
                    PasswordInput.Focus();
                }
                e.Handled = true;
            }
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            // Enter is handled by IsDefault on the Sign in button; nothing extra needed.
        }

        private void PasswordInput_KeyUp(object sender, KeyEventArgs e)
        {
            UpdateCapsLockHint();
        }

        private void UpdateCapsLockHint()
        {
            try
            {
                bool capsOn = Console.CapsLock;
                bool passwordFocused = PasswordInput.IsFocused || VisiblePasswordInput.IsFocused;
                CapsLockHint.Visibility = (capsOn && passwordFocused)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            catch
            {
                CapsLockHint.Visibility = Visibility.Collapsed;
            }
        }

        // ---- Sign in ----------------------------------------------------------------

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string username = UsernameInput.Text.Trim();
            string password = _isPasswordVisible ? VisiblePasswordInput.Text : PasswordInput.Password;

            if (!ValidateInputs(username, password))
            {
                return;
            }

            SetBusy(true);
            try
            {
                // Simulate an async auth call (e.g. await _authService.SignInAsync(...)).
                await Task.Delay(900);

                if (!TryAuthenticate(username, password))
                {
                    ShowError("Invalid username or password. Try the demo account below.");
                    PasswordBorder.BorderBrush = ErrorBorder;
                    return;
                }

                PersistRememberMe(username);

                var main = new MainWindow(username);
                main.Show();
                Close();
            }
            finally
            {
                // If we navigated away the window is closing; restoring state is harmless.
                SetBusy(false);
            }
        }

        private bool ValidateInputs(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                ShowError("Enter your username or email.");
                UsernameBorder.BorderBrush = ErrorBorder;
                UsernameInput.Focus();
                return false;
            }

            if (username.Contains("@") && !LooksLikeEmail(username))
            {
                ShowError("That email address doesn't look right. Check it and try again.");
                UsernameBorder.BorderBrush = ErrorBorder;
                UsernameInput.Focus();
                return false;
            }

            if (string.IsNullOrEmpty(password))
            {
                ShowError("Enter your password.");
                PasswordBorder.BorderBrush = ErrorBorder;
                PasswordInput.Focus();
                return false;
            }

            if (password.Length < 4)
            {
                ShowError("Your password must be at least 4 characters.");
                PasswordBorder.BorderBrush = ErrorBorder;
                PasswordInput.Focus();
                return false;
            }

            return true;
        }

        private static bool LooksLikeEmail(string value)
        {
            int at = value.IndexOf('@');
            if (at <= 0 || at != value.LastIndexOf('@'))
            {
                return false;
            }
            int dot = value.IndexOf('.', at);
            return dot > at + 1 && dot < value.Length - 1;
        }

        private static bool TryAuthenticate(string username, string password)
        {
            // TODO: replace with a real authentication service.
            return string.Equals(username, DemoUsername, StringComparison.OrdinalIgnoreCase)
                && string.Equals(password, DemoPassword, StringComparison.Ordinal);
        }

        private void PersistRememberMe(string username)
        {
            try
            {
                Properties.Settings.Default.RememberedUsername =
                    RememberMeCheckBox.IsChecked == true ? username : string.Empty;
                Properties.Settings.Default.Save();
            }
            catch
            {
                // Non-critical: the user is still signed in for this session.
            }
        }

        private void SetBusy(bool busy)
        {
            LoginButton.IsEnabled = !busy;
            LoginButton.Content = busy ? "Signing in…" : "Sign in";
            UsernameInput.IsEnabled = !busy;
            PasswordInput.IsEnabled = !busy;
            VisiblePasswordInput.IsEnabled = !busy;
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorBox.Visibility = Visibility.Visible;
        }

        private void ClearError()
        {
            ErrorBox.Visibility = Visibility.Collapsed;
            UsernameBorder.BorderBrush = UsernameInput.IsFocused ? FocusBorder : NormalBorder;
            PasswordBorder.BorderBrush =
                (PasswordInput.IsFocused || VisiblePasswordInput.IsFocused) ? FocusBorder : NormalBorder;
        }

        // ---- Secondary actions -------------------------------------------------------

        private void ForgotPassword_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(this,
                "Password reset isn't wired up yet. Contact your administrator to reset your password.",
                "Forgot password",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void SignUp_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(this,
                "Self-registration isn't available in this build. Ask your administrator for an account.",
                "Create an account",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
