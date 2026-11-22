# Site support: fix and improve plan

Checked live on 22 November 2026, with the bundled yt-dlp 2026.10.26, by fetching each site directly from this machine and reading the code.

**Status: Phases 1 and 2 are done (22 November, not committed yet).** See "Phase 1 results" below. The table shows the sites as they were found, before the fixes.

## Where each site stood (before Phase 1)

| Site | Single video | Playlists / collections | Main problems |
|------|--------------|-------------------------|---------------|
| **PMVHaven** | **Broken.** After the site redesign the scraper picks an escaped link it can't parse, and its fallback host `video.pmvhaven.com` no longer resolves. yt-dlp doesn't support the site. | Finds the videos, but each one then fails to play. | Needs a new scraper for the redesigned pages. |
| **Rule34Video** | Works through the app's own scraper (1080p). yt-dlp's Rule34Video extractor fails with "No video formats found", but the app still tries yt-dlp first. | **Only the first 60 videos.** No page-following, even though a paginated importer exists in the code. | Resolved links expire in under ~40 minutes. |
| **Hypnotube** | Works for public videos (480p, signed link valid ~4 hours). VIP-only videos aren't detected. | **Blocked from this location.** Playlist and channel pages return "Access to this content is restricted in your location". | The app reports this as "page structure might have changed". |
| **Iwara** | Works through yt-dlp ("Source" quality). | Works through Iwara's API. | Signed out, the API only returns "ecchi"-rated videos. The cookie setting most likely can't sign in, because the API uses a bearer token. |

All sites: with local caching switched on (the default), **importing a Hypnotube, Rule34Video or Iwara playlist downloads every video in full, one after another, before the import finishes.**

---

## Phase 1 results

What changed:

| Site | Now |
|------|-----|
| **PMVHaven** | Videos resolve to their own stream at the highest quality, for current uploads (`…/1080p.m3u8`) and older ones (`…/<id>_720p.m3u8`). Preview clips are never picked. Playlist titles are right: the first video used to get the playlist's own name. |
| **Rule34Video** | The app's own scraper runs first (1080p) and yt-dlp is only a fallback. Playlists follow every page, with titles. Resolved links are cached for 20 minutes at most. |
| **Hypnotube** | VIP-only, friends-only and location-blocked pages are recognised. The player and launcher say so instead of "the site may have changed", and no pointless yt-dlp attempts follow. Playlist links are stored without `?p=`. |
| **All sites** | Importing never downloads. A playlist's own titles are used, each video keeps its page link, and the media link is found when it plays. Only YouTube, Vimeo and similar sites with separate video and audio are still downloaded, and that happens when they play. |
| **yt-dlp** | The "retry without browser cookies when the cookie database is locked" step now works, and also covers title lookups. |

Checked:
- 32 new tests, built from saved copies of real pages in `EdgeLoop.Tests/TestAssets/Sites`. The full suite passes (297 tests), and the Release build passes with CI's code-style checks.
- Live, with the app's own code. Each import was followed by resolving its first video like the player does, and every media request returned its first bytes (HTTP 206):
  - Hypnotube: playlist of 73 videos in 3 s.
  - PMVHaven: playlist of 3 videos in 1 s.
  - Rule34Video: playlists of 10 videos in 1 s, and 6,000 videos in 25 s. That playlist has 91,681 videos; the import stopped at the 100-page limit in Settings.
  - Single PMVHaven, Rule34Video and Hypnotube videos.
  - A Hypnotube VIP video reports "It's for Hypnotube VIP members only."
- Not checked: playback inside the app's own player window.

Corrections to the findings above:
- **Hypnotube's location block was most likely a VPN.** Playlist and channel pages were blocked at 13:40 and have been open since 17:50 (rechecked at 18:00). Hypnotube decides by the country a connection comes from, so a VPN exit in a blocked country triggers it. The detection stays, and its message now mentions a VPN.
- **yt-dlp 2026.10.26 is already the latest release**, so there was nothing to update. Its Rule34Video support is still broken, and the app no longer depends on it.
- **Iwara's API refuses the app's own connection.** Cloudflare returns 403 to .NET's HTTP client whatever the HTTP version or compression, while curl and yt-dlp get through with the same headers. Playlists therefore work only through the yt-dlp fallback (26 of 26 videos in a test). Single videos stream fine through yt-dlp with the app's headers. This is now part of Phase 2.

---

## Phase 1: fix what's broken

