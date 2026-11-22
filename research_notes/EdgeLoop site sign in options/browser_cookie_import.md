# Reading a site's cookies from the user's installed browser on Windows 10/11 (as of November 2026)

Scope: whether EdgeLoop (.NET 10 WPF, bundles yt-dlp) can automatically pick up the signed-in PHPSESSID cookie for Hypnotube and Rule34Video from the user's browser (this user: Firefox), either by reading the browser's cookie store itself or by asking yt-dlp to export it. Research used docs, issues and source code only. No real cookie stores were read and yt-dlp was not run.

Source-quality note: entries marked "(search summary)" came from search-result summaries, not a page I fetched and read in full. Treat them as lower confidence.

## Firefox: location, profiles, encryption, locking, copying, containers, HttpOnly

### Takeaway
Firefox is the one mainstream Windows browser where a normal, non-elevated app can still read cookies reliably in November 2026. cookies.sqlite is plain SQLite with unencrypted values. Firefox holds an exclusive lock on it while running, but copying the file and reading the copy works, which is what yt-dlp does. The real pitfalls are choosing the right profile, recent changes that sit only in the `-wal` file, containers, and a new Firefox SQLite-encryption effort that could end plaintext access in future.

### Cited Findings
- **Where yt-dlp looks on Windows.** yt-dlp searches `%APPDATA%\Mozilla\Firefox\Profiles` and the Microsoft Store (MSIX) path `%LOCALAPPDATA%\Packages\Mozilla.Firefox_n80bbvh6b1yt2\LocalCache\Roaming\Mozilla\Firefox\Profiles`. It globs for `cookies.sqlite` (patterns `''`, `'*/'`, `'Profiles/*/'`) and does **not** parse profiles.ini. — [yt-dlp cookies.py](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/cookies.py)
- **How yt-dlp picks a profile.** With no profile given, it takes the `cookies.sqlite` with the newest modification time (`_newest()`, `max(... st_mtime)`). A profile name is joined to each search root, and a value containing a path separator is used directly as a path. — [yt-dlp cookies.py](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/cookies.py)
- **Workaround with an explicit profile.** One user in the Chrome issue suggested signing in on a dedicated Firefox profile and passing `--cookies-from-browser firefox:<profile dir>`. — [yt-dlp #10927](https://github.com/yt-dlp/yt-dlp/issues/10927)
- **Firefox's own profile choice (profiles.ini).** Since Firefox 67, profiles.ini has per-install sections whose names are a hash of the install directory. Each one records that install's dedicated default profile and whether the profile is "locked" to that install. — [Mozilla KB: Understanding in depth the profile per installation feature](https://support.mozilla.org/en-US/kb/understanding-depth-profile-installation) (search summary); [Mozilla support: What does "Locked=1" in profiles.ini do?](https://support.mozilla.org/en-US/questions/1276002) (search summary)
- **Encryption.** Firefox stores cookie values in plaintext in cookies.sqlite.
  - Bug 56788, "Cookies need to be encrypted when stored on disk", is the long-standing tracker. — [Bugzilla 56788](https://bugzilla.mozilla.org/show_bug.cgi?id=56788) (search summary)
  - In 2025 a reporter showed that a non-admin user could export all Firefox cookies silently on Windows 11 using yt-dlp's cookie extractor. Mozilla called it a "well known issue", said encrypting the cookie database is "potentially" on the roadmap, pointed to Device Bound Session Credentials, and closed the bug as a duplicate of 56788. — [Bugzilla 1964105](https://bugzilla.mozilla.org/show_bug.cgi?id=1964105)
- **Locking.** Since Firefox 3.5, cookies.sqlite is opened with an exclusive lock "for performance reasons" (see bug 449987, "open cookie db unshared and exclusive"). A request to allow third-party read access was RESOLVED WONTFIX. — [Bugzilla 499099](https://bugzilla.mozilla.org/show_bug.cgi?id=499099)
- **Copying.** yt-dlp copies the database to a temporary directory before opening it (`_open_database_copy` → `shutil.copy` then `sqlite3.connect`). The code comment reads "cannot open sqlite databases if they are already in use (e.g. by the browser)". It copies only the main file, and yt-dlp has no Firefox-specific locked-file error handler. — [yt-dlp cookies.py](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/cookies.py)
- **WAL and staleness.**
  - Firefox keeps an in-memory cookie table and uses cookies.sqlite only as an on-disk backing store. The `-wal` and `-shm` files exist while Firefox runs. A copy of cookies.sqlite alone can miss recent changes; in one support thread, deleted cookies "reappeared" because of the `cookies.sqlite-wal` file. — Mozilla support threads, for example [questions/1273455](https://support.mozilla.org/questions/1273455) and [questions/864014](https://support.mozilla.org/en-US/questions/864014) (search summary; I did not confirm which thread holds which quote)
  - A Python tutor thread notes that copying cookies.sqlite is the usual way to read it while Firefox is open. — [python.org tutor list 2020](https://mail.python.org/pipermail/tutor/2020-June/116830.html) (search summary)
- **Containers.**
  - yt-dlp supports `firefox::CONTAINER`. It resolves the name through `containers.json` (matching `name` or `l10nID`) and filters with `originAttributes LIKE '%userContextId=<id>'`.
  - `::none` selects only cookies with no `userContextId` (`WHERE NOT INSTR(originAttributes,'userContextId=')`).
  - Error strings include `could not find firefox container "{container}" in containers.json`.
  - — [yt-dlp cookies.py](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/cookies.py)
- **HttpOnly.** yt-dlp's Firefox query is `SELECT host, name, value, path, expiry, isSecure FROM moz_cookies ...` with no HttpOnly filter. — [yt-dlp cookies.py](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/cookies.py)
- **Future encryption risk (new in 2026).**
  - Bug 1996558, "Automatically encrypt SQLite databases on disk" (Core :: SQLite), is RESOLVED FIXED (milestone 154 branch, firefox153 "fixed").
  - It makes ObfuscatingVFS the default SQLite VFS, with per-file keys from a local "lockstore" key (`KekType::LocalKey`), not DPAPI or an OS keystore.
  - It is gated behind the pref `security.storage.encryption.sqlite.enabled`, which can be set through enterprise policy. The patches that would turn it on by default were marked obsolete.
  - I could not confirm that cookies.sqlite is covered; it appears only in obsolete work-in-progress patch text.
  - — [Bugzilla 1996558](https://bugzilla.mozilla.org/show_bug.cgi?id=1996558)
- **Related open work.** Meta bug 2014232, "[meta] Encrypt sensitive storage" (P1/S1, Enterprise Products :: Firefox), is still open; its reporter says "Firefox profiles are stored plainly on disk". Bug 2049157, a migration from plaintext to encrypted SQLite, is ASSIGNED with work-in-progress patches only and no release targeted (firefox161–163 flags unset). — [Bugzilla 2014232](https://bugzilla.mozilla.org/show_bug.cgi?id=2014232); [Bugzilla 2049157](https://bugzilla.mozilla.org/show_bug.cgi?id=2049157)
- **Expiry units.** Bug 1972757, "Store cookie expiry value as msec", is fixed in the 142 branch. It concerns the CookieStore API, and I could not confirm whether it changed the `moz_cookies.expiry` units on disk. — [Bugzilla 1972757](https://bugzilla.mozilla.org/show_bug.cgi?id=1972757) (search summary)

### Inferences
- **HttpOnly does not matter.** HttpOnly only limits page JavaScript. The PHPSESSID row is in `moz_cookies` like any other cookie, and yt-dlp's query does not exclude it.
- **The newest-file heuristic can pick the wrong profile.** If the user has several profiles, for example an old one Firefox still touches, it may return the wrong session. A robust EdgeLoop should:
  - Parse `%APPDATA%\Mozilla\Firefox\profiles.ini` and `installs.ini`, take the `[Install…] Default=` profile (falling back to `[Profile*] Default=1`), and also check the MSIX path.
  - Let the user pick a profile when there is more than one.
  - Pass the explicit profile path to yt-dlp (`firefox:<path>`) rather than relying on mtime.
- **Copy all three files to get recent changes.** Copying cookies.sqlite, cookies.sqlite-wal and cookies.sqlite-shm together to a temporary folder (same base names), then opening the copy, should let SQLite replay the WAL and include a sign-in made seconds earlier. Copying only the main file, as yt-dlp does, may miss a just-created PHPSESSID until Firefox checkpoints or exits. This follows from SQLite's WAL design and the Mozilla threads above; I did not test it.
- **Why copying works despite the lock.** SQLite's Windows locks are byte-range locks outside the data region for small databases, so a plain file copy can usually read the file. This is background knowledge, not a source I fetched. yt-dlp's long use of copy-then-open on Windows, and the bug 1964105 report of silent extraction on Windows 11, are consistent with it.
- **Containers.** If the user signed in to Hypnotube inside a Multi-Account Container, the cookie carries `userContextId=N`. When no container is given, yt-dlp's code appears to return cookies from all containers mixed together. This is inferred from the branch structure, as only the named-container and `none` branches were quoted. EdgeLoop should either accept any container or filter by host and take the newest.
- **Private windows.** Private-browsing cookies are not written to cookies.sqlite, so a sign-in made in a private window cannot be picked up. This is background knowledge, not sourced.
- **Firefox's plaintext store is not guaranteed to last.** A pref-gated encryption layer already exists in Firefox 153+. If Mozilla turns it on for consumer profiles and it covers cookies.sqlite, reading the file directly would break the same way Chrome did in 2024.

### Gaps
- I could not confirm the default value of `security.storage.encryption.sqlite.enabled` in consumer Firefox as of November 2026, or whether cookies.sqlite is in scope.
- I did not verify how Firefox's newer profile manager (profile groups) changes profiles.ini or profile discovery.
- I did not fetch the SQLite documentation on exclusive locking mode with WAL, so the "copy the -wal too" advice is untested reasoning.
- I could not confirm whether the Firefox 142 expiry-units change affects `moz_cookies.expiry` or yt-dlp's parsing.

## Chrome: app-bound encryption and the state of third-party decryption in 2026

### Takeaway
Since Chrome 127 (July 2024), Chrome cookies on Windows are encrypted with an app-bound key that only Chrome's SYSTEM-level elevation service will unwrap for Chrome's own executable. A normal non-elevated app cannot decrypt them in 2026 without techniques that are themselves infostealer behaviour. yt-dlp's `--cookies-from-browser chrome` has been broken on Windows since late 2024, and its maintainers treat this as an external issue they will not work around. Chrome also locks its cookie database while running.

### Cited Findings
- **What app-bound encryption is.** Chrome 127 introduced App-Bound Encryption on Windows, starting with cookies. Passwords, payment data and other tokens were to follow. A privileged service checks the identity of the requesting app at encryption and decryption time, so "if another app on the system tries to decrypt the same data, it will fail". The service runs with system privileges, so attackers must gain those privileges or inject into Chrome.
  - Chrome logs Event ID 257 (source "Chrome", Application log) when verification fails.
  - The key is bound to the machine, so the feature breaks roaming profiles; the `ApplicationBoundEncryptionEnabled` policy can turn it off.
  - The post describes no supported way for third-party apps to get access.
  - — [Google Security Blog, 30 Jul 2024](https://security.googleblog.com/2024/07/improving-security-of-chrome-cookies-on.html?m=1)
- **Key details and known bypasses.** The `app_bound_encrypted_key` in Local State is protected by both the SYSTEM and the user DPAPI master keys. The elevation service's `DecryptData` COM method checks the caller's path. Chrome adds an extra Chrome-only `PostProcessData` layer.
  - A "threat actor can no longer steal cookies while running as any other user-level process."
  - Remaining routes are running from the browser's directory, injecting into the browser, enabling remote debugging, loading a custom extension, or re-implementing the decryption as SYSTEM.
  - — [SpecterOps, "Dough No! Revisiting Cookie Theft", 27 Aug 2025](https://specterops.io/blog/2025/08/27/dough-no-revisiting-cookie-theft/)
- **Debugging and extension routes were closed off.**
  - From Chrome 136 (March 2025 post), `--remote-debugging-port` and `--remote-debugging-pipe` are ignored for the default data directory. Google says it saw more attackers use remote debugging to extract cookies after app-bound encryption arrived. — [Chrome for Developers: Changes to remote debugging switches](https://developer.chrome.com/blog/remote-debugging-port)
  - Chrome 137+ blocks `--load-extension` unless `--disable-features=DisableLoadExtensionCommandLineSwitch` is set. — [SpecterOps 2025](https://specterops.io/blog/2025/08/27/dough-no-revisiting-cookie-theft/)
- **Bypasses keep appearing (2025–2026).**
  - An August 2026 SpecterOps post describes turning on the DevTools server from inside an already-running chrome.exe or msedge.exe. — [SpecterOps, "Return of the Cookie Monster", 13 Aug 2026](https://specterops.io/blog/2026/08/13/chrome-devtools-protocol-cookie-theft/) (search summary)
  - The VoidStealer infostealer (March 2026) attaches to Chrome or Edge as a debugger and reads the key from memory, without privilege escalation or injection. — [Kaspersky blog](https://www.kaspersky.com/blog/chrome-application-bound-encryption-bypass-voidstealer/55735/) (search summary)
  - CyberArk's "C4 Bomb" research showed decryption as a low-privileged user. — [CyberArk](https://www.cyberark.com/resources/threat-research-blog/c4-bomb-blowing-up-chromes-appbound-cookie-encryption) (search summary; I did not confirm patch status)
- **yt-dlp status, issue #10927 (still open in 2026).**
  - "`--cookies-from-browser`: WARNING: failed to decrypt with DPAPI / ERROR: 'NoneType' object has no attribute 'decode'" was opened on 5 Sep 2024 and is labelled `bug`, `core:cookies`, `external-issue`. The conversation was locked on 7 Jan 2026.
  - A contributor warned that once the change rolls out, "`--cookies-from-browser chrome` will be permanently broken on Windows". No maintainer committed to supporting v20 cookies.
  - Suggested workarounds: use Firefox, export a cookies.txt with an extension, or use a dedicated Firefox profile.
  - One user (Jan 2026, Chrome 143) reported success after disabling the `ApplicationBoundEncryptionEnabled` policy, reinstalling Chrome and signing in again. A maintainer warned this disables a security feature and "It will wipe all of your cookies."
  - — [yt-dlp #10927](https://github.com/yt-dlp/yt-dlp/issues/10927)
- **What yt-dlp's source does with v20 cookies.**
  - The current cookies.py reads only `os_crypt.encrypted_key` (the `DPAPI`-prefixed v10 key). It decrypts `v10` values with AES-GCM and treats any other prefix as raw DPAPI. There is no `v20` or app-bound handling, so v20 values fall through to DPAPI and fail with "Failed to decrypt with DPAPI. See https://github.com/yt-dlp/yt-dlp/issues/10927 for more info".
  - It also strips a 32-byte hash prefix from decrypted values when the DB `meta` version is 24 or higher.
  - — [yt-dlp cookies.py](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/cookies.py)
- **Database locking, yt-dlp issue #7271 (still open).**
  - "Permission denied" errors when Chrome or a Chromium-based browser is open on Windows, opened 9 Jun 2023, labelled `external-issue`, locked 29 Dec 2025, with a duplicate marked on 7 Feb 2026.
  - Maintainer workarounds: fully close Chrome, launch it with `--disable-features=LockProfileCookieDatabase`, or use the ChromeCookieUnlock plugin. Users in late 2025 reported the plugin no longer helps.
  - The source raises "Could not copy Chrome cookie database. See https://github.com/yt-dlp/yt-dlp/issues/7271 for more info" on Windows errno 13.
  - — [yt-dlp #7271](https://github.com/yt-dlp/yt-dlp/issues/7271); [yt-dlp cookies.py](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/cookies.py)
- **Other libraries.**
  - The Rust library rookie says it can bypass Chrome's file locking and app-bound encryption but "requires admin rights on Windows from v130.x". Its repository was archived (read-only) on 7 Jun 2026. — [rookie on GitHub](https://github.com/thewh1teagle/rookie)
  - I found no evidence that browser_cookie3 handles v20 cookies. — [browser_cookie3](https://github.com/borisbabic/browser_cookie3) (search summary)
- **Direction of travel: Device Bound Session Credentials (DBSC).** DBSC binds sessions to TPM-held keys with short-lived cookies. It shipped on Windows in Chrome 145/146 (sources differ on the version) and was generally available for Google Workspace on 28 May 2026. — [Chrome for Developers: DBSC on Windows](https://developer.chrome.com/blog/dbsc-windows-announcement) (search summary); [Google Workspace Updates, May 2026](https://workspaceupdates.googleblog.com/2026/05/prevent-account-takeovers-with-DBSC-now-generally-available-in-the-Chrome-browser-for-Windows.html) (search summary)

### Inferences
- **Do not try to read Chrome cookies.** For a consumer video player, reading Chrome cookies in 2026 is not a reasonable feature. Every working method needs admin or SYSTEM rights, debugger attachment, injection, remote debugging, or turning off a browser security policy. Those are the same behaviours EDR tools hunt for, and Google keeps closing them. EdgeLoop's "Browser to sign in with" setting should not offer Chrome on Windows as an automatic option, or should warn that it will fail.
- **Locking hurts Chrome users even for unencrypted data.** Because Chrome locks its cookie DB, Chrome would need to be fully closed even when the data is readable.
- **DBSC is not a near-term threat for these sites.** It only applies to sites that implement it, and Hypnotube and Rule34Video are unlikely to. It shows browsers moving toward cookies that cannot be used off-device.

### Gaps
- I did not find an official Chromium statement from 2025–2026 that third-party access will never be supported. Google's post simply offers none.
- I could not verify whether the VoidStealer, C4 or in-process DevTools techniques were patched by November 2026.
- I did not confirm exactly which Chrome version introduced `LockProfileCookieDatabase`; the issue dates suggest mid-2023.

## Microsoft Edge, Brave, Opera and Vivaldi

### Takeaway
Edge has supported app-bound encryption since version 127, and it is on by default (enabled or unset means on). In practice Edge behaves like Chrome for third-party readers: v20 cookies cannot be decrypted, and the DB is locked while Edge runs. yt-dlp users report Edge failing in late 2025. For Brave, Opera and Vivaldi I found no primary source; secondary sources say they inherit Chromium's app-bound encryption.

### Cited Findings
- **Edge policy.** `ApplicationBoundEncryptionEnabled` is supported on Windows from Edge 127. "Enabling this policy or leaving it unset binds the encryption keys used for local data storage to Microsoft Edge whenever possible."
  - Disabling it lets "unknown and potentially hostile apps" retrieve the keys. It should only be turned off for compatibility, "such as scenarios where other applications need legitimate access to Microsoft Edge's data".
  - Registry value: `HKLM\SOFTWARE\Policies\Microsoft\Edge\ApplicationBoundEncryptionEnabled` (DWORD); browser restart required. The page was updated 3 Nov 2026.
  - — [Microsoft Learn: ApplicationBoundEncryptionEnabled](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-browser-policies/applicationboundencryptionenabled)
- **Edge's weaker spots (as of Aug 2025).** Edge uses the `app_bound_encrypted_key` but does not implement Chrome's extra `PostProcessData` layer. Edge also still honoured `--remote-debugging-port` without an alternate data directory, provided all Edge instances were closed, and still allowed `--load-extension`. — [SpecterOps 2025](https://specterops.io/blog/2025/08/27/dough-no-revisiting-cookie-theft/)
- **yt-dlp and Edge.** A maintainer said the lock problem "affects Windows versions of Chrome and some other chromium-based browsers such as Edge". A December 2025 user reported that `--cookies-from-browser edge` fails whether or not the browser is open. — [yt-dlp #7271](https://github.com/yt-dlp/yt-dlp/issues/7271)
- **Browsers yt-dlp lists.** yt-dlp lists `brave, chrome, chromium, edge, opera, vivaldi, whale` as Chromium-based, all sharing the same Windows decryptor with no v20 support. — [yt-dlp cookies.py](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/cookies.py)
- **Brave, Opera, Vivaldi.** Coverage of VoidStealer says Edge, Opera, Vivaldi and Brave are "compatible with" app-bound encryption and potentially exposed to the same bypass. — [Kaspersky blog](https://www.kaspersky.com/blog/chrome-application-bound-encryption-bypass-voidstealer/55735/) (search summary). A Vivaldi forum thread refers to a cookie-encryption feature enabled via vivaldi://flags, but does not name app-bound encryption. — [Vivaldi forum](https://forum.vivaldi.net/topic/103076/add-option-to-encrypt-cookies-in-security-settings) (search summary)

### Inferences
- **Treat all Chromium browsers like Chrome.** Edge, Brave, Opera and Vivaldi on Windows should be treated as "automatic import will not work" unless a specific browser is shown to still write v10 cookies. Even then the DB lock means the browser must be closed. Only Firefox and its forks (for example LibreWolf, Zen, Waterfox) are realistic automatic sources. That fork support is an inference: they share Firefox's storage code, and rookie lists LibreWolf and Zen.

### Gaps
- I found no first-party documentation from Brave, Opera or Vivaldi confirming app-bound encryption is on by default in their 2026 Windows builds.
- I found no Microsoft blog post explaining how Edge implements it.

## yt-dlp as the exporter (cookies.txt) and which browsers work today

### Takeaway
yt-dlp can dump a browser's cookies to a Netscape cookies.txt by combining `--cookies-from-browser BROWSER` with `--cookies FILE`. The FAQ example has no URL. It exports cookies for all sites, not just the requested one. In November 2026 this works for Firefox, including profile and container selection, and fails for Chrome, Edge and other Chromium browsers on Windows.

### Cited Findings
- **Export command.** The FAQ shows `yt-dlp --cookies-from-browser chrome --cookies cookies.txt`; yt-dlp "extract[s] the browser cookies and save[s] them to the filepath specified after --cookies". It warns the file holds your browser's cookies "for ALL sites (even if you passed a URL to yt-dlp)".
  - Users should "take care in not letting this text file fall into the wrong hands".
  - The file must begin with `# HTTP Cookie File` or `# Netscape HTTP Cookie File`, and line endings matter: CRLF on Windows, LF on Unix-like systems. "HTTP Error 400: Bad Request" is a symptom of wrong line endings.
  - The old "Get cookies.txt" Chrome extension "has been reported as malware and removed from the Chrome Web Store"; the FAQ recommends "Get cookies.txt LOCALLY" for Chrome and "cookies.txt" for Firefox.
  - — [yt-dlp FAQ wiki](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)
- **Supported browsers.** `SUPPORTED_BROWSERS = CHROMIUM_BASED_BROWSERS | {'firefox', 'safari'}`; Safari is accepted only on macOS and iOS. Firefox supports `:PROFILE` and `::CONTAINER`. — [yt-dlp cookies.py](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/cookies.py)
- **Current breakage.** Chromium browsers on Windows fail with "Failed to decrypt with DPAPI" (#10927) or "Could not copy Chrome cookie database" (#7271). Both issues are open and locked as of early 2026. — [yt-dlp #10927](https://github.com/yt-dlp/yt-dlp/issues/10927); [yt-dlp #7271](https://github.com/yt-dlp/yt-dlp/issues/7271)
- **Firefox complaints are usually site-side.** A 2025 Firefox report was really YouTube rotating cookies; a maintainer said "Nothing was changed with cookie handling in the latest update". This is a reminder that some sites invalidate exported sessions when the browser keeps using them. — [yt-dlp #13445](https://github.com/yt-dlp/yt-dlp/issues/13445)

### Inferences
- **Two ways for EdgeLoop to use yt-dlp.** Both copy only the main cookies.sqlite, so a very fresh sign-in may be missed until Firefox checkpoints the WAL or closes.
  - **Option A (no extra file):** keep passing `--cookies-from-browser firefox[:profile]` to yt-dlp, as EdgeLoop already does. yt-dlp then gets the session for its own requests.
  - **Option B (export, then filter):** run the bundled yt-dlp once with `--cookies-from-browser firefox[:profile] --cookies <temp>\cookies.txt`, keep only lines whose domain ends in `hypnotube.com` or `rule34video.com`, then delete the temp file. Without a URL, yt-dlp probably exits with "You must provide at least one URL" after writing the file. That comes from my reading of yt-dlp's exit behaviour, not from a source; passing a harmless URL with `--skip-download` is safer. The drawback is that every site's cookies are written to disk in plaintext, briefly.
- **Reading cookies.sqlite directly is better for privacy.** For Firefox, EdgeLoop can open its own copy of cookies.sqlite (plus `-wal` and `-shm`) with Microsoft.Data.Sqlite. It can then run `SELECT name, value, host, path, expiry, isSecure, isHttpOnly, originAttributes FROM moz_cookies WHERE host LIKE '%hypnotube.com' OR host LIKE '%rule34video.com'` and delete the copy. Only the two sites' cookies ever enter EdgeLoop's memory, no full-jar file is written, and recent changes in the WAL are included. The `isHttpOnly` and `originAttributes` column names are background knowledge of the Firefox schema; the yt-dlp source confirms `host`, `name`, `value`, `path`, `expiry`, `isSecure` and `originAttributes`.
- **An expiry check gives a clear prompt.** Either way, EdgeLoop should check the cookie's expiry and tell the user to sign in again in Firefox when PHPSESSID is missing or expired.

### Gaps
- I could not fetch the README's `--cookies-from-browser` help text (the page was truncated), so the exact `BROWSER[+KEYRING][:PROFILE][::CONTAINER]` wording and default-profile text are taken from the source, not the README.
- I did not verify yt-dlp's exit code or behaviour when the export runs without a URL.

## Security and privacy: antivirus/EDR, user expectations, how other tools handle it

### Takeaway
Reading a browser's cookie store from a non-browser process is the core behaviour of infostealers. Public detection rules target exactly this, though at low severity and with false-positive notes for legitimate tools. Touching Chrome's key machinery (DPAPI decrypts of browser data, the elevation service, debugging or injection) is far more likely to be flagged than copying Firefox's cookies.sqlite. I found no evidence that consumer antivirus blocks a signed app that reads a single Firefox cookie DB, but this could not be verified. Respected tools document the risk and leave the choice to the user, not doing it silently.

### Cited Findings
- **Sigma rule.** The community rule "Suspicious File Access to Browser Credential Storage" (experimental, low; 2025-05-22) detects non-browser processes opening `cookies.sqlite`, `\Cookies`, `\Login Data`, `logins.json`, `key4.db` and similar under browser profile paths.
  - It excludes browser executables, vendor folders, and any image under `C:\Program Files\`, `C:\Program Files (x86)\`, `System32` or `SysWOW64`.
  - Listed false positives include antivirus and "Legitimate software accessing browser data for synchronization or backup purposes."
  - — [detection.fyi / SigmaHQ](https://detection.fyi/sigmahq/sigma/windows/file/file_access/file_access_win_susp_process_access_browser_cred_files/)
- **Other vendor rules.**
  - FortiSIEM ships a rule titled "PH RULE SQLite Firefox Cookie DB Access". — [Fortinet FortiSIEM rule](https://help.fortinet.com/fsiem/Public_Resource_Access/7_1_1/rules/PH_RULE_SQLite_Firefox_Cookie_DB_Access.htm) (title only, search summary)
  - Elastic has a prebuilt macOS rule for unsigned or untrusted processes opening cookies.sqlite or Cookies. — [Elastic: Suspicious Web Browser Sensitive File Access](https://www.elastic.co/guide/en/security/current/suspicious-web-browser-sensitive-file-access.html) (search summary)
  - Splunk detects non-Chrome processes opening Chrome's "Login Data" via event 4663. — [Splunk research](https://research.splunk.com/endpoint/0d32ba37-80fc-4429-809c-0ba15801aeaf) (search summary)
- **DPAPI audit logging.** Chromium has set `CRYPTPROTECT_AUDIT` on its DPAPI calls since 2013. Defenders can enable DPAPI event 16385 (Microsoft-Windows-Crypto-DPAPI/Debug) plus process-creation auditing and alert when a non-browser process decrypts browser-tagged data. Google notes these logs alone cannot prove the caller is really Chrome. — [Google Security Blog, Apr 2024](https://security.googleblog.com/2024/04/detecting-browser-data-theft-using.html) (search summary)
- **Chrome's own signal.** Chrome itself emits Event 257 when app-bound verification fails. Google says the protection makes attacker actions "more suspicious to antivirus software". — [Google Security Blog, Jul 2024](https://security.googleblog.com/2024/07/improving-security-of-chrome-cookies-on.html?m=1)
- **Other detection guidance.** SpecterOps recommends alerting on `--remote-debugging-port` and `--load-extension` in browser command lines, on DPAPI decrypts by processes other than `elevation_service.exe`, and on Sysmon events 8 and 10 for injection. — [SpecterOps 2025](https://specterops.io/blog/2025/08/27/dough-no-revisiting-cookie-theft/)
- **yt-dlp's own extraction has been reported as a vulnerability.** A 2025 reporter filed yt-dlp's silent Firefox cookie export as a "High-Impact Cookie Theft Vulnerability On Firefox - No Admin Rights Required". Mozilla treated it as a known limitation, not a bug in yt-dlp. — [Bugzilla 1964105](https://bugzilla.mozilla.org/show_bug.cgi?id=1964105)
- **How yt-dlp documents it.** yt-dlp warns that exported files contain all your cookies and must be guarded. It also flags a cookies.txt browser extension that turned out to be malware. — [yt-dlp FAQ wiki](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)
- **rookie.** Its README gives no responsible-use or antivirus guidance, and it was archived in June 2026. — [rookie on GitHub](https://github.com/thewh1teagle/rookie)

### Inferences
- **Install location affects detection.** An EdgeLoop installed per-user (for example under `%LOCALAPPDATA%`) that opens cookies.sqlite would match the Sigma rule's logic. Installed under Program Files, it is excluded by that rule's broad path exclusion. Enterprise EDR may still log it, while consumer Defender has no published rule I could find.
- **Do not touch the Chrome or Edge key path.** Reading the app-bound key, calling the elevation service, or attaching to the browser would put EdgeLoop's behaviour in the same class as VoidStealer-style malware.
- **What users expect from a video player.** Users do not expect a video player to read their browser's cookie store. Good practice, modelled on yt-dlp's explicit opt-in flag:
  - Make it an explicit, off-by-default action ("Import my Hypnotube / Rule34Video sign-in from Firefox").
  - Name the browser and profile.
  - Read only the two sites' rows, never write a full cookies.txt.
  - Keep the imported PHPSESSID in EdgeLoop's own protected store (for example DPAPI CurrentUser), not in plain settings JSON.
  - Show what was imported and offer "Forget".
- **Code signing helps.** Code-signing EdgeLoop's executable would reduce reputation-based flags (SmartScreen or AV heuristics). This is general background, not specifically sourced here.

### Gaps
- I found no documentation from Microsoft Defender, or other consumer antivirus such as Norton, Avast or Kaspersky, saying whether they flag or block a signed or unsigned app that copies Firefox's cookies.sqlite. No source either confirms or denies false positives for yt-dlp's cookie extraction on Windows.
- I did not review gallery-dl's or HTTPie plugins' documentation. I found nothing specific in the time available, so their handling is not covered here.

## Maintenance: how often this broke, 2024–2026

### Takeaway
Chromium-family import on Windows broke twice and has not recovered: DB locking from mid-2023, then app-bound encryption from July 2024. It was followed by a run of Google hardening changes (Chrome 136, Chrome 137) and new bypasses, roughly every few months. Firefox's cookie storage stayed stable and readable through 2024–2026. The main risk is Mozilla's 2026 work on encrypting SQLite databases, which has landed behind a pref.

### Cited Findings
- **Timeline.**
  - **Jun 2023:** Chrome/Edge locks the cookie DB while running; yt-dlp #7271 is still open in 2026. — [yt-dlp #7271](https://github.com/yt-dlp/yt-dlp/issues/7271)
  - **Jul 2024:** Chrome 127 app-bound encryption. — [Google Security Blog](https://security.googleblog.com/2024/07/improving-security-of-chrome-cookies-on.html?m=1)
  - **Sep 2024 onward:** yt-dlp Chrome decryption fails on Windows (#10927, open, locked Jan 2026). — [yt-dlp #10927](https://github.com/yt-dlp/yt-dlp/issues/10927)
  - **Undated:** the Chrome cookie DB `meta` version 24 change added a 32-byte hash prefix, which yt-dlp now strips. This was another format change tools had to follow. — [yt-dlp cookies.py](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/cookies.py)
  - **Chrome 130:** rookie needs admin rights from this version. — [rookie](https://github.com/thewh1teagle/rookie)
  - **Mar 2025:** Chrome 136 remote-debugging change. — [Chrome for Developers](https://developer.chrome.com/blog/remote-debugging-port)
  - **Chrome 137:** `--load-extension` blocked. — [SpecterOps 2025](https://specterops.io/blog/2025/08/27/dough-no-revisiting-cookie-theft/)
  - **Mar 2026:** VoidStealer bypass. — [Kaspersky](https://www.kaspersky.com/blog/chrome-application-bound-encryption-bypass-voidstealer/55735/) (search summary)
  - **Aug 2026:** in-process DevTools bypass. — [SpecterOps 2026](https://specterops.io/blog/2026/08/13/chrome-devtools-protocol-cookie-theft/) (search summary)
  - **Jun 2026:** rookie archived. — [rookie](https://github.com/thewh1teagle/rookie)
- **Firefox.**
  - Cookies have been plaintext throughout. Mozilla closed a 2025 report as a duplicate of its long-standing bug. — [Bugzilla 1964105](https://bugzilla.mozilla.org/show_bug.cgi?id=1964105)
  - Exclusive locking has been in place since Firefox 3.5 and is WONTFIX. — [Bugzilla 499099](https://bugzilla.mozilla.org/show_bug.cgi?id=499099)
  - Pref-gated SQLite at-rest encryption (ObfuscatingVFS) landed around Firefox 153/154, with further "Encrypt sensitive storage" work open at P1. — [Bugzilla 1996558](https://bugzilla.mozilla.org/show_bug.cgi?id=1996558); [Bugzilla 2014232](https://bugzilla.mozilla.org/show_bug.cgi?id=2014232); [Bugzilla 2049157](https://bugzilla.mozilla.org/show_bug.cgi?id=2049157)
  - An expiry-units change (msec) landed in Firefox 142, but it concerns the CookieStore API. — [Bugzilla 1972757](https://bugzilla.mozilla.org/show_bug.cgi?id=1972757) (search summary)

### Inferences
- **Build for Firefox only, and degrade gracefully.** An EdgeLoop import feature should be built for Firefox only. When the database cannot be opened as plain SQLite (for example because Firefox turned on encrypted storage), it should fall back to a clear message ("couldn't read Firefox's cookies; sign in inside EdgeLoop / paste cookie instead").
- **Prefer EdgeLoop's own small reader for the import.** yt-dlp is updated frequently and tracks Firefox schema changes, but it cannot help with Chromium on Windows and copies only the main file. For the import step, a small EdgeLoop reader for two hosts is low-maintenance (the moz_cookies schema has been stable) and more privacy-preserving. yt-dlp's `--cookies-from-browser firefox` can remain for yt-dlp's own requests.
- **Do not chase Chrome or Edge.** Each Chrome hardening step has broken the previous workaround within months, and the remaining workarounds are malware techniques.

### Gaps
- I did not compile a complete list of Firefox releases in 2024–2026 that changed `moz_cookies` columns, or of yt-dlp Firefox cookie fixes in that period. I found no reports of Firefox cookie extraction breaking on Windows, but absence of evidence is not proof.
- I could not confirm whether Mozilla plans to turn on SQLite encryption by default for consumer (non-enterprise) Firefox, or when.
