# Collaborate-Bench for LinkedIn (Chrome / Edge extension)

A side panel that sits next to LinkedIn, like the recruiter extensions in commercial ATS tools.

**On a LinkedIn profile:**
- See at once if the person is already in Collaborate-Bench, and who owns them.
- **Save as lead:** AI reads the profile for title, skills, experience and summary. Leads stay off the bench until you move them there from the portal's **LinkedIn Leads** page.
- Map the person to a requirement as a lead, or reject them for it.
- Log each touch (LinkedIn message, InMail, call, email, SMS…) with its outcome.
- Set reminders ("call back Thursday").
- See the whole activity timeline, including your teammates' touches.
- **Jobs** tab: open requirements that fit this person, best first.
- Heads-ups, for example:
  - a teammate contacted them this week
  - they said "not interested"
  - they live in the EU/EEA or the UK (GDPR consent)
  - they're based outside the US
  - a reminder is overdue

**On a LinkedIn job post:**
- AI drafts a requirement from the post (title, client or vendor, mode, contract type, rate, skills, visas) for you to check and save.
- See which bench consultants match, before and after saving.

**What it does not do:**
- It reads only the page you have open, and only while the panel is open.
- Nothing is saved until you click.
- It doesn't crawl LinkedIn, send messages or apply to jobs.

Automated scraping is against LinkedIn's User Agreement. Keep usage to normal recruiter browsing.

## Install (developer mode)

1. Open `chrome://extensions` (or `edge://extensions`) and turn on **Developer mode**.
2. Click **Load unpacked** and pick this `extension` folder.
3. Pin **Collaborate-Bench for LinkedIn**, then click its icon to open the side panel.
4. Sign in:
   - **Server address:** the portal address. In development that's `http://localhost:5173`, which proxies `/api` to the API. In production it's your site, e.g. `https://bench.yourcompany.com`; Chrome asks once to allow it.
   - **Password users:** use your work email and password.
   - **Microsoft sign-in users:** in the portal, open **LinkedIn Leads** and press **Connect the extension**. Paste the key into the panel. Keys last 14 days (`Jwt:ExtensionLifetimeDays`).

To publish for a whole team, zip this folder and upload it to the Chrome Web Store as an unlisted or private item. Alternatively, push it with Chrome Enterprise policy (`ExtensionInstallForcelist`).

## Files

| File | What it does |
|---|---|
| `manifest.json` | Manifest V3: side panel and permissions (LinkedIn, local dev servers, and your server once you allow it) |
| `background.js` | Opens the side panel when the toolbar icon is clicked |
| `sidepanel.html/.css/.js` | The panel. It reads the active LinkedIn tab with `chrome.scripting` and calls `/api/linkedin/*` |

LinkedIn changes its page markup from time to time. If names or job details stop being read, update the selectors in `readLinkedInPage()` in `sidepanel.js`.
