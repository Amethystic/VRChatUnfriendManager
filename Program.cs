// Add all necessary using statements for the VRChat API and the GUI library.
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;
using System.Text.Json;
using Terminal.Gui;

public class Program
{
    // --- Configuration ---
    private static readonly int minDelaySeconds = 20;
    private static readonly int maxDelaySeconds = 45;

    // --- NEW: Define a User-Agent for our application ---
    // You should replace the email with your own contact info (like a GitHub profile)
    // as per VRChat's API guidelines.
    private const string AppUserAgent = "VRChatUnfriendManager/1.1 github.com/YourUsername/ProjectRepo";

    public static void Main()
    {
        Application.Init(); 

        // (The color scheme setup remains the same)
        var neutralScheme = new ColorScheme {
            Normal = new Terminal.Gui.Attribute(Color.Gray, Color.Black),
            Focus = new Terminal.Gui.Attribute(Color.White, Color.Black),
            HotNormal = new Terminal.Gui.Attribute(Color.BrightCyan, Color.Black),
            HotFocus = new Terminal.Gui.Attribute(Color.BrightCyan, Color.Black),
            Disabled = new Terminal.Gui.Attribute(Color.DarkGray, Color.Black)
        };
        Colors.Base = neutralScheme; Colors.Dialog = neutralScheme; Colors.Menu = neutralScheme; Colors.TopLevel = neutralScheme;
        Colors.Error = new ColorScheme { Normal = new Terminal.Gui.Attribute(Color.White, Color.Red) };
        
        var top = Application.Top;
        var win = new Window("VRChat Unfriend Manager") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        top.Add(win);

        // (The UI Element definitions remain the same)
        var usernameLabel = new Label("Username:") { X = 3, Y = 2 };
        var usernameText = new TextField("") { X = 15, Y = 2, Width = 40 };
        var passwordLabel = new Label("Password:") { X = 3, Y = 4 };
        var passwordText = new TextField("") { X = 15, Y = 4, Width = 40, Secret = true };
        var loginButton = new Button("Login") { X = 15, Y = 6 };
        var friendsFrame = new FrameView("Friends (Favorites are automatically excluded from this list)") { X = 3, Y = 8, Width = Dim.Fill(3), Height = Dim.Percent(60) };
        var friendsListView = new ListView(new List<LimitedUserFriend>()) { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(1), AllowsMarking = true };
        friendsFrame.Add(friendsListView);
        var statusLabel = new Label("Status: Idle. Please log in.") { X = 3, Y = Pos.Bottom(friendsFrame) + 1 };
        var unfriendButton = new Button("Unfriend Marked Friends") { X = 3, Y = Pos.Bottom(statusLabel) + 1, Enabled = false };
        var backupButton = new Button("Backup All Friends to JSON") { X = Pos.Right(unfriendButton) + 2, Y = Pos.Bottom(statusLabel) + 1, Enabled = false };
        var markAllButton = new Button("Mark All Non-Favorites") { X = Pos.Right(backupButton) + 2, Y = Pos.Bottom(statusLabel) + 1, Enabled = false };
        var unmarkAllButton = new Button("Unmark All") { X = Pos.Right(markAllButton) + 2, Y = Pos.Bottom(statusLabel) + 1, Enabled = false };
        win.Add(usernameLabel, usernameText, passwordLabel, passwordText, loginButton, friendsFrame, statusLabel, unfriendButton, backupButton, markAllButton, unmarkAllButton);

        // --- Global State ---
        Configuration? authenticatedConfig = null;
        List<LimitedUserFriend> allFriends = new List<LimitedUserFriend>();
        List<LimitedUserFriend> nonFavoriteFriends = new List<LimitedUserFriend>();

        #region Event Handlers

        loginButton.Clicked += async () => {
            statusLabel.Text = "Status: Logging in..."; Application.Refresh();

            // --- THIS IS THE FIX ---
            // Add the UserAgent to the configuration when creating it.
            var config = new Configuration {
                Username = usernameText.Text.ToString(),
                Password = passwordText.Text.ToString(),
                UserAgent = AppUserAgent // Identify our application to the VRChat API
            };
            // --- END OF FIX ---

            var authApi = new AuthenticationApi(config);
            bool loggedIn = false;

            try { await authApi.GetCurrentUserAsync(); loggedIn = true; }
            catch (ApiException e) when (e.ErrorCode == 401 && e.ErrorContent != null && e.ErrorContent.ToString().Contains("Two-Factor"))
            {
                // (2FA logic remains the same)
                var code = "";
                var dialog = new Dialog("2FA Required", 60, 7);
                var label = new Label("Enter 6-digit code:") { X = 1, Y = 1 };
                var codeText = new TextField(code) { X = 1, Y = 2, Width = Dim.Fill(1) };
                var okButton = new Button("OK") { IsDefault = true };
                okButton.Clicked += () => { code = codeText.Text.ToString(); Application.RequestStop(); };
                dialog.Add(label, codeText); dialog.AddButton(okButton); Application.Run(dialog);
                
                try {
                    if(!string.IsNullOrEmpty(code) && (await authApi.Verify2FAAsync(new TwoFactorAuthCode { Code = code })).Verified) loggedIn = true;
                    else MessageBox.ErrorQuery("Error", "2FA verification failed.", "OK");
                } catch (Exception ex) { MessageBox.ErrorQuery("2FA Error", ex.Message, "OK"); }
            }
            catch (Exception ex) { MessageBox.ErrorQuery("Login Error", ex.Message, "OK"); }

            if (loggedIn)
            {
                authenticatedConfig = config;
                statusLabel.Text = "Status: Login successful! Fetching friends..."; Application.Refresh();
                
                var friendsApi = new FriendsApi(authenticatedConfig);
                allFriends = await friendsApi.GetFriendsAsync();
                
                nonFavoriteFriends = allFriends.Where(f => !(f.Tags.Contains("group_0") || f.Tags.Contains("group_1") || f.Tags.Contains("group_2"))).ToList();
                
                friendsListView.SetSource(nonFavoriteFriends.Select(f => f.DisplayName).ToList());
                unfriendButton.Enabled = true; backupButton.Enabled = true; markAllButton.Enabled = true; unmarkAllButton.Enabled = true;
                statusLabel.Text = $"Status: Found {nonFavoriteFriends.Count} non-favorite friends. Mark friends for removal.";
            }
            else { statusLabel.Text = "Status: Login failed."; }
        };
        
        // (All other button event handlers remain exactly the same)
        backupButton.Clicked += async () => { /* ... */ };
        markAllButton.Clicked += () => { /* ... */ };
        unmarkAllButton.Clicked += () => { /* ... */ };
        unfriendButton.Clicked += async () => { /* ... */ };

        #endregion

        // Hide the full implementation of the other buttons for brevity, as they are unchanged.
        #region Unchanged Event Handlers
        backupButton.Clicked += async () => {
            string fileName = $"VRChatFriends_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json";
            try {
                await System.IO.File.WriteAllTextAsync(fileName, JsonSerializer.Serialize(allFriends, new JsonSerializerOptions { WriteIndented = true }));
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
            var friendsToUnfriend = new List<LimitedUserFriend>();
            for(int i = 0; i < nonFavoriteFriends.Count; i++) {
                if(friendsListView.Source.IsMarked(i)) friendsToUnfriend.Add(nonFavoriteFriends[i]);
            }
            
            if(friendsToUnfriend.Count == 0) { MessageBox.ErrorQuery("No one selected", "You must mark at least one friend to unfriend.", "OK"); return; }

            int dialogResult = MessageBox.Query("Confirm Action", $"Are you SURE you want to unfriend {friendsToUnfriend.Count} marked friend(s)?\nThis cannot be undone.", "Yes, proceed", "Cancel");
            if (dialogResult == 0)
            {
                var friendsApi = new FriendsApi(authenticatedConfig);
                var random = new Random();
                int successCount = 0;

                for (int i = 0; i < friendsToUnfriend.Count; i++) {
                    var friend = friendsToUnfriend[i];
                    statusLabel.Text = $"({i + 1}/{friendsToUnfriend.Count}) Unfriending {friend.DisplayName}..."; Application.Refresh();
                    
                    try {
                        await friendsApi.UnfriendAsync(friend.Id);
                        successCount++;
                        if (i < friendsToUnfriend.Count - 1) {
                            int delay = random.Next(minDelaySeconds, maxDelaySeconds + 1);
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
            }
        };
        #endregion

        Application.Run();
        Application.Shutdown();
    }
}