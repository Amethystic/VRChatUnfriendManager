using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Numerics;
using System.Runtime.InteropServices;
using Raylib_cs;
using ImGuiNET;
using rlImGui_cs;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;
using File = System.IO.File;

#if WINDOWS
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
#endif

namespace VRChatUnfriendManager
{
    public static class Paths
    {
        public static readonly string AppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRChatUnfriendManager");
        public static readonly string CookieFile = Path.Combine(AppDataFolder, "session.cookie");
        public static readonly string ConfigFile = Path.Combine(AppDataFolder, "user.config");

        public static void EnsureExists()
        {
            if (!Directory.Exists(AppDataFolder))
                Directory.CreateDirectory(AppDataFolder);
        }
    }

    public class SafeLimitedUserFriend
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string LastLogin { get; set; } = "";
    }

    public class AppConfig
    {
        public string Username { get; set; } = "";
        public string EncodedPassword { get; set; } = "";
        public string Cookie { get; set; } = "";
        public bool RememberMe { get; set; } = true;
        public bool ExcludeFavorites { get; set; } = true;
        public bool InactiveEnabled { get; set; } = false;
        public int InactiveValue { get; set; } = 3;
        public int InactiveUnitIndex { get; set; } = 1;
        public int SortOptionIndex { get; set; } = 0;
        public bool AutoUnfriendEnabled { get; set; } = false;
        public int AutoUnfriendHour { get; set; } = 3;
        public int AutoUnfriendMinute { get; set; } = 0;
        public int AutoUnfriendMode { get; set; } = 0;
    }

    public class VRChatApiService
    {
        private const string UA = "VRChatUnfriendManager/3.0";
        private static readonly Uri BaseUri = new("https://api.vrchat.cloud/api/1/");
        private readonly HttpClient client;
        private readonly CookieContainer cookies = new();
        private Configuration? cfg;

        private TaskCompletionSource<string?>? tfaTcs;
        private string tfaCode = "";
        private bool show2FADialog = false;

        public VRChatApiService()
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                UseCookies = true,
                AllowAutoRedirect = true
            };
            client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UA);
        }

        private void SaveCookies()
        {
            Paths.EnsureExists();

            var authCookie = cookies.GetCookies(BaseUri)["auth"];
            var tfaCookie = cookies.GetCookies(BaseUri)["twoFactorAuth"];

            if (authCookie == null) return;

            var fullCookie = $"auth={authCookie.Value}";
            if (tfaCookie != null && !string.IsNullOrEmpty(tfaCookie.Value))
                fullCookie += $"; twoFactorAuth={tfaCookie.Value}";

            try { File.WriteAllText(Paths.CookieFile, fullCookie); }
            catch { }

            Program.config.Cookie = fullCookie;
            Program.SaveConfig();
        }

        private async Task<bool> TestSessionAsync()
        {
            if (cfg == null) return false;
            try
            {
                using var test = new HttpClient();
                test.DefaultRequestHeaders.UserAgent.ParseAdd(UA);
                if (cfg.DefaultHeaders.TryGetValue("Cookie", out var c))
                    test.DefaultRequestHeaders.Add("Cookie", c);

                var r = await test.GetAsync("https://api.vrchat.cloud/api/1/auth/user");
                if (!r.IsSuccessStatusCode) return false;
                var body = await r.Content.ReadAsStringAsync();
                return body.Contains("\"id\"", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private async Task<string?> GetCurrentDisplayNameAsync()
        {
            if (cfg == null) return null;
            try
            {
                var user = await new AuthenticationApi(cfg).GetCurrentUserAsync();
                return user.DisplayName ?? user.Username;
            }
            catch { return null; }
        }

        public async Task<(bool success, string? displayName)> RestoreSessionFromDiskOrConfigAsync()
        {
            if (!string.IsNullOrWhiteSpace(Program.config.Cookie) && Program.config.Cookie.Contains("auth="))
            {
                cfg = new Configuration { UserAgent = UA };
                cfg.DefaultHeaders["Cookie"] = Program.config.Cookie.Trim();
                if (await TestSessionAsync())
                    return (true, await GetCurrentDisplayNameAsync() ?? "You");
            }

            if (File.Exists(Paths.CookieFile))
            {
                var cookie = await File.ReadAllTextAsync(Paths.CookieFile);
                if (!string.IsNullOrWhiteSpace(cookie) && cookie.Contains("auth="))
                {
                    cfg = new Configuration { UserAgent = UA };
                    cfg.DefaultHeaders["Cookie"] = cookie.Trim();
                    if (await TestSessionAsync())
                    {
                        Program.config.Cookie = cookie.Trim();
                        Program.SaveConfig();
                        return (true, await GetCurrentDisplayNameAsync() ?? "You");
                    }
                }
            }

            if (Program.config.RememberMe && 
                !string.IsNullOrEmpty(Program.config.Username) &&
                !string.IsNullOrEmpty(Program.config.EncodedPassword))
            {
                var pass = Encoding.UTF8.GetString(Convert.FromBase64String(Program.config.EncodedPassword));
                var (success, name, error) = await LoginWithCredentialsAsync(Program.config.Username, pass);
                if (success && name != null) return (true, name);
            }

            return (false, null);
        }

        private string ExtractCsrfToken(string html)
        {
            const string marker = "name=\"csrf_token\" value=\"";
            int start = html.IndexOf(marker);
            if (start == -1) return "";
            start += marker.Length;
            int end = html.IndexOf('"', start);
            return end == -1 ? "" : html.Substring(start, end - start);
        }

        public async Task<(bool success, string? displayName, string? error)> LoginWithCredentialsAsync(string username, string password)
        {
            try
            {
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", UA);

                var loginPageResp = await client.GetAsync("https://vrchat.com/login");
                var loginPageHtml = await loginPageResp.Content.ReadAsStringAsync();
                var csrfToken = ExtractCsrfToken(loginPageHtml);

                if (string.IsNullOrEmpty(csrfToken))
                {
                    Console.WriteLine("CSRF token not found!");
                    return (false, null, "Failed to get login page (CSRF token missing)");
                }

                var cookiesHeader = loginPageResp.Headers
                    .FirstOrDefault(h => h.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                    .Value?.FirstOrDefault()?.Split(';')[0];

                var formData = new Dictionary<string, string>
                {
                    { "username", username },
                    { "password", password },
                    { "csrf_token", csrfToken }
                };

                var content = new FormUrlEncodedContent(formData);

                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", UA);
                client.DefaultRequestHeaders.Add("Referer", "https://vrchat.com/login");
                client.DefaultRequestHeaders.Add("Origin", "https://vrchat.com");
                if (!string.IsNullOrEmpty(cookiesHeader))
                    client.DefaultRequestHeaders.Add("Cookie", cookiesHeader);

                var response = await client.PostAsync("https://vrchat.com/api/1/auth/user/login", content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine("=== LOGIN FAILED ===");
                    Console.WriteLine($"Status: {response.StatusCode}");
                    Console.WriteLine($"Body: {body}");
                    return (false, null, $"Login failed ({(int)response.StatusCode})\n{body}");
                }

                var authCookieHeader = response.Headers
                    .FirstOrDefault(h => h.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                    .Value?.FirstOrDefault(c => c.Contains("auth="));

                if (string.IsNullOrEmpty(authCookieHeader))
                    return (false, null, "No auth cookie received");

                var authPart = authCookieHeader.Split(';')[0];
                var fullCookie = authPart;

                var tfaPart = response.Headers
                    .FirstOrDefault(h => h.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                    .Value?.FirstOrDefault(c => c.Contains("twoFactorAuth="))
                    ?.Split(';')[0];

                if (!string.IsNullOrEmpty(tfaPart))
                    fullCookie += $"; {tfaPart}";

                cfg = new Configuration { UserAgent = UA };
                cfg.DefaultHeaders["Cookie"] = fullCookie;
                SaveCookies();

                var user = await new AuthenticationApi(cfg).GetCurrentUserAsync();
                return (true, user.DisplayName ?? user.Username, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Login Exception: {ex}");
                return (false, null, $"Error: {ex.Message}");
            }
        }

        private Task<string?> Request2FACodeAsync()
        {
            tfaCode = "";
            tfaTcs = new TaskCompletionSource<string?>();
            show2FADialog = true;
            return tfaTcs.Task;
        }

        public void Draw2FADialog()
        {
            if (!show2FADialog || tfaTcs == null) return;

            ImGui.OpenPopup("2FA Required");
            ImGui.SetNextWindowPos(new Vector2(Raylib.GetScreenWidth() / 2f, Raylib.GetScreenHeight() / 2f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            if (ImGui.BeginPopupModal("2FA Required", ref show2FADialog, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
            {
                ImGui.Text("Two-Factor Authentication Required");
                ImGui.Separator();
                ImGui.TextWrapped("Enter your 2FA code:");
                ImGui.SetNextItemWidth(200);
                ImGui.InputText("##2fa", ref tfaCode, 10, ImGuiInputTextFlags.CharsDecimal);

                if ((ImGui.IsItemFocused() && Raylib.IsKeyPressed(KeyboardKey.Enter)) || ImGui.Button("Submit"))
                {
                    tfaTcs.SetResult(tfaCode.Trim());
                    tfaTcs = null;
                    show2FADialog = false;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    tfaTcs.SetResult(null);
                    tfaTcs = null;
                    show2FADialog = false;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        public FriendsApi Friends => cfg != null ? new FriendsApi(cfg) : throw new InvalidOperationException("Not logged in");
        public FavoritesApi Favorites => cfg != null ? new FavoritesApi(cfg) : throw new InvalidOperationException("Not logged in");

        public async Task UnfriendAsync(string id) => await Friends.UnfriendAsync(id);

        public async Task<List<SafeLimitedUserFriend>> GetAllFriendsAsync()
        {
            var list = new List<SafeLimitedUserFriend>();
            for (int offset = 0; ; offset += 100)
            {
                var page = await Friends.GetFriendsAsync(offset: offset, n: 100, offline: false);
                list.AddRange(page.Select(u => new SafeLimitedUserFriend
                {
                    Id = u.Id,
                    DisplayName = u.DisplayName ?? "Unknown",
                    LastLogin = u.LastLogin?.ToString("o") ?? ""
                }));
                if (page.Count < 100) break;
            }
            for (int offset = 0; ; offset += 100)
            {
                var page = await Friends.GetFriendsAsync(offset: offset, n: 100, offline: true);
                list.AddRange(page.Select(u => new SafeLimitedUserFriend
                {
                    Id = u.Id,
                    DisplayName = u.DisplayName ?? "Unknown",
                    LastLogin = u.LastLogin?.ToString("o") ?? ""
                }));
                if (page.Count < 100) break;
            }
            return list;
        }

        public async Task<HashSet<string>> GetFavoriteFriendIdsAsync()
        {
            var set = new HashSet<string>();
            for (int offset = 0; ; offset += 100)
            {
                var page = await Favorites.GetFavoritesAsync(type: "friend", n: 100, offset: offset);
                foreach (var f in page) set.Add(f.FavoriteId);
                if (page.Count < 100) break;
            }
            return set;
        }
    }

    class Program
    {
        static VRChatApiService api = new();
        static List<SafeLimitedUserFriend> friends = new();
        static HashSet<string> favorites = new();
        static List<SafeLimitedUserFriend> shown = new();
        static HashSet<int> selected = new();
        static string user = "", pass = "";
        static string loggedInAs = "";
        static bool remember = true;
        static bool hideFavs = true;
        static bool inactiveOn = false;
        static int inactiveVal = 3;
        static int inactiveUnit = 1;
        static int sort = 0;
        static string status = "Starting up...";
        static bool working = false;
        static bool isUnfriending = false;
        static bool isPaused = false;
        static int unfriendTotal = 0;
        static int unfriendDone = 0;
        static CancellationTokenSource? unfriendCts;
        public static AppConfig config = new();
        static bool autoRunning = false;
        static CancellationTokenSource? autoCts;
        static readonly string[] units = { "Days", "Months", "Years" };
        static readonly string[] sorts = { "Oldest", "Newest", "A-Z", "Z-A" };
        static readonly string[] autoModes = { "Inactive Only (3+ mo)", "All Shown", "Marked Only" };
        static bool isLoggedIn = false;
        static bool sessionRestored = false;
        
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]   private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_HIDE = 0;

        static async Task Main()
        {
            Paths.EnsureExists();
            Raylib.InitWindow(1280, 800, "VRChat Unfriend Manager v3");
            Raylib.SetTargetFPS(60);
            rlImGui.Setup(true);

            LoadConfig();

            user = config.Username;
            remember = config.RememberMe;
            hideFavs = config.ExcludeFavorites;
            inactiveOn = config.InactiveEnabled;
            inactiveVal = config.InactiveValue;
            inactiveUnit = config.InactiveUnitIndex;
            sort = config.SortOptionIndex;

            bool firstFrame = true;

            while (!Raylib.WindowShouldClose())
            {
                rlImGui.Begin();
                Raylib.BeginDrawing();
                Raylib.ClearBackground(new Color(20, 20, 30, 255));

                ImGui.SetNextWindowPos(Vector2.Zero);
                ImGui.SetNextWindowSize(new Vector2(Raylib.GetScreenWidth(), Raylib.GetScreenHeight()));
                ImGui.Begin("Main", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar);

                if (firstFrame)
                {
                    firstFrame = false;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        var (restored, name) = await api.RestoreSessionFromDiskOrConfigAsync();
                        if (restored && name != null)
                        {
                            loggedInAs = name;
                            isLoggedIn = true;
                            sessionRestored = true;
                            status = $"Welcome back, {name}";
                            await Refresh();
                            if (config.AutoUnfriendEnabled) StartAutoScheduler();
                        }
                        else
                        {
                            status = "Login required";
                        }
                    });
                }

                if (sessionRestored || isLoggedIn)
                {
                    DrawMainUI();
                }
                else
                {
                    DrawLoginScreen();
                }

                api.Draw2FADialog();
                ImGui.End();
                rlImGui.End();
                Raylib.EndDrawing();
            }

            SaveConfig();
            rlImGui.Shutdown();
            Raylib.CloseWindow();
        }

        static void DrawLoginScreen()
        {
#if RELEASE
            var hWnd = GetConsoleWindow();
            if (hWnd != IntPtr.Zero) ShowWindow(hWnd, SW_HIDE);
#endif
            
            ImGui.Text("VRChat Unfriend Manager v3");
            ImGui.Separator();

            if (status.Contains("Login failed") || status.Contains("Wrong") || status.Contains("CSRF") || status.Contains("cookie"))
                ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), status);
            else
                ImGui.Text(status);

            ImGui.InputText("Username", ref user, 100);
            ImGui.InputText("Password", ref pass, 100, ImGuiInputTextFlags.Password);
            ImGui.Checkbox("Remember me", ref remember);

            ImGui.SameLine();
            if (ImGui.Button("Login") && !working)
            {
                working = true;
                status = "Logging in...";
                _ = Task.Run(async () =>
                {
                    var (success, name, error) = await api.LoginWithCredentialsAsync(user, pass);
                    if (success && name != null)
                    {
                        loggedInAs = name;
                        isLoggedIn = true;
                        sessionRestored = true;
                        if (remember)
                        {
                            config.Username = user;
                            config.EncodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(pass));
                            config.RememberMe = true;
                            SaveConfig();
                        }
                        await Refresh();
                        status = $"Logged in as {name}";
                    }
                    else
                    {
                        status = error ?? "Login failed";
                    }
                    working = false;
                });
            }
        }

        static void DrawMainUI()
        {
            
            if (ImGui.BeginTabBar("Tabs"))
            {
                if (ImGui.BeginTabItem("Unfriend Manager"))
                {
                    ImGui.Text("VRChat Unfriend Manager v3");

                    if (isLoggedIn)
                    {
                        ImGui.TextColored(new Vector4(0, 1, 0, 1), $"Logged in as: {loggedInAs}");
                        if (ImGui.Button("Logout"))
                        {
                            File.Delete(Paths.CookieFile);
                            config.Cookie = "";
                            SaveConfig();
                            api = new VRChatApiService();
                            friends.Clear(); favorites.Clear(); selected.Clear();
                            loggedInAs = ""; isLoggedIn = false; sessionRestored = false;
                            status = "Logged out";
                        }
                    }

                    if (isLoggedIn)
                    {
                        if (ImGui.Checkbox("Hide Favorites", ref hideFavs)) { config.ExcludeFavorites = hideFavs; SaveConfig(); }
                        ImGui.SameLine();
                        if (ImGui.Checkbox("Inactive >=", ref inactiveOn)) { config.InactiveEnabled = inactiveOn; SaveConfig(); }

                        if (inactiveOn)
                        {
                            ImGui.SameLine(); ImGui.SetNextItemWidth(80);
                            if (ImGui.InputInt("##val", ref inactiveVal)) if (ImGui.IsItemDeactivatedAfterEdit()) { config.InactiveValue = inactiveVal; SaveConfig(); }
                            ImGui.SameLine();
                            if (ImGui.Combo("##unit", ref inactiveUnit, units, units.Length)) { config.InactiveUnitIndex = inactiveUnit; SaveConfig(); }
                        }

                        ImGui.SameLine();
                        if (ImGui.Combo("Sort", ref sort, sorts, sorts.Length)) { config.SortOptionIndex = sort; SaveConfig(); }

                        ImGui.Separator();
                        ImGui.Text(status);
                        if (working && !isUnfriending) ImGui.ProgressBar(-1f, new Vector2(-1, 20), "");

                        if (ImGui.BeginChild("list", new Vector2(0, -50), ImGuiChildFlags.Borders))
                        {
                            shown.Clear();
                            var temp = friends.ToList();
                            if (hideFavs) temp = temp.Where(f => !favorites.Contains(f.Id)).ToList();
                            if (inactiveOn && inactiveVal > 0)
                            {
                                var cutoff = inactiveUnit switch
                                {
                                    0 => DateTime.UtcNow.AddDays(-inactiveVal),
                                    1 => DateTime.UtcNow.AddMonths(-inactiveVal),
                                    _ => DateTime.UtcNow.AddYears(-inactiveVal)
                                };
                                temp = temp.Where(f => string.IsNullOrEmpty(f.LastLogin) || DateTime.Parse(f.LastLogin) < cutoff).ToList();
                            }
                            temp = sort switch
                            {
                                0 => temp.OrderBy(f => string.IsNullOrEmpty(f.LastLogin) ? DateTime.MinValue : DateTime.Parse(f.LastLogin)).ToList(),
                                1 => temp.OrderByDescending(f => string.IsNullOrEmpty(f.LastLogin) ? DateTime.MinValue : DateTime.Parse(f.LastLogin)).ToList(),
                                2 => temp.OrderBy(f => f.DisplayName).ToList(),
                                _ => temp.OrderByDescending(f => f.DisplayName).ToList()
                            };
                            shown = temp;

                            for (int i = 0; i < shown.Count; i++)
                            {
                                var ago = string.IsNullOrEmpty(shown[i].LastLogin) ? "never" : Ago(DateTime.Parse(shown[i].LastLogin));
                                bool sel = selected.Contains(i);
                                if (ImGui.Selectable($"{shown[i].DisplayName,-40} {ago}", sel))
                                {
                                    if (Raylib.IsKeyDown(KeyboardKey.LeftControl))
                                        _ = sel ? selected.Remove(i) : selected.Add(i);
                                    else { selected.Clear(); selected.Add(i); }
                                }
                            }
                            ImGui.EndChild();
                        }

                        if (ImGui.Button("Mark All")) for (int i = 0; i < shown.Count; i++) selected.Add(i);
                        ImGui.SameLine(); if (ImGui.Button("Unmark All")) selected.Clear();
                        ImGui.SameLine(); if (ImGui.Button("Refresh")) _ = Refresh();
                        ImGui.SameLine(); if (ImGui.Button("Backup JSON")) File.WriteAllText($"backup_{DateTime.Now:yyyyMMdd_HHmmss}.json", JsonSerializer.Serialize(shown, new JsonSerializerOptions { WriteIndented = true }));

                        string btn = isUnfriending ? (isPaused ? "Resume" : "Pause") : $"Unfriend Selected ({selected.Count})";
                        if (ImGui.Button(btn) && selected.Count > 0)
                            if (isUnfriending) isPaused = !isPaused; else ImGui.OpenPopup("Confirm");

                        if (ImGui.BeginPopupModal("Confirm", ImGuiWindowFlags.AlwaysAutoResize))
                        {
                            ImGui.Text($"{selected.Count} users will be unfriended permanently.");
                            if (ImGui.Button("Yes, do it")) { _ = Task.Run(StartUnfriendProcess); ImGui.CloseCurrentPopup(); }
                            ImGui.SameLine();
                            if (ImGui.Button("No")) ImGui.CloseCurrentPopup();
                            ImGui.EndPopup();
                        }

                        if (isUnfriending)
                        {
                            float p = unfriendTotal > 0 ? unfriendDone / (float)unfriendTotal : 0f;
                            ImGui.ProgressBar(p, new Vector2(-1, 35), $"{unfriendDone}/{unfriendTotal}");
                            ImGui.TextColored(new Vector4(1, 0.8f, 0, 1), isPaused ? "PAUSED" : "Unfriending...");
                            if (ImGui.Button("Cancel")) { unfriendCts?.Cancel(); isUnfriending = false; isPaused = false; }
                        }
                    }
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Auto Settings"))
                {
                    ImGui.Text("Auto-Unfriend Scheduler");
                    ImGui.Separator();

                    bool autoEnabled = config.AutoUnfriendEnabled;
                    int autoHour = config.AutoUnfriendHour;
                    int autoMinute = config.AutoUnfriendMinute;
                    int autoMode = config.AutoUnfriendMode;

                    if (ImGui.Checkbox("Enable Auto-Unfriend", ref autoEnabled))
                    {
                        config.AutoUnfriendEnabled = autoEnabled;
                        SaveConfig();
                        if (config.AutoUnfriendEnabled) StartAutoScheduler();
                        else autoCts?.Cancel();
                    }

                    ImGui.BeginDisabled(!config.AutoUnfriendEnabled);

                    ImGui.Text("Run daily at:");
                    ImGui.SameLine(); ImGui.SetNextItemWidth(60);
                    if (ImGui.InputInt("Hour", ref autoHour))
                    {
                        autoHour = Math.Clamp(autoHour, 0, 23);
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        config.AutoUnfriendHour = autoHour;
                        SaveConfig();
                    }

                    ImGui.SameLine(); ImGui.Text(":");
                    ImGui.SameLine(); ImGui.SetNextItemWidth(60);
                    if (ImGui.InputInt("Minute", ref autoMinute))
                    {
                        autoMinute = Math.Clamp(autoMinute, 0, 59);
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        config.AutoUnfriendMinute = autoMinute;
                        SaveConfig();
                    }

                    if (ImGui.Combo("Mode", ref autoMode, autoModes, autoModes.Length))
                    {
                        config.AutoUnfriendMode = autoMode;
                        SaveConfig();
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("Test Now")) _ = RunAutoUnfriend();

                    ImGui.TextColored(new Vector4(0.5f, 1, 0.5f, 1), autoRunning ? "RUNNING NOW" : "Stopped");
                    ImGui.EndDisabled();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }

        static async Task Refresh()
        {
            working = true; status = "Loading friends...";
            try
            {
                favorites = await api.GetFavoriteFriendIdsAsync();
                friends = await api.GetAllFriendsAsync();
                status = $"Loaded {friends.Count} friends";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Refresh failed: {ex}");
                status = "Session expired - please re-login";
                isLoggedIn = false;
                sessionRestored = false;
            }
            selected.Clear();
            working = false;
        }

        static async Task StartUnfriendProcess()
        {
            isUnfriending = true; isPaused = false;
            unfriendTotal = selected.Count; unfriendDone = 0;
            unfriendCts = new CancellationTokenSource();
            var list = selected.Select(i => shown[i]).ToList();

            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    while (isPaused && !unfriendCts.Token.IsCancellationRequested)
                        await Task.Delay(200, unfriendCts.Token);
                    if (unfriendCts.Token.IsCancellationRequested) break;

                    status = $"Unfriending {list[i].DisplayName}...";
                    try { await api.UnfriendAsync(list[i].Id); unfriendDone++; }
                    catch { }
                    if (i < list.Count - 1)
                        await Task.Delay(Random.Shared.Next(7000, 13000), unfriendCts.Token);
                }
            }
            finally
            {
                isUnfriending = false; isPaused = false;
                status = unfriendDone == unfriendTotal ? "All done!" : "Cancelled";
                ShowToast("Unfriend Complete", $"{unfriendDone} users removed");
                selected.Clear();
                await Refresh();
            }
        }

        static async Task RunAutoUnfriend()
        {
            await Refresh();
            var targets = config.AutoUnfriendMode switch
            {
                0 => shown.Where(f => string.IsNullOrEmpty(f.LastLogin) || DateTime.Parse(f.LastLogin) < DateTime.UtcNow.AddMonths(-3)).ToList(),
                1 => shown.ToList(),
                _ => shown.Where((_, i) => selected.Contains(i)).ToList()
            };

            if (targets.Count == 0) { status = "Auto: nothing to remove"; return; }

            autoRunning = true;
            for (int i = 0; i < targets.Count; i++)
            {
                try { await api.UnfriendAsync(targets[i].Id); } catch { }
                status = $"AUTO: {i + 1}/{targets.Count}";
                await Task.Delay(Random.Shared.Next(7000, 13000));
            }
            ShowToast("Auto Unfriend", $"{targets.Count} removed");
            await Refresh();
            autoRunning = false;
        }

        static void StartAutoScheduler()
        {
            autoCts?.Cancel();
            autoCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!autoCts.IsCancellationRequested && config.AutoUnfriendEnabled)
                {
                    var now = DateTime.Now;
                    var target = new DateTime(now.Year, now.Month, now.Day, config.AutoUnfriendHour, config.AutoUnfriendMinute, 0);
                    if (target <= now) target = target.AddDays(1);
                    var delay = target - now;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, autoCts.Token);
                    await RunAutoUnfriend();
                }
            });
        }

        static string Ago(DateTime dt)
        {
            var span = DateTime.UtcNow - dt.ToUniversalTime();
            if (span.TotalDays < 1) return "today";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays}d";
            if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30.4)}mo";
            return $"{(int)(span.TotalDays / 365.25)}y";
        }

        static void ShowToast(string title, string msg)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            try
            {
                var xml = $"<toast><visual><binding template='ToastGeneric'><text>{title}</text><text>{msg}</text></binding></visual></toast>";
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                ToastNotificationManager.CreateToastNotifier("VRChat Unfriend Manager").Show(new ToastNotification(doc));
            }
            catch { }
        }

        static void LoadConfig()
        {
            Paths.EnsureExists();
            if (!File.Exists(Paths.ConfigFile)) return;

            try
            {
                var json = File.ReadAllText(Paths.ConfigFile);
                var c = JsonSerializer.Deserialize<AppConfig>(json);
                if (c != null) config = c;
            }
            catch { }
        }

        public static void SaveConfig()
        {
            Paths.EnsureExists();
            config.Username = user;
            config.EncodedPassword = remember ? Convert.ToBase64String(Encoding.UTF8.GetBytes(pass)) : "";
            config.RememberMe = remember;
            config.ExcludeFavorites = hideFavs;
            config.InactiveEnabled = inactiveOn;
            config.InactiveValue = inactiveVal;
            config.InactiveUnitIndex = inactiveUnit;
            config.SortOptionIndex = sort;

            try
            {
                File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}