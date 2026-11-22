# Easier ways to copy browser cookies into EdgeLoop (as of 22 November 2026)

Scope: copy-paste routes a non-technical Windows user (Firefox first, also Chrome and Edge) can use to get the HttpOnly `PHPSESSID` session cookie for Hypnotube and Rule34Video into EdgeLoop's Settings, and what EdgeLoop would have to parse for each. Everything below comes from docs, store listings, the AMO API and extension or browser source code fetched on 2026-11-22. No extension was installed and no real cookie was read.

Baseline in EdgeLoop today (local code, not web):
- `CleanCookieString` only trims the text and removes a leading `Cookie:` prefix. `SignInCookieNote` warns when no `PHPSESSID=` is present. — D:/Projects/EdgeLoop/EdgeLoop/ViewModels/SettingsViewModel.cs (lines 449-484)
- `CreateTempCookieFile` splits the stored string on `;` and writes each pair twice (`.domain TRUE` and `domain FALSE`, secure TRUE, expiry 2147483647) to a temporary Netscape file for yt-dlp. — D:/Projects/EdgeLoop/EdgeLoop/Classes/YtDlpService.cs (lines 794-826)

So EdgeLoop accepts only a raw header value or `Cookie: ...` today.

---

## 1. Cookie-export browser extensions: reputation, open source, HttpOnly, formats, current-site export, and the 2023 "Get cookies.txt" incident

### Takeaway
Three open-source extensions are worth recommending, each with a clipboard Copy option and an export limited to the current site: "Get cookies.txt LOCALLY" (Chrome/Edge/Firefox, MIT), "cookies.txt" by Lennon Hill (Firefox, GPL-3.0) and Cookie-Editor (all browsers, GPL-3.0). All three read cookies through the browser's cookies API, which returns HttpOnly cookies. Only Cookie-Editor marks them with `#HttpOnly_`, and that difference is the main parsing trap. The 2023 incident shows that store badges, reviews and a previously clean history don't protect against a malicious update. Recommend an extension as optional, link the exact listing, and tell users to remove it after use.

### Cited Findings

