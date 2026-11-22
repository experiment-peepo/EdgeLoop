# Hypnotube and Rule34Video sign-in mechanics (as of 2026-11-22)

Method: plain GET requests with curl (desktop Chrome User-Agent, no cookies sent) on 2026-11-22 from the user's machine, plus reading public source code on GitHub. No login form was submitted, no credentials were entered, no account was created. The saved copies of the pages and scripts were kept in a temporary folder. Line numbers below refer to those saved copies of the live HTML, which can shift whenever the sites change their templates. Where I cite "live fetch", the fact comes from that GET on 2026-11-22.

Caveat on the user's VPN: per the project memory, the user sometimes browses through a VPN, and Hypnotube has a location gate. These fetches came from the user's machine at about 16:22 UTC; whether a VPN was on is unknown.

## 1. Login form: URL, action, method, field names, hidden fields; form post or AJAX?

### Takeaway
Hypnotube uses a plain HTML form post to `https://hypnotube.com/login` with only two fields (`ahd_username`, `ahd_password`) and no hidden CSRF token. Rule34Video uses a KVS AJAX form: POST to `/login/` with `username`, `pass`, `remember_me`, `action=login`, a per-page `remember_me_csrf_token` hidden field, and `mode=async&format=json` added by the site's script, which returns JSON.

