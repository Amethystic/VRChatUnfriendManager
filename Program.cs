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
using System.Numerics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Diagnostics; // Added for Process (Shortcut creation)
using Raylib_cs;
using ImGuiNET;
using rlImGui_cs;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;
using File = System.IO.File;
using Microsoft.Win32;
using Color = Raylib_cs.Color;
using Image = Raylib_cs.Image;

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
        
        // VRCX Paths
        public static readonly string VrcxBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCX");
        public static readonly string VrcxStartup = Path.Combine(VrcxBase, "startup");

        public static void EnsureExists() => Directory.CreateDirectory(AppDataFolder);
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
        public bool RunOnStartup { get; set; } = false;
        
        // VRCX Settings
        public bool VrcxStartupDesktop { get; set; } = false;
        public bool VrcxStartupVr { get; set; } = false;
        
        public bool UseCustomTitleBar { get; set; } = true;
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
            var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true, AllowAutoRedirect = true };
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
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

            try { File.WriteAllText(Paths.CookieFile, fullCookie); } catch { }
            Program.config.Cookie = fullCookie;
            Program.SaveConfig();
            Console.WriteLine("[DEBUG] Cookies saved");
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
            Console.WriteLine("[DEBUG] Restoring session...");
            if (!string.IsNullOrWhiteSpace(Program.config.Cookie) && Program.config.Cookie.Contains("auth="))
            {
                cfg = new Configuration { UserAgent = UA };
                cfg.DefaultHeaders["Cookie"] = Program.config.Cookie.Trim();
                if (await TestSessionAsync())
                {
                    var name = await GetCurrentDisplayNameAsync() ?? "You";
                    Console.WriteLine($"[DEBUG] Session restored from config: {name}");
                    return (true, name);
                }
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
                        var name = await GetCurrentDisplayNameAsync() ?? "You";
                        Console.WriteLine($"[DEBUG] Session restored from file: {name}");
                        return (true, name);
                    }
                }
            }

            if (Program.config.RememberMe && !string.IsNullOrEmpty(Program.config.Username) && !string.IsNullOrEmpty(Program.config.EncodedPassword))
            {
                var pass = Encoding.UTF8.GetString(Convert.FromBase64String(Program.config.EncodedPassword));
                Console.WriteLine($"[DEBUG] Auto-login: {Program.config.Username}");
                var (success, name, error) = await LoginWithCredentialsAsync(Program.config.Username, pass);
                if (success && name != null) return (true, name);
            }

            Console.WriteLine("[DEBUG] No session. Login required.");
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
            Console.WriteLine($"[LOGIN] Attempting login: {username}");
            try
            {
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("User-Agent", UA);
                var loginPageResp = await client.GetAsync("https://vrchat.com/login");
                var loginPageHtml = await loginPageResp.Content.ReadAsStringAsync();
                var csrfToken = ExtractCsrfToken(loginPageHtml);

                if (string.IsNullOrEmpty(csrfToken)) return (false, null, "CSRF token missing");

                var cookiesHeader = loginPageResp.Headers.FirstOrDefault(h => h.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
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
                    return (false, null, $"Login failed ({(int)response.StatusCode})\n{body}");

                var authCookieHeader = response.Headers
                    .FirstOrDefault(h => h.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                    .Value?.FirstOrDefault(c => c.Contains("auth="));

                if (string.IsNullOrEmpty(authCookieHeader))
                    return (false, null, "No auth cookie");

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
                Console.WriteLine($"[LOGIN] Success: {user.DisplayName ?? user.Username}");
                return (true, user.DisplayName ?? user.Username, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOGIN] Exception: {ex.Message}");
                return (false, null, $"Error: {ex.Message}");
            }
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
                    Console.WriteLine($"[2FA] Code submitted: {tfaCode.Trim()}");
                    tfaTcs.SetResult(tfaCode.Trim());
                    tfaTcs = null;
                    show2FADialog = false;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    Console.WriteLine("[2FA] Cancelled");
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

        public async Task UnfriendAsync(string id)
        {
            Console.WriteLine($"[UNFRIEND] Request: {id}");
            await Friends.UnfriendAsync(id);
        }

        public async Task<List<SafeLimitedUserFriend>> GetAllFriendsAsync()
        {
            Console.WriteLine("[FRIENDS] Loading...");
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
                Console.WriteLine($"[FRIENDS] Online: {page.Count} (offset {offset})");
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
                Console.WriteLine($"[FRIENDS] Offline: {page.Count} (offset {offset})");
                if (page.Count < 100) break;
            }

            Console.WriteLine($"[FRIENDS] Total: {list.Count}");
            return list;
        }

        public async Task<HashSet<string>> GetFavoriteFriendIdsAsync()
        {
            Console.WriteLine("[FAVORITES] Loading...");
            var set = new HashSet<string>();

            for (int offset = 0; ; offset += 100)
            {
                var page = await Favorites.GetFavoritesAsync(type: "friend", n: 100, offset: offset);
                foreach (var f in page) set.Add(f.FavoriteId);
                Console.WriteLine($"[FAVORITES] Page: {page.Count} (offset {offset})");
                if (page.Count < 100) break;
            }

            Console.WriteLine($"[FAVORITES] Total: {set.Count}");
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
        static bool shouldExit = false;

        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_HIDE = 0;

        // --- ICON FIX IMPORTS ---
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        private const uint WM_SETICON = 0x80;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        // ------------------------

        static async Task Main(string[] args)
        {
            Console.WriteLine("VRChat Unfriend Manager v3 Starting...");
            Paths.EnsureExists();
            LoadConfig();

            // Sync VRCX Shortcuts on startup if needed
            if (Directory.Exists(Paths.VrcxStartup))
            {
                UpdateVrcxShortcut("desktop", config.VrcxStartupDesktop);
                UpdateVrcxShortcut("vr", config.VrcxStartupVr);
            }

            if (config.RunOnStartup) UpdateStartupRegistry(true);

            ConfigFlags flags = ConfigFlags.ResizableWindow | ConfigFlags.HighDpiWindow;
            if (config.UseCustomTitleBar) flags |= ConfigFlags.UndecoratedWindow;

            Raylib.SetConfigFlags(flags);
            Raylib.InitWindow(1280, 800, "VRChat Unfriend Manager v3");
            
            // --- WIN32 ICON LOADING FIX ---
            try
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    IntPtr hIcon = ExtractIcon(IntPtr.Zero, exePath, 0);
                    if (hIcon != IntPtr.Zero)
                    {
                        unsafe
                        {
                            IntPtr hwnd = new IntPtr(Raylib.GetWindowHandle());
                            SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, hIcon);
                            SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hIcon);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ICON] Failed to set icon: {ex.Message}");
            }
            // ------------------------------

            Raylib.SetTargetFPS(60);

#if !DEBUG
            var consoleHwnd = GetConsoleWindow();
            if (consoleHwnd != IntPtr.Zero) ShowWindow(consoleHwnd, SW_HIDE);
#endif

            rlImGui.Setup(true);

            user = config.Username;
            remember = config.RememberMe;
            hideFavs = config.ExcludeFavorites;
            inactiveOn = config.InactiveEnabled;
            inactiveVal = config.InactiveValue;
            inactiveUnit = config.InactiveUnitIndex;
            sort = config.SortOptionIndex;

            bool firstFrame = true;

            while (!shouldExit)
            {
                if (Raylib.WindowShouldClose()) { shouldExit = true; continue; }

                rlImGui.Begin();
                Raylib.BeginDrawing();
                Raylib.ClearBackground(new Color(20, 20, 30, 255));

                ImGui.SetNextWindowPos(Vector2.Zero);
                ImGui.SetNextWindowSize(new Vector2(Raylib.GetScreenWidth(), Raylib.GetScreenHeight()));
                ImGui.Begin("Main", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar);

                if (config.UseCustomTitleBar) DrawCustomTitleBar();

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
                        else status = "Login required";
                    });
                }

                if (sessionRestored || isLoggedIn) DrawMainUI();
                else DrawLoginScreen();

                api.Draw2FADialog();
                ImGui.End();
                rlImGui.End();
                Raylib.EndDrawing();
            }

            SaveConfig();
            rlImGui.Shutdown();
            Raylib.CloseWindow();
        }

        private static void ShowUnfriendToast(string displayName)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            try
            {
                string xml = $$"""
                    <toast>
                        <visual>
                            <binding template="ToastGeneric">
                                <text>Unfriended</text>
                                <text>{{displayName}} has been removed from your friends list.</text>
                                <image placement="appLogoOverride" hint-crop="circle" src="https://i.imgur.com/0z3Z8Yb.png"/>
                            </binding>
                        </visual>
                        <audio src="ms-winsoundevent:Notification.Default"/>
                    </toast>
                    """;

                var doc = new Windows.Data.Xml.Dom.XmlDocument();
                doc.LoadXml(xml);
                ToastNotificationManager.CreateToastNotifier("VRChat Unfriend Manager").Show(new ToastNotification(doc));
            }
            catch { }
        }

        static void DrawCustomTitleBar()
        {
            float titleBarHeight = 40f;
            float windowWidth = Raylib.GetScreenWidth();
            float windowHeight = Raylib.GetScreenHeight();

            ImGui.SetCursorPos(Vector2.Zero);
            ImGui.BeginChild("titlebar", new Vector2(windowWidth, titleBarHeight), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

            var drawList = ImGui.GetWindowDrawList();
            var windowPos = ImGui.GetWindowPos();

            drawList.AddRectFilledMultiColor(
                windowPos,
                windowPos + new Vector2(windowWidth, titleBarHeight),
                ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.15f, 1f)),
                ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.15f, 1f)),
                ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.2f, 1f)),
                ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.2f, 1f))
            );

            ImGui.SetCursorPos(new Vector2(15, 12));
            ImGui.Text("VRChat Unfriend Manager v3");

            float buttonSize = 32f;
            float totalButtonsWidth = (buttonSize + 8) * 3 - 8;
            float buttonsStartX = windowWidth - totalButtonsWidth - 10;

            ImGui.SetCursorPos(new Vector2(buttonsStartX, 4));
            var minimizeHovered = ImGui.Button("-", new Vector2(buttonSize, buttonSize - 8));
            if (minimizeHovered) Raylib.MinimizeWindow();

            ImGui.SameLine();
            string maximizeLabel = Raylib.IsWindowMaximized() ? "[-]" : "[O]";
            var maximizeHovered = ImGui.Button(maximizeLabel, new Vector2(buttonSize, buttonSize - 8));
            if (maximizeHovered)
            {
                if (Raylib.IsWindowMaximized()) Raylib.RestoreWindow();
                else Raylib.MaximizeWindow();
            }

            ImGui.SameLine();
            var closeHovered = ImGui.Button("X", new Vector2(buttonSize, buttonSize - 8));
            if (closeHovered) shouldExit = true;

            if (minimizeHovered || maximizeHovered || closeHovered)
            {
                var hoverColor = closeHovered ? new Vector4(0.8f, 0.2f, 0.2f, 1f) : new Vector4(0.3f, 0.3f, 0.35f, 1f);
                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(hoverColor));
                drawList.AddText(min + (max - min) * 0.5f - ImGui.CalcTextSize(closeHovered ? "X" : minimizeHovered ? "-" : maximizeLabel) * 0.5f,
                    ImGui.GetColorU32(Vector4.One), closeHovered ? "X" : minimizeHovered ? "-" : maximizeLabel);
            }

            if (ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
                ImGui.IsWindowHovered() &&
                !minimizeHovered && !maximizeHovered && !closeHovered &&
                ImGui.GetMousePos().Y - windowPos.Y < titleBarHeight)
            {
                if (Raylib.IsWindowMaximized())
                {
                    Raylib.RestoreWindow();
                    Raylib.SetWindowPosition((int)(ImGui.GetMousePos().X - Raylib.GetScreenWidth() * 0.3f), 10);
                }

                if (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 5f))
                {
                    var delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
                    var pos = Raylib.GetWindowPosition();
                    Raylib.SetWindowPosition((int)(pos.X + delta.X), (int)(pos.Y + delta.Y));
                    ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
                }
            }

            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) &&
                ImGui.IsWindowHovered() &&
                !minimizeHovered && !maximizeHovered && !closeHovered &&
                ImGui.GetMousePos().Y - windowPos.Y < titleBarHeight)
            {
                if (Raylib.IsWindowMaximized()) Raylib.RestoreWindow();
                else Raylib.MaximizeWindow();
            }

            ImGui.EndChild();

            drawList.AddLine(
                windowPos + new Vector2(0, titleBarHeight),
                windowPos + new Vector2(windowWidth, titleBarHeight),
                ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.4f, 1f)));

            var gripSize = 16f;
            var gripPos = windowPos + new Vector2(windowWidth - gripSize, windowHeight - gripSize);
            drawList.AddTriangleFilled(
                gripPos + new Vector2(gripSize, 0),
                gripPos + new Vector2(gripSize, gripSize),
                gripPos + new Vector2(0, gripSize),
                ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.6f)));

            drawList.AddTriangleFilled(
                gripPos + new Vector2(gripSize * 0.6f, 0),
                gripPos + new Vector2(gripSize * 0.6f, gripSize * 0.6f),
                gripPos + new Vector2(0, gripSize * 0.6f),
                ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.4f)));

            ImGui.SetCursorPos(new Vector2(0, titleBarHeight + 5));
        }

        static void DrawLoginScreen()
        {
            ImGui.Text("VRChat Unfriend Manager v3");
            ImGui.Separator();

            ImGui.TextColored(
                status.Contains("Login failed", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Wrong", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("CSRF", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                    ? new Vector4(1f, 0.3f, 0.3f, 1f)
                    : ImGui.GetStyle().Colors[(int)ImGuiCol.Text],
                status
            );

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
                    else status = error ?? "Login failed";
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
                            Console.WriteLine("[LOGOUT] User logged out");
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
                        // ==================== FILTERS ====================

                        if (ImGui.Checkbox("Hide Favorites", ref hideFavs))
                        {
                            config.ExcludeFavorites = hideFavs;
                            SaveConfig();
                        }

                        ImGui.AlignTextToFramePadding();
                        if (ImGui.Checkbox("Inactive >=", ref inactiveOn))
                        {
                            config.InactiveEnabled = inactiveOn;
                            SaveConfig();
                        }

                        if (inactiveOn)
                        {
                            ImGui.SameLine();
                            float indent = ImGui.GetStyle().ItemSpacing.X + 15f;
                            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);

                            ImGui.SetNextItemWidth(60f);
                            if (ImGui.InputInt("##inactive_val", ref inactiveVal))
                            {
                                if (inactiveVal < 1) inactiveVal = 1;
                                config.InactiveValue = inactiveVal;
                                SaveConfig();
                            }

                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(100f);
                            if (ImGui.Combo("##unit", ref inactiveUnit, units, units.Length))
                            {
                                config.InactiveUnitIndex = inactiveUnit;
                                SaveConfig();
                            }

                            ImGui.SameLine();
                            ImGui.TextDisabled("|");
                            ImGui.SameLine();

                            var cutoff = inactiveUnit switch
                            {
                                0 => DateTime.UtcNow.AddDays(-inactiveVal),
                                1 => DateTime.UtcNow.AddMonths(-inactiveVal),
                                _ => DateTime.UtcNow.AddYears(-inactiveVal)
                            };

                            int matchCount = friends.Count(f =>
                                string.IsNullOrEmpty(f.LastLogin) ||
                                DateTime.Parse(f.LastLogin) < cutoff);

                            if (hideFavs)
                                matchCount = friends.Count(f =>
                                    !favorites.Contains(f.Id) &&
                                    (string.IsNullOrEmpty(f.LastLogin) || DateTime.Parse(f.LastLogin) < cutoff));

                            ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), $"({matchCount} users match)");
                        }

                        ImGui.NewLine();

                        ImGui.SetNextItemWidth(140f);
                        if (ImGui.Combo("Sort", ref sort, sorts, sorts.Length))
                        {
                            config.SortOptionIndex = sort;
                            SaveConfig();
                        }

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

                        if (ImGui.Button("Mark All")) { for (int i = 0; i < shown.Count; i++) selected.Add(i); }
                        ImGui.SameLine(); if (ImGui.Button("Unmark All")) selected.Clear();
                        ImGui.SameLine(); if (ImGui.Button("Refresh")) _ = Refresh();
                        ImGui.SameLine(); if (ImGui.Button("Backup JSON")) File.WriteAllText($"backup_{DateTime.Now:yyyyMMdd_HHmmss}.json", JsonSerializer.Serialize(shown, new JsonSerializerOptions { WriteIndented = true }));

                        string btn = isUnfriending ? (isPaused ? "Resume" : "Pause") : $"Unfriend Selected ({selected.Count})";
                        if (ImGui.Button(btn) && selected.Count > 0)
                        {
                            if (isUnfriending) isPaused = !isPaused;
                            else ImGui.OpenPopup("Confirm");
                        }

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

                if (ImGui.BeginTabItem("Settings"))
                {
                    ImGui.Text("Application Settings");
                    ImGui.Separator();

                    ImGui.Text("Window Options:");
                    bool useCustomTitleBar = config.UseCustomTitleBar;
                    if (ImGui.Checkbox("Use custom title bar (requires restart)", ref useCustomTitleBar))
                    {
                        config.UseCustomTitleBar = useCustomTitleBar;
                        SaveConfig();
                        ShowToast("VRChat Unfriend Manager", "Restart application for changes to take effect");
                    }

                    ImGui.Text("Startup Options:");
                    bool runOnStartup = config.RunOnStartup;
                    if (ImGui.Checkbox("Run on system startup", ref runOnStartup))
                    {
                        config.RunOnStartup = runOnStartup;
                        SaveConfig();
                        UpdateStartupRegistry(runOnStartup);
                    }

                    // --- VRCX INTEGRATION ---
                    if (Directory.Exists(Paths.VrcxStartup))
                    {
                        ImGui.Separator();
                        ImGui.Text("VRCX Integration");
                        ImGui.TextDisabled("Detected VRCX startup folder.");
                        
                        bool vrcxDesktop = config.VrcxStartupDesktop;
                        if (ImGui.Checkbox("Launch with VRCX (Desktop)", ref vrcxDesktop))
                        {
                            config.VrcxStartupDesktop = vrcxDesktop;
                            UpdateVrcxShortcut("desktop", vrcxDesktop);
                            SaveConfig();
                        }
                        
                        bool vrcxVr = config.VrcxStartupVr;
                        if (ImGui.Checkbox("Launch with VRCX (VR)", ref vrcxVr))
                        {
                            config.VrcxStartupVr = vrcxVr;
                            UpdateVrcxShortcut("vr", vrcxVr);
                            SaveConfig();
                        }
                    }
                    // ------------------------

                    ImGui.Separator();
                    ImGui.Text("Auto-Unfriend Scheduler");
                    ImGui.Separator();

                    bool autoEnabled = config.AutoUnfriendEnabled;
                    if (ImGui.Checkbox("Enable Auto-Unfriend", ref autoEnabled))
                    {
                        config.AutoUnfriendEnabled = autoEnabled;
                        SaveConfig();
                        Console.WriteLine($"[AUTO] {(autoEnabled ? "ENABLED" : "DISABLED")}");
                        if (autoEnabled) StartAutoScheduler();
                        else autoCts?.Cancel();
                    }

                    ImGui.BeginDisabled(!config.AutoUnfriendEnabled);

                    ImGui.Text("Run daily at:");
                    ImGui.SameLine();

                    // ----------- CLEAN TIME PICKER -----------
                    int displayHour = config.AutoUnfriendHour == 0 ? 12 :
                        (config.AutoUnfriendHour > 12 ? config.AutoUnfriendHour - 12 : config.AutoUnfriendHour);
                    bool isPM = config.AutoUnfriendHour >= 12;

                    ImGui.SetNextItemWidth(60);
                    // FIXED: Setting step and step_fast to 0 hides the stepper buttons
                    if (ImGui.InputInt("##hour12", ref displayHour, 0, 0))
                    {
                        displayHour = Math.Clamp(displayHour, 1, 12);
                        config.AutoUnfriendHour = isPM
                            ? (displayHour == 12 ? 12 : displayHour + 12)
                            : (displayHour == 12 ? 0 : displayHour);
                        SaveConfig();
                    }

                    ImGui.SameLine();
                    if (ImGui.ArrowButton("##h_down", ImGuiDir.Left))
                    {
                        displayHour = displayHour <= 1 ? 12 : displayHour - 1;
                        config.AutoUnfriendHour = isPM
                            ? (displayHour == 12 ? 12 : displayHour + 12)
                            : (displayHour == 12 ? 0 : displayHour);
                        SaveConfig();
                    }

                    ImGui.SameLine();
                    if (ImGui.ArrowButton("##h_up", ImGuiDir.Right))
                    {
                        displayHour = displayHour >= 12 ? 1 : displayHour + 1;
                        config.AutoUnfriendHour = isPM
                            ? (displayHour == 12 ? 12 : displayHour + 12)
                            : (displayHour == 12 ? 0 : displayHour);
                        SaveConfig();
                    }

                    ImGui.SameLine(); ImGui.Text(":");
                    ImGui.SameLine();

                    int minute = config.AutoUnfriendMinute;

                    ImGui.SetNextItemWidth(60);
                    // FIXED: Removed extra arrows, kept default +/- buttons
                    if (ImGui.InputInt("##min", ref minute))
                    {
                        minute = Math.Clamp(minute, 0, 59);
                        config.AutoUnfriendMinute = minute;
                        SaveConfig();
                    }

                    // AM/PM toggle
                    ImGui.SameLine();
                    ImGui.TextColored(
                        isPM ? new Vector4(0.3f, 0.8f, 1f, 1f) : new Vector4(1f, 0.8f, 0.3f, 1f),
                        isPM ? "PM" : "AM"
                    );

                    if (ImGui.IsItemClicked())
                    {
                        config.AutoUnfriendHour = (config.AutoUnfriendHour + 12) % 24;
                        SaveConfig();
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("Test Now"))
                    {
                        Console.WriteLine("[TEST] Test Now clicked");
                        ImGui.OpenPopup("TestAutoUnfriend");
                    }

                    int mode = config.AutoUnfriendMode;
                    ImGui.SameLine();
                    if (ImGui.Combo("Mode##auto", ref mode, autoModes, autoModes.Length))
                    {
                        config.AutoUnfriendMode = mode;
                        SaveConfig();
                        Console.WriteLine($"[AUTO] Mode: {autoModes[mode]}");
                    }

                    var preview = new DateTime(2025, 1, 1, config.AutoUnfriendHour, config.AutoUnfriendMinute, 0);
                    ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1f), preview.ToString("h:mm tt").ToUpper());

                    ImGui.SameLine(); ImGui.TextDisabled("|"); ImGui.SameLine();
                    ImGui.TextColored(
                        autoRunning ? new Vector4(0.2f, 1f, 0.2f, 1f) : new Vector4(0.6f, 0.6f, 0.6f, 1f),
                        autoRunning ? "RUNNING NOW" : "Stopped"
                    );

                    if (ImGui.BeginPopupModal("TestAutoUnfriend", ImGuiWindowFlags.AlwaysAutoResize))
                    {
                        ImGui.Text("Start Auto-Unfriend Test?");
                        ImGui.Separator();
                        ImGui.TextWrapped("This will run the auto-unfriend process immediately using current settings.");

                        if (ImGui.Button("Yes, Start Test"))
                        {
                            Console.WriteLine("=== AUTO-UNFRIEND TEST STARTED ===");
                            Console.WriteLine($"Mode: {autoModes[config.AutoUnfriendMode]}");
                            Console.WriteLine($"Filter: {(config.InactiveEnabled ? $"{config.InactiveValue} {units[config.InactiveUnitIndex]}" : "None")}");
                            Console.WriteLine($"Favorites hidden: {config.ExcludeFavorites}");

                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await RunAutoUnfriend();
                                    Console.WriteLine("=== TEST COMPLETED SUCCESSFULLY ===");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"=== TEST FAILED ===\n{ex}");
                                }
                            });

                            ImGui.CloseCurrentPopup();
                        }

                        ImGui.SameLine();
                        if (ImGui.Button("Cancel"))
                        {
                            Console.WriteLine("[TEST] Cancelled");
                            ImGui.CloseCurrentPopup();
                        }

                        ImGui.EndPopup();
                    }

                    ImGui.EndDisabled();

                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        static async Task Refresh()
        {
            working = true; status = "Loading friends...";
            Console.WriteLine("[REFRESH] Starting...");

            try
            {
                favorites = await api.GetFavoriteFriendIdsAsync();
                friends = await api.GetAllFriendsAsync();
                status = $"Loaded {friends.Count} friends";
                Console.WriteLine($"[REFRESH] Complete: {friends.Count} friends");
            }
            catch (Exception ex)
            {
                status = "Session expired - please re-login";
                isLoggedIn = false;
                sessionRestored = false;
                Console.WriteLine($"[REFRESH] Failed: {ex.Message}");
            }

            selected.Clear();
            working = false;
        }

        static async Task StartUnfriendProcess()
        {
            Console.WriteLine($"[UNFRIEND] Manual: {selected.Count} users");
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

                    var user = list[i];
                    status = $"Unfriending {user.DisplayName}...";
                    Console.WriteLine($"[UNFRIEND] ({i + 1}/{list.Count}) {user.DisplayName}");

                    try
                    {
                        await api.UnfriendAsync(user.Id);
                        unfriendDone++;
                        ShowUnfriendToast(user.DisplayName);
                        Console.WriteLine($"[UNFRIEND] Success: {user.DisplayName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[UNFRIEND] Failed: {user.DisplayName} - {ex.Message}");
                    }

                    if (i < list.Count - 1)
                        await Task.Delay(Random.Shared.Next(7000, 13000), unfriendCts.Token);
                }
            }
            finally
            {
                isUnfriending = false; isPaused = false;
                status = unfriendDone == unfriendTotal ? "All done!" : "Cancelled";
                ShowToast("Unfriend Complete", $"{unfriendDone} users removed");
                Console.WriteLine($"[UNFRIEND] Finished: {unfriendDone}/{unfriendTotal}");
                selected.Clear();
                await Refresh();
            }
        }

        static async Task RunAutoUnfriend()
        {
            Console.WriteLine("[AUTO] Starting...");
            await Refresh();

            var targets = config.AutoUnfriendMode switch
            {
                0 => shown.Where(f => string.IsNullOrEmpty(f.LastLogin) || DateTime.Parse(f.LastLogin) < DateTime.UtcNow.AddMonths(-3)).ToList(),
                1 => shown.ToList(),
                _ => shown.Where((_, i) => selected.Contains(i)).ToList()
            };

            Console.WriteLine($"[AUTO] Targets: {targets.Count}");
            if (targets.Count == 0)
            {
                status = "Auto: nothing to remove";
                Console.WriteLine("[AUTO] Nothing to do");
                return;
            }

            autoRunning = true;

            for (int i = 0; i < targets.Count; i++)
            {
                var user = targets[i];
                Console.WriteLine($"[AUTO] ({i + 1}/{targets.Count}) {user.DisplayName}");
                try
                {
                    await api.UnfriendAsync(user.Id);
                    ShowUnfriendToast(user.DisplayName);
                    Console.WriteLine($"[AUTO] Success: {user.DisplayName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AUTO] Failed: {user.DisplayName} - {ex.Message}");
                }

                status = $"AUTO: {i + 1}/{targets.Count} ({user.DisplayName})";
                await Task.Delay(Random.Shared.Next(7000, 13000));
            }

            ShowToast("Auto Unfriend Complete", $"{targets.Count} users removed");
            Console.WriteLine($"[AUTO] Complete: {targets.Count} processed");
            await Refresh();
            autoRunning = false;
        }

        static void StartAutoScheduler()
        {
            autoCts?.Cancel();
            autoCts = new CancellationTokenSource();
            Console.WriteLine("[AUTO] Scheduler started");

            _ = Task.Run(async () =>
            {
                while (!autoCts.IsCancellationRequested && config.AutoUnfriendEnabled)
                {
                    var now = DateTime.Now;
                    var target = new DateTime(now.Year, now.Month, now.Day, config.AutoUnfriendHour, config.AutoUnfriendMinute, 0);
                    if (target <= now) target = target.AddDays(1);

                    var delay = target - now;
                    Console.WriteLine($"[AUTO] Next run in {delay:h\\:mm\\:ss}");
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, autoCts.Token);
                    await RunAutoUnfriend();
                }

                Console.WriteLine("[AUTO] Scheduler stopped");
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

        private static void UpdateStartupRegistry(bool enable)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            try
            {
                const string appName = "VRChatUnfriendManager";
                const string runKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
                using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
                if (key == null) return;

                if (enable)
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath)) key.SetValue(appName, $"\"{exePath}\"");
                }
                else key.DeleteValue(appName, false);
            }
            catch { }
        }
        
        // --- VRCX SHORTCUT HELPER ---
        private static void UpdateVrcxShortcut(string subfolder, bool enable)
        {
            try
            {
                var targetDir = Path.Combine(Paths.VrcxStartup, subfolder);
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;

                var linkName = "VRChatUnfriendManager.lnk";
                var linkPath = Path.Combine(targetDir, linkName);

                if (enable)
                {
                    // Use PowerShell to create a shortcut to avoid adding a COM reference
                    var script = $"-NoProfile -WindowStyle Hidden -Command \"$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('{linkPath}'); $s.TargetPath = '{exePath}'; $s.WorkingDirectory = '{Path.GetDirectoryName(exePath)}'; $s.Save()\"";
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = script,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    Console.WriteLine($"[VRCX] Shortcut created in {subfolder}");
                }
                else
                {
                    if (File.Exists(linkPath))
                    {
                        File.Delete(linkPath);
                        Console.WriteLine($"[VRCX] Shortcut removed from {subfolder}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VRCX] Failed to update shortcut: {ex.Message}");
            }
        }
        // ----------------------------

        static void LoadConfig()
        {
            Paths.EnsureExists();
            if (!File.Exists(Paths.ConfigFile)) return;

            try
            {
                var json = File.ReadAllText(Paths.ConfigFile);
                var c = JsonSerializer.Deserialize<AppConfig>(json);
                if (c != null) config = c;
                Console.WriteLine("[CONFIG] Loaded");
            }
            catch (Exception ex) { Console.WriteLine($"[CONFIG] Load failed: {ex.Message}"); }
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
            
            // VRCX Config is saved directly when toggled

            try
            {
                File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("[CONFIG] Saved");
            }
            catch (Exception ex) { Console.WriteLine($"[CONFIG] Save failed: {ex.Message}"); }
        }
    }
}