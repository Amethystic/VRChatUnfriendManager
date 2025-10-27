# VRChat Unfriend Manager

A simple, powerful, terminal-based tool for managing your VRChat friends list in bulk. Built for speed and efficiency, this tool allows you to quickly clean up your friends list without the tedious process of using the in-game or website UI.

![S1](https://cdn.discordapp.com/attachments/1398149285400281199/1432155901447508008/image.png?ex=6900069f&is=68feb51f&hm=e90ebc100545652c2745809896b7c0c5f565275f33b5f5e3790bf62172b76acb&)
![S2](https://media.discordapp.net/attachments/1286497694587686963/1432167189024215100/image.png?ex=69001122&is=68febfa2&hm=974ec307689afedfaec5d9c8c38b44b72473c264d4a94711ada6bef0a2598775&=&format=png&quality=lossless)

# Before: ![Before](https://cdn.discordapp.com/attachments/1286497694587686963/1432175167303581706/image.png?ex=69001890&is=68fec710&hm=fc3605f339ebbfae02965a4158b630bdf9bd8db8a6a3bdd0880a44ffa1dd8141&)

# After: ![After](https://cdn.discordapp.com/attachments/1286497694587686963/1432175198760730746/image.png?ex=69001897&is=68fec717&hm=4dde554da89008c03de343ac474750f4c8c60d39c982c8d2c1c760d725c6348c&)

## Features

-   **Fetch Entire Friends List:** Automatically fetches your complete friends list, including both online and offline users, bypassing the API's pagination limits.
-   **Optional Favorite Filtering:** Choose whether to include or exclude your favorited friends and friends in favorite groups with a simple checkbox. The list updates instantly.
-   **Multi-Select & Bulk Actions:** Mark individual friends, or use the "Mark All" and "Unmark All" buttons for quick selections.
-   **Safe, Randomized Delays:** The unfriend process includes a safe, randomized delay (5-10 seconds by default) between each API call to prevent your account from being rate-limited.
-   **Full 2FA Support:** Securely log in with your Two-Factor Authentication code, supporting Authenticator Apps (TOTP), Email Codes (OTP), and Recovery Codes.
-   **Session Persistence:** Use the "Remember me" option to save your session securely, allowing you to close and reopen the app without needing to log in again.
-   **Backup to JSON:** Before making any changes, you can back up your entire current friends list to a timestamped `.json` file with a single click.

## Requirements

-   [.NET 6.0 Runtime (or newer)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)

## How to Use

### Quick Start

1.  Go to the **Releases** page and download the latest version for your operating system.
2.  Unzip the folder to a location of your choice.
3.  Run the executable (`Unfriendmaxxing.exe` on Windows).

### Step-by-Step Guide

1.  **Login:**
    -   Enter your VRChat **Username** and **Password**.
    -   If you have 2FA enabled, a dialog box will appear after you click "Login". Enter your 6-digit code.
    -   Check **"Remember me"** to save your login session for the next time you open the app.

2.  **Filtering:**
    -   The **"Exclude Favorites"** checkbox is checked by default. This will show you a list of only your non-favorited friends.
    -   **Uncheck** this box to display your **entire** friends list, including all favorites. The list will update instantly.

3.  **Managing Friends:**
    -   Use the **Arrow Keys** to navigate the friends list.
    -   Press the **Spacebar** to mark or unmark the currently selected friend.
    -   Click the **"Mark All"** or **"Unmark All"** buttons to manage the entire displayed list.

4.  **Actions:**
    -   **Unfriend Marked Friends:** This will begin the bulk unfriend process for all friends who have a checkmark next to their name. A confirmation dialog will appear first.
    -   **Backup All Friends to JSON:** This saves a complete, unfiltered copy of your friends list to a `.json` file in the same directory as the application.

---

> ### **DISCLAIMER: USE AT YOUR OWN RISK**
>
> -   This is an **unofficial tool** and is not affiliated with, endorsed, or supported by VRChat Inc.
> -   **Unfriending is a permanent action and cannot be undone.** Always be certain before confirming the unfriend operation. It is highly recommended to use the **Backup** feature first.
> -   While this tool is designed to be safe, using third-party applications to interact with the VRChat API is against the Terms of Service. Use this tool responsibly. The developer is not responsible for any actions taken against your account.

## Building from Source

If you wish to build the project yourself:

1.  Clone the repository: `git clone <repository_url>`
2.  Navigate to the project directory: `cd Unfriendmaxxing`
3.  Ensure you have the .NET 6.0 SDK (or newer) installed.
4.  Run the application: `dotnet run`

## License

This project is licensed under the MIT License.