### Cited Findings
**Hypnotube**
- `https://hypnotube.com/login` returns 200 with the login page (title "Login - Hypnotube"). `https://hypnotube.com/login.php` returns 404. `https://hypnotube.com/` returns 301 to `https://hypnotube.com/index.php`. — [live fetch of https://hypnotube.com/login](https://hypnotube.com/login), 2026-11-22
- The form is `<form class="form-block" id="login-form" name="loginForm" method="post" action="">`, so it posts back to the same URL (`/login`). — [https://hypnotube.com/login](https://hypnotube.com/login), saved HTML line 720
- Fields: `<input ... id="ahd_username" name="ahd_username" type="text" placeholder="Username">` (line 725), `<input ... id="ahd_password" name="ahd_password" type="password" placeholder="Password">` (line 732), and a submit button `<button class="btn btn-default" type="submit" name="Submit">` (line 751). The form has no hidden inputs, no CSRF token or nonce, and no "remember me" checkbox. — [https://hypnotube.com/login](https://hypnotube.com/login), lines 720-756
- The field asks for a username (placeholder "Username"), not an email address. — same page, line 725
- The site's own scripts (`/core/js/Tube.js`, `/templates/hypnotube/js/scripts.js`) contain no AJAX login handler. scripts.js only has AJAX calls for ratings (`/includes/rating/jRating.php`), playlists (`/action.php?action=...`) and a random toggle. — [https://cdn.hypnotube.com/core/js/Tube.js](https://cdn.hypnotube.com/core/js/Tube.js), [https://cdn.hypnotube.com/templates/hypnotube/js/scripts.js](https://cdn.hypnotube.com/templates/hypnotube/js/scripts.js), live fetch
- JDownloader's Hypnotube plugin logs in the same way: GET `https://www.hypnotube.com/login`, find the form with id `login-form` (or any form containing `ahd_username`), put `ahd_username` and `ahd_password`, submit the form. — [mirror/jdownloader HypnotubeCom.java lines 230-244](https://github.com/mirror/jdownloader/blob/master/src/jd/plugins/hoster/HypnotubeCom.java) (this mirror was last pushed 2024-09-28, so the code reflects 2024 or earlier)

**Rule34Video**
- `https://rule34video.com/login/` returns 200 with a full login page (title "Log in"). — [live fetch of https://rule34video.com/login/](https://rule34video.com/login/), 2026-11-22
- The form is `<form class="rda-form" action="/login/" data-form="ajax" method="post" novalidate>`. — [https://rule34video.com/login/](https://rule34video.com/login/), saved HTML line 630
- Hidden and visible fields, in order: `remember_me_csrf_token` (hidden, a 64-character hex value that changes per page load, line 631); `username` (label "E-mail or username", line 636); `pass` (line 643); `remember_me` checkbox, value `1`, checked by default, labelled "Keep me logged in" (line 648); hidden `action=login` (line 649); hidden `email_link=https://rule34video.com/email/` (line 650); hidden `redirect_to=/` (line 651). — same page, lines 630-651
- `data-form="ajax"` forms are wired up by the KVS script `kvs.min.js` with jQuery `ajaxForm({data:{format:"json",mode:"async"}, ...})`, so the real request is a POST to `/login/` with the form fields plus `format=json` and `mode=async`. — [https://rule34video.com/static/js/kvs.min.js?v=3dbf75d2e5](https://rule34video.com/static/js/kvs.min.js?v=3dbf75d2e5), live fetch
- The same handler reads the reply: if the Content-Type is `application/json` and `status` is `"failure"`, it shows each entry of `errors[]` (`field`, `code`, `message`) next to the named field; if `status` is `"success"`, it goes to `redirect` if present, otherwise reloads the page. A `text/html` reply is shown as a message box. — same file
- The redesigned site's own `custom.min.js` also hooks `submit` on `.rda-form` for client-side checks (for example "Enter your password" when `pass` is empty) before the KVS handler posts. — [https://rule34video.com/static/js/custom.min.js?v=0046d754f0](https://rule34video.com/static/js/custom.min.js?v=0046d754f0), live fetch

### Inferences
- Hypnotube's login is the simplest possible programmatic case: one GET (to get a `PHPSESSID`), then one form-encoded POST of `ahd_username`, `ahd_password` (and probably `Submit=`) to `https://hypnotube.com/login`, keeping cookies.
- Rule34Video needs one GET of `/login/` first, to get a `PHPSESSID` and the `remember_me_csrf_token` from the HTML, then a POST with that token. The token's name suggests it protects the remember-me cookie; it is probably tied to the session, so the GET and POST must share one cookie jar. The JSON reply makes success and failure easy to detect.

### Gaps
- What Hypnotube's server returns on a successful or failed login (redirect target, error text, new cookies) could not be checked without submitting the form.
- Whether Rule34Video's server rejects a POST without `remember_me_csrf_token`, or with a stale one, could not be tested.
- The exact JSON body Rule34Video returns on success is not verified; it is inferred from the KVS client code.

## 2. Captchas and bot protection

### Takeaway
Neither login form shows a captcha today. Both sites put captchas on sign-up (Hypnotube: math image on sign-up and password reset; Rule34Video: image code on sign-up), and the KVS client code is built to handle a captcha field (`code`) or reCAPTCHA on any AJAX form, so a captcha on Rule34Video's login after failures is possible but unconfirmed. Rule34Video sits behind DDoS-Guard but served full pages to plain curl; Hypnotube is plain nginx with no challenge, but has a location/age gate.

### Cited Findings
**Hypnotube**
- The login form has no captcha element (lines 720-756). — [https://hypnotube.com/login](https://hypnotube.com/login), live fetch
- The sign-up form has a math captcha: `<img src="https://hypnotube.com/captcha.php?ts=..." class="captcha captcha-img">` and `<input ... name="captchaaa" placeholder="Solve the math problem">`. — [https://hypnotube.com/signup](https://hypnotube.com/signup), saved HTML lines 751-752
- The password-reset form has the same math captcha (`captcha.php`, field `captchaaa`). — [https://hypnotube.com/forgot-pass](https://hypnotube.com/forgot-pass), lines 739-740
- Response headers show `Server: nginx/1.22.1` and no Cloudflare or DDoS-Guard headers or cookies; curl received the full page (40,735 bytes) with status 200. — [https://hypnotube.com/login](https://hypnotube.com/login), live response headers
- scripts.js contains a "geo gate": on AJAX/fetch responses it reads an `X-Geo-Gate` response header with values `pending` or `blocked`, and on those it sends the browser to `/age-gate` or `/blocked`. — [https://cdn.hypnotube.com/templates/hypnotube/js/scripts.js](https://cdn.hypnotube.com/templates/hypnotube/js/scripts.js), live fetch
- The `/login` response on 2026-11-22 carried no `X-Geo-Gate` header. — live response headers

**Rule34Video**
- The login form has no captcha element and no `data-recaptcha-key` element. — [https://rule34video.com/login/](https://rule34video.com/login/), lines 630-655
- The sign-up form has an image captcha: `<img src="/captcha/signup/?rand=...">` and `<input type="text" name="code" id="signup_code">`. — [https://rule34video.com/signup/](https://rule34video.com/signup/), saved HTML lines 659-661
- The site's URL map includes a generic captcha route `"captcha":"https://rule34video.com/captcha/%ID%/"`. — [https://rule34video.com/login/](https://rule34video.com/login/), line 27 (`langUrls`)
- kvs.min.js, on a failed AJAX form reply, refreshes `.captcha-control img` when the failing field is `code`, and renders/resets Google reCAPTCHA for any `[data-recaptcha-key]` element. So the KVS client supports a captcha appearing on any form, including login. — [https://rule34video.com/static/js/kvs.min.js?v=3dbf75d2e5](https://rule34video.com/static/js/kvs.min.js?v=3dbf75d2e5)
- Every response has `Server: ddos-guard` and sets DDoS-Guard cookies: `__ddg1_` (HttpOnly, 1-year expiry), and `__ddg8_`, `__ddg9_` (holds the client IP address), `__ddg10_` (20-minute expiry). — [live response headers of https://rule34video.com/ and /login/](https://rule34video.com/)
- Despite DDoS-Guard, plain curl with no cookies got status 200 and the full page (208,374 bytes for `/`, 119,593 bytes for `/login/`), with no JavaScript challenge page. — live fetch, 2026-11-22

### Inferences
- A programmatic Hypnotube login is not blocked by a captcha today. The sign-up captcha shows the script can add one, so a login captcha could appear later (or after failed attempts) without notice.
- On Rule34Video, DDoS-Guard was in passive mode for this client at this time. DDoS-Guard can switch to a JavaScript challenge during attacks; an HttpClient login would then fail where a browser would pass.
- The Hypnotube geo/age gate matters for any approach: if the gate is "pending" or "blocked" for the user's location (or VPN exit), pages may redirect to `/age-gate` or `/blocked` whether or not the user is signed in.

### Gaps
- Whether either site shows a captcha on login after repeated failed attempts could not be tested (it would require failed login submissions).
- What `/age-gate` asks for, and whether signing in satisfies it, was not checked.
- Whether DDoS-Guard ever challenges .NET HttpClient specifically (TLS fingerprint differs from curl's) was not tested here; EdgeLoop's scraper reportedly works today (project memory).

## 3. Session cookies: names, lifetimes, remember-me

### Takeaway
Hypnotube issues `PHPSESSID` with a fixed 3-day `Max-Age` even to anonymous visitors, and has no remember-me option. Rule34Video's `PHPSESSID` is a browser-session cookie (no expiry); KVS keeps a signed-in user across browser restarts with a `kt_member` cookie when "Keep me logged in" is ticked (ticked by default).

### Cited Findings
**Hypnotube**
- `Set-Cookie: PHPSESSID=...; expires=Wed, 25-Nov-2026 16:22:12 GMT; Max-Age=259200; path=/; secure; HttpOnly; SameSite=Lax` (259,200 s = 3 days). This was the only cookie set on the anonymous `/login` GET. — [https://hypnotube.com/login](https://hypnotube.com/login), live response headers
- The login form has no remember-me checkbox. — same page, lines 720-756
- JDownloader treats a Hypnotube page as signed in when its HTML contains `/logout"`, and saves all of the host's cookies after login (it does not name a specific member cookie). — [mirror/jdownloader HypnotubeCom.java lines 251, 256-257](https://github.com/mirror/jdownloader/blob/master/src/jd/plugins/hoster/HypnotubeCom.java) (2024 or earlier)

**Rule34Video**
- `Set-Cookie: PHPSESSID=...; path=/; domain=.rule34video.com; secure; HttpOnly; SameSite=Lax`, with no `expires`/`Max-Age`, so it is a session cookie. — [live response headers of https://rule34video.com/login/](https://rule34video.com/login/)
- Also set to anonymous visitors: `kt_acctoken` (Base64 of the client's IP address, `Max-Age=86400`, 1 day) and `kt_referer=deleted`. — live response headers of https://rule34video.com/
- The login form has `remember_me=1`, checked by default, labelled "Keep me logged in", protected by `remember_me_csrf_token`. — [https://rule34video.com/login/](https://rule34video.com/login/), lines 631, 648
- JDownloader's generic KVS plugin decides "logged in" by the presence of a non-deleted `kt_member` cookie. — [mirror/jdownloader KernelVideoSharingComV2.java lines 1311-1316](https://github.com/mirror/jdownloader/blob/master/src/jd/plugins/hoster/KernelVideoSharingComV2.java) (2024 or earlier)
- Cumination (Kodi add-on) builds its KVS request cookies from `PHPSESSID`, `kt_ips` and `kt_member` (plus a constant `kt_tcookie=1`), and re-logs in when `kt_member` is missing. — [dobbelina/repository.dobbelina theyarehuge.py lines 196-208](https://github.com/dobbelina/repository.dobbelina/blob/master/plugin.video.cumination/resources/lib/sites/theyarehuge.py) (last changed 2026-10-20)
- Rule34Video exposes a JSON sign-in status check: `GET https://rule34video.com/?mode=async&format=json&action=js_user_status` returned `{"id":0}` (Content-Type `application/json`) with no cookies; the site's script treats `id > 0` as a signed-in member. — [live fetch](https://rule34video.com/?mode=async&format=json&action=js_user_status); [custom.min.js](https://rule34video.com/static/js/custom.min.js?v=0046d754f0) (`onMember` when `parseInt(n.id,10)>0`)
- custom.min.js also creates a client-side `csrf_token` cookie (random string) via `getCsrfToken()` for actions such as rating, flagging and playlists; the login form does not use it (it has its own `remember_me_csrf_token`). — [custom.min.js](https://rule34video.com/static/js/custom.min.js?v=0046d754f0)

### Inferences
- For Rule34Video, a cookie set of `PHPSESSID` alone will stop working when the server-side PHP session expires; `kt_member` is the long-lived part to capture or paste. Whether the server re-creates the session from `kt_member` is the standard KVS remember-me behaviour as other tools rely on it, but not verified here.
- For Hypnotube, a pasted `PHPSESSID` likely lasts at most about 3 days unless the server refreshes `Max-Age` on each request (the anonymous GET did set a fresh 3-day expiry on a new session). That would explain why hand-pasted Hypnotube cookies need repeating.
- Rule34Video's `js_user_status` endpoint is a cheap, reliable way for EdgeLoop to check "are these cookies still signed in?" For Hypnotube, the only known check is looking for `/logout"` in a page's HTML.

### Gaps
- The name, lifetime and value format of any cookie Hypnotube sets after a successful login (beyond `PHPSESSID`) are unknown without signing in.
- The expiry of Rule34Video's `kt_member` cookie is unknown (not documented publicly; would need a real sign-in to see).
- Whether Hypnotube's server slides the `PHPSESSID` expiry forward on activity for signed-in users is unknown.

## 4. Rule34Video and the standard KVS login flow

### Takeaway
Rule34Video runs KVS (Kernel Video Sharing). The standard KVS login is a POST to `/login/` with `action=login`, `username`, `pass`, `remember_me=1`, `email_link`, plus `mode=async&format=json`, returning JSON `{"status":"success"|"failure", ...}`. It is not done through `function=get_block`; `get_block` is the KVS mechanism for loading page blocks.

### Cited Findings
- KVS signs: scripts `/static/js/kvs.min.js` and `/static/js/footer-kvs.min.js`, player `/player/kt_seek.js?v=6.4.0.6`, cookies with the `kt_` prefix, and `mode=async&function=get_block&block_id=...` URLs throughout custom.min.js. — [https://rule34video.com/login/](https://rule34video.com/login/), lines 729-737; [custom.min.js](https://rule34video.com/static/js/custom.min.js?v=0046d754f0)
- JDownloader lists rule34video.com (and rule34video.party) under its generic KVS plugin (`Rule34videoCom extends` the KVS base and has a `KVSConfigRule34videoCom` config). — [mirror/jdownloader Rule34videoCom.java](https://github.com/mirror/jdownloader/blob/master/src/jd/plugins/hoster/Rule34videoCom.java); [KernelVideoSharingComV2.java](https://github.com/mirror/jdownloader/blob/master/src/jd/plugins/hoster/KernelVideoSharingComV2.java) comment listing "rule34video.com" (2024 or earlier)
- JDownloader's generic KVS login: GET `/login/`, take the form whose action matches `.*login.*` (or build a bare POST form if none), fill the user field (`username`, else `email`) and password field (`pass`, else `password`), submit, then require a `kt_member` cookie. — [KernelVideoSharingComV2.java lines 1239-1258, 1276-1316](https://github.com/mirror/jdownloader/blob/master/src/jd/plugins/hoster/KernelVideoSharingComV2.java)
- Cumination's KVS login for theyarehuge.com posts `{'action': 'login', 'email_link': '<site>/email/', 'format': 'json', 'mode': 'async', 'pass': ..., 'remember_me': '1', 'username': ...}` to `<site>/login/`, and treats a reply containing "success" as signed in. It sends no CSRF token. — [theyarehuge.py lines 148-181](https://github.com/dobbelina/repository.dobbelina/blob/master/plugin.video.cumination/resources/lib/sites/theyarehuge.py) (2026-10-20)
- Rule34Video's live form contains exactly those fields plus `remember_me_csrf_token` and `redirect_to=/`, and the KVS script adds `format=json` and `mode=async`. — [https://rule34video.com/login/](https://rule34video.com/login/), lines 630-651; [kvs.min.js](https://rule34video.com/static/js/kvs.min.js?v=3dbf75d2e5)
- The JSON reply shape, from kvs.min.js: `status` = `"failure"` with `errors: [{field, code, message}]`, or `"success"` with optional `redirect` (and optional `data`). — [kvs.min.js](https://rule34video.com/static/js/kvs.min.js?v=3dbf75d2e5)
- Requesting `/login/?mode=async&function=get_block&block_id=logon_logon_form` returned the full HTML page (200, about 119 KB), not a bare block, so that guessed block id is not valid here. — live fetch

### Inferences
- Rule34Video's form matches the KVS standard closely; the one addition over Cumination's 2026 code is `remember_me_csrf_token`, which this KVS version (or this site's template) adds. Any app login must scrape it from `/login/` first.
- Because KVS is a commercial engine used by many sites, its login endpoint and field names are stable across sites and versions; the fragile parts are the per-site template (Rule34Video was recently redesigned: `rda-form`, `data-rd-auth`) and the CSRF field.

### Gaps
- No official KVS documentation on the login block, captcha-on-login settings, or `kt_member` handling was found (searches returned nothing relevant). KVS docs appear to be available only to licence holders.
- Whether Rule34Video's login enforces `remember_me_csrf_token` is not verified.

## 5. Hypnotube's engine and login flow

### Takeaway
Hypnotube runs Mechbunny's tube script (the page says `<meta name="author" content="Mechbunny.com">`), shared with sites such as shesfreaky.com. Login is a non-AJAX form post of `ahd_username`/`ahd_password` back to `/login`; no AJAX login endpoint was found.

### Cited Findings
- `<meta name="author" content="Mechbunny.com">` in the page head. — [https://hypnotube.com/login](https://hypnotube.com/login), live fetch
- Mechbunny template structure: `/core/js/Tube.js`, `/templates/hypnotube/js/scripts.js`, `/includes/functions.min.js`, `/action.php`, `/includes/rating/jRating.php`, `searchgate.php`. — [https://hypnotube.com/login](https://hypnotube.com/login), lines 30-33, 125; [scripts.js](https://cdn.hypnotube.com/templates/hypnotube/js/scripts.js)
- JDownloader handles hypnotube.com and shesfreaky.com in one plugin with the same `ahd_username`/`ahd_password` fields ("both"), differing only in form id (`login-form` on Hypnotube, `formLogin` on ShesFreaky). — [HypnotubeCom.java lines 231-244](https://github.com/mirror/jdownloader/blob/master/src/jd/plugins/hoster/HypnotubeCom.java) (2024 or earlier)
- The form posts with `method="post" action=""` (same URL), with no AJAX handler in the site scripts. — [https://hypnotube.com/login](https://hypnotube.com/login), line 720; [Tube.js](https://cdn.hypnotube.com/core/js/Tube.js); [scripts.js](https://cdn.hypnotube.com/templates/hypnotube/js/scripts.js)
- `functions.min.js` (618 KB, a bundled library file) contains no "login" or "captcha" strings. — [https://cdn.hypnotube.com/includes/functions.min.js](https://cdn.hypnotube.com/includes/functions.min.js), live fetch

### Inferences
- The same field names have lasted at least since JDownloader's 2024-era code and are still live in November 2026, which suggests the Mechbunny login is stable.
- The JDownloader approach (look for `/logout"` in the reply) is a usable success check for an app login.

### Gaps
- Which Mechbunny version Hypnotube runs is not exposed.
- Whether a successful login changes anything visible besides a logout link (for example a member cookie) is unknown.

## 6. Official API, OAuth or app passwords

### Takeaway
Neither site offers a public API, OAuth, or app passwords that I could find. Hypnotube has an internal, undocumented `/api/v1/search.php` used by its own search box; Rule34Video has only KVS's internal `mode=async` endpoints.

### Cited Findings
- Hypnotube's page script calls `/api/v1/search.php?index=...&filter=...&sort=...&q=...` for search. — [https://hypnotube.com/login](https://hypnotube.com/login), saved HTML line 276
- Hypnotube's scripts.js treats URLs under `/api/` (or `controller=api`) specially for the geo gate. — [scripts.js](https://cdn.hypnotube.com/templates/hypnotube/js/scripts.js)
- Rule34Video's internal JSON endpoints use `?mode=async&format=json&action=...` (for example `js_user_status`, `add_to_favourites`), and listing blocks use `?mode=async&function=get_block&block_id=...`. — [custom.min.js](https://rule34video.com/static/js/custom.min.js?v=0046d754f0)
- Neither login page offers "sign in with" buttons (Google, Discord, etc.) or any OAuth link. — [https://hypnotube.com/login](https://hypnotube.com/login); [https://rule34video.com/login/](https://rule34video.com/login/), live fetch

### Inferences
- With no API or OAuth, the options are the site's own login form, an embedded browser, or reusing browser cookies.

### Gaps
- I did not find any developer or API documentation page for either site; absence is inferred from the pages read and the lack of search results, not confirmed by the operators.

## 7. Existing open-source tools that log in to these sites

### Takeaway
JDownloader implements login for both (Hypnotube via its own plugin; Rule34Video via its generic KVS plugin, with a cookie-login option). Cumination implements KVS logins for other KVS sites in the same way. yt-dlp's Rule34Video extractor has no login, yt-dlp has no Hypnotube extractor, and gallery-dl supports neither site.

### Cited Findings
- JDownloader `HypnotubeCom.java`: account support (`enablePremium(".../signup")`), cookie re-use with validation, full login by GET `/login` + submit form with `ahd_username`/`ahd_password`, success if HTML contains `/logout"`. — [mirror/jdownloader HypnotubeCom.java lines 48, 203-257](https://github.com/mirror/jdownloader/blob/master/src/jd/plugins/hoster/HypnotubeCom.java)
- JDownloader `KernelVideoSharingComV2.java`: first tries user-supplied cookies (`account.loadUserCookies()`), validating them by loading the home page and checking `kt_member`; otherwise does a full login as described in section 4. It also has `requiresCookieLogin()` for KVS sites where form login is impossible. — [KernelVideoSharingComV2.java lines 1205-1263](https://github.com/mirror/jdownloader/blob/master/src/jd/plugins/hoster/KernelVideoSharingComV2.java)
- The GitHub `mirror/jdownloader` repository was last pushed 2024-09-28, so these JDownloader details are from 2024 or earlier; the official JDownloader SVN may be newer. — [GitHub API for mirror/jdownloader](https://github.com/mirror/jdownloader)
- Cumination (Kodi): KVS login for theyarehuge.com (and similar `kt_member` code in whoreshub.py, javbangers.py); its Hypnotube module has no login. — [theyarehuge.py](https://github.com/dobbelina/repository.dobbelina/blob/master/plugin.video.cumination/resources/lib/sites/theyarehuge.py); [hypnotube.py](https://github.com/dobbelina/repository.dobbelina/blob/master/plugin.video.cumination/resources/lib/sites/hypnotube.py)
- yt-dlp `rule34video.py` (123 lines, last commit 2026-06-21) defines `Rule34VideoIE` with no `_NETRC_MACHINE` and no login code. — [yt-dlp rule34video.py](https://github.com/yt-dlp/yt-dlp/blob/master/yt_dlp/extractor/rule34video.py)
- A GitHub code search for "hypnotube" in yt-dlp/yt-dlp and in mikf/gallery-dl returned nothing; gallery-dl's extractor folder has rule34us, rule34vault and rule34xyz but nothing for rule34video or Hypnotube. — [GitHub code search](https://github.com/search?q=hypnotube&type=code); [gallery-dl extractors](https://github.com/mikf/gallery-dl/tree/master/gallery_dl/extractor)
- The stashapp CommunityScrapers `Hypnotube.yml` contains no cookie, login or driver settings (plain public scraping). — [stashapp/CommunityScrapers Hypnotube.yml](https://github.com/stashapp/CommunityScrapers/blob/master/scrapers/Hypnotube.yml)

### Inferences
- The JDownloader and Cumination code paths are working references for an EdgeLoop implementation: same endpoints, same field names, same success checks (`/logout"` for Hypnotube; `kt_member` cookie or JSON `success` for KVS).
- Since yt-dlp has no login for either site, EdgeLoop's yt-dlp fallback can only use cookies (`--cookies` or `--cookies-from-browser`).

### Gaps
- Current (2026) JDownloader source was not checked; the official SVN was not accessed.
- Whether gallery-dl's generic or KVS-like code could cover rule34video was not investigated beyond the extractor listing.

## 8. Overall: is a programmatic HTTP POST login feasible, and how fragile?

### Takeaway
Yes for both, as of 2026-11-22: neither login form has a captcha, neither site challenged a plain HTTP client, and both flows are two requests (GET then POST). Hypnotube is the easier one (no token at all); Rule34Video needs the scraped `remember_me_csrf_token` and returns clean JSON. Fragility comes from things outside the app's control: a captcha being switched on (both sites already use captchas on sign-up), DDoS-Guard moving to a JavaScript challenge, Hypnotube's geo/age gate, and template changes.

### Cited Findings
- Hypnotube login: no hidden fields, no captcha, plain form post (section 1-2). — [https://hypnotube.com/login](https://hypnotube.com/login)
- Rule34Video login: AJAX JSON form with CSRF hidden field, no captcha on the login form, DDoS-Guard passive for curl (sections 1-2). — [https://rule34video.com/login/](https://rule34video.com/login/)
- Both sites already have captcha code paths: Hypnotube `captcha.php` on sign-up and password reset; KVS `code`/reCAPTCHA handling in kvs.min.js and Rule34Video's `/captcha/signup/`. — [https://hypnotube.com/signup](https://hypnotube.com/signup); [https://rule34video.com/signup/](https://rule34video.com/signup/); [kvs.min.js](https://rule34video.com/static/js/kvs.min.js?v=3dbf75d2e5)
- Other tools have kept these logins working over time: JDownloader (2024 code, field names still live in 2026) and Cumination (KVS login code updated 2026-10-20). — [HypnotubeCom.java](https://github.com/mirror/jdownloader/blob/master/src/jd/plugins/hoster/HypnotubeCom.java); [theyarehuge.py](https://github.com/dobbelina/repository.dobbelina/blob/master/plugin.video.cumination/resources/lib/sites/theyarehuge.py)

### Inferences
- An app login would need to store the user's password (or ask every time the 3-day Hypnotube session lapses). An embedded browser or cookie import avoids storing passwords and survives captchas and DDoS-Guard challenges; a programmatic login is simpler for the user but breaks silently if a captcha is switched on.
- A hybrid is plausible: try a programmatic login, and fall back to an embedded browser window when the reply shows a captcha field (`code` error in Rule34Video's JSON, or a captcha image in Hypnotube's reply HTML).
- Rule34Video's `js_user_status` JSON endpoint gives a clean way to check stored cookies before each session; for Hypnotube, check for a `/logout"` link.

### Gaps
- None of the success paths was exercised (by design: no credentials submitted). The user would need to test a real sign-in to confirm which cookies each site sets, their lifetimes, and whether the 720p Hypnotube files appear with them.
