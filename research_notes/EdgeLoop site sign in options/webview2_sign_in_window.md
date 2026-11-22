# WebView2 "Sign in" window for capturing site session cookies (EdgeLoop, .NET 10 WPF, November 2026)

Scope: whether a small WPF window hosting Microsoft Edge WebView2 can show the Hypnotube / Rule34Video login page, let the user sign in (including captchas), then read the site's cookies (including the HttpOnly `PHPSESSID`), store them with EdgeLoop's existing DPAPI `CookieProtector` (`D:/Projects/EdgeLoop/EdgeLoop/Classes/CookieProtector.cs`, `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`) and replay them from EdgeLoop's own `HttpClient`. EdgeLoop targets `net10.0-windows` (`D:/Projects/EdgeLoop/EdgeLoop/EdgeLoop.csproj`) and has no WebView2 reference yet.

Version context (checked 2026-11-22): latest stable SDK on NuGet is **Microsoft.Web.WebView2 1.0.4370.26 (updated 11/21/2026)**; latest prerelease **1.0.4392-prerelease (11/18/2026)** — [NuGet](https://www.nuget.org/packages/Microsoft.Web.WebView2). The Learn API reference monikers go up to 1.0.4370.26 / 1.0.4392-prerelease, with 1.0.4314.35 as the default docs view — [Learn: CoreWebView2Cookie](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2cookie).

## 1. Can CoreWebView2CookieManager read HttpOnly cookies, what comes back, and how do you detect sign-in succeeded?

### Takeaway
Yes, in practice. `CookieManager` is a host-side (C#) API, not page JavaScript. Each returned `CoreWebView2Cookie` has an `IsHttpOnly` flag, which only makes sense if HttpOnly cookies are returned. But Microsoft's page for `GetCookiesAsync` never says "HttpOnly cookies are included" in so many words. There is no cookie-changed event, so sign-in has to be detected by watching navigation and response events, then checking cookies or making a test request.

### Cited Findings
- Signature: `Task<List<CoreWebView2Cookie>> GetCookiesAsync(string uri)`. "If uri is empty string or null, all cookies under the same profile are returned." The docs sample turns each cookie into a `System.Net.Cookie` with `ToSystemNetCookie()` and prints `IsSession` / `Expires`. Remarks: you can change cookies through `AddOrUpdateCookie(CoreWebView2Cookie)` "and the changes will be applied to the webview." — [Learn: GetCookiesAsync](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2cookiemanager.getcookiesasync)
- `CoreWebView2Cookie` properties: `Name` (get), `Value` (get/set), `Domain` (get), `Path` (get), `Expires` ("The expiration date and time for the cookie"), `IsHttpOnly` ("Determines whether this cookie is http-only"), `IsSecure` (get/set), `IsSession` (default `true`), `SameSite` (default `CoreWebView2CookieSameSiteKind.Lax`). The one method is `ToSystemNetCookie()` ("Converts this to a System.Net.Cookie"). — [Learn: CoreWebView2Cookie](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2cookie)
- The Win32 doc describes `IsHttpOnly` as true when "a page script or other active content cannot access this cookie". So the flag describes a cookie that was returned, and the cookie is not hidden. (This wording comes from the search-result summary of the Win32 `ICoreWebView2Cookie` reference) — [Learn: ICoreWebView2Cookie (Win32)](https://learn.microsoft.com/zh-cn/microsoft-edge/webview2/reference/win32/icorewebview2cookie)
- The cookie manager also has methods to create, copy and delete cookies — [Learn: CoreWebView2CookieManager](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2cookiemanager). Since SDK 1.0.1210-era builds it is also available per profile as `CoreWebView2Profile.CookieManager` — [Learn: CoreWebView2Profile](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2profile)
- No cookie-changed event: a Microsoft Q&A user asking how to be told when cookies are read or written got no event-based answer. They note the CookieManager "provides lots of methods to read/write cookies", but nothing to notify — [Microsoft Q&A 1661952](https://docs.Microsoft.com/en-us/answers/questions/1661952/how-to-prevent-the-web-page-to-read-or-write-cooki)
- `WebResourceResponseReceived` (on `ICoreWebView2_2` / `CoreWebView2`) lets the host "view the actual request and response for a web resource". That can be used to watch the login POST response and its redirect — [Learn: ICoreWebView2_2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2_2)
- Reported pitfall: the Win32 GetCookies completion handler "is not called if the WebView is idle" — [Microsoft Q&A 2152449](https://learn.microsoft.com/en-gb/answers/questions/2152449/icorewebview2getcookiescompletedhandler-is-not-cal)
- PHP defaults that matter for `PHPSESSID`: `session.name` defaults to `PHPSESSID`. `session.cookie_lifetime` defaults to `0` ("until the browser is closed"), so the cookie is a session cookie. `session.gc_maxlifetime` defaults to `1440` s (24 minutes) of server-side idle lifetime. `session.cookie_httponly` defaults to `0`, so HttpOnly is a choice the site made — [PHP manual: session configuration](https://www.php.net/manual/en/session.configuration.php)

### Inferences
- Expect `PHPSESSID` to come back with `IsSession == true` and no meaningful `Expires` (if the sites keep PHP's default `cookie_lifetime=0`). EdgeLoop must not treat "no expiry" as "expired". It should keep the cookie until a request shows it is no longer signed in.
- `PHPSESSID` usually exists **before** login (any visit that starts a session). Its presence is therefore no proof of sign-in. Robust detection:
  1. Hook `CoreWebView2.NavigationCompleted` (check `IsSuccess`) and `SourceChanged`. When the URL leaves the login path, or the login POST's response in `WebResourceResponseReceived` is a redirect, call `GetCookiesAsync("https://<site>/")`.
  2. Confirm by sending the captured cookies from EdgeLoop's `HttpClient` to a page only signed-in users can see (account/favourites). Look for a logout link or username, the same way EdgeLoop's site rules already parse pages.
  3. Always offer a manual "I'm signed in" / "Done" button as a fallback.
- Capture **all** cookies for the site's domain, not just `PHPSESSID`. Sites often add "remember me" tokens, and Cloudflare/DDoS-Guard add their own cookies (`cf_clearance`, `__ddg*`). A remember-me cookie may outlive the 24-minute idle GC on the PHP session.
- Call `GetCookiesAsync` with the site URL, not `null`. `null` returns every cookie in the profile (ad and tracker cookies included), and EdgeLoop shouldn't store those.

### Gaps
- I found no Microsoft sentence saying in so many words that `GetCookiesAsync` returns HttpOnly cookies. The conclusion rests on the `IsHttpOnly` property and the API being host-side. A 10-minute spike against a test site would settle it.
- Not verified whether `WebResourceResponseReceived` exposes `Set-Cookie` response headers in full (Chromium sometimes filters them in devtools-style APIs).
- I could not find whether Hypnotube or Rule34Video regenerate the session id on login, or set extra remember-me cookies. That needs a live check by the user.

## 2. NuGet packages for WPF, .NET 10 support, and size impact

### Takeaway
Use one package, `Microsoft.Web.WebView2`. The WPF control (`Microsoft.Web.WebView2.Wpf.WebView2`) is in `Microsoft.Web.WebView2.Wpf.dll` inside that package, so no separate "Wpf" NuGet is needed. The nupkg is 9.37 MB and has no dependencies. What actually ships with an x64 app is the Core and WPF managed DLLs plus one native `WebView2Loader.dll`. I could not confirm a byte count.

### Cited Findings
- `Microsoft.Web.WebView2` latest stable 1.0.4370.26 (11/21/2026), prerelease 1.0.4392-prerelease (11/18/2026). Recent cadence: 1.0.4314.35 (10/23/2026), 1.0.4258.31 (9/28/2026), 1.0.4191.47 (8/28/2026). Package download size 9.37 MB; "No dependencies" — [NuGet](https://www.nuget.org/packages/Microsoft.Web.WebView2)
- The WPF `WebView2` class lives in assembly `Microsoft.Web.WebView2.Wpf.dll`, package `Microsoft.Web.WebView2` (v1.0.4370.26 listed) — [Learn: WPF WebView2 class](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.wpf.webview2)
- Files to ship for a managed app: `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WPF.dll` (and `.Winforms.dll` in the generic layout), plus `runtimes\win-{arm64,x64,x86}\native\WebView2Loader.dll`. "WebView2Loader.dll is a native and architecture-specific binary". The loader can also be statically linked (native apps) — [Learn: Distribution — Files to ship](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- The NuGet "Frameworks" tab, as summarised, showed net462 (with computed net47x/net48x), native and UAP 10.0. The summary did not list netcoreapp3.0 / net5.0-windows / net10.0 — [NuGet](https://www.nuget.org/packages/Microsoft.Web.WebView2)

### Inferences
- The WPF/WinForms .NET (Core) builds have long shipped in this package, and the current docs list the WPF types for every version through 1.0.4370.26. So .NET 10 WPF projects most likely consume it without trouble. Still, confirm with a throwaway branch build (`dotnet add package Microsoft.Web.WebView2 --version 1.0.4370.26`) before committing.
- EdgeLoop is x64-only. If it publishes with a `win-x64` RuntimeIdentifier, only the x64 loader should be copied. The app grows by the managed DLLs plus one loader, likely a few MB rather than the 9.37 MB nupkg. Not measured.
- The real cost is not disk. It is the Edge browser processes (`msedgewebview2.exe`) the runtime starts while the sign-in window is open. These are shared, already installed, and exit when the window's WebView is disposed (see section 8).

### Gaps
- Exact on-disk size of the added files for an x64 .NET 10 publish was not verified.
- Could not see the NuGet page's full list of .NET (Core) target frameworks in this session.

## 3. Is the Evergreen Runtime reliably present on Windows 10/11 in 2026? What if it's missing?

### Takeaway
On Windows 11 it is part of the OS. On Windows 10 Microsoft says "the vast majority" of devices have it, and it will keep getting updates on Windows 10 22H2 until at least October 2028. A small number of devices may still lack it (or users may have removed it), so EdgeLoop should check and degrade gracefully. The cheap fallbacks are linking to Microsoft's download page or running the ~2 MB bootstrapper with consent. The Fixed Version runtime (>250 MB) is overkill here.

### Cited Findings
- "The Evergreen WebView2 Runtime will be included as part of the Windows 11 operating system… some devices might not have the Runtime pre-installed, so it's a good practice to check whether the Runtime is present on the client." — [Learn: Distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- "The vast majority of Windows 10 devices have the WebView2 Runtime installed already… A small number of Windows 10 devices don't have the WebView2 Runtime installed." The doc recommends deploying it programmatically or sending users to the [Download Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/consumer/) page — [Learn: Distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution); background in [Windows blog, Dec 2022 (older)](https://blogs.windows.com/msedgedev/2022/12/14/delivering-microsoft-edge-webview2-runtime-to-managed-windows-10-devices/)
- Detection, two ways:
  - Read the `pv (REG_SZ)` value under `HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` (per-machine) or `HKCU\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` (per-user). The runtime is missing if neither exists or the value is null, empty or 0.0.0.0.
  - Or call `GetAvailableCoreWebView2BrowserVersionString` (`CoreWebView2Environment.GetAvailableBrowserVersionString()` in .NET); a null result means not installed. — [Learn: Distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- Install options:
  - **Evergreen Bootstrapper**, "approximately 2 MB", downloads the matching runtime; `MicrosoftEdgeWebview2Setup.exe /silent /install`. Run non-elevated, it installs per-user.
  - **Evergreen Standalone Installer** for offline use: `MicrosoftEdgeWebView2RuntimeInstaller{X64/X86/ARM64}.exe /silent /install`.
  - **Fixed Version**: "The Fixed Version binaries are over 250 MB". No auto-update, so the app must service it. On Windows 10, from Fixed Version 120 onward, unpackaged Win32 apps must grant ACLs with `icacls … /grant *S-1-15-2-2:(OI)(CI)(RX)` and `*S-1-15-2-1`. It can't run from a UNC path.
  — [Learn: Distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- Missing runtime or failed start gives errors such as "Microsoft Edge runtime unable to start" / "Failed to create WebView2 environment". Microsoft says to wrap creation in try/catch because "These errors tend to be fatal errors" — [Learn: User data folder](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder)
- Production apps can't fall back to the installed Edge Stable browser: "A production release of a WebView2 app can only use the WebView2 Runtime as the backing web platform, not Microsoft Edge." — [Learn: Distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- Windows 10 lifetime: "Microsoft Edge and the Microsoft WebView2 Runtime will continue to receive updates on Windows 10 22H2 until at least October 2028" — [Learn: Microsoft Edge Lifecycle](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-support-lifecycle). Conflict: the same page then says "The ESU program requires devices to continue receiving…", while press coverage says ESU is *not* required — [Windows Central](https://www.windowscentral.com/microsoft/windows-10/microsoft-edge-support-windows-10-until-2028-for-free). The Learn sentence reads like a typo, but I can't confirm that.
- Runtime churn: "Starting with Stable channel version 152, Microsoft Edge moves to a two-week major release cadence" — [Learn: Microsoft Edge Lifecycle](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-support-lifecycle). Evergreen apps should feature-detect newer APIs because "the client machine might not have the latest WebView2 Runtime". A running app keeps the old runtime until restart (`NewBrowserVersionAvailable`) — [Learn: Distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)

### Inferences
- EdgeLoop should check for the runtime only when the user clicks "Sign in", not at startup. If it's missing, show a dialog that links to Microsoft's download page. Running the bootstrapper silently would install software on the user's machine, so it should need explicit consent. EdgeLoop's existing paste-cookie / browser-import path keeps working without WebView2.
- Don't use Fixed Version for EdgeLoop. It adds 250+ MB, makes EdgeLoop responsible for browser security patches, and needs extra Windows 10 ACL steps.
- Use only APIs that are old and widely shipped (CookieManager, NavigationCompleted, profile settings) so a slightly old runtime still works.

### Gaps
- No current (2026) telemetry figure for the share of Windows 10 devices without the runtime. Microsoft only says "vast majority".
- I didn't verify whether a user can uninstall the Evergreen runtime on Windows 11 in 2026.

## 4. User data folder: default location, setting it, clearing it ("sign out"), persistent vs throwaway

### Takeaway
The default UDF is `<exe path>.WebView2` next to the executable. That fails with "Access Denied" when the app is installed under Program Files. Microsoft says to set a custom folder (for example `%LOCALAPPDATA%\EdgeLoop\WebView2`) through `CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: …)` or `CreationProperties`. "Sign out" can be done in-session with `CoreWebView2Profile.ClearBrowsingDataAsync(...)` or the cookie manager's delete methods. Deleting the folder requires the browser processes to exit first. A persistent profile keeps a **second copy** of the session (plus history and cache) outside EdgeLoop's DPAPI store. A throwaway or InPrivate profile avoids that but forces fresh logins and captchas.

### Cited Findings
- The UDF stores "browser data, such as cookies, permissions, and cached resources". Data kinds include Cookies, DiskCache, BrowsingHistory, GeneralAutofill (names, addresses, "payment data"), PasswordAutosave, LocalStorage and IndexedDb — [Learn: User data folder](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder)
- Default for .NET (WPF/WinForms): "the directory that the app executable (.exe) is running in… exe path of your app + `.WebView2`". For example, `D:\WebView2App\WebView2.exe` gives `D:\WebView2App\WebView2.exe.WebView2\`. Microsoft: "In most cases, you should specify a custom UDF location". Installer technologies "put the app… in a protected area of the filesystem, where WebView2 isn't able to create the UDF". The errors are "Unable to create user data folder, Access Denied." / "User data folder cannot be created because a file with the same name already exists." — [Learn: User data folder](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder)
- Setting it: `CoreWebView2Environment.CreateAsync(BrowserExecutableFolder, UserDataFolder, new CoreWebView2EnvironmentOptions(...))`. The WinUI sample uses `Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "YOURAPPNAME", "WebView2")` and notes you must decide whether uninstall removes it — [Learn: User data folder](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder). In WPF you can instead set `CreationProperties` before initialization — [Learn: WPF WebView2 class](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.wpf.webview2)
- .NET apps never remove the UDF automatically ("For .NET (WPF & WinForms) host apps, the UDF is not automatically removed"), and uninstallers don't either — [Learn: User data folder](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder)
- Deleting: "You cannot delete a UDF if the WebView2 session is currently active… wait for the browser process and all child processes to exit". Use `BrowserProcessId` to monitor them. While a session is running, "it's better to call the *clear browsing data* methods instead of deleting the UDF." — [Learn: User data folder](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder); see also [Learn: Clear browsing data](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/clear-browsing-data) (linked, not fetched)
- `CoreWebView2Profile` APIs:
  - Clearing data: `ClearBrowsingDataAsync()`, `ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds)`, and an overload with a time range.
  - Profile management: `Delete()` ("Delete this profile and close the corresponding CoreWebView2s") and a `Deleted` event.
  - Properties: `CookieManager`, `IsInPrivateModeEnabled`, `IsPasswordAutosaveEnabled`, `IsGeneralAutofillEnabled`, `ProfilePath`, `PreferredTrackingPreventionLevel`.
  — [Learn: CoreWebView2Profile](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2profile)
- Several profiles can share one UDF to keep data separate without extra browser processes — [Learn: User data folder](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder); [Learn: Multi-profile support](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/multi-profile-support) (linked, not fetched)
- A UDF can serve only one WebView2 session at a time. It can be shared by host apps in the same logon session, and two copies of the app share the same UDF — [Learn: User data folder](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder)

### Inferences
- Recommended layout: `%LOCALAPPDATA%\EdgeLoop\WebView2`, with one named profile per site (for example `hypnotube`, `rule34video`) via `CoreWebView2ControllerOptions.ProfileName`. That makes "Sign out of Hypnotube" one call: `profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile)` (or `Delete()`), plus wiping EdgeLoop's DPAPI copy.
- Turn off `IsPasswordAutosaveEnabled` and `IsGeneralAutofillEnabled` so the UDF never holds the user's password or form data.
- Persistent profile. Pros: Cloudflare/DDoS-Guard clearance and any remember-me cookie survive, so a re-login may skip the captcha. Cons: an extra copy of the session (and history of adult-site visits) sits on disk. EdgeLoop doesn't control how it's encrypted, and it lingers after uninstall unless cleaned.
- Throwaway profile (InPrivate, or delete the profile after capture). Pros: nothing left on disk but EdgeLoop's DPAPI blob. Cons: every re-login repeats the captcha and clearance.
- A sensible default for a privacy-sensitive adult-content player: persistent profile with password and autofill off, plus an explicit "Sign out & forget" button that clears the profile and deletes stored cookies.

### Gaps
- I didn't verify whether Chromium inside WebView2 encrypts the on-disk cookie store (for example with DPAPI) or writes session cookies to disk at all.
- I didn't verify what InPrivate mode in WebView2 persists after the controller closes.

## 5. Do Cloudflare / DDoS-Guard / reCAPTCHA sites work in WebView2 as in Edge? Embedded-browser blocks and bot detection

### Takeaway
WebView2 is real Chromium/Edge with a real user at the keyboard, so captchas generally behave like they do in Edge. That is the main advantage over scripted HTTP. Known friction:
- Google blocks **Google-account OAuth** in embedded webviews (not relevant to these sites' own username/password forms).
- Some reports of Cloudflare Turnstile verify-loops in embedded or customised browsers, usually tied to custom Chromium flags or an altered UA.
- WebView2 identifies itself: its client-hint brands include a "Microsoft Edge WebView2" entry. A site *could* block it, and I found no evidence these two sites do.

### Cited Findings
- Google has blocked OAuth sign-in from embedded webviews since its policy took full effect ("all embedded webviews will be blocked starting on September 30, 2021"); the error is `disallowed_useragent`. This is older but still current policy — [Google Developers Blog (2021)](https://developers.googleblog.com/2021/06/upcoming-security-changes-to-googles-oauth-2.0-authorization-endpoint.html); [Auth0 blog](https://auth0.com/blog/google-blocks-oauth-requests-from-embedded-browsers/)
- A developer of a WebView2-based browser reported Cloudflare Turnstile looping ("the page refreshes and the verification prompt appears again") after adding Chromium command-line arguments — [AnswerOverflow thread](https://www.answeroverflow.com/m/1477112537232179402). Similar Turnstile failures happen in other embedded webviews (Android System WebView, Obsidian's web viewer) — [GrapheneOS forum](https://discuss.grapheneos.org/d/40379-cloudflare-turnstile-and-webview); [Obsidian forum](https://forum.obsidian.md/t/surfing-vs-webviewer-cloudflare-verification-issues/105699)
- Cloudflare's own troubleshooting advice is generic: check browser compatibility, disable extensions, send a feedback report — [Cloudflare: Challenge solve issues](https://developers.cloudflare.com/cloudflare-challenges/troubleshooting/challenge-solve-issues/)
- Setting `CoreWebView2Settings.UserAgent` "may clear User Agent Client Hints headers Sec-CH-UA-* and script values from navigator.userAgentData". A mismatched or missing client hint is a classic bot signal — [Learn: CoreWebView2Settings.UserAgent](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2settings.useragent)
- DuckDuckGo's Windows browser (built on WebView2) ships code to "Filter Microsoft Edge WebView2" from the client-hint brand list. That implies WebView2 advertises a distinct "Microsoft Edge WebView2" brand — [duckduckgo/content-scope-scripts PR #2028](https://github.com/duckduckgo/content-scope-scripts/pull/2028)

### Inferences
- **Don't** change the UA, add Chromium flags, or inject anti-detection scripts. Use WebView2's defaults so the captcha sees a normal, current Edge-family browser. That is the configuration least likely to loop.
- reCAPTCHA and hCaptcha are widely used inside Chromium-based embedded browsers. I found no WebView2-specific reports for them in this session (see Gaps). Since a human solves the captcha, failure would most likely come from risk scoring, not from the widget not rendering.
- DDoS-Guard: no WebView2-specific information found. It's a JS-challenge system like Cloudflare's, so the same "don't alter the browser" advice applies.
- The project memory notes Hypnotube has a **location block**. A geo/IP block is enforced server-side by IP, so WebView2 won't get around it. The sign-in window will show the same block page.

### Gaps
- No WebView2Feedback issue was found specifically about reCAPTCHA, hCaptcha or DDoS-Guard failing inside WebView2. The WebView2-specific evidence above is third-party.
- Exact WebView2 `Sec-CH-UA` brand string not confirmed from Microsoft docs. The [Learn: Browser features differences](https://learn.microsoft.com/en-gb/microsoft-edge/webview2/concepts/browser-features) page was not fetched.
- Whether Hypnotube or Rule34Video sit behind Cloudflare or DDoS-Guard today, or block embedded browsers, needs a live check by the user.

## 6. Reusing the cookies in EdgeLoop's HttpClient; User-Agent and IP binding

### Takeaway
Reuse is simple: `cookie.ToSystemNetCookie()` → `CookieContainer`, or build a `Cookie:` header. For the PHP session itself, the defaults don't bind to UA or IP, though a site can add that. Cloudflare-type clearance cookies **are** bound to the visitor and device. So EdgeLoop's `HttpClient` should send the exact User-Agent WebView2 used (read `CoreWebView2.Settings.UserAgent` after init and store it with the cookies) and run from the same machine and network.

### Cited Findings
- `CoreWebView2Cookie.ToSystemNetCookie()` "Converts this to a System.Net.Cookie". The docs sample round-trips cookies through it — [Learn: CoreWebView2Cookie](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2cookie); [Learn: GetCookiesAsync](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2cookiemanager.getcookiesasync)
- `CoreWebView2Settings.UserAgent`: "The default value is the default User Agent of the Edge browser. This property may be overridden if the User-Agent header is set in a request." Reading the property gives the UA string the WebView is sending — [Learn: CoreWebView2Settings.UserAgent](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2settings.useragent)
- Cloudflare says pre-clearance `cf_clearance` is "securely tied to the specific visitor and device it was issued to… designed to prevent the cookie from being easily transferred and re-used on other machines" (per search summary of the official page) — [Cloudflare: Turnstile pre-clearance](https://developers.cloudflare.com/turnstile/get-started/pre-clearance/); [Cloudflare: Clearance](https://developers.cloudflare.com/cloudflare-challenges/concepts/clearance)
- Third-party claim, not official: clearance is bound to IP, User-Agent and TLS/JA3 fingerprint, and changing IP mid-session causes rejection — [DEV Community post](https://dev.to/bshahin/cloudflare-cfclearance-why-it-expires-and-how-to-stop-the-re-challenge-loop-do9)
- PHP's built-in session settings listed above contain nothing about UA or IP binding. The cookie is just a session id with a 24-minute default idle GC — [PHP manual: session configuration](https://www.php.net/manual/en/session.configuration.php)

### Inferences
- If a site uses Cloudflare clearance, EdgeLoop's .NET `HttpClient` may still be challenged even with the right cookie and UA. Its TLS/HTTP2 fingerprint differs from Chromium's (unverified, per the third-party claim above). That is a real risk for the "sign in in WebView2, fetch with HttpClient" plan. It can't be settled without a live test on the actual sites. If it bites, a fallback is to fetch the needed pages *through* the WebView2 (hidden window, or `CoreWebView2.NavigateWithWebResourceRequest` / reading the DOM). That is heavier and less clean.
- Store the capture as one unit: {cookies, UA string, capture time, site}, DPAPI-encrypted via `CookieProtector`. Send the same UA from `HttpClient` and add matching `Sec-CH-UA*` headers if needed.
- The server-side PHP session can expire (24-minute idle default unless the site raised it) even though the cookie has no expiry. EdgeLoop needs a "session expired → please sign in again" path. Regular requests may keep the session alive, since PHP's idle timer resets on each request that starts the session.

### Gaps
- Not verified whether Hypnotube or Rule34Video bind sessions to UA or IP, or how long their sessions last. Needs a live test.
- No official Cloudflare statement found listing exactly which attributes `cf_clearance` is bound to (IP vs UA vs TLS fingerprint).

## 7. How other desktop apps do it, and security guidance

### Takeaway
Embedded-browser login windows are a common pattern for desktop apps that need a website session (Electron and WebView2 apps). The alternative is importing cookies from the user's real browser (yt-dlp `--cookies-from-browser`, covered by other research). Microsoft's guidance for a sign-in window like this:
- Treat the page as untrusted.
- Restrict navigation (`NavigationStarting` / `FrameNavigationStarting`).
- Turn off host objects and web messages.
- Avoid `AddScriptToExecuteOnDocumentCreated` and `ExecuteScript` unless you have checked the origin.
- Run non-elevated.
- Keep the Evergreen runtime current, because the content is third-party.

### Cited Findings
- "Treat all web content as insecure". Check the document origin (`Source`) before `ExecuteScript` / `PostWebMessage*`. "Be careful with `AddScriptToExecuteOnDocumentCreated`. All future navigations run the same script" — [Learn: Develop secure WebView2 apps](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)
- Restrict functionality through `CoreWebView2Settings`: `AreHostObjectsAllowed = false`, `IsWebMessageEnabled = false`, and (where possible) `AreDefaultScriptDialogsEnabled = false`. "use the `NavigationStarting` and `FrameNavigationStarting` events to check page or frame navigation, and then conditionally block the navigation" — [Learn: Develop secure WebView2 apps](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)
- Run the host non-elevated. In elevated processes, `WEBVIEW2_*` env-var and HKCU overrides are ignored. "WebView2 cannot be run as a system user" — [Learn: Develop secure WebView2 apps](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)
- A long-running app keeps the old runtime until restart. "if your WebView2 app accesses third-party content, that content should be considered untrusted, so your app would need to use the latest version of the WebView2 Runtime." Handle `NewBrowserVersionAvailable` — [Learn: Distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- The general advice for OAuth providers is to use the system browser, not an embedded one (Google's stance) — [Google Developers Blog (2021)](https://developers.googleblog.com/2021/06/upcoming-security-changes-to-googles-oauth-2.0-authorization-endpoint.html)

### Inferences
- Concrete hardening for EdgeLoop's sign-in window:
  1. In `NavigationStarting` / `FrameNavigationStarting`, allow only `https://` hosts on the target site's domain plus a small allow-list of captcha/CDN hosts (Cloudflare challenges, Google reCAPTCHA, hCaptcha, DDoS-Guard). Open anything else in the user's default browser or block it. The allow-list must include the captcha iframes, or captchas will break.
  2. Handle `NewWindowRequested` by loading the URL in the same view or blocking it (pop-up ads).
  3. Set `AreDevToolsEnabled = false`, `AreHostObjectsAllowed = false`, `IsWebMessageEnabled = false`, `AreBrowserAcceleratorKeysEnabled = false`, and turn off password autosave and autofill on the profile.
  4. Don't inject scripts. If sign-in detection reads the DOM, do it via `ExecuteScriptAsync` only after checking `Source` is the expected host.
  5. Show the current URL/host in the window chrome so the user can see they are on the real site (Google's stated reason for blocking webviews was the missing URL bar).
  6. Block `DownloadStarting`.
- Being honest with users: the app sees whatever is typed on that page (in principle). Users must trust EdgeLoop just as they trust it with pasted cookies. A tooltip should say the password goes only to the site and isn't stored.

### Gaps
- I didn't fetch specific open-source examples (for example GitHub projects implementing "login via WebView2 → cookies → HttpClient") in this session. I can't cite a particular app's implementation.
- `AreDevToolsEnabled`, `AreBrowserAcceleratorKeysEnabled` and `NewWindowRequested` are standard `CoreWebView2Settings` / `CoreWebView2` members, but their doc pages weren't fetched here. Only the security page's list (host objects, web messages, script, dialogs) was quoted.

## 8. Threading and lifetime gotchas in WPF

### Takeaway
Create and use WebView2 only on the WPF UI (STA) thread, always `await` and never `.Result`, and don't open modal dialogs from inside WebView2 event handlers. Initialize explicitly with `EnsureCoreWebView2Async(env)`, using one shared `CoreWebView2Environment` per UDF. Dispose the control when the sign-in window closes. After `Dispose` it can't be reused. Because the WPF control is an `HwndHost`, WPF elements can't be drawn on top of it (airspace).

### Cited Findings
- "The WebView2 control is based on the Component Object Model (COM) and must run on a Single Threaded Apartments (STA) thread… All callbacks occur on that thread, and requests into the WebView2 must be done on that thread." — [Learn: Threading model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model)
- Cookie-specific gotcha: "calling `CoreWebView2CookieManager.GetCookiesAsync(null)` from a thread other than Main will succeed… however, attempting to access the cookies' properties (such as `c.Domain`) after such a call will throw an exception." — [Learn: Threading model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model)
- Reentrancy: creating "a nested message loop or modal UI synchronously within a WebView2 event handler… isn't supported… and would leave the event handler in the stack indefinitely". Post the work with `SynchronizationContext.Current.Post` instead. Blocking with `Task.Result` deadlocks because "WebView2 relies on the message pump of the UI thread". Use `using (args.GetDeferral())` for async handlers — [Learn: Threading model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model)
- WPF control lifecycle: `CoreWebView2` is null at first. Initialization happens explicitly through `EnsureCoreWebView2Async(CoreWebView2Environment, CoreWebView2ControllerOptions)`, or implicitly by setting `Source`. Pass your own environment, or set `CreationProperties` "prior to initialization". On completion, `CoreWebView2InitializationCompleted` fires first, then navigation to `Source`, then the `EnsureCoreWebView2Async` task completes — [Learn: WPF WebView2 class](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.wpf.webview2)
- Dispose: "Calling Dispose(Boolean) will release the CoreWebView2 and its underlying resources (except any that are also being used by other WebViews)… After Dispose… the CoreWebView2 cannot be re-initialized, and any attempt to use functionality which requires it will throw an ObjectDisposedException." — [Learn: WPF WebView2 class](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.wpf.webview2)
- The WPF control "extends HwndHost in order to embed windows which live outside of the WPF ecosystem", which affects input/output behaviour (airspace). Accelerator-key handlers block the browser process, so keep them light — [Learn: WPF WebView2 class](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.wpf.webview2)
- Several WebView2 controls using the same UDF share one browser session or process. Avoid running many different UDFs at once (memory and disk). When reacting to `NewBrowserVersionAvailable`, close all WebViews on that UDF — [Learn: User data folder](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder)

### Inferences
- Pattern for EdgeLoop:
  1. Keep one lazily created `static Task<CoreWebView2Environment>` per process, made with `CreateAsync(null, %LOCALAPPDATA%\EdgeLoop\WebView2)`.
  2. The `SignInWindow` (non-modal or `ShowDialog` from a button click, *not* from inside a WebView2 event) calls `await webView.EnsureCoreWebView2Async(env, controllerOptionsWithProfileName)` in `Loaded`.
  3. Configure settings and handlers in the initialization-completed step, then `Navigate(loginUrl)`.
  4. On success, read cookies on the UI thread, hand plain strings to the view model, `Close()`, and `Dispose()` the control in `Closed`.
- Wrap environment creation and `EnsureCoreWebView2Async` in try/catch. Map failures to "WebView2 Runtime missing / couldn't start" UI that offers the download link.
- Allow only one sign-in window at a time (per site, or globally). Two windows on the same profile share cookies, and signing into two sites at once only adds complexity.
- Airspace: don't plan WPF overlays such as spinners or toasts *on top of* the WebView. Put status UI in a bar above or below it. (`WebView2CompositionControl` exists in newer SDKs as a non-HwndHost alternative. I didn't verify its status in this session.)
- EdgeLoop's main video window is separate, so the WebView2 browser processes run only while the sign-in window is open.

### Gaps
- Status and maturity of `Microsoft.Web.WebView2.Wpf.WebView2CompositionControl` (airspace-free WPF control) not verified in this session.
- Didn't verify whether disposing the last WebView releases the UDF lock right away, or whether EdgeLoop must wait on `BrowserProcessExited` before deleting the UDF folder. The UDF doc says to wait for processes to exit, but doesn't give WPF-specific timing.