Each fix gets a test built from a saved copy of today's real page (see Phase 3).

### 1.1 PMVHaven playback
- `VideoUrlExtractor.ExtractPmvHavenUrlAsync`: the page data now escapes slashes as `/`, which `CleanExtractedUrl` doesn't decode, so the first `.m3u8` it finds can't be parsed. Decode JSON string escapes before matching.
- Take this video's own stream (`videos/<file>.mp4/master.m3u8`, or the highest variant such as `1080p.m3u8`), not the first `.m3u8` on the page.
- Drop the hard-coded `https://video.pmvhaven.com/` fallback. Media is now on `pmvhavencloud.s3.eu-west-par.io.cloud.ovh.net`, so use the host that appears in the page.
- `SiteRules`: set `StripQueryFromPageUrl` for PMVHaven (home-page links carry `?from=home`), and treat `/videos/<id>` and `/video/<slug>_<id>` as the same video so playlists don't add duplicates.

### 1.2 Stop downloading whole videos during import
- `PlaylistImporter.ImportPlaylistAsync` calls `ExtractVideoMetadataAsync` for every item. For any site in `YtDlpService.IsDownloadRequiredSite`, that calls `DownloadBestQualityAsync`, followed by a second yt-dlp run just for the title.
- Fix: import never downloads. It keeps the page URL and the title from the playlist page. The real media link is found when the video plays (that path already exists), and caching happens in the background or on first play.
- Limit `IsDownloadRequiredSite` to sites whose video and audio come as separate streams that must be merged (YouTube, Vimeo and similar). Hypnotube, Rule34Video and Iwara all serve single MP4 files.

### 1.3 Rule34Video
- Use the app's scraper first and keep yt-dlp as a fallback only, since its extractor is currently broken. Re-check after updating yt-dlp (1.5).
- Playlists: send Rule34Video to the paginated importer (`ExtractRule34VideoPlaylistAsync`, currently never called). Read the `data-playlist-item` links and `data-rd-plp-title` titles, then follow `?from=2`, `?from=3`… until a page adds nothing new or `MaxPlaylistPages` is reached. Tested: `?from=2` returns the next 60 videos, with no overlap.
- Links from `get_file` expire fast, so cache resolved Rule34Video links for about 20 minutes instead of the default 60. That way a stale link isn't the first thing tried. Recovery after a 403 already works.

### 1.4 Hypnotube restrictions
- Detect "You must be a VIP member to view this video". Only "friends-only" is detected today.
- Detect "Access to this content is restricted in your location" on playlist and channel pages.
- Report both by name, instead of "likely restricted or removed" or "page structure might have changed", and skip the yt-dlp attempts that are bound to fail.
- Open question: does signing in lift the location block? This needs a test with real Hypnotube cookies.

### 1.5 yt-dlp fixes
- `YtDlpService.GetBestVideoUrlAsync`: the "retry without cookies when the browser's cookie database is locked" step checks `options.CookiesFromBrowser`. Browser cookies are added with `AddCustomOption("--cookies-from-browser")`, so the check is always false and the retry never runs. The retry also passes `url` instead of the cleaned `fetchUrl`.
- Update the bundled yt-dlp (2026.10.26) and re-test Rule34Video.

---

## Phase 2: Iwara sign-in

**Done on 22 November (not committed yet).** What changed:
- **Connection fixed.** Cloudflare refuses TLS connections that don't name their protocol (ALPN), and .NET's HttpClient names none for HTTP/1.1. A hand-written .NET request naming `http/1.1` got through where the normal one got 403. The new `IwaraApi` makes its TLS connection that way, so the app reads Iwara's API itself.
- **Iwara account in Settings › Privacy & Cookies.** It replaces the Iwara cookie box: yt-dlp's Iwara code confirms the API signs in with a token, never cookies. There's an email, a password (encrypted like the cookies) and a "Check sign-in" button. Saving also deletes old Iwara cookies.
- **Signed-in API calls.** The email and password give a user token (about three weeks), swapped for an hourly access token, as Iwara's site and yt-dlp do. After a failed sign-in the app waits five minutes before trying again, unless the account is changed.
- **yt-dlp gets the account** through `--username`/`--password`. Iwara no longer borrows browser cookies.
- **Playlist import:**
  - Reads every page and stops when `count` is reached, with titles straight from the API.
  - No trailing slash when a video has no slug.
  - No per-video search lookups.
  - yt-dlp is now only a fallback for when the API can't be read; its Iwara playlists give no titles and read Iwara's general video feed past page 1.
