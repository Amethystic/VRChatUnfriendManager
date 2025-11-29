using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui;
using Microsoft.Toolkit.Uwp.Notifications;

using Color = Terminal.Gui.Color;
using Attribute = Terminal.Gui.Attribute;
using File = System.IO.File;

// --- CONFIGURATION MODEL ---
public class AppConfig
{
    public string Username { get; set; } = "";
    public string EncodedPassword { get; set; } = "";
    public bool ExcludeFavorites { get; set; } = true;
    public bool InactiveEnabled { get; set; } = false;
    public string InactiveValue { get; set; } = "1";
    public int InactiveUnitIndex { get; set; } = 0;
    public int SortOptionIndex { get; set; } = 0;
}

public class SafeLimitedUserFriend
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("last_login")]
    public string LastLogin { get; set; } = string.Empty;
}

public class VRChatApiService
{
    private const string AppUserAgent = "VRChatUnfriendManager/1.7 (github.com/Amethystic/VRChatUnfriendManager)";
    private const string SessionCookieFileName = "session.cookie";

    private readonly HttpClient _client;
    private readonly CookieContainer _cookieContainer;
    private Configuration? _apiConfig;

    private FriendsApi? _friendsApi;
    private FriendsApi Friends => _friendsApi ??= new FriendsApi(_apiConfig);

    private FavoritesApi? _favoritesApi;
    private FavoritesApi Favorites => _favoritesApi ??= new FavoritesApi(_apiConfig);

    public bool IsLoggedIn { get; private set; }

