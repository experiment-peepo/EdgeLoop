# Plain login (app posts the user's email/password to a site's own login) — general practice, security and maintenance, as of November 2026

Scope note: this covers the site-independent side only. The specific Hypnotube and Rule34Video forms are covered by another track. No login forms were submitted and no credentials were entered while researching; everything below comes from docs, issues, changelogs and source code. EdgeLoop code was read without changes for context.

EdgeLoop context (read-only, 2026-11-22 working tree):
- `EdgeLoop/Classes/YtDlpService.cs` lines 475-481 add `--username <email>` and `--password <password>` to the yt-dlp child process through `ProcessStartInfo.ArgumentList` when an Iwara account is set. So the plain-text password is on yt-dlp.exe's command line while it runs.
- `EdgeLoop/Classes/CookieProtector.cs` uses `ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)`, with no optional entropy. If encryption throws, it falls back to saving the plain text (lines 30-34, "Fallback: return plaintext so the app still works").
- `EdgeLoop/Classes/IwaraApi.cs` keeps an access token and a user token. When a token "ExpiresSoon", it calls `LoginAsync(email, password)` again (lines 106-114). That is the silent re-login a stored password allows.

## 1. How established downloaders do site logins, and where they have moved to cookies

### Takeaway
yt-dlp and gallery-dl both still support per-site username/password logins, but only for sites where someone wrote and maintains a login routine. Both point to cookies as the general fallback "due to CAPTCHA or similar". Since late 2025, yt-dlp has been removing broken password logins (Twitter/X, Facebook, iQiyi, Reddit, LinkedIn). The stated reasons are CAPTCHAs and accounts being flagged as suspicious. For YouTube, the maintainers refuse password login outright as infeasible and a risk to the project.

### Cited Findings
**yt-dlp mechanics**
- yt-dlp's authentication options are `-u/--username`, `-p/--password` ("If this option is left out, yt-dlp will ask interactively"), `-2/--twofactor`, `-n/--netrc`, `--netrc-location`, `--netrc-cmd` ("Command to execute to get the credentials for an extractor") and `--video-password`. — [yt-dlp README](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/README.md)
- On .netrc, the README says it avoids passing credentials on every run and helps "prevent tracking plain text passwords in the shell command history". It concedes this means "keeping your passwords in a plain text file" and says to restrict it with `chmod a-rwx,u+rw`. Entries are `machine <extractor> login <user> password <pass>`. `--netrc-cmd` example: `gpg --decrypt ~/.authinfo.gpg`. — [yt-dlp README](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/README.md)
- In yt-dlp's base extractor, `_perform_login` is documented as "Login with username and password. Redefine in subclasses". `supports_login()` is `bool(cls._NETRC_MACHINE)`, so password login exists only where an extractor implements it. — [yt-dlp extractor/common.py](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/extractor/common.py)
- When a user gives `--username` for an extractor without login support, yt-dlp warns "Login with password is not supported for this website. Use --cookies-from-browser or --cookies for the authentication. {cookies_hint}". The login-hint builder also has a `'session_cookies'` variant: "Use --cookies for the authentication (--cookies-from-browser might not work)". — [yt-dlp extractor/common.py](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/yt_dlp/extractor/common.py)
- The yt-dlp FAQ calls cookies "a good way to workaround login when a particular extractor does not implement it explicitly". It recommends `--cookies-from-browser` and warns that an exported cookies file covers all sites, so "take care in not letting this text file fall into the wrong hands". It also notes that the "Get cookies.txt" (not "LOCALLY") Chrome extension "has been reported as malware". — [yt-dlp FAQ](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)
- The FAQ's rate-limit (429/402) advice: solve the CAPTCHA in a browser, then pass its cookies from the same IP, possibly with a matching `--user-agent`. Its Cloudflare 403 advice: cookies from a fresh browser session "within the past 30 minutes" with a matching current User-Agent. — [yt-dlp FAQ](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)

**yt-dlp moving away from password logins (2025-2026)**
- The changelog lists "Remove broken login support" for twitter (#15432), facebook (#15434) and iqiyi (#15441) in release 2026.01.29, and for reddit (#17038) and linkedin (#17039) in release 2026.07.04. No changelog entry in the portion read adds password login to an extractor. — [yt-dlp Changelog](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/Changelog.md) (only the first 100k characters of the 788k-character changelog were read, which covers roughly late 2025 to 2026)
- Twitter/X: issue #12616 (opened 2025-03-15) is titled "login with `--username`+`--password`/`--netrc` fails and flags account as suspicious". yt-dlp returned "Twitter rejected this login attempt as suspicious". The reporter's account was then temporarily blocked from interacting ("looks like it might be automated") for about 24 hours. Removal PR #15432 was merged 2025-12-30. — [yt-dlp issue #12616](https://github.com/yt-dlp/yt-dlp/issues/12616), [yt-dlp PR #15432](https://github.com/yt-dlp/yt-dlp/pull/15432)
- Reddit: PR #17038 (merged 2026-06-26) removed login "Because of Google Captcha verification required for both old.reddit.com and www.reddit.com." — [yt-dlp PR #17038](https://github.com/yt-dlp/yt-dlp/pull/17038)
- YouTube: a 2023 request for YouTube `--username/--password` was closed as not planned. Maintainer coletdjnz (2023-10-07): "Google puts significant effort into not allowing bots to go through the user/pass login to the point that bypassing that could be of risk to the project", "It's not feasible", and "Also, maintainability hell." — [yt-dlp issue #8079](https://github.com/yt-dlp/yt-dlp/issues/8079) (2023, older but still the stated position)
- Cookie logins have their own maintenance: the changelog has instagram "Detect when cookies are invalidated" (#17126, 2026.07.04) and "Fix logged-in extraction" (#17278, 2026.08.19). — [yt-dlp Changelog](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/Changelog.md)
- Security hardening of the credential path: release 2026.02.21 fixed CVE-2026-26331 by limiting `--netrc-cmd` arguments to a safe character subset, and limited `netrc_machine` to shell-safe characters. — [yt-dlp Changelog](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/Changelog.md)

**gallery-dl**
- Its README says some extractors take a username & password. For "sites where login with username & password is not possible due to CAPTCHA or similar, or has not been implemented yet, you can use the cookies from a browser login session" (cookies.txt, name-value pairs, or reading from Firefox/Chromium/Safari). Some sites use OAuth instead: required for pixiv, optional for deviantart, flickr, reddit, smugmug, tumblr and mastodon. — [gallery-dl README](https://raw.githubusercontent.com/mikf/gallery-dl/master/README.rst)
- configuration.rst lists the sites that support `username`/`password`: aibooru, ao3, aryion, atfbooru, bluesky, booruvar, coomer, danbooru, deviantart, e621, e6ai, e926, girlswithmuscle, horne, idolcomplex, imgbb, inkbunny, **iwara**, kemono, madokami, mangadex, mangoxo, newgrounds, nijie, nudostarforum, pillowfort, **rule34xyz**, sankaku, scrolller, seiga, simpcity, subscribestar, tapas, vipergirls and zerochan. On the booru sites the "password" is an API key, "not the actual account password". Neither Hypnotube nor Rule34Video (as distinct from rule34xyz) is on the list. — [gallery-dl configuration.rst](https://raw.githubusercontent.com/mikf/gallery-dl/master/docs/configuration.rst)

**JDownloader**
- JDownloader's Account Manager has plugins marked "Cookie login only" or "Cookie login optional". Support staff sometimes suggest cookie login "as a workaround for problems via normal 'user & password' login method". The user logs in in a private window or separate profile, exports cookies as JSON with an add-on (Cookie-Editor, EditThisCookie V3, FlagCookies), and pastes them into the account dialog. The article warns that logging out in the browser also ends the JDownloader session. — [JDownloader support: account cookie login](https://support.jdownloader.org/en/knowledgebase/article/account-cookie-login-instructions)

### Inferences
- The industry pattern is per-site password login where it still works, with cookies as the universal fallback. The direction since 2025 is away from password login on large sites with bot defences. Small PHP sites without such defences (the Hypnotube/R34 case) are the kind of site where password logins tend to survive. Whether that holds for these two is for the site-specific track.
- yt-dlp removes a login once it breaks rather than fixing it each time. The maintainers treat a password login as ongoing maintenance with a real chance of becoming impossible.
- Neither yt-dlp nor gallery-dl has a Hypnotube or Rule34Video password login to reuse. EdgeLoop would have to write and maintain both itself, and feed the result to yt-dlp as cookies.

### Gaps
- I could not find when yt-dlp first dropped YouTube password login (it predates 2023). Issue #8079 only shows that the warning existed by 2023.07.06.
- I did not read the Facebook (#15434) and LinkedIn (#17039) PRs for their specific reasons.
- I found no official JDownloader page listing which hosts are "cookie login only", or why.

## 2. What typically breaks app-run form logins

### Takeaway
Site defences against credential stuffing are exactly what break a legitimate app's scripted login. These include CAPTCHAs on "suspicious" logins, JavaScript-evaluated tokens, connection and device fingerprinting, multi-step forms with CSRF tokens, and new-device notifications, MFA or blocks. A desktop app posting a form looks like a script to these systems. The documented real-world failures are CAPTCHA (Reddit), "suspicious" flags and temporary blocks (Twitter/X), and Cloudflare challenges.

### Cited Findings
- OWASP's Credential Stuffing Prevention Cheat Sheet recommends that sites use the following, each of which an app-run login can trip:
  - MFA, possibly only for risky logins such as new devices or unusual locations.
  - CAPTCHA shown when a login looks suspicious or high-risk.
  - Graduated IP-reputation responses, including checks on hosting and proxy ranges.
  - Device fingerprinting and connection fingerprinting (e.g. JA3), which "may provide more accurate results" than IP or User-Agent checks.
  - Requiring JavaScript evaluation, because many tools only POST requests.
  - Multi-step login, e.g. requiring a CSRF token first.
  - Notifying users of suspicious activity, and temporary account locks after IP blocks.

  — [OWASP Credential Stuffing Prevention Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Credential_Stuffing_Prevention_Cheat_Sheet.html)
- The same sheet says CAPTCHA is tied to suspicious or high-risk logins rather than to every failure, and it warns against relying on "a single predictable volume limit". So when a CAPTCHA or block appears depends on the site's risk scoring, not a fixed count. — [OWASP Credential Stuffing Prevention Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Credential_Stuffing_Prevention_Cheat_Sheet.html)
- Real cases: Reddit's login now needs Google CAPTCHA, so yt-dlp removed it (June 2026) — [PR #17038](https://github.com/yt-dlp/yt-dlp/pull/17038). X flagged yt-dlp's password login as suspicious and temporarily blocked the account (2025) — [issue #12616](https://github.com/yt-dlp/yt-dlp/issues/12616). Google actively blocks bots from its user/pass login — [issue #8079](https://github.com/yt-dlp/yt-dlp/issues/8079).
- Cloudflare: yt-dlp's FAQ says Cloudflare-blocked sites need cookies from a browser session less than 30 minutes old, with the browser's User-Agent. A plain HTTP client posting a form cannot pass the JS challenge itself. — [yt-dlp FAQ](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)
- 2FA: yt-dlp needs a separate `-2/--twofactor` code, which an unattended background re-login cannot supply. — [yt-dlp README](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/README.md)

### Inferences
- The failure modes, ordered by how likely they are to hit a small PHP site:
  1. Changed field names, form URL or CSRF token handling. These are silent breakages after a site redesign; the memory note says PMVHaven had one, though that is not a login.
  2. A CAPTCHA (reCAPTCHA, hCaptcha or Turnstile) added to the login form, often after abuse.
  3. A Cloudflare or DDoS-Guard challenge in front of the whole site.
  4. Risk-based blocks on repeated logins.

  An app cannot recover from 2-4 without showing a real browser. This ordering is my inference from the sources above, not a measured frequency.
- Silent re-login multiplies login events, because each session expiry produces another automated login. That raises the odds of tripping risk scoring (the X case) compared with a user signing in once.
- A WebView2 sign-in window avoids most of these, because the site's own page runs its own CAPTCHA, JS and 2FA. That is an inference; WebView2 was not researched in this track.

### Gaps
- I found no published data on how often small sites add CAPTCHAs to login forms, or on lockout thresholds. Thresholds are site-specific and usually undisclosed.
- I did not research "new device" email behaviour on Hypnotube or Rule34Video (site-specific track).

## 3. Security of storing third-party passwords on Windows, and passing them to yt-dlp

### Takeaway
Microsoft's current guidance (July 2026) ranks the options as: avoid passwords, then Windows Credential Manager (CredWrite/CredRead), then DPAPI (CryptProtectData). Both of the last two protect against offline theft of the file or another Windows user, but not against code running as the same user. EdgeLoop's bigger avoidable exposure is the yt-dlp command line. Microsoft itself warns that command lines "can contain sensitive or private information such as passwords" and can end up in the security event log in plain text.

### Cited Findings
- Microsoft's "Handling Passwords" page (ms.date 2026-07-16) gives this preference order for new Windows development:
  1. Eliminate passwords where possible.
  2. Use Windows Credential Manager via CredWrite/CredRead, "which encrypts them with the user's logon session key".
  3. Use DPAPI (CryptProtectData) for secrets that must be persisted locally, described as "defense-in-depth".

  The same page says to collect passwords late and discard them early, to clear buffers with SecureZeroMemory, to "Never log passwords or secrets" (including crash dumps), and to always use TLS. — [Microsoft Learn: Handling Passwords](https://learn.microsoft.com/en-us/windows/win32/secbp/handling-passwords)
- On DPAPI's scope: "Typically, only a user with logon credentials that match those of the user who encrypted the data can decrypt the data". Decryption is usually limited to the same computer, except with roaming profiles. `CRYPTPROTECT_LOCAL_MACHINE` lets any user on the machine decrypt. `pOptionalEntropy` is an extra secret that must be supplied again on decryption. A MAC guards against tampering. — [Microsoft Learn: CryptProtectData](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata) (ms.date 2025-11-13)
- The .NET ProtectedData sample creates random entropy and passes it to Protect/Unprotect. EdgeLoop passes `null`. — [Microsoft Learn: How to use data protection](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection) (ms.date 2026-07-22)
- Credential Manager: Microsoft's "Kinds of Credentials" page says generic credentials can be read and written by user processes. CredEnumerate lists the current user's credential set. MITRE ATT&CK T1555.004 documents malware abusing CredEnumerateA and Credential Manager to steal saved credentials. — [Microsoft Learn: Kinds of Credentials](https://learn.microsoft.com/en-us/windows/win32/secauthn/kinds-of-credentials), [Microsoft Learn: CredEnumerate](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credenumeratea), [MITRE ATT&CK T1555.004](https://attack.mitre.org/techniques/T1555/004/) (these three come from search-result summaries; I did not open the pages myself)
- Chrome moved beyond plain DPAPI for its cookies on Windows with "App-Bound Encryption" (Google Security Blog, 2024-07-30). Since then, yt-dlp's `--cookies-from-browser chrome` fails on Windows with "failed to decrypt with DPAPI". A contributor warned it "will likely mean `--cookies-from-browser chrome` will be permanently broken on Windows"; the issue is still open and was locked on 2026-01-07. — [Google Security Blog](https://security.googleblog.com/2024/07/improving-security-of-chrome-cookies-on.html), [yt-dlp issue #10927](https://github.com/yt-dlp/yt-dlp/issues/10927)
- Command-line exposure: Microsoft's documentation of the "Include command line in process creation events" policy says that with it enabled, "the command line information for every process will be logged in plain text in the security event log" (event 4688). "any user with access to read the security events will be able to read the command line arguments", and "Command line arguments can contain sensitive or private information such as passwords". The policy is off by default. — [Microsoft Learn: Command line process auditing](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/component-updates/command-line-process-auditing)
- WMI's `Win32_Process` exposes a `CommandLine` property, which is how tools read other processes' command lines. Raymond Chen notes that the Win32 API itself has no call to get another process's command line. — [The Old New Thing, 2009-11-25](https://devblogs.microsoft.com/oldnewthing/20091125-00/?p=15923) (2009, older), [Microsoft Learn: Win32_Process](https://learn.microsoft.com/windows/win32/cimwin32prov/win32-process)
- CWE-214 ("Invocation of Process Using Visible Sensitive Information") describes credentials passed as process arguments being readable through OS process listings. — [CWE-214 summary (Mondoo)](https://mondoo.com/vulnerability-intelligence/cwe/CWE-214) (secondary source; the MITRE CWE page was not opened)
- yt-dlp's own options for keeping the password off the command line are: the interactive prompt (omit `-p`), `--netrc` with `--netrc-location`, and `--netrc-cmd`. The README frames .netrc as avoiding plain-text passwords in shell history, at the cost of a plain-text file. — [yt-dlp README](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/README.md)
- RFC 6749 (OAuth 2.0) lists the risks of the pattern where third-party apps hold the user's password:
  - apps must "store the resource owner's credentials for future use", "typically a password in clear-text";
  - they "gain overly broad access";
  - owners "cannot revoke access to an individual third party without revoking access to all third parties" and must change the password;
  - "Compromise of any third-party application results in compromise of the end-user's password".

  — [RFC 6749 §1](https://datatracker.ietf.org/doc/html/rfc6749#section-1)

### Inferences
- DPAPI CurrentUser (EdgeLoop's current choice) and Credential Manager offer about the same protection against the threats that matter: a copied settings file, or another Windows account. Neither stops malware running as the user. The main gains from Credential Manager are visibility to the user (they can see and delete the entry in Control Panel), separation from the settings JSON, and alignment with Microsoft's stated order. It does not make the password harder for same-user malware to steal.
- Adding a fixed optional entropy to ProtectedData only slightly raises the bar, since the entropy ships in the EdgeLoop binary. It stops casual decryption of the blob by generic DPAPI tools that don't know it.
- `CookieProtector.Protect` falls back to plain text when encryption fails. For a password that is worse than for a cookie, because a password is often reused across sites. For passwords, refusing to save, or warning the user, would be safer.
- Keeping the password off yt-dlp's command line, in rough order of preference:
  1. Do the login in EdgeLoop and pass only the resulting session (the cookie file EdgeLoop already builds for other sites, or a short-lived header). Then yt-dlp never sees the password, and a leaked argument is a revocable session rather than a reusable password.
  2. Write a temporary netrc in a user-only folder and pass `--netrc --netrc-location <path>`, deleting it afterwards.
  3. Keep `--username/--password`.

  For Iwara, EdgeLoop already holds a bearer token from its own login (IwaraApi). Whether yt-dlp's Iwara extractor accepts a token through a header or cookie instead of a password was not checked in this track.
- Piping the password to yt-dlp's interactive prompt over stdin is probably unreliable on Windows. Python's Windows `getpass` reads from the console, not a redirected stdin. This comes from my knowledge of CPython; I did not check it in this session.

### Gaps
- I did not find an authoritative Microsoft statement on exactly which access right is needed to read a same-user process's command line, i.e. whether any same-user, non-elevated process can read it. Process Explorer and Task Manager show it in practice, but I have no citation for the rights.
- I could not get the body of the Google App-Bound Encryption post (the fetch returned only the title and date). The claim that DPAPI does not stop same-user malware, which motivated it, is therefore not quoted here from Google's own text.
- I found no OWASP page aimed at desktop apps storing third-party passwords. The OWASP Password Storage cheat sheet covers server-side hashing, which does not apply because a reusable password must be decryptable. I did not open the OWASP Secrets Management Cheat Sheet.
- I did not check WinRT `PasswordVault` limits (it is backed by the same credential store).

## 4. Privacy, trust and terms of service

### Takeaway
The standard industry framing, in RFC 6749, is that giving a third-party app your password is an anti-pattern. The app holds the password, gets full account access, cannot be revoked separately, and its compromise exposes the password itself. Sites' anti-automation systems also treat scripted logins as suspicious. I found no user study measuring how users of desktop media apps feel about entering a site password.

### Cited Findings
- RFC 6749 §1 sets out the password-sharing problems quoted above (clear-text storage, overly broad access, no individual revocation, compromise exposes the password and "all of the data protected by that password"). These are why OAuth-style "sign in on the site itself" exists. — [RFC 6749 §1](https://datatracker.ietf.org/doc/html/rfc6749#section-1)
- Microsoft's top preference is to "Eliminate passwords where possible". — [Microsoft Learn: Handling Passwords](https://learn.microsoft.com/en-us/windows/win32/secbp/handling-passwords)
- yt-dlp warns: "By using your account with yt-dlp, you run the risk of it being banned (temporarily or permanently)". It advises being "mindful with the request rate" and to "consider using a throwaway account" (said about YouTube, but it shows how a major downloader frames account risk to users). — [yt-dlp wiki: Extractors](https://github.com/yt-dlp/yt-dlp/wiki/Extractors)
- An automated login was treated by X as automated activity, leading to a temporary interaction block. — [yt-dlp issue #12616](https://github.com/yt-dlp/yt-dlp/issues/12616)
- The cookie route carries its own trust risk. yt-dlp's FAQ warns that a browser cookie export contains every site's cookies, and that one popular cookie-export extension "has been reported as malware". — [yt-dlp FAQ](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)

### Inferences
- Stated neutrally:
  - App-held passwords give the app the most power over the account. A session cookie or token is narrower: it expires and is revoked by logging out.
  - A WebView2 window showing the site's own page does not require the user to trust EdgeLoop's handling of the password in storage, though the user still has to trust the embedded window not to read it.
  - Cookie copy-paste exposes only one site's session, if the user exports only that site.
- Terms of service: many sites prohibit automated access or scraping in their terms. Whether a user-initiated login from a personal player counts as "automated access" depends on each site's wording (site-specific track). Neither yt-dlp nor gallery-dl documentation addresses ToS in what I read. Logging in to the user's own account to watch content differs from bulk scraping, but the site's anti-automation systems cannot tell the two apart.
- Disclosing in Settings that the password is stored encrypted for this Windows user and sent only to that site would answer the main trust question. This is a design suggestion, not a sourced finding.

### Gaps
- I found no empirical studies (2024-2026) on user perception of third-party desktop apps asking for site passwords versus in-site sign-in.
- I did not read Hypnotube's or Rule34Video's terms (other track).

## 5. Session lifetime: silent re-login versus redoing cookies

### Takeaway
This matters a great deal for PHP-session sites. PHP's defaults are a session cookie that lasts "until the browser is closed" and server-side session data treated as garbage after 24 minutes of inactivity, unless the site changes them. So a copied PHPSESSID can die quickly. A stored password lets the app sign in again silently, as EdgeLoop already does for Iwara. The cost is that each silent re-login is another automated login event that site defences can flag.

### Cited Findings
- PHP's defaults: `session.name` = `PHPSESSID`; `session.cookie_lifetime` = 0 ("until the browser is closed"); `session.gc_maxlifetime` = 1440 seconds ("24 minutes"), after which session data "will be seen as 'garbage' and potentially cleaned up"; GC probability 1/100 per request. — [PHP manual: Session runtime configuration](https://www.php.net/manual/en/session.configuration.php)
- Cookies get rotated or invalidated by sites. YouTube "rotates account cookies frequently on open YouTube browser tabs as a security measure", so yt-dlp's guide has users export from a private window and then close it. — [yt-dlp wiki: Extractors](https://github.com/yt-dlp/yt-dlp/wiki/Extractors). yt-dlp added Instagram detection "when cookies are invalidated" in 2026. — [yt-dlp Changelog](https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/Changelog.md)
- Logging out in the browser ends the session shared with JDownloader, so its guide says to close the window rather than log out. — [JDownloader support](https://support.jdownloader.org/en/knowledgebase/article/account-cookie-login-instructions)
- Cloudflare clearance cookies must be fresh (within ~30 minutes) per yt-dlp's FAQ. — [yt-dlp FAQ](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)
- EdgeLoop's IwaraApi already re-logs in when a token "ExpiresSoon" (`IwaraApi.cs` lines 106-114, read locally).

### Inferences
- With default PHP settings, a pasted PHPSESSID that sits unused for more than about 24 minutes may already be gone server-side. That would make cookie copy-paste a repeated chore for a video player used in occasional sessions, unless the site sets a longer lifetime or a separate "remember me" cookie. The actual Hypnotube and Rule34Video lifetimes are the site-specific track's question, and I could not verify them here.
- With a stored password, EdgeLoop can make a fresh session when a request fails as logged-out or when it starts. Users never see an expiry, which is the main usability win of plain login.
- Re-login should be rate-limited (e.g. at most once per session or on demand, with backoff after failures). That avoids the repeated-login pattern that got the X account flagged, and avoids locking the user out after a password change.

### Gaps
- I could not confirm the real session or remember-me lifetimes on Hypnotube or Rule34Video (site-specific track).
- I found no data on how often users have to refresh cookies in practice with yt-dlp or JDownloader cookie logins.

## 6. Accessibility for non-technical users

### Takeaway
Email/password in Settings is the familiar one-step pattern. Every documented cookie flow takes several technical steps: a private window, installing an extension, exporting JSON or Netscape-format cookies, getting the newline format right, and pasting. The "read the browser's cookies" shortcut has been broken for Chrome on Windows since 2024.

### Cited Findings
- JDownloader's cookie-login procedure: a separate profile or private window, log in with "keep session", install Cookie-Editor, EditThisCookie V3 or FlagCookies, export JSON to the clipboard, and paste it into the account dialog (into the password field on the MyJDownloader web UI). — [JDownloader support](https://support.jdownloader.org/en/knowledgebase/article/account-cookie-login-instructions)
- yt-dlp's cookies.txt must be Netscape format with a `# HTTP Cookie File` or `# Netscape HTTP Cookie File` first line and the right line endings (CRLF on Windows). "HTTP Error 400: Bad Request" is a symptom of wrong newlines. — [yt-dlp FAQ](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)
- The YouTube export procedure takes a private window, robots.txt, export, then close, and warns not to use `--cookies-from-browser` for it. — [yt-dlp wiki: Extractors](https://github.com/yt-dlp/yt-dlp/wiki/Extractors)
- `--cookies-from-browser chrome` fails on Windows because of Chrome App-Bound Encryption. The workarounds are Firefox, an extension export, or a registry policy that "will wipe all of your cookies". The issue is still open (locked 2026-01-07). — [yt-dlp issue #10927](https://github.com/yt-dlp/yt-dlp/issues/10927)
- Users told to install cookie-export extensions are exposed to malicious extensions; the FAQ flags one as reported malware. — [yt-dlp FAQ](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)

### Inferences
- For non-technical users the ranking is probably: email/password in Settings (easiest; fails only when the login breaks), then an embedded sign-in window (nearly as easy; also handles CAPTCHA and 2FA), then reading browser cookies (zero steps when it works, but Chrome/Edge on Windows mostly don't), then manual cookie copy-paste (hardest, and it repeats whenever the session expires). This ranking is an inference from the documented procedures, not a usability study.
- Plain login's accessibility advantage disappears when the site adds a CAPTCHA. The app then needs a fallback, probably an embedded sign-in window, so a robust design likely needs both. This is an inference.

### Gaps
- I found no usability studies comparing password fields with cookie flows for desktop downloaders or players.
- I did not research Edge (WebView2's engine) cookie encryption separately from Chrome. Edge is Chromium-based, but whether it has App-Bound Encryption on by default was not checked.
