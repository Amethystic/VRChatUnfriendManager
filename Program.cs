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
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Text.Json.Serialization;

// This helper class is correct and does not need changes.
public class SafeLimitedUserFriend
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; }

    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; set; }
}

// The API service class is correct and does not need changes.
public class VRChatApiService
{
    private const string AppUserAgent = "VRChatUnfriendManager/1.1 (github.com/Amethystic/VRChatUnfriendManager)";
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
            {
                return (false, TryParseApiErrorMessage(responseBody, "Login Error"));
            }

            var userDoc = JsonDocument.Parse(responseBody);
            
            if (userDoc.RootElement.TryGetProperty("requiresTwoFactorAuth", out var requires2fa) && requires2fa.ValueKind == JsonValueKind.Array)
            {
                string? code = await Program.GetTwoFactorCodeAsync();
                if (string.IsNullOrEmpty(code)) return (false, "2FA cancelled.");

                var jsonContent = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json");
                bool verified = false;

                var totpResponse = await _client.PostAsync("auth/twofactorauth/totp/verify", jsonContent);
                if (totpResponse.IsSuccessStatusCode) { verified = true; }
                else
                {
                    jsonContent = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json"); 
                    var emailResponse = await _client.PostAsync("auth/twofactorauth/emailotp/verify", jsonContent);
                    if (emailResponse.IsSuccessStatusCode) { verified = true; }
                    else
                    {
                        jsonContent = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json");
                        var otpResponse = await _client.PostAsync("auth/twofactorauth/otp/verify", jsonContent);
                        if (otpResponse.IsSuccessStatusCode) { verified = true; }
                    }
                }
                if (!verified) return (false, "2FA verification failed. The code may be incorrect or expired.");
            }
            
            var authCookie = _cookieContainer.GetCookies(_client.BaseAddress!)["auth"];
            if (authCookie == null) return (false, "CRITICAL: Failed to retrieve authentication cookie after login.");

            var twoFactorAuthCookie = _cookieContainer.GetCookies(_client.BaseAddress!)["twoFactorAuth"];
            
            var fullCookieHeader = $"auth={authCookie.Value}";
            if (twoFactorAuthCookie != null)
            {
                fullCookieHeader += $"; twoFactorAuth={twoFactorAuthCookie.Value}";
            }

            _apiConfig = new Configuration { UserAgent = AppUserAgent };
            _apiConfig.DefaultHeaders.Add("Cookie", fullCookieHeader);
            IsLoggedIn = true;
            
            await SaveSessionAsync(fullCookieHeader);
            return (true, "Login successful.");
        }
        catch (Exception ex)
        {
            return (false, "An unexpected error occurred during login.\n\nDetails: " + ex.Message);
        }
    }
    
    public async Task<(bool success, string? restoredUsername)> RestoreSessionAsync()
    {
        if (!System.IO.File.Exists(SessionCookieFileName)) return (false, null);

        var fullCookieHeader = await System.IO.File.ReadAllTextAsync(SessionCookieFileName);
        if (string.IsNullOrEmpty(fullCookieHeader)) return (false, null);

        var config = new Configuration {
            UserAgent = AppUserAgent,
        };
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
            try { System.IO.File.Delete(SessionCookieFileName); } catch {}
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
        List<SafeLimitedUserFriend> currentPageFriends;
        
        do
        {
            var response = await Friends.GetFriendsWithHttpInfoAsync(offset: offset, n: pageSize, offline: false);
            currentPageFriends = JsonSerializer.Deserialize<List<SafeLimitedUserFriend>>(response.RawContent, options) ?? new List<SafeLimitedUserFriend>();
            allFriends.AddRange(currentPageFriends);
            offset += pageSize;
        } 
        while (currentPageFriends.Count == pageSize);

        offset = 0;
        do
        {
            var response = await Friends.GetFriendsWithHttpInfoAsync(offset: offset, n: pageSize, offline: true);
            currentPageFriends = JsonSerializer.Deserialize<List<SafeLimitedUserFriend>>(response.RawContent, options) ?? new List<SafeLimitedUserFriend>();
            allFriends.AddRange(currentPageFriends);
            offset += pageSize;
        }
        while (currentPageFriends.Count == pageSize);

        return allFriends;
    }

    public async Task<HashSet<string>> GetFavoriteFriendIdsAsync()
    {
        if (!IsLoggedIn || _apiConfig == null) throw new InvalidOperationException("Not logged in.");

        var favoriteIds = new HashSet<string>();
        int offset = 0;
        const int pageSize = 100;
        List<Favorite> currentPageFavorites;

        do
        {
            currentPageFavorites = await Favorites.GetFavoritesAsync(type: "friend", n: pageSize, offset: offset);
            foreach (var favorite in currentPageFavorites)
            {
                favoriteIds.Add(favorite.FavoriteId);
            }
            offset += pageSize;
        } while (currentPageFavorites.Count == pageSize);

        return favoriteIds;
    }

    public async Task UnfriendAsync(string userId)
    {
        if (!IsLoggedIn || _apiConfig == null) throw new InvalidOperationException("Not logged in.");
        await Friends.UnfriendAsync(userId);
    }
    
    private static string TryParseApiErrorMessage(string responseBody, string title)
    {
        try {
            var errorDoc = JsonDocument.Parse(responseBody);
            return errorDoc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg) 
                ? msg.GetString()! : "Could not parse API error response.";
        } catch { return "Received an invalid (non-JSON) response from the API."; }
    }
    
    private static async Task SaveSessionAsync(string fullCookieHeader)
    {
        if (!string.IsNullOrEmpty(fullCookieHeader))
        {
            try { await System.IO.File.WriteAllTextAsync(SessionCookieFileName, fullCookieHeader); } catch { /* Ignore */ }
        }
    }
}

