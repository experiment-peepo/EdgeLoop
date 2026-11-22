using System;
using System.Threading.Tasks;
using System.Windows.Input;
using EdgeLoop.Classes;

namespace EdgeLoop.ViewModels
{
    /// <summary>
    /// One site account in Settings (Hypnotube, Rule34Video): the username and password as typed,
    /// and a "Check sign-in" that signs in with them to show whether the site accepts them.
    /// </summary>
    public sealed class SiteAccountSetting : ObservableObject
    {
        private readonly Func<string, string, Task<SiteSignInResult>> _signIn;

        /// <param name="lastError">Why EdgeLoop's last automatic sign-in failed, shown until the account is edited.</param>
        /// <param name="signIn">Signs in with a username and password; the result is only shown, not kept.</param>
        public SiteAccountSetting(Site site, string username, string password, string lastError, Func<string, string, Task<SiteSignInResult>> signIn)
        {
            Site = site;
            _username = username;
            _password = password;
            _signIn = signIn;
            OriginalUsername = username;
            OriginalPassword = password;
            if (!string.IsNullOrEmpty(lastError))
            {
                _status = $"Last sign-in failed: {lastError}";
                _succeeded = false;
            }
            CheckCommand = new RelayCommand(async _ => await CheckAsync(), _ => !IsChecking);
        }

        public Site Site { get; }

        internal string OriginalUsername { get; }
        internal string OriginalPassword { get; }

        /// <summary>Whether the account differs from the saved one (its session then belongs to the old account).</summary>
        internal bool Changed =>
            !string.Equals(Clean(Username), Clean(OriginalUsername), StringComparison.Ordinal) ||
            !string.Equals(Password ?? "", OriginalPassword ?? "", StringComparison.Ordinal);

        private string _username;
        public string Username
        {
            get => _username;
            set
            {
                if (SetProperty(ref _username, value)) ClearStatus();
            }
        }

        private string _password;
        /// <summary>Set from the window's password box (a password box can't be bound).</summary>
        public string Password
        {
            get => _password;
            set
            {
                if (SetProperty(ref _password, value)) ClearStatus();
            }
        }

        private string _status;
        /// <summary>What the last check found, or why the last automatic sign-in failed; null if nothing.</summary>
        public string Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        private bool? _succeeded;
        /// <summary>Whether the last check signed in: true, false, or null while unchecked.</summary>
        public bool? Succeeded
        {
            get => _succeeded;
            private set => SetProperty(ref _succeeded, value);
        }

        private bool _isChecking;
        public bool IsChecking
        {
            get => _isChecking;
            private set => SetProperty(ref _isChecking, value);
        }

        public ICommand CheckCommand { get; }

        /// <summary>Signs in with the username and password as typed, to show whether they work.</summary>
        internal async Task CheckAsync()
        {
            if (IsChecking) return;
            IsChecking = true;
            Succeeded = null;
            Status = "Checking…";
            try
            {
                var result = await _signIn(Clean(Username), Password);
                Succeeded = result.Success;
                Status = result.Message;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Settings] {Site} sign-in check failed: {ex.Message}");
                Succeeded = false;
                Status = $"Couldn't check: {ex.Message}";
            }
            finally
            {
                IsChecking = false;
            }
        }

        internal static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private void ClearStatus()
        {
            Status = null;
            Succeeded = null;
        }
    }
}