- **Hidden videos are reported.** When a playlist declares more videos than Iwara shows, the launcher says how many it showed and that the others may need a signed-in account.
- **Account problems are named.** "Needs sign-in" and "wrong password" from yt-dlp are no longer retried three times. The player says "Iwara only shows it to signed-in accounts: add yours in Settings › Privacy & Cookies."

Checked: 17 new tests against a fake Iwara API. The full suite passes (314 tests), and the Release build passes with CI's code-style checks. Live and signed out, the app's own client imported a 26-video playlist and a 79-video playlist (two pages) in full, with titles, in under 2 seconds each.

**Found in your testing, and fixed: Iwara playlists stopped after the first video.**
- Getting the next video ready swaps its page for the direct file link (`https://hime.iwara.tv/view?…&filename=…_Source.mp4`).
- The app judged "file or page?" only by the end of the link's path, so it took that link for a page and tried to resolve it again. The tidy-up also stripped the link's signature, so Iwara refused (403) and the load timed out after 30 seconds.
- This only surfaced once Iwara stopped being downloaded first (Phase 1).
- Now a link that names a video file in its query counts as a file everywhere: page detection, URL validation, link tidy-up and the extractors. A player-level test reproduces the exact sequence; it fails without the fix and passes with it.
- Checked: Iwara's file servers accept the app's background downloads (206), so caching works when it's switched on.

**Not checked: anything signed in.** That needs a real Iwara account:
- "Check sign-in".
- Importing a playlist with explicit videos.
- Playing an explicit video.

The original Phase 2 plan, for reference:

- Iwara's API refuses .NET's HTTP client (see "Corrections" above), so the native playlist import never works today. Either send Iwara's API calls through yt-dlp, or through a client Cloudflare accepts, and stop trying the native call first.
- Signed out, the Iwara API only returns "ecchi"-rated videos. Even `rating=explicit` comes back as ecchi. So explicit videos are probably missing from imported playlists, and explicit single videos fail.
- Iwara's API signs in with a bearer token (`Authorization: Bearer …`), not cookies. This is very likely but needs confirming with a real account. If so, `IwaraCookies` and `--cookies-from-browser` do nothing for Iwara.
- Add an Iwara sign-in to Settings: email and password exchanged for a token at `api.iwara.tv/user/login`, stored encrypted like the cookies (`CookieProtector`). Send the token on API calls, and give yt-dlp the account through `--username`/`--password`, which its Iwara extractor supports.
- Playlist import (`ExtractIwaraPlaylistAsync`):
  - Stop when `count` and `limit` say the last page is done, instead of fetching an extra empty page.
  - Build `…/video/<id>` without a trailing slash when the slug is null.
  - Drop the search-API title lookup per item, because the playlist API already returns titles.
  - Send `Accept: application/json` to the API instead of the browser-page headers.
- If the API reports a playlist's full size, tell the user how many videos were hidden because they aren't signed in.

---

## Live test, 22 November (after Phases 1 and 2)

A scratch harness ran the app's own extractors, importer and yt-dlp service against every supported site, with an empty URL cache. Each resolved video was checked to stay a file link (not taken for a page), then FFmpeg decoded 4 seconds of video and audio with the player's own request headers.

**Final run (after Node.js 26 was installed and the fixes below): every check passes.**