    public VRChatApiService()
    {
        _cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = _cookieContainer };
        _client = new HttpClient(handler) { BaseAddress = new Uri("https://api.vrchat.cloud/api/1/") };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(AppUserAgent);
    }

    public async Task<(bool success, string message)> LoginAsync(string username, string password)
    {
        try
        {
            var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}"));
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);

            var response = await _client.GetAsync("auth/user");
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return (false, TryParseApiErrorMessage(responseBody, "Login Error"));

            var userDoc = JsonDocument.Parse(responseBody);

            if (userDoc.RootElement.TryGetProperty("requiresTwoFactorAuth", out var requires2fa) && requires2fa.ValueKind == JsonValueKind.Array && requires2fa.GetArrayLength() > 0)
            {
                string? code = await Program.GetTwoFactorCodeAsync();
                if (string.IsNullOrEmpty(code)) return (false, "2FA cancelled.");

                var jsonContent = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json");
                bool verified = false;

                var totpResponse = await _client.PostAsync("auth/twofactorauth/totp/verify", jsonContent);
                if (totpResponse.IsSuccessStatusCode) verified = true;
                else
                {
                    jsonContent = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json");
                    var emailResponse = await _client.PostAsync("auth/twofactorauth/emailotp/verify", jsonContent);
                    if (emailResponse.IsSuccessStatusCode) verified = true;
                    else
                    {
                        jsonContent = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json");
                        var otpResponse = await _client.PostAsync("auth/twofactorauth/otp/verify", jsonContent);
                        if (otpResponse.IsSuccessStatusCode) verified = true;
                    }
                }
                if (!verified) return (false, "2FA verification failed.");
            }

            var authCookie = _cookieContainer.GetCookies(_client.BaseAddress!)["auth"];
            if (authCookie == null) return (false, "Failed to get auth cookie.");

            var twoFactorAuthCookie = _cookieContainer.GetCookies(_client.BaseAddress!)["twoFactorAuth"];

            var fullCookieHeader = $"auth={authCookie.Value}";
            if (twoFactorAuthCookie != null)
                fullCookieHeader += $"; twoFactorAuth={twoFactorAuthCookie.Value}";

            _apiConfig = new Configuration { UserAgent = AppUserAgent };
            _apiConfig.DefaultHeaders.Add("Cookie", fullCookieHeader);
            IsLoggedIn = true;

            await SaveSessionAsync(fullCookieHeader);
            return (true, "Login successful.");
        }
        catch (Exception ex)
        {
            return (false, "Login error: " + ex.Message);
        }
    }

    public async Task<(bool success, string? restoredUsername)> RestoreSessionAsync()
    {
        if (!File.Exists(SessionCookieFileName)) return (false, null);

        var fullCookieHeader = await File.ReadAllTextAsync(SessionCookieFileName);
        if (string.IsNullOrEmpty(fullCookieHeader)) return (false, null);

        var config = new Configuration { UserAgent = AppUserAgent };
        config.DefaultHeaders.Add("Cookie", fullCookieHeader);

        try
        {
            var currentUser = await new AuthenticationApi(config).GetCurrentUserAsync();
            _apiConfig = config;
            IsLoggedIn = true;
            return (true, currentUser.Username);
        }
        catch
        {
            try { File.Delete(SessionCookieFileName); } catch { }
            return (false, null);
        }
    }

    public async Task<List<SafeLimitedUserFriend>> GetAllFriendsAsync()
    {
        if (!IsLoggedIn || _apiConfig == null) throw new InvalidOperationException("Not logged in.");

        var allFriends = new List<SafeLimitedUserFriend>();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        int offset = 0;
        const int pageSize = 100;

        List<SafeLimitedUserFriend> page;
        do
        {
            var response = await Friends.GetFriendsWithHttpInfoAsync(offset: offset, n: pageSize, offline: false);
            page = JsonSerializer.Deserialize<List<SafeLimitedUserFriend>>(response.RawContent, options) ?? new();
            allFriends.AddRange(page);
            offset += pageSize;
        } while (page.Count == pageSize);

        offset = 0;
        do
        {
            var response = await Friends.GetFriendsWithHttpInfoAsync(offset: offset, n: pageSize, offline: true);
            page = JsonSerializer.Deserialize<List<SafeLimitedUserFriend>>(response.RawContent, options) ?? new();
            allFriends.AddRange(page);
            offset += pageSize;
        } while (page.Count == pageSize);

        return allFriends;
    }

    public async Task<HashSet<string>> GetFavoriteFriendIdsAsync()
    {
        if (!IsLoggedIn || _apiConfig == null) throw new InvalidOperationException("Not logged in.");

        var favoriteIds = new HashSet<string>();
        int offset = 0;
        const int pageSize = 100;

        List<Favorite> page;
        do
        {
            page = await Favorites.GetFavoritesAsync(type: "friend", n: pageSize, offset: offset);
            foreach (var fav in page)
                favoriteIds.Add(fav.FavoriteId);
            offset += pageSize;
        } while (page.Count == pageSize);

        return favoriteIds;
    }

    public async Task UnfriendAsync(string userId)
    {
        if (!IsLoggedIn || _apiConfig == null) throw new InvalidOperationException("Not logged in.");
        await Friends.UnfriendAsync(userId);
    }

    public async Task<(bool success, string message)> SendFriendRequestAsync(string userId)
    {
        if (!IsLoggedIn || _apiConfig == null) return (false, "Not logged in.");
        try
        {
            var response = await _client.PostAsync($"user/{userId}/friendRequest", new StringContent(""));
            if (response.IsSuccessStatusCode)
                return (true, "Sent");

            var body = await response.Content.ReadAsStringAsync();
            return (false, TryParseApiErrorMessage(body, "Request failed"));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string TryParseApiErrorMessage(string responseBody, string fallback)
    {
        try
        {
            var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString() ?? fallback;
        }
        catch { }
        return fallback;
    }

    private static async Task SaveSessionAsync(string fullCookieHeader)
    {
        try { await File.WriteAllTextAsync(SessionCookieFileName, fullCookieHeader); } catch { }
    }
}

public class Program
{
    private static readonly int minDelaySeconds = 5;
    private static readonly int maxDelaySeconds = 10;
    private const string ConfigFileName = "user.config";

    private static readonly List<string> InactivityUnits = new() { "Days", "Months", "Years" };
    
    private static readonly List<string> SortOptions = new() { 
        "Last Seen: Oldest", 
        "Last Seen: Newest", 
        "Name (A-Z)", 
        "Name (Z-A)" 
    };

    private static volatile bool _isPaused = false;
    private static CancellationTokenSource? _currentCts;
    private static Button? _pauseButton;
    private static ProgressBar? _progressBar;
    private static Button? _cancelButton;
    private static FrameView? _friendsFrame;
    private static Button[]? _bottomButtons;
    private static Label? _statusLabel;

    // Static references to UI elements for saving configuration
    private static TextField? _usernameText;
    private static TextField? _passwordText;
    private static CheckBox? _excludeFavoritesCheck;
    private static CheckBox? _inactivityCheck;
    private static TextField? _inactivityValue;
    private static ComboBox? _inactivityUnit;
    private static ComboBox? _sortCombo;

    public static void ShowNotification(string title, string message)
    {
        if (OperatingSystem.IsWindows())
            new ToastContentBuilder().AddText(title).AddText(message).Show();
    }

    public static Task<string?> GetTwoFactorCodeAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        Application.MainLoop.Invoke(() =>
        {
            var dialog = new Dialog("2FA Required", 60, 7);
            var label = new Label("Enter code (Authenticator/Email/Backup):") { X = 1, Y = 1 };
            var codeText = new TextField("") { X = 1, Y = 2, Width = Dim.Fill(1) };
            var ok = new Button("OK") { IsDefault = true };
            var cancel = new Button("Cancel");
            ok.Clicked += () => { tcs.TrySetResult(codeText.Text.ToString()); Application.RequestStop(); };
            cancel.Clicked += () => { tcs.TrySetResult(null); Application.RequestStop(); };
            dialog.Add(label, codeText);
            dialog.AddButton(ok);
            dialog.AddButton(cancel);
            Application.Run(dialog);
        });
        return tcs.Task;
    }

    private static DateTimeOffset? ParseLastLogin(string iso) =>
        string.IsNullOrWhiteSpace(iso)
            ? null
            : DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
                ? dt.ToUniversalTime()
                : null;

    private static string FormatRelativeTime(DateTimeOffset dt)
    {
        var ago = DateTimeOffset.UtcNow - dt;
        if (ago.TotalDays < 1) return "today";
        if (ago.TotalDays < 30) return $"{(int)ago.TotalDays} days ago";
        if (ago.TotalDays < 365)
        {
            int months = (int)Math.Round(ago.TotalDays / 30.4375);
            return months == 1 ? "1 month ago" : $"{months} months ago";
        }
        int years = (int)Math.Round(ago.TotalDays / 365.25);
        return years == 1 ? "1 year ago" : $"{years} years ago";
    }

    private static async Task WaitIfPausedOrCancelled(CancellationToken token)
    {
        while (_isPaused && !token.IsCancellationRequested)
        {
            await Task.Delay(200, token);
        }
    }

    private static void StartOperation()
    {
        _friendsFrame!.Height = Dim.Fill(6);
        _progressBar!.Visible = true;
        _pauseButton!.Visible = true;
        _cancelButton!.Visible = true;

        foreach (var btn in _bottomButtons!)
            btn.Y = Pos.Bottom(_progressBar!) + 1;

        Application.Refresh();
    }

    private static void EndOperation()
    {
        _friendsFrame!.Height = Dim.Fill(5);
        _progressBar!.Visible = false;
        _pauseButton!.Visible = false;
        _cancelButton!.Visible = false;
        _progressBar!.Fraction = 0;
        _isPaused = false;

        foreach (var btn in _bottomButtons!)
            btn.Y = Pos.Bottom(_statusLabel!) + 1;

        Application.Refresh();
    }

    // --- CONFIGURATION METHODS ---

    private static void SaveConfig()
    {
        try 
        {
            var config = new AppConfig
            {
                Username = _usernameText?.Text.ToString() ?? "",
                EncodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(_passwordText?.Text.ToString() ?? "")),
                ExcludeFavorites = _excludeFavoritesCheck?.Checked ?? true,
                InactiveEnabled = _inactivityCheck?.Checked ?? false,
                InactiveValue = _inactivityValue?.Text.ToString() ?? "1",
                InactiveUnitIndex = _inactivityUnit?.SelectedItem ?? 0,
                SortOptionIndex = _sortCombo?.SelectedItem ?? 0
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFileName, json);
        }
        catch {}
    }

    private static AppConfig LoadConfig()
    {
        if (!File.Exists(ConfigFileName)) return new AppConfig();
        try 
        {
            var content = File.ReadAllText(ConfigFileName);
            
            // Backward compatibility check for old format (username|password)
            if (!content.TrimStart().StartsWith("{"))
            {
                if (content.Contains("|"))
                {
                    var parts = content.Split('|', 2);
                    return new AppConfig { Username = parts[0], EncodedPassword = parts[1] };
                }
                return new AppConfig { Username = content };
            }

            return JsonSerializer.Deserialize<AppConfig>(content) ?? new AppConfig();
        }
        catch { return new AppConfig(); }
    }

    public static async Task Main()
    {
        var config = LoadConfig();
        string initPass = "";
        try { if(!string.IsNullOrEmpty(config.EncodedPassword)) initPass = Encoding.UTF8.GetString(Convert.FromBase64String(config.EncodedPassword)); } catch {}

        Application.Init();

        var neutralScheme = new ColorScheme
        {
            Normal = new Attribute(Color.Gray, Color.Black),
            Focus = new Attribute(Color.White, Color.Black),
            HotNormal = new Attribute(Color.BrightCyan, Color.Black),
            HotFocus = new Attribute(Color.BrightCyan, Color.Black),
            Disabled = new Attribute(Color.DarkGray, Color.Black)
        };

        Colors.Base = neutralScheme;
        Colors.Dialog = neutralScheme;
        Colors.Menu = neutralScheme;
        Colors.TopLevel = neutralScheme;
        Colors.Error = new ColorScheme { Normal = new Attribute(Color.White, Color.Red) };

        var win = new Window("VRChat Unfriend/Re-Add Manager") { Width = Dim.Fill(), Height = Dim.Fill() };
        Application.Top.Add(win);

        // --- UI INITIALIZATION FROM CONFIG ---

        _usernameText = new TextField(config.Username) { X = 15, Y = 2, Width = 40 };
        _passwordText = new TextField(initPass) { X = 15, Y = 4, Width = 40, Secret = true };
        var loginButton = new Button("Login") { X = 15, Y = 6 };
        var rememberMeCheck = new CheckBox("Remember me") { X = Pos.Right(loginButton) + 5, Y = 6, Checked = !string.IsNullOrEmpty(config.Username) };

        _excludeFavoritesCheck = new CheckBox("Exclude Favorites") { X = 15, Y = 9, Checked = config.ExcludeFavorites };

        _inactivityCheck = new CheckBox("Only show inactive ≥") { X = 15, Y = 11, Checked = config.InactiveEnabled };
        _inactivityValue = new TextField(config.InactiveValue) { X = Pos.Right(_inactivityCheck) + 1, Y = 11, Width = 4, Enabled = config.InactiveEnabled };
        _inactivityUnit = new ComboBox() { X = Pos.Right(_inactivityValue) + 1, Y = 11, Width = 15, Enabled = config.InactiveEnabled };
        _inactivityUnit.SetSource(InactivityUnits);
        _inactivityUnit.SelectedItem = config.InactiveUnitIndex;

        var sortLabel = new Label("Sort by:") { X = 15, Y = 13 };
        _sortCombo = new ComboBox() { X = Pos.Right(sortLabel) + 1, Y = 13, Width = 35 };
        _sortCombo.SetSource(SortOptions);
        _sortCombo.SelectedItem = config.SortOptionIndex;

        _friendsFrame = new FrameView("Friends List")
        {
            X = 3,
            Y = Pos.Bottom(_sortCombo) + 2,
            Width = Dim.Fill(3),
            Height = Dim.Fill(5)
        };
        var friendsListView = new ListView(new List<string>())
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = true,
            AllowsMultipleSelection = true
        };
        _friendsFrame.Add(friendsListView);

        _statusLabel = new Label("Status: Not logged in") { X = 3, Y = Pos.Bottom(_friendsFrame) + 1 };

        _progressBar = new ProgressBar()
        {
            X = 3,
            Y = Pos.Bottom(_statusLabel) + 1,
            Width = Dim.Fill(35),
            Height = 1,
            Fraction = 0,
            Visible = false,
            ColorScheme = new ColorScheme()
            {
                Normal = new Attribute(Color.BrightGreen, Color.Black),
                Focus = new Attribute(Color.BrightGreen, Color.Black)
            }
        };

        _pauseButton = new Button("Pause")
        {
            X = Pos.AnchorEnd(27),
            Y = Pos.Bottom(_statusLabel) + 1,
            Visible = false
        };

        _cancelButton = new Button("Cancel")
        {
            X = Pos.AnchorEnd(12),
            Y = Pos.Bottom(_statusLabel) + 1,
            Visible = false
        };
        _cancelButton.ColorScheme = Colors.Error;

        var unfriendButton = new Button("Unfriend Marked") { X = 3, Y = Pos.Bottom(_statusLabel) + 1, Enabled = false };
        var readdButton = new Button("Re-add from JSON") { X = Pos.Right(unfriendButton) + 2, Y = Pos.Bottom(_statusLabel) + 1, Enabled = false };
        var backupButton = new Button("Backup Displayed") { X = Pos.Right(readdButton) + 2, Y = Pos.Bottom(_statusLabel) + 1, Enabled = false };
        var refreshButton = new Button("Refresh List") { X = Pos.Right(backupButton) + 2, Y = Pos.Bottom(_statusLabel) + 1, Enabled = false };
        var markAllButton = new Button("Mark All") { X = Pos.Right(refreshButton) + 2, Y = Pos.Bottom(_statusLabel) + 1, Enabled = false };
        var unmarkAllButton = new Button("Unmark All") { X = Pos.Right(markAllButton) + 2, Y = Pos.Bottom(_statusLabel) + 1, Enabled = false };

        _bottomButtons = new[] { unfriendButton, readdButton, backupButton, refreshButton, markAllButton, unmarkAllButton };

        win.Add(
            new Label("Username:") { X = 3, Y = 2 }, _usernameText,
            new Label("Password:") { X = 3, Y = 4 }, _passwordText,
            loginButton, rememberMeCheck,
            _excludeFavoritesCheck,
            _inactivityCheck, _inactivityValue, _inactivityUnit,
            sortLabel, _sortCombo,
            _friendsFrame,
            _statusLabel,
            _progressBar,
            _pauseButton,
            _cancelButton,
            unfriendButton, readdButton, backupButton, refreshButton, markAllButton, unmarkAllButton
        );

        _pauseButton.Clicked += () =>
        {
            _isPaused = !_isPaused;
            _pauseButton.Text = _isPaused ? "Resume" : "Pause";
        };

        _cancelButton.Clicked += () =>
        {
            _currentCts?.Cancel();
        };

        var apiService = new VRChatApiService();
        List<SafeLimitedUserFriend> allFriendsCache = new();
        HashSet<string> favoriteFriendIdsCache = new();
        List<SafeLimitedUserFriend> friendsToDisplay = new();

        // Helper to update config if user is already logged in and changes settings
        void TriggerSaveIfLoggedIn()
        {
            if (apiService.IsLoggedIn && File.Exists(ConfigFileName))
            {
                SaveConfig();
            }
        }

        void UpdateFriendsList()
        {
            var candidates = allFriendsCache.ToList();

            if (_excludeFavoritesCheck.Checked == true)
                candidates = candidates.Where(f => !favoriteFriendIdsCache.Contains(f.Id)).ToList();

            int numVal = 0;
            bool applyInactivity = _inactivityCheck.Checked == true &&
                                   int.TryParse(_inactivityValue.Text.ToString(), out numVal) && numVal > 0;

            DateTimeOffset? cutoff = null;
            if (applyInactivity)
            {
                var now = DateTimeOffset.UtcNow;
                cutoff = InactivityUnits[_inactivityUnit.SelectedItem] switch
                {
                    "Years" => now.AddYears(-numVal),
                    "Months" => now.AddMonths(-numVal),
                    _ => now.AddDays(-numVal)
                };
                candidates = candidates.Where(f =>
                {
                    var last = ParseLastLogin(f.LastLogin);
                    return last.HasValue && last.Value < cutoff.Value;
                }).ToList();
            }

            friendsToDisplay = _sortCombo.SelectedItem switch
            {
                0 => candidates.OrderBy(f => ParseLastLogin(f.LastLogin) ?? DateTimeOffset.MinValue).ToList(),           // Last Seen: Oldest
                1 => candidates.OrderByDescending(f => ParseLastLogin(f.LastLogin) ?? DateTimeOffset.MinValue).ToList(), // Last Seen: Newest
                2 => candidates.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),                  // Name (A-Z)
                3 => candidates.OrderByDescending(f => f.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),        // Name (Z-A)
                _ => candidates
            };

            var lines = friendsToDisplay.Select(f =>
            {
                var last = ParseLastLogin(f.LastLogin);
                string time = last.HasValue ? $" - last seen {FormatRelativeTime(last.Value)}" : " - last seen: never";
                return $"{f.DisplayName}{time}";
            }).ToList();

            friendsListView.SetSource(lines);

            string status = $"Status: {friendsToDisplay.Count} friends shown";
            if (_excludeFavoritesCheck.Checked == true) status += " | no favorites";
            if (applyInactivity) status += $" | inactive ≥ {numVal} {InactivityUnits[_inactivityUnit.SelectedItem].ToLower()}";
            status += $" | {SortOptions[_sortCombo.SelectedItem]}";
            _statusLabel!.Text = status;
        }

        _excludeFavoritesCheck.Toggled += (_) => { TriggerSaveIfLoggedIn(); if (apiService.IsLoggedIn) UpdateFriendsList(); };
        _inactivityCheck.Toggled += (_) =>
        {
            bool on = _inactivityCheck.Checked == true;
            _inactivityValue.Enabled = on;
            _inactivityUnit.Enabled = on;
            TriggerSaveIfLoggedIn();
            if (apiService.IsLoggedIn) UpdateFriendsList();
        };
        _inactivityValue.TextChanged += (_) => { if (_inactivityCheck.Checked == true) UpdateFriendsList(); };
        _inactivityValue.Leave += (_) => TriggerSaveIfLoggedIn();
        _inactivityUnit.SelectedItemChanged += (_) => { TriggerSaveIfLoggedIn(); if (_inactivityCheck.Checked == true) UpdateFriendsList(); };
        _sortCombo.SelectedItemChanged += (_) => { TriggerSaveIfLoggedIn(); if (apiService.IsLoggedIn) UpdateFriendsList(); };

        async Task LoadData()
        {
            _statusLabel!.Text = "Fetching friends & favorites...";
            Application.Refresh();

            favoriteFriendIdsCache = await apiService.GetFavoriteFriendIdsAsync();
            allFriendsCache = await apiService.GetAllFriendsAsync();

            UpdateFriendsList();

            unfriendButton.Enabled = readdButton.Enabled = backupButton.Enabled =
            refreshButton.Enabled = markAllButton.Enabled = unmarkAllButton.Enabled = true;

            Application.Top.LayoutSubviews();
            Application.Refresh();
        }

        Application.MainLoop.Invoke(async () =>
        {
            var (restored, restoredName) = await apiService.RestoreSessionAsync();
            if (restored && restoredName != null)
            {
                _usernameText.Text = restoredName;
                await LoadData();
            }
            else if (!string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(initPass))
            {
                _statusLabel!.Text = "Auto-logging in...";
                Application.Refresh();
                
                var (success, msg) = await apiService.LoginAsync(config.Username, initPass);
                if (success)
                {
                    await LoadData();
                }
                else
                {
                    _statusLabel.Text = $"Auto-login failed: {msg}";
                }
            }
        });

        loginButton.Clicked += async () =>
        {
            var user = _usernameText.Text.ToString()!;
            var pass = _passwordText.Text.ToString()!;
            var (success, msg) = await apiService.LoginAsync(user, pass);
            if (success)
            {
                if (rememberMeCheck.Checked == true)
                    SaveConfig(); // Saves credentials AND current UI settings
                else
                {
                    if (File.Exists(ConfigFileName)) File.Delete(ConfigFileName);
                    if (File.Exists("session.cookie")) File.Delete("session.cookie");
                }
                await LoadData();
            }
            else
                MessageBox.ErrorQuery("Login Failed", msg, "OK");
        };

        backupButton.Clicked += async () =>
        {
            string file = $"VRChatFriends_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json";
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(friendsToDisplay, new JsonSerializerOptions { WriteIndented = true }));
            MessageBox.Query("Backup Saved", $"Saved {friendsToDisplay.Count} friends to:\n{Path.GetFullPath(file)}", "OK");
        };

        refreshButton.Clicked += async () => await LoadData();

        markAllButton.Clicked += () => { for (int i = 0; i < friendsListView.Source.Count; i++) friendsListView.Source.SetMark(i, true); friendsListView.SetNeedsDisplay(); };
        unmarkAllButton.Clicked += () => { for (int i = 0; i < friendsListView.Source.Count; i++) friendsListView.Source.SetMark(i, false); friendsListView.SetNeedsDisplay(); };

        unfriendButton.Clicked += async () =>
        {
            var marked = new List<SafeLimitedUserFriend>();
            for (int i = 0; i < friendsToDisplay.Count; i++)
                if (friendsListView.Source.IsMarked(i))
                    marked.Add(friendsToDisplay[i]);

            if (marked.Count == 0) { MessageBox.ErrorQuery("Nothing marked", "Select friends first.", "OK"); return; }
            if (MessageBox.Query("Confirm Unfriend", $"Unfriend {marked.Count} people?\nCannot be undone.", "Yes", "Cancel") != 0) return;

            var cts = new CancellationTokenSource();
            _currentCts = cts;
            _isPaused = false;

            StartOperation();

            int success = 0;
            bool cancelled = false;

            try
            {
                for (int i = 0; i < marked.Count; i++)
                {
                    await WaitIfPausedOrCancelled(cts.Token);

                    if (cts.Token.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    var f = marked[i];
                    _statusLabel!.Text = _isPaused ? $"Paused at {i + 1}/{marked.Count}: {f.DisplayName}" : $"({i + 1}/{marked.Count}) Unfriending {f.DisplayName}...";
                    _progressBar!.Fraction = (i + 1f) / marked.Count;
                    Application.Refresh();

                    try
                    {
                        await apiService.UnfriendAsync(f.Id);
                        success++;
                    }
                    catch { }

                    if (i < marked.Count - 1)
                        await Task.Delay(new Random().Next(minDelaySeconds, maxDelaySeconds + 1) * 1000, cts.Token);
                }
            }
            finally
            {
                EndOperation();
                _currentCts = null;

                string finalMsg = cancelled 
                    ? $"Unfriend cancelled ({success}/{marked.Count} completed)" 
                    : $"Unfriended {success}/{marked.Count}";

                _statusLabel!.Text = finalMsg;
                MessageBox.Query("Complete", $"{finalMsg}\nList will now refresh.", "OK");
                if (!cancelled) ShowNotification("Unfriend Complete", $"{success}/{marked.Count} removed");
                
                await LoadData();
            }
        };

        readdButton.Clicked += () =>
        {
            var dlg = new OpenDialog("Select Backup JSON", "Choose a previously saved friends list") { AllowsMultipleSelection = false };
            dlg.AllowedFileTypes = new[] { "json" };
            Application.Run(dlg);

            if (dlg.Canceled || dlg.FilePath == null) return;

            try
            {
                var json = File.ReadAllText(dlg.FilePath.ToString()!);
                var toAdd = JsonSerializer.Deserialize<List<SafeLimitedUserFriend>>(json);
                if (toAdd == null || toAdd.Count == 0) { MessageBox.ErrorQuery("Empty file", "No users found.", "OK"); return; }

                if (MessageBox.Query("Re-add Friends", $"Send friend requests to {toAdd.Count} users?", "Yes", "Cancel") != 0) return;

                var cts = new CancellationTokenSource();
                _currentCts = cts;
                _isPaused = false;

                StartOperation();

                Application.MainLoop.Invoke(async () =>
                {
                    int sent = 0;
                    bool cancelled = false;

                    try
                    {
                        for (int i = 0; i < toAdd.Count; i++)
                        {
                            await WaitIfPausedOrCancelled(cts.Token);

                            if (cts.Token.IsCancellationRequested)
                            {
                                cancelled = true;
                                break;
                            }

                            var f = toAdd[i];
                            _statusLabel!.Text = _isPaused ? $"Paused at {i + 1}/{toAdd.Count}: {f.DisplayName}" : $"({i + 1}/{toAdd.Count}) Re-adding {f.DisplayName}...";
                            _progressBar!.Fraction = (i + 1f) / toAdd.Count;
                            Application.Refresh();

                            var (ok, _) = await apiService.SendFriendRequestAsync(f.Id);
                            if (ok) sent++;

                            if (i < toAdd.Count - 1)
                                await Task.Delay(new Random().Next(minDelaySeconds, maxDelaySeconds + 1) * 1000, cts.Token);
                        }
                    }
                    finally
                    {
                        EndOperation();
                        _currentCts = null;

                        string finalMsg = cancelled 
                            ? $"Re-add cancelled ({sent}/{toAdd.Count} sent)" 
                            : $"Sent {sent}/{toAdd.Count} friend requests";

                        _statusLabel!.Text = finalMsg;
                        MessageBox.Query("Complete", $"{finalMsg}\nList will now refresh.", "OK");
                        if (!cancelled) ShowNotification("Re-add Complete", $"{sent}/{toAdd.Count} requests sent");

                        await LoadData();
                    }
                });
            }
            catch (Exception ex)
            {
                EndOperation();
                MessageBox.ErrorQuery("Error", ex.Message, "OK");
            }
        };

        Application.Run();
        Application.Shutdown();
    }
}