**Get cookies.txt LOCALLY (kairi003)**
- Chrome Web Store: about 900,000 users, 4.7 stars from 114 ratings, version 0.7.2, updated 21 Oct 2025, developer kairi003, with a "Featured" badge. The listing says it is not affiliated with the original "Get cookies.txt", which it describes as something "we have been told was malware in the past". Exports Netscape cookies.txt or JSON. — [Chrome Web Store listing](https://chromewebstore.google.com/detail/get-cookiestxt-locally/cclelndahbckbenkjhflpdbgdldlbecc)
- Firefox AMO: 17,691 average daily users, 4.67 stars from 18 ratings, version 0.7.2, last updated 2025-10-05, MIT licence. Permissions: activeTab, cookies, downloads, notifications, and host permission `<all_urls>`. Summary: "Get cookies.txt, NEVER send information outside with open-source". — [AMO API](https://addons.mozilla.org/api/v5/addons/addon/get-cookies-txt-locally/)
- The README says it never sends data externally and that its code is open and unobfuscated. It supports Chrome and Firefox and notes the Firefox version "may have limited features" and may need site access allowed in the extension manager. — [GitHub README](https://github.com/kairi003/Get-cookies.txt-LOCALLY)
- Source has three formats: `netscape`, `json` (plain `JSON.stringify` of the browser's cookie objects) and `header` (`name=value;` pairs joined by a space, with a trailing `;`). The Netscape header is `# Netscape HTTP Cookie File`, `# https://curl.haxx.se/rfc/cookie_spec.html`, `# This is a generated file! Do not edit.`, then a blank line. The include-subdomains flag is TRUE when the domain starts with `.`. Session cookies get expiry `0`. `httpOnly` is never read, so no line gets a `#HttpOnly_` prefix. — [cookie_format.mjs](https://raw.githubusercontent.com/kairi003/Get-cookies.txt-LOCALLY/master/src/modules/cookie_format.mjs)
- The popup filters by the active tab's URL, with a `topLevelSite` partition key, for the current-site export, table and Copy. A separate "Export All" exports everything. A Copy button writes the text to the clipboard with `navigator.clipboard.writeText`, and a format dropdown remembers the last choice. The code never mentions `storeId`, so Firefox containers and private windows aren't handled explicitly. — [popup.mjs](https://raw.githubusercontent.com/kairi003/Get-cookies.txt-LOCALLY/master/src/popup.mjs)

**cookies.txt (Firefox, Lennon Hill)**
- AMO: 41,710 average daily users, 4.77 stars from 91 ratings, version 1.0 (file created 2025-11-21), last updated 2025-11-26, GPL-3.0-only, not promoted. Permissions: cookies, downloads, contextualIdentities, `<all_urls>`, tabs, clipboardWrite. Its summary still says it "Exports all cookies to a Netscape HTTP Cookie File". — [AMO API](https://addons.mozilla.org/api/v5/addons/addon/cookies-txt/)
- The yt-dlp FAQ recommends this add-on for Firefox. — [yt-dlp FAQ](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)
- The popup has four rows, each with a Copy and a Download button: `all`, `current`, `container-all` and `container-current` (i18n keys `popupSitesAll`, `popupSitesCurrent`, and so on). — [popup/choose_option.html](https://raw.githubusercontent.com/hrdl-github/cookies-txt/main/popup/choose_option.html)
- `background.js` writes the same three-line header as Get cookies.txt LOCALLY. It adds a leading `.` for non-host-only cookies, sets the subdomain flag from `hostOnly`, and writes expiry `0` for session cookies. The `#HttpOnly_` prefix line is commented out (`// co.httpOnly ? '#HttpOnly_' : '',`). It calls `browser.cookies.getAll` with `storeId` and `firstPartyDomain: null`, never uses `partitionKey`, and Copy sends the text to the clipboard through the active tab. — [background.js](https://raw.githubusercontent.com/hrdl-github/cookies-txt/main/background.js)
- The README says each Firefox container gets its own file (`cookies.<container>.txt`). — [GitHub repo](https://github.com/hrdl-github/cookies-txt)

**Cookie-Editor (Moustachauve / cgagnier)**
- AMO: 137,801 average daily users, 4.13 stars from 293 ratings, version 1.13.0, last updated 2024-03-01, GPL-3.0-only, not promoted. Permissions: cookies, tabs, storage, devtools, `<all_urls>`. The description lists import and export as "JSON, Netscape, Header string". — [AMO API](https://addons.mozilla.org/api/v5/addons/addon/cookie-editor/)
- Supports Chrome, Firefox, Safari, Edge and Opera on desktop, plus Firefox for Android and Safari/Edge on mobile. It works on the current tab's cookies. The repo has about 1.8k stars, 40 open issues and no GitHub Releases. — [GitHub repo](https://github.com/Moustachauve/cookie-editor)
- Netscape export writes `#HttpOnly_` before the domain when `cookie.httpOnly` is true. The header lines are `# Netscape HTTP Cookie File`, `# http://curl.haxx.se/rfc/cookie_spec.html`, `# This file was generated by Cookie-Editor`. The subdomain flag is `hostOnly ? FALSE : TRUE`. Session cookies get an expiry of now plus 86,400 s, not 0. On import, `#HttpOnly_` is stripped before the comment check, lines split on `\r?\n` and on tabs only, and 6 or 7 fields are required. — [netscapeFormat.js](https://raw.githubusercontent.com/Moustachauve/cookie-editor/master/interface/lib/netscapeFormat.js)
- The "Header string" export is `name=value` pairs joined by `;` with no space (`a=1;b=2`). Import splits on `;`, trims, and splits on the first `=`. — [headerstringFormat.js](https://raw.githubusercontent.com/Moustachauve/cookie-editor/master/interface/lib/headerstringFormat.js)
- JSON export is a pretty-printed array of the browser's cookie objects, with `storeId` set to null and `sameSite` "unspecified" set to null. — [jsonFormat.js](https://raw.githubusercontent.com/Moustachauve/cookie-editor/master/interface/lib/jsonFormat.js)
- The project site says you can export directly to the clipboard. Its footer says "Copyright © 2023". — [cookie-editor.com](https://cookie-editor.com/)

**The 2023 "Get cookies.txt" incident and later copycats**
- The original "Get cookies.txt" Chrome extension started out harmless. A later update added code that sent device information and cookies by POST to an outside server. It had the Chrome Web Store "Featured" (おすすめ) badge. A user publicly warned about it on 28 Feb 2023, after reviews had flagged spyware-like behaviour earlier that month. By 8 Mar 2023 Google had removed the listing and Chrome blocked copies already installed. The article notes that checking reviews would not have caught a malicious update. — [Mado no Mori (Impress Watch), 28 Feb 2023, updated 8 Mar 2023](https://forest.watch.impress.co.jp/docs/news/1482079.html)
- yt-dlp's FAQ says the older "Get cookies.txt" (not "LOCALLY") "has been reported as malware and removed from the Chrome Web Store" and tells users to uninstall it. It adds: "As with any browser extension, be careful about what you install." — [yt-dlp FAQ](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)
- Look-alike names still exist. For example, "Get cookies.txt Clean" describes itself as a spyware-free version of "Get cookies.txt" (seen only in a search result; listing not opened). — [Chrome Web Store, via search](https://chromewebstore.google.com/detail/get-cookiestxt-clean/ahmnmhfbokciafffnknlekllgcnafnie)
- A related case from early 2025: after Google removed the original EditThisCookie, which was a Manifest V2 casualty, a copycat called "EditThisCookie®" built on Manifest V3 stayed listed. Reports say it had obfuscated code, phishing and ad scripts, and could steal data. One outlet gives 50,000 users, which was the listing's own claim; another says about 30,000 installs before removal. Seen through search results only, because the TechSpot page returned 403. — [Cyber Security News](https://cybersecuritynews.com/malicious-editthiscookie-chrome-extension/amp/); [TechSpot](https://www.techspot.com/news/106177-google-removed-editthiscookie-chrome-extension-but-malicious-copycat.html); [GBHackers](https://gbhackers.com/malicious-editthiscookie-extension/amp)

### Inferences
- **HttpOnly cookies are included.** All three use the WebExtensions cookies API (`cookies` permission plus `cookies.getAll`). Cookie-Editor reads `cookie.httpOnly` to decide on the `#HttpOnly_` prefix, which shows the API returns HttpOnly cookies along with their flag. Get cookies.txt LOCALLY and cookies.txt therefore include `PHPSESSID` too, just without the marker.
- **Each tool needs one extra click to export only the current site.** In Get cookies.txt LOCALLY it is the default "Export/Copy" button, not "Export All". In cookies.txt it is the "current" row's Copy button, not "all". In Cookie-Editor it is the export button in the popup, which works on the current tab. Instructions must name the exact button. Otherwise users may paste every cookie they have, from every site.
- **Lessons for recommending extensions:**
  - Treat any extension as optional, with DevTools as the route that needs no install.
  - Link the exact store listing ID, never just a name, because copycats and similar names exist.
  - Prefer open source with few permissions.
  - Suggest removing or disabling it after a one-time export. Auto-update means today's clean code isn't tomorrow's.
  - Don't treat "Featured" or high ratings as proof. The malicious extension had both.
  - None of the three is in Mozilla's Recommended/promoted programme: the `promoted` field was empty for all three.
- Cookie-Editor's last AMO update was March 2024, about 2.7 years before this research. That is a staleness flag, not evidence of a problem.

### Gaps
- No Chrome Web Store user count, last-update date or Manifest V3 status was fetched for Cookie-Editor, and none for cookies.txt (Firefox-only).
- The visible English labels of the cookies.txt popup rows ("current site" and so on) were not read. Only the i18n keys were seen, and `_locales/en/messages.json` was not fetched.
- Whether the Firefox build of Get cookies.txt LOCALLY handles containers or private windows was not checked. `get_all_cookies.mjs` was not read.
- Install counts for the malicious 2023 "Get cookies.txt" were not found.
- Edge Add-ons listings were not checked.

---

## 2. DevTools "Copy as cURL" in Firefox and Chrome/Edge: menu path, output format, cookie placement, escaping, HttpOnly, and whether it is easier

### Takeaway
On Windows, both browsers offer two cURL variants with different quoting. The pasted text is a long multi-line command full of quoting, so EdgeLoop would need a small shell-quote parser for each variant. Firefox always puts cookies in `-H 'Cookie: ...'`. Chrome and Edge put them in `-b '...'`. For the user it takes fewer steps than finding the Cookie header and choosing Copy Value, but they still have to pick the right request. For Firefox, the separate "Copy Request Headers" item is a simpler alternative that EdgeLoop could parse just as easily.

### Cited Findings

**Firefox**
- Open the Network Monitor with Ctrl+Shift+E, or the toolbox with Ctrl+Shift+I. The Storage Inspector is Shift+F9. — [Firefox DevTools keyboard shortcuts](https://firefox-source-docs.mozilla.org/devtools-user/keyboard_shortcuts/index.html)
- Right-click a request row and open Copy. It offers Copy URL, Copy as cURL, Copy as Fetch, Copy Request Headers, Copy Response Headers, Copy Response and Copy All As HAR. Copy as cURL emits one `-H` per request header and uses `--compressed` in place of Accept-Encoding. — [Firefox Network Monitor request list docs](https://firefox-source-docs.mozilla.org/devtools-user/network_monitor/request_list/index.html)
- Firefox's localisation strings include "Copy Value", "Copy as PowerShell", "Copy as cURL", "%S (Windows)" and "%S (POSIX)", "Copy Request Headers" and "Copy All". So on Windows the cURL item shows as "Copy as cURL (Windows)" and "Copy as cURL (POSIX)". — [netmonitor.properties](https://raw.githubusercontent.com/mozilla-firefox/firefox/main/devtools/client/locales/en-US/netmonitor.properties)
- The Windows and POSIX split was added for Windows in Firefox 70 (Bug 1097836). Later bugs include Bug 1970351 (the POSIX variant added `^` before `&` in Firefox 139), Bug 1970082 (a command-injection problem in the Windows variant, reported fixed in Nightly 143) and Bug 1605540 (`--compressed` fails on some Windows curl builds). These were seen through search-result summaries. — [Bug 1097836](https://bugzilla.mozilla.org/show_bug.cgi?id=1097836); [Bug 1970351](https://bugzilla.mozilla.org/show_bug.cgi?id=1970351); [Bug 1970082](https://bugzilla.mozilla.org/show_bug.cgi?id=1970082); [Bug 1605540](https://bugzilla.mozilla.org/show_bug.cgi?id=1605540)
- In Firefox's `curl.js`:
  - Every header, Cookie included, is written as `-H` plus an escaped `Name: value`. It never uses `-b`/`--cookie`. Host is always skipped, and Content-Length is skipped when a body is sent.
  - **POSIX variant:** single quotes. Values containing non-printable or non-ASCII characters, `!` or `'` switch to ANSI-C `$'...'`, with `\\`, `\'`, `\n`, `\r`, `\041` for `!`, and `\xHH`/`\uHHHH`. Arguments are joined with ` \` plus newline plus two spaces, and the command starts with `curl`.
  - **Windows variant:** the command starts with `curl.exe` and each value is wrapped in `^"…^"`. Backslashes are doubled and `"` becomes `\"`. Any character outside an allowlist of letters, digits, whitespace and ``-_:=+~/.',?;()*` `` gets a `^` prefix, which covers `^ & | < > %`. `%` followed by a letter, digit or underscore becomes `%^`. Lines continue with ` ^` plus newline.
  - Firefox's `curl.js` has no PowerShell variant. A separate "Copy as PowerShell" string exists in the localisation file.
  - — [curl.js](https://raw.githubusercontent.com/mozilla-firefox/firefox/main/devtools/client/shared/curl.js)
- The request details Headers tab has a context menu: "Copy" copies one name/value pair and "Copy All" copies the whole header set as JSON. A **Raw** toggle shows headers unformatted. A Cookies tab lists the cookies sent with the request. — [Firefox request details docs](https://firefox-source-docs.mozilla.org/devtools-user/network_monitor/request_details/index.html)

**Chrome and Edge (Chromium DevTools)**
- Right-click a request in the Name column and open Copy. It offers Copy URL, Copy as cURL, Copy as PowerShell, Copy as fetch, Copy as fetch (Node.js), Copy response and others. "Copy all as HAR (sanitized)" leaves out the `Cookie`, `Set-Cookie` and `Authorization` headers. — [Chrome DevTools Network reference](https://developer.chrome.com/docs/devtools/network/reference)
- On Windows (`Host.Platform.isWin()`), the menu shows "Copy as cURL (cmd)" and "Copy as cURL (bash)" in place of a single "Copy as cURL". There is also "Copy as PowerShell".
- In Chromium's `generateCurlCommand`:
  - A `cookie` header whose value contains `=` is written as `-b '<value>'`. Otherwise it falls back to `-H 'cookie: …'`. The code comment says this stops curl from reading a file name as a cookie source.
  - Skipped headers: accept-encoding, host, and the pseudo-headers method, path, scheme, version, authority and protocol.
  - **cmd variant:** `^"…^"` wrapping, `"` written as `""`, `^` before anything outside `[a-zA-Z0-9\s_\-:=+~'\/.',?;*]`, `%` handled as `%^`, and ` ^` plus newline for continuation.
  - **bash variant:** single quotes, or `$'…'` for control characters, `!` and `'`.
- The PowerShell variant uses `Invoke-WebRequest -UseBasicParsing -WebSession $session` with `$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession`. It adds one `$session.Cookies.Add((New-Object System.Net.Cookie(name, value, "/", domain)))` line per cookie and leaves `cookie` out of `-Headers`.
- — [NetworkLogView.ts](https://raw.githubusercontent.com/ChromeDevTools/devtools-frontend/main/front_end/panels/network/NetworkLogView.ts)
- The cmd variant's `^` escapes break tools such as Postman and curlconverter, and the curlconverter maintainers tell Windows users to pick "Copy as cURL (bash)". Seen through search snippets. — [curlconverter issue #143](https://www.github.com/curlconverter/curlconverter/issues/143)
- A tool that only reads cookies from the browser's database does not get the User-Agent. Hydrus notes some sites "sync" the User-Agent to their cookie record, which is why Hydrus Companion forwards it. — [Hydrus downloading docs](https://hydrusnetwork.github.io/hydrus/getting_started_downloading.html)

### Inferences
- **HttpOnly is included.** "Copy as cURL" copies the request's actual headers, so the Cookie header holds every cookie the browser sent, HttpOnly ones included. Neither browser's code filters HttpOnly.
- **Ease for a non-technical user:**
  - cURL removes two hard steps: finding the Cookie header among about 15 headers, then finding "Copy Value".
  - It adds a choice the user doesn't understand: Windows/POSIX or cmd/bash. EdgeLoop should accept all four, so the instructions can say "pick either".
  - Picking the right request (the first one, the page's own address) is still needed in every DevTools route.
  - On Firefox, "Copy Request Headers" sits on the same right-click menu with no variant choice. EdgeLoop could find the `Cookie:` line in that block, which makes it possibly the simplest DevTools route for Firefox. Its exact output format wasn't verified (see Gaps).
- **Extra benefit of cURL:** it also carries the User-Agent, and the URL shows which host the cookies belong to. EdgeLoop could check that host against the site and offer to reuse the User-Agent. That matters for Cloudflare-style cookies, which yt-dlp's FAQ says must be fresh.
- **Recommended Firefox wording:** "Network tab (Ctrl+Shift+E), press F5, right-click the first row, Copy, then Copy as cURL (either one), and paste into EdgeLoop."
- The HAR route is unreliable for cookies, at least in Chrome, because the sanitised HAR drops them.

### Gaps
- The exact text of Firefox's "Copy Request Headers" wasn't checked. It is probably a request line followed by `Name: value` lines, but this wasn't confirmed.
- Firefox's "Copy as PowerShell" output format wasn't read (its source wasn't fetched).
- Where Firefox's "Copy Value" item appears (Headers-panel context menu) is only inferred from the localisation string, because the docs mention "Copy" and "Copy All". EdgeLoop's current instructions say Copy Value works, but it wasn't verified here.
- Whether Edge's menu matches Chrome's exactly wasn't checked. Edge uses Chromium DevTools, but Microsoft may change labels.
- Whether Chrome's request Copy submenu now has "Copy request headers" wasn't confirmed from the docs page.
- Whether Firefox Bugs 1970351 and 1970082 are fixed in Release (not just Nightly) wasn't confirmed.

---

## 3. Firefox Storage Inspector (Shift+F9) and Chrome Application > Cookies: can a user easily copy only PHPSESSID, and is that simpler?

### Takeaway
Both panels list `PHPSESSID` with an HttpOnly column, and both let you double-click the Value cell to edit it. Selecting the text there and pressing Ctrl+C is the practical way to copy it. Neither browser has a clear "Copy value" item on the table: Firefox's only Copy is in the details sidebar. Copying only PHPSESSID gives a short paste and an easy check that the right cookie was taken. But it involves finding one row among many, risks editing the live cookie by accident, and fails if the site also needs other cookies. It works best as a fallback that EdgeLoop accepts, not as the main instruction.

### Cited Findings
- **Firefox table and context menu:** the cookies table has Name, Value, Domain, Path, Expires / Max-Age ("Session" for session cookies), Size, HttpOnly, Secure, SameSite, Last accessed, Created and HostOnly. Double-clicking a cell edits it. The context menu has Add item, Delete <name>.<domain>, Delete All From <domain>, Delete All and Delete All Session Cookies, with no Copy. — [Firefox Storage Inspector cookies docs](https://firefox-source-docs.mozilla.org/devtools-user/storage_inspector/cookies/index.html)
- **Firefox sidebar:** selecting a row opens a sidebar with the row's details, and it can show values parsed as objects. — [Storage Inspector overview](https://firefox-source-docs.mozilla.org/devtools-user/storage_inspector/index.html)
- **Firefox "Copy" item:** the localisation defines `storage-context-menu-copy = Copy`. — [storage.ftl](https://raw.githubusercontent.com/mozilla-firefox/firefox/main/devtools/client/locales/en-US/storage.ftl) In `ui.js` the table menu wires up only add and delete commands. The Copy command belongs to the sidebar (variables view) popup and calls `this.view._copyItem()`. — [storage ui.js](https://raw.githubusercontent.com/mozilla-firefox/firefox/main/devtools/client/storage/ui.js)
- **Firefox shortcut:** Shift+F9 opens the Storage Inspector. — [Firefox DevTools keyboard shortcuts](https://firefox-source-docs.mozilla.org/devtools-user/keyboard_shortcuts/index.html)
- **Chrome:** Application > Storage > Cookies, then pick an origin. Columns: Name, Value, Domain, Path, Expires / Max-Age, Size, HttpOnly, Secure, SameSite, Partition Key, Priority. Selecting a cookie shows its value, and a "Show URL-decoded" checkbox is available. Double-clicking a field edits it. A Filter box matches Name or Value. — [Chrome DevTools cookies docs](https://developer.chrome.com/docs/devtools/application/cookies)

### Inferences
- **Practical Firefox path:**
  1. Shift+F9.
  2. Open Cookies, then `https://hypnotube.com` (or the Rule34Video origin).
  3. Find the PHPSESSID row (HttpOnly column says true).
  4. Double-click its Value cell, press Ctrl+A then Ctrl+C, then Esc to cancel without changing anything.

  Chrome and Edge follow the same pattern using the Filter box with "PHPSESSID". This takes about as many steps as the Network-tab route, so it isn't clearly simpler.
- **Risks of copying only PHPSESSID:**
  - Pressing Enter or typing while the cell is in edit mode changes the real cookie and can sign the user out.
  - Users may copy the Name cell instead of the Value cell. EdgeLoop can detect a paste of exactly "PHPSESSID".
  - With Show URL-decoded on, Chrome may show a decoded value that differs from what was sent. PHP session IDs normally contain no encoded characters, so this is probably harmless (unverified).
- **What EdgeLoop would accept:** a bare token with no `=`, `;` or whitespace, which it stores as `PHPSESSID=<token>` for these two sites. A plausibility check could require roughly 20 or more characters from `[A-Za-z0-9,-]`, which is PHP's default session ID alphabet. That alphabet comes from general knowledge and wasn't verified in this session.

### Gaps
- What text Firefox's sidebar "Copy" places on the clipboard (value only, or `name: value`) is undetermined, because `VariablesView.sys.mjs` wasn't read.
- Whether Hypnotube or Rule34Video need any cookie besides PHPSESSID (for example an age-gate or Cloudflare cookie) wasn't researched. The PHPSESSID-only route depends on that.
- Whether Chrome's cookie value preview pane supports a right-click "Copy" isn't documented on the fetched page.

---

## 4. Netscape cookies.txt format: layout, `#HttpOnly_`, comments, producers, and how to parse and filter by domain

### Takeaway
Each cookie is one line of seven tab-separated fields: domain, include-subdomains, path, secure, expiry, name, value. Lines starting with `#` are comments, except that `#HttpOnly_` before the domain marks an HttpOnly cookie. Producers differ: some write `#HttpOnly_` and some don't, and session-cookie expiry varies. A parser must handle `#HttpOnly_` before skipping comments, accept 6 or 7 fields, tolerate CRLF or LF, and keep only lines whose domain matches the site.

### Cited Findings
- **Fields:** seven TAB-separated fields per line: 0 domain, 1 include subdomains (boolean), 2 path, 3 HTTPS only (boolean), 4 expiry ("seconds since Jan 1st 1970, or 0"), 5 name, 6 value. Lines starting with `#` are comments, and each line must end with a newline. — [Everything curl: cookie file format](https://everything.curl.dev/http/cookies/fileformat.html)
- **Prefix and session expiry:** `#HttpOnly_` is an exception to the comment rule and marks HttpOnly cookies. Expiry 0 marks a session cookie. — [curl HTTP cookies docs](https://curl.se/docs/http-cookies.html)
- **curl's `-b`:** it takes a file name or a literal string (`-b name=var` or `-b cookiefile`). Chromium's code relies on this, writing `-b` only when the value contains `=` so curl won't treat it as a file. — [curl HTTP cookies docs](https://curl.se/docs/http-cookies.html); [NetworkLogView.ts](https://raw.githubusercontent.com/ChromeDevTools/devtools-frontend/main/front_end/panels/network/NetworkLogView.ts)
- **yt-dlp's rules:** the file must be Mozilla/Netscape format. The first line must be `# HTTP Cookie File` or `# Netscape HTTP Cookie File`. Line endings must match the OS (CRLF on Windows), and "HTTP Error 400: Bad Request" with `--cookies` usually means wrong newlines. `--cookies-from-browser` combined with `--cookies` writes the browser's cookies for ALL sites to a file. — [yt-dlp FAQ](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)
- **Producers:**
  - Get cookies.txt LOCALLY: no `#HttpOnly_`, expiry 0 for session cookies, subdomain flag from a leading dot. — [cookie_format.mjs](https://raw.githubusercontent.com/kairi003/Get-cookies.txt-LOCALLY/master/src/modules/cookie_format.mjs)
  - cookies.txt for Firefox: no `#HttpOnly_` (commented out), expiry 0 for session cookies. — [background.js](https://raw.githubusercontent.com/hrdl-github/cookies-txt/main/background.js)
  - Cookie-Editor: writes `#HttpOnly_`, and session expiry is now plus one day. — [netscapeFormat.js](https://raw.githubusercontent.com/Moustachauve/cookie-editor/master/interface/lib/netscapeFormat.js)
- **gallery-dl** accepts a path to a Mozilla/Netscape cookies.txt, a name-to-value object, or a browser spec list whose optional fifth element is a domain ("Prefix it with a dot . to include cookies for subdomains"). — [gallery-dl configuration docs](https://gdl-org.github.io/docs/configuration.html)
- **Hydrus** tells users to export cookies.txt with a browser add-on and drag it onto the session cookies dialog under the network menu. It warns this "sometimes does not work if your add-on's export formatting is unusual". — [Hydrus downloading docs](https://hydrusnetwork.github.io/hydrus/getting_started_downloading.html)

### Inferences

**Parser for pasted cookies.txt text:**
1. Split on `\r?\n`.
2. Trim leading whitespace from each line.
3. If a line starts with `#HttpOnly_` (case-insensitive), remove the prefix and mark it HttpOnly.
4. Then skip blank lines and lines starting with `#`.
5. Split on `\t` and require 6 or 7 fields. A missing 7th field means an empty value, as Cookie-Editor does.
6. If a line doesn't split into 6 or 7 fields on tabs, try splitting on runs of whitespace as a fallback. Some editors and chat apps turn tabs into spaces, and cookie names and values can't legally contain spaces (RFC 6265 cookie-octet, from general knowledge, not fetched here).

**Domain filter:** strip a leading `.` from field 0, lower-case it, and keep the line if the site host (for example `hypnotube.com`, `www.hypnotube.com`) equals that domain or ends with `.` plus that domain. Discard everything else before storing. Never keep other sites' cookies, because "all sites" exports are common.

**Fields to ignore:** expiry and the secure flag. Export tools disagree on them (0, now+1 day or a real date), and EdgeLoop regenerates its own temp file anyway. EdgeLoop's current temp file already writes the two required header lines. Writing it with CRLF on Windows (`File.WriteAllLines` default) is consistent with the FAQ.

**Header line not required on input:** Cookie-Editor's import doesn't need it, and users may paste only some lines. Detect the format by tab-separated 7-field lines, not by the header.

**Main pitfall:** a parser that drops every `#` line, as many naive ones do, silently loses PHPSESSID from Cookie-Editor exports, because it is HttpOnly and written as `#HttpOnly_.hypnotube.com…`. Get cookies.txt LOCALLY and cookies.txt exports would still work, so the bug would only show up for some users.

### Gaps
- yt-dlp's own `--cookies-from-browser` export format (whether it writes `#HttpOnly_`) wasn't checked in source.
- An official Netscape specification wasn't fetched. curl's page is the de facto reference. The `curl.haxx.se/rfc/cookie_spec.html` link in the headers was not followed.

---

## 5. How popular tools guide users through cookie export, and what users find confusing

### Takeaway
yt-dlp and gallery-dl steer users toward reading cookies straight from the browser profile, with a cookies.txt extension as the fallback. Hydrus steers users to a companion extension or a cookies.txt drag-and-drop. All of them warn about extension safety or sensitive files. The recurring confusions are: wrong extension (the malicious look-alike), wrong format (header, newlines), exporting all sites, and expired or stale cookies.

### Cited Findings
- **yt-dlp:** the easiest method is `--cookies-from-browser`. Otherwise use `--cookies FILE`, with "Get cookies.txt LOCALLY" recommended for Chrome and "cookies.txt" for Firefox, plus a warning to be careful with extensions and to uninstall the old "Get cookies.txt". The file must start with the Netscape header, line endings matter (HTTP 400 as a symptom), and the exported file holds cookies for ALL sites, so "take care in not letting this text file fall into the wrong hands". For Cloudflare 403s, cookies must come from a session "within the past 30 minutes", and Firefox reportedly works best while some users had trouble with Edge. — [yt-dlp FAQ](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)
- **gallery-dl:** the `cookies` option takes a cookies.txt path, a `{name: value}` object, or a browser list such as `["firefox"]` (with optional profile, keyring, container and domain). `cookies-update` writes updated session cookies back to the cookies.txt file. — [gallery-dl configuration docs](https://gdl-org.github.io/docs/configuration.html)
- **Hydrus:** the built-in login system is "ancient" and "often breaks", so users should "copy your cookies across instead with Hydrus Companion or similar!", or drag an exported cookies.txt onto the session cookies dialog. Unusual add-on formatting can fail. Hydrus Companion also forwards the User-Agent. Hydrus warns it "does not store credentials securely". — [Hydrus downloading docs](https://hydrusnetwork.github.io/hydrus/getting_started_downloading.html)
- **Fragile cmd output:** the cmd variant of "Copy as cURL" confuses users of other tools, and curlconverter's answer is to point Windows users at "Copy as cURL (bash)". — [curlconverter issue #143](https://www.github.com/curlconverter/curlconverter/issues/143)
- **Copycat extensions:** confusion is a real risk, as with "EditThisCookie®" and "Get cookies.txt Clean". — [Cyber Security News](https://cybersecuritynews.com/malicious-editthiscookie-chrome-extension/amp/); [Chrome Web Store, via search](https://chromewebstore.google.com/detail/get-cookiestxt-clean/ahmnmhfbokciafffnknlekllgcnafnie)

### Inferences
- The points of confusion that matter for EdgeLoop:
  1. `document.cookie` misses HttpOnly cookies, which EdgeLoop already warns about.
  2. Choosing the wrong request in the Network tab.
  3. Choosing "all sites" in an extension.
  4. Pasting a format the app doesn't understand.
  5. Signing out in the browser later, which probably ends the server-side session, so EdgeLoop's copy stops working. That is inferred from how PHP sessions usually work, not verified for these sites.

  Accepting every common format removes point 4 entirely and lets the instructions be shorter.
- Other tools' habit of trusting a cookies.txt that may contain every site suggests EdgeLoop should filter by domain at paste time and show "Found PHPSESSID for hypnotube.com (N cookies kept, M other-site cookies ignored)". That confirmation tells users it worked without making them understand formats.

### Gaps
- Guidance in JDownloader and Stash was not researched in this session (tool budget). Stash scrapers are known to take cookies in YAML config, but no source was fetched.
- No forum or issue-tracker survey of the most common user mistakes was done beyond the FAQ and the curlconverter thread.
- Browser cookie extraction (`--cookies-from-browser firefox`, Chrome's App-Bound Encryption on Windows) is outside this copy-paste scope and wasn't researched here.

---

## 6. Can EdgeLoop accept every pasted format and auto-detect which one it got? Pitfalls

### Takeaway
Yes. The formats look different enough that EdgeLoop can tell them apart reliably by checking in a fixed order: JSON, then cURL, then PowerShell, then cookies.txt, then a headers block, then a `Cookie:` header, then pairs, then a bare token. Every route then becomes the same name/value list, filtered to the site's domain, and the existing PHPSESSID check reports success. The main pitfalls are `#HttpOnly_` lines treated as comments, other sites' cookies in all-site exports, a cURL command copied from the wrong host, cmd-style `^` escaping, and Cookie-Editor's semicolon-without-space header string.

### Cited Findings
- **Cookie-Editor:** Header string is `a=1;b=2` (no space), JSON is an array of cookie objects, and Netscape uses `#HttpOnly_`. — [headerstringFormat.js](https://raw.githubusercontent.com/Moustachauve/cookie-editor/master/interface/lib/headerstringFormat.js); [jsonFormat.js](https://raw.githubusercontent.com/Moustachauve/cookie-editor/master/interface/lib/jsonFormat.js); [netscapeFormat.js](https://raw.githubusercontent.com/Moustachauve/cookie-editor/master/interface/lib/netscapeFormat.js)
- **Get cookies.txt LOCALLY:** header format is `name=value;` joined with a space (trailing `;`), JSON is the raw `chrome.cookies.Cookie` array, and Netscape has no `#HttpOnly_`. — [cookie_format.mjs](https://raw.githubusercontent.com/kairi003/Get-cookies.txt-LOCALLY/master/src/modules/cookie_format.mjs)
- **Firefox cURL:** cookies appear as `-H 'Cookie: …'`, or in the Windows variant `-H ^"Cookie: …^"` with `^` escapes and `\"`. — [Firefox curl.js](https://raw.githubusercontent.com/mozilla-firefox/firefox/main/devtools/client/shared/curl.js)
- **Chromium cURL and PowerShell:** cookies appear as `-b '…'` (bash) or `-b ^"…^"` (cmd, with `""` for quotes). The fallback is `-H 'cookie: …'`, with the header name in lower case because of HTTP/2. PowerShell uses `System.Net.Cookie("name","value","/","domain")` lines. — [NetworkLogView.ts](https://raw.githubusercontent.com/ChromeDevTools/devtools-frontend/main/front_end/panels/network/NetworkLogView.ts)
- **Domain scope:** extensions' "all" exports cover every site, and gallery-dl's browser import takes an explicit domain filter for this reason. — [AMO API, cookies.txt](https://addons.mozilla.org/api/v5/addons/addon/cookies-txt/); [gallery-dl configuration docs](https://gdl-org.github.io/docs/configuration.html)
- **Sanitised HAR:** Chrome's "Copy all as HAR (sanitized)" removes Cookie headers, so a HAR paste may contain no cookies at all. — [Chrome DevTools Network reference](https://developer.chrome.com/docs/devtools/network/reference)
- **Current EdgeLoop parsing:** it splits only on `;` and strips `Cookie:`, so a pasted cURL command, cookies.txt or JSON would be stored as garbage today. — D:/Projects/EdgeLoop/EdgeLoop/ViewModels/SettingsViewModel.cs (lines 476-484); D:/Projects/EdgeLoop/EdgeLoop/Classes/YtDlpService.cs (lines 806-825)

### Inferences

#### Proposed detection order (first match wins, on trimmed text)
1. **JSON.** Text starts with `[` or `{` and parses. Take an array (or `{cookies:[...]}`) of objects with `name` and `value`. Filter on `domain` when it is present. Both Cookie-Editor and Get cookies.txt LOCALLY use the browser's cookie object field names (`domain`, `name`, `value`, `httpOnly`, `hostOnly`, `expirationDate`, `session`), so one parser covers both. Also accept a flat `{ "name": "value" }` object, the gallery-dl style.
2. **cURL.** Text starts with `curl ` or `curl.exe ` (case-insensitive).
   - First unescape. If the text has `^"`, treat it as cmd style: remove ` ^` plus newline continuations, turn `^X` into `X`, and turn `""` (Chromium) or `\"` (Firefox) into `"`. Otherwise treat it as POSIX: remove ` \` plus newline continuations, and decode `'…'` and `$'…'` (`\\ \' \n \r \xHH \uHHHH \NNN`).
   - Then tokenise the arguments and collect cookies from `-b`/`--cookie <value>`, and from `-H`/`--header <value>` whose name is `cookie` (case-insensitive). Ignore a `-b` value with no `=`, because that would be a file name.
   - Also read the URL argument's host, checked against the target site, and the `User-Agent` header (optional reuse).
3. **PowerShell (Chromium).** Text contains `Invoke-WebRequest` or `WebRequestSession`. Regex out each `System.Net.Cookie("name", "value", "/", "domain")` and undo backtick escapes (`` `" `` and `` `$ ``). Also check for a `"Cookie"="…"` entry in `-Headers @{}` in case Firefox's PowerShell variant uses headers, which is unverified.
4. **Netscape cookies.txt.** Any line, after an optional `#HttpOnly_`, has 6 or 7 tab-separated fields with TRUE/FALSE in fields 1 and 3, or the text contains `# Netscape HTTP Cookie File` or `# HTTP Cookie File`. Parse as in question 4 and filter by domain.
5. **A block of raw request headers** (Firefox "Copy Request Headers", the Raw-view selection, or "Copy All" JSON). Several lines look like `Name: value`, or there is a request line such as `GET / HTTP/2`. Take the `Cookie:` line's value. For Copy All JSON, look for a `Cookie` key.
6. **A single header.** Text starts with `Cookie:`, which is the current behaviour.
7. **Header value.** Text contains `=`. Split on `;`, trim each part, split on the first `=`, and drop empty parts. This handles Cookie-Editor's `a=1;b=2`, the LOCALLY extension's trailing `;`, and DevTools' `a=1; b=2`.
8. **Bare token.** No `=`, `;` or whitespace, and the token looks like a session ID. Treat it as `PHPSESSID=<token>` for Hypnotube or Rule34Video, and show that assumption to the user. Reject the literal text "PHPSESSID".

#### After parsing
- Normalise to `name=value; name2=value2` and store that, which keeps the current storage and `CreateTempCookieFile` unchanged.
- Remove duplicate names, last one wins. A cookies.txt with both `.site` and `www.site` entries can have duplicates.
- Run the existing PHPSESSID check, and report what was found: format detected, cookie count kept, cookies from other sites dropped, and whether PHPSESSID is present.
- Never store cookies whose domain doesn't match the site box they were pasted into. For a cURL paste, warn if the URL host isn't the site, for example when the user right-clicked an ad or CDN request.

#### Pitfalls to design around
- **`#HttpOnly_`:** a parser that treats it as a comment loses PHPSESSID, but only for Cookie-Editor users.
- **All-site exports:** these are privacy-heavy, so filter at once and don't keep the raw paste in an undo buffer or logs.
- **cmd escaping:** `^` and `%^` sequences left unremoved would corrupt values. This matters for any URL-encoded cookie value containing `%`.
- **Firefox cURL:** never uses `-b`. Chromium uses `-b` only when the value has `=`.
- **Header name case:** Chromium's `-H 'cookie: …'` fallback is lower case, so match header names case-insensitively.
- **Wrong cookie scope:** a cURL or Cookie header only holds cookies for that request's URL. Copying a subresource request from a different host gives the wrong site's cookies, or none.
- **Sanitised HAR:** contains no cookies, so a HAR paste should be rejected with a clear message, or the Cookie header pulled from it if present.
- **Tabs to spaces:** cookies.txt pasted through some apps loses its tabs, so use the whitespace-split fallback.
- **Masked text box:** EdgeLoop's box shows dots unless "Show" is on (SettingsWindow.xaml line 53). Long cURL or cookies.txt pastes are fine for parsing, but EdgeLoop should show the parse summary rather than the raw text.
- **Safety:** cURL and cookies.txt pastes hold more secrets than needed. Other headers such as Authorization could be present, and all-site cookies too. Store only the filtered name/value pairs, which the existing Windows encryption already protects.

#### Ranking for a non-technical Firefox user (inference)
1. **Firefox DevTools, right-click the first request, Copy, then "Copy as cURL" (either variant) or "Copy Request Headers".** Nothing to install, it includes HttpOnly cookies, it is scoped to one site, and it is only a couple of clicks. It needs the parsers above.
2. **The "cookies.txt" add-on's current-site Copy button, or Get cookies.txt LOCALLY's Copy button.** This is the easiest flow by clicks, but it means installing an extension, which brings the trust and auto-update risk. It needs the Netscape parser.
3. **The current header Copy Value route.** It works, but is the most fiddly.
4. **PHPSESSID only, from the Storage Inspector.** A short paste, but there is a risk of editing the live cookie, and it is unclear whether one cookie is enough.

Cookie-Editor is the most capable extension and runs on every browser, but its formats (`#HttpOnly_`, no-space header string) need the most careful parsing, and its last update was in 2024.

### Gaps
- Whether Hypnotube or Rule34Video bind the session to the User-Agent or IP address, which would make reusing the cURL User-Agent necessary rather than optional, wasn't researched.
- No sample output of Firefox "Copy Request Headers", Firefox "Copy as PowerShell" or Firefox Headers "Copy All" (JSON) was obtained, so the exact detection rules for formats 3 and 5 need testing against real copies (made by the user, not by an agent).
- The PHP session ID alphabet and length used for the bare-token check weren't verified against the PHP manual in this session.