| Site | Videos resolved and decoded | Imports | Notes |
|------|-----------------------------|---------|-------|
| PMVHaven | 4 of 4, 720p to 4K | Playlist: 3 of 3 | |
| Rule34Video | 4 of 4, 720p to 4K | 10 of 10; 180 (3-page limit) | A playlist entry deleted from the site (404) is skipped, correctly |
| Hypnotube (signed out) | 5 of 5, 360p | Playlist 90 (matches the site's 90 at the same moment); channel 60 (3-page limit) | VIP video gets "It's for Hypnotube VIP members only." |
| Iwara (signed out) | 5 of 5, 720p to 4K | 26 of 26; 79 of 79 | |
| YouTube, streaming (caching off) | watch, youtu.be and Shorts links; a 4K film streams at 1080p | Playlists of 1 and 29 | Streams YouTube's HLS stream |
| YouTube, download (caching on) | Downloaded and merged in 8 s, then played from disk | | Best quality |

**What the test found, and what was fixed:**
- **YouTube needed Node.js 22 or newer.** The PC had Node 20.19.5, so yt-dlp couldn't unlock any YouTube video. Node.js 26.13.1 is installed now. If the runtime is missing again, the app says "YouTube needs Node.js 22 or newer (or Deno)…" within about 7 seconds, instead of retrying for over a minute.
- **YouTube streaming never worked (old bug).** For streaming the app asked yt-dlp for "best video + best audio". On YouTube those are separate files, so yt-dlp returned two files to merge and no link to play, then retried silently for about 50 seconds. It now asks for the best single file with both picture and sound ("best"), and stops when there's no single link.
- **YouTube titles:** a single YouTube video added on its own was titled "watch" when yt-dlp failed. It now gets the page's real title ("Me at the zoo").
- **Hypnotube quality choice:**
  - It guessed quality from the whole link first, including its random token. Letters like "hd", "sd" or "4k" in the token could make a 480p file look like 2160p.
  - Now the tag's own label (`sizes='720'`) comes first, and only the link's path is read.
  - This matters once you're signed in, because pages then offer 720p alongside 480p.
- **Fewer pointless retries:** errors that won't change, such as 404s, aren't retried.

**Other findings:**
- **Hypnotube 720p needs sign-in.** Public videos are 360p or 480p, and signing in gives 720p. The app only signs in to Hypnotube with cookies pasted into Settings › Privacy & Cookies. Borrowing the browser's sign-in reaches yt-dlp, not the app's own Hypnotube scraper. No Hypnotube cookies are set at the moment, so the app plays the public versions.
- **Security:** every yt-dlp call passes `--no-check-certificates`, and the player sets FFmpeg's `tls_verify=0`. Both switch off certificate checks, so a network attacker could swap in other content or read session cookies. This belongs in Phase 4.

---

## Hypnotube and Rule34Video sign-in (22 November, not committed yet)

Chosen from the research in `reports/EdgeLoop site sign in options.md`: plain sign-in with the user's account, with pasted cookies as the fallback.

What changed:
- **Accounts in Settings › Privacy & Cookies:** Hypnotube takes a username and Rule34Video an email or username, each with a password and a "Check sign-in" button.
  - Passwords are encrypted with DPAPI and never saved as plain text. If encryption fails they aren't kept, and the Iwara password now follows the same rule.
- **`SiteSignIn` signs in the way each site's own form does:**
  - **Hypnotube:** POST `/login` with `ahd_username`, `ahd_password` and `Submit`. It's signed in when the page links to `/logout"`, the same check JDownloader uses.
  - **Rule34Video (KVS):** GET `/login/` for the hidden fields, including the one-time `remember_me_csrf_token`. Then POST them with `username`, `pass`, `remember_me=1`, `mode=async` and `format=json`; the reply is JSON.
  - **Signed-in check for Rule34Video:** `js_user_status` returns an id above 0.
- **Keeping the session:**
  - Before fetching a Hypnotube or Rule34Video page, the app makes sure the account's session works. It checks at most every 30 minutes and signs in again when needed.
  - After a failed sign-in it waits 10 minutes before trying again, so the site doesn't see a burst of logins.
  - A Hypnotube page that comes back signed out triggers a fresh check.
  - The session is stored encrypted, separately from the pasted cookies, and takes priority over them. A failed sign-in drops the dead session, so pasted cookies take over.
  - Changing the account in Settings starts a fresh session.
- **Captchas:** if a site asks for one, the app stops and says to paste cookies instead.
- **Page cache:** cached pages are keyed by the session too, so a page fetched while signed out isn't reused after signing in.

Checked:
- 20 new tests against fake sites with a real cookie jar, using Rule34Video's real login form as a saved page. They cover each site's posted fields, the session cookie carried from the login page to the post, wrong password, captcha, the 30-minute recheck, the 10-minute wait after a failure, and the Settings account. 358 tests and the Release build pass.
- Live, without any account: the app reads Rule34Video's real login form (its 64-character token and hidden fields), and Hypnotube's form is as expected with no captcha. Both "signed in?" checks answer no for a made-up session, and Rule34Video's DDoS-Guard gives .NET real JSON.

**Not checked: an actual sign-in.** That needs your accounts:
- What each site returns on success.
- Whether 720p appears.
- How long the sessions last.

**Also done the same day:**
- **The Iwara password is off yt-dlp's command line.** yt-dlp now gets the account in a short-lived config file (`--config-locations`) in the user's own temp folder, deleted when the call ends.
  - Values are shell-quoted, since yt-dlp reads config files with `shlex.split`. Passwords with quotes, spaces, `#` or `\` round-trip unchanged.
  - The file is written with a UTF-8 byte-order mark; without one, yt-dlp decodes it with Windows' code page.
- **The cookie paste box reads any common copy:** an extension's JSON export, cookies.txt (including `#HttpOnly_` lines), Firefox and Chrome/Edge "Copy as cURL" in either quoting style, Chrome's "Copy as PowerShell", a block of request headers, a `Cookie:` line, plain pairs, or a bare PHPSESSID value.
  - It keeps only that site's cookies and says what it found, e.g. "Found 2 cookies for hypnotube.com in a cURL command; left out 38 for other sites."
  - The Settings steps now lead with "Copy as cURL".
- **Tests:** 23 new, and a timing-dependent player test was made stable. 381 tests and the Release build pass.

---

## Phase 3: make breakage visible and cheap to fix

- **Say why a request failed.** `StandardHtmlFetcher.FetchHtmlAsync` returns `null` for every failure, so callers can't tell "blocked" from "gone" from "rate-limited". For example, the `catch (… "403")` in the Iwara importer can never run. Return the status (OK, 403, 404, 429, location-blocked, Cloudflare challenge) instead.
- **One failure reason per video.** VIP-only, friends-only, location-blocked, needs sign-in, removed, or "site changed", shown on the launcher's video badge.
- **Tests from real pages.** Save today's pages under `EdgeLoop.Tests/TestAssets`: a Hypnotube public video, VIP video and blocked listing; Rule34Video playlist pages 1 and 2 and a video page; a PMVHaven video and playlist page; Iwara playlist JSON. Test the parsers offline against them.
- **Optional live check before a release.** A test, off by default and in CI, that resolves one known video and one playlist per site. It catches site redesigns before users do.

---

## Phase 4: restructure (after Phase 1 lands, with tests in place)

- **One handler per site.** Each handler says whether a URL belongs to it and whether it's a video page, lists a collection, resolves the media link, gets the title and recognises its site's restriction messages. This replaces the three separate if/else chains in `ExtractVideoMetadataAsync`, `ExtractVideoUrlInternalAsync` and `ImportPlaylistAsync`. `SiteRules` stays as the table of headers and cookies.
- **Page-following per site.** Each site gets its own next-page rule, replacing the shared guesswork in `ExtractNextPageUrl`.
- **Remove dead code:**
  - `ExtractVideoLinksFromHtml` and `ExtractPlaylistContainerHtml`.
  - The second slug-to-title block in `ImportPlaylistAsync` (`TitleSanitizer.FromUrlSlug` already covers it).
  - The `IsHypnotubeVideoPageUrl`/`IsRule34VideoPageUrl` wrappers.
  - The commented-out cache in `VideoUrlExtractor`.

---

## Phase 5: improvements once things are stable

- **Faster imports.** With 1.2 done, a 60-video playlist costs a page request or two instead of 60+ lookups and downloads. Titles come from the playlist page.
- **More things to import:**
  - PMVHaven profiles and search results.
  - Rule34Video member uploads.
  - Iwara user pages (yt-dlp already has `iwara:user`).
  - Hypnotube channels and search, where not location-blocked.
- **Quality.** Check whether a signed-in Hypnotube account gets more than 480p.
- **Titles.** Hypnotube through yt-dlp adds " (1)" to titles, so prefer the page's `og:title`.
- **New sites** (Erome, from `Future_Implementation_Plan.md`) only after Phase 4, so each new site is one handler.

---

## Suggested order

1.1 PMVHaven, then 1.2 no downloads at import, 1.3 Rule34Video, 1.4 Hypnotube messages and 1.5 yt-dlp fixes. Each lands with its saved-page test. Then Phase 2 (Iwara sign-in), Phase 3's failure reasons and live check, Phase 4, and Phase 5.

## Not checked yet

- Nothing was run inside the app. The findings come from fetching the sites directly and reading the code.
- Signed-in behaviour on every site.
- YouTube and the other sites handled only by yt-dlp.
- Large PMVHaven playlists. Only one- and two-video playlists turned up, so the page may cap long lists.
- Iwara titles in non-Latin scripts. yt-dlp printed an empty title for a Japanese one, most likely a console display issue only.