public class Program
{
    // --- DEFINITIVE FIX: Updated the delay range ---
    private static readonly int minDelaySeconds = 5;
    private static readonly int maxDelaySeconds = 10;
    private const string ConfigFileName = "user.config";

    public static void ShowNotification(string title, string message)
    {
        if (OperatingSystem.IsWindows()) new ToastContentBuilder().AddText(title).AddText(message).Show();
    }
    
    public static Task<string?> GetTwoFactorCodeAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        Application.MainLoop.Invoke(() => {
            var dialog = new Dialog("2FA Required", 60, 7);
            var label = new Label("Enter 6-digit code (Authenticator, Email, or Recovery):") { X = 1, Y = 1 };
            var codeText = new TextField("") { X = 1, Y = 2, Width = Dim.Fill(1) };
            var okButton = new Button("OK") { IsDefault = true };
            var cancelButton = new Button("Cancel");
            okButton.Clicked += () => { tcs.TrySetResult(codeText.Text?.ToString()); Application.RequestStop(); };
            cancelButton.Clicked += () => { tcs.TrySetResult(null); Application.RequestStop(); };
            dialog.Add(label, codeText); dialog.AddButton(okButton); dialog.AddButton(cancelButton);
            Application.Run(dialog);
        });
        return tcs.Task;
    }

    public static async Task Main()
    {
        string initialUsername = "";
        if (System.IO.File.Exists(ConfigFileName)) { try { initialUsername = await System.IO.File.ReadAllTextAsync(ConfigFileName); } catch { /* Ignore */ } }
        
        Application.Init();
        
        var neutralScheme = new ColorScheme { Normal = new Terminal.Gui.Attribute(Color.Gray, Color.Black), Focus = new Terminal.Gui.Attribute(Color.White, Color.Black), HotNormal = new Terminal.Gui.Attribute(Color.BrightCyan, Color.Black), HotFocus = new Terminal.Gui.Attribute(Color.BrightCyan, Color.Black), Disabled = new Terminal.Gui.Attribute(Color.DarkGray, Color.Black) };
        Colors.Base = neutralScheme; Colors.Dialog = neutralScheme; Colors.Menu = neutralScheme; Colors.TopLevel = neutralScheme;
        Colors.Error = new ColorScheme { Normal = new Terminal.Gui.Attribute(Color.White, Color.Red) };
        var top = Application.Top;
        var win = new Window("VRChat Unfriend Manager") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        top.Add(win);
        var usernameLabel = new Label("Username:") { X = 3, Y = 2 };
        var usernameText = new TextField(initialUsername) { X = 15, Y = 2, Width = 40 };
        var passwordLabel = new Label("Password:") { X = 3, Y = 4 };
        var passwordText = new TextField("") { X = 15, Y = 4, Width = 40, Secret = true };
        var loginButton = new Button("Login") { X = 15, Y = 6 };
        var rememberMeCheck = new CheckBox("Remember me") { X = Pos.Right(loginButton) + 5, Y = 6, Checked = true };
        
        var excludeFavoritesCheck = new CheckBox("Exclude Favorites") { X = 15, Y = Pos.Bottom(loginButton), Checked = true };
        
        var friendsFrame = new FrameView("Friends List") { X = 3, Y = Pos.Bottom(excludeFavoritesCheck) + 1, Width = Dim.Fill(3), Height = Dim.Percent(60) };
        
        var friendsListView = new ListView(new List<string>()) { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(1), AllowsMarking = true };
        friendsFrame.Add(friendsListView);
        var statusLabel = new Label("Status: Idle. Please log in or wait for session restore.") { X = 3, Y = Pos.Bottom(friendsFrame) + 1 };
        var unfriendButton = new Button("Unfriend Marked Friends") { X = 3, Y = Pos.Bottom(statusLabel) + 1, Enabled = false };
        var backupButton = new Button("Backup All Friends to JSON") { X = Pos.Right(unfriendButton) + 2, Y = Pos.Bottom(statusLabel) + 1, Enabled = false };
        var markAllButton = new Button("Mark All") { X = Pos.Right(backupButton) + 2, Y = Pos.Bottom(statusLabel) + 1, Enabled = false };
        var unmarkAllButton = new Button("Unmark All") { X = Pos.Right(markAllButton) + 2, Y = Pos.Bottom(statusLabel) + 1, Enabled = false };
        win.Add(usernameLabel, usernameText, passwordLabel, passwordText, loginButton, rememberMeCheck, excludeFavoritesCheck, friendsFrame, statusLabel, unfriendButton, backupButton, markAllButton, unmarkAllButton);

        var apiService = new VRChatApiService();
        
        List<SafeLimitedUserFriend> allFriendsCache = new List<SafeLimitedUserFriend>();
        HashSet<string> favoriteFriendIdsCache = new HashSet<string>();
        List<SafeLimitedUserFriend> friendsToDisplay = new List<SafeLimitedUserFriend>();

        void UpdateFriendsList()
        {
            if (excludeFavoritesCheck.Checked == true)
            {
                friendsToDisplay = allFriendsCache.Where(friend => !favoriteFriendIdsCache.Contains(friend.Id)).ToList();
                friendsListView.SetSource(friendsToDisplay.Select(f => f.DisplayName).ToList());
                statusLabel.Text = $"Status: Logged in. Displaying {friendsToDisplay.Count} non-favorite friends.";
                markAllButton.Text = "Mark All Non-Favorites";
            }
            else
            {
                friendsToDisplay = allFriendsCache;
                friendsListView.SetSource(friendsToDisplay.Select(f => f.DisplayName).ToList());
                statusLabel.Text = $"Status: Logged in. Displaying {friendsToDisplay.Count} total friends.";
                markAllButton.Text = "Mark All";
            }
            friendsListView.SetNeedsDisplay();
        }

        excludeFavoritesCheck.Toggled += (previous) => {
            if (apiService.IsLoggedIn)
            {
                UpdateFriendsList();
            }
        };

        async Task FinishLoginUiUpdate()
        {
            statusLabel.Text = "Status: Login successful. Fetching data..."; 
            Application.Refresh();
            try
            {
                favoriteFriendIdsCache = await apiService.GetFavoriteFriendIdsAsync();
                allFriendsCache = await apiService.GetAllFriendsAsync();

                if (rememberMeCheck.Checked) { try { await System.IO.File.WriteAllTextAsync(ConfigFileName, usernameText.Text.ToString()); } catch {} } 
                else { 
                    if(System.IO.File.Exists(ConfigFileName)) try { System.IO.File.Delete(ConfigFileName); } catch {}
                    if(System.IO.File.Exists("session.cookie")) try { System.IO.File.Delete("session.cookie"); } catch {}
                }
                
                UpdateFriendsList();
                
                unfriendButton.Enabled = true; backupButton.Enabled = true; markAllButton.Enabled = true; unmarkAllButton.Enabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery("Error Fetching Data", $"Could not process data from VRChat.\n\nDetails: {ex.Message}", "OK");
                statusLabel.Text = "Status: Error after login. Could not fetch data.";
            }
        }
        
        statusLabel.Text = "Status: Checking for saved session...";
        Application.MainLoop.Invoke(async () => {
            var (restored, restoredUsername) = await apiService.RestoreSessionAsync();
            if (restored)
            {
                statusLabel.Text = "Status: Session restored.";
                usernameText.Text = restoredUsername;
                await FinishLoginUiUpdate();
            } else { statusLabel.Text = "Status: No active session found. Please log in."; }
        });

        loginButton.Clicked += async () => {
            statusLabel.Text = "Status: Logging in..."; 
            Application.Refresh();
            
            var (success, message) = await apiService.LoginAsync(usernameText.Text.ToString(), passwordText.Text.ToString());

            if (success) {
                await FinishLoginUiUpdate();
            } else {
                MessageBox.ErrorQuery("Login Error", message, "OK");
                statusLabel.Text = "Status: Login failed.";
            }
        };

        backupButton.Clicked += async () => {
            string fileName = $"VRChatFriends_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json";
            try {
                await System.IO.File.WriteAllTextAsync(fileName, JsonSerializer.Serialize(friendsToDisplay, new JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Query("Success", $"Backup successful!\nSaved to: {Path.GetFullPath(fileName)}", "OK");
            } catch (Exception ex) { MessageBox.ErrorQuery("Backup Error", ex.Message, "OK"); }
        };

        markAllButton.Clicked += () => {
            if (friendsListView.Source == null) return;
            for (int i = 0; i < friendsListView.Source.Count; i++) friendsListView.Source.SetMark(i, true);
            friendsListView.SetNeedsDisplay();
        };

        unmarkAllButton.Clicked += () => {
            if (friendsListView.Source == null) return;
            for (int i = 0; i < friendsListView.Source.Count; i++) friendsListView.Source.SetMark(i, false);
            friendsListView.SetNeedsDisplay();
        };

        unfriendButton.Clicked += async () => {
            var friendsToUnfriend = new List<SafeLimitedUserFriend>();
            for(int i = 0; i < friendsToDisplay.Count; i++) {
                if(friendsListView.Source.IsMarked(i)) friendsToUnfriend.Add(friendsToDisplay[i]);
            }
            if(friendsToUnfriend.Count == 0) { MessageBox.ErrorQuery("No one selected", "You must mark at least one friend to unfriend.", "OK"); return; }
            int dialogResult = MessageBox.Query("Confirm Action", $"Are you SURE you want to unfriend {friendsToUnfriend.Count} marked friend(s)?\nThis cannot be undone.", "Yes, proceed", "Cancel");
            if (dialogResult == 0) {
                ShowNotification("VRChat Unfriend Manager", "The unfriending process has started.");
                int successCount = 0;
                for (int i = 0; i < friendsToUnfriend.Count; i++) {
                    var friend = friendsToUnfriend[i];
                    statusLabel.Text = $"({i + 1}/{friendsToUnfriend.Count}) Unfriending {friend.DisplayName}..."; Application.Refresh();
                    try {
                        await apiService.UnfriendAsync(friend.Id);
                        successCount++;
                        if (i < friendsToUnfriend.Count - 1) {
                            int delay = new Random().Next(minDelaySeconds, maxDelaySeconds + 1);
                            statusLabel.Text += $" Success! Waiting for {delay} seconds..."; Application.Refresh();
                            await Task.Delay(delay * 1000);
                        }
                    } catch (Exception ex) {
                        statusLabel.Text = $"Error unfriending {friend.DisplayName}. Skipping..."; Application.Refresh();
                        await Task.Delay(3000);
                    }
                }
                MessageBox.Query("Process Complete", $"Successfully unfriended {successCount} out of {friendsToUnfriend.Count} selected friends.", "OK");
                statusLabel.Text = "Status: Done. You can re-login to refresh the list.";
                ShowNotification("VRChat Unfriend Manager", $"Process Complete! Unfriended: {successCount}, Skipped: {friendsToUnfriend.Count - successCount}");
            }
        };

        Application.Run();
        Application.Shutdown();
    }
}