# Rollcall brand and design guide

Last updated: Oct 21, 2026

This guide defines how Rollcall looks, sounds, and behaves. It is written for the developers and agents building the portal, the docs site, and the install script output, so every rule is concrete enough to implement. When this guide and a mockup disagree, this guide wins. The product itself is described in [product-spec.md](product-spec.md).

Assets live in [`docs/brand/`](brand/). Design tokens ship as [`docs/brand/tokens.css`](brand/tokens.css).

## 1. The brand in one paragraph

Rollcall is the calm, exact colleague who has done a thousand onboardings and never skips a step. It holds standing admin rights over a Microsoft 365 tenant, so it must look and sound trustworthy before it looks clever. The visual identity is quiet and functional: a deep teal brand color, a single warm marigold accent used sparingly, a typeface built for telling characters apart, and a UI that always shows what will happen before it happens.

**Personality:** calm, precise, plain-spoken, careful, helpful.
**Not:** playful, salesy, cute, alarmist, corporate.

### Design principles

1. **Show before you change.** Every action has a preview. The preview is the most important screen in the product and gets the most design care.
2. **Make state obvious.** Every user, job, step, and approval has a visible state, shown with an icon and a word as well as a color.
3. **Exact over friendly.** Names, counts, UPNs, and group names appear exactly as Microsoft 365 stores them. Never round, truncate silently, or paraphrase an identifier.
4. **Quiet by default.** Color is for state and action. Most of the screen is neutral so that a red step or a pending approval stands out.
5. **Readable under pressure.** Offboardings happen on bad days. Text is large enough, contrast is high, and destructive actions are hard to trigger by accident.

## 2. Name

- In prose, write **Rollcall**: one word, capital R, lowercase c. Never "RollCall", "Roll Call", or "ROLLCALL".
- The logo wordmark is set in lowercase ("rollcall"). That is a logo treatment only; do not write the name in lowercase in text.
- The product name does not take "the" ("Rollcall sends a welcome pack", not "the Rollcall sends").
- The CLI, container images, and repository use `rollcall` in lowercase.

### Microsoft names and trademarks

Rollcall works with Microsoft products but is not a Microsoft product.

- Refer to Microsoft products by their current names: Microsoft 365, Microsoft Entra ID, Exchange Online, Active Directory, Microsoft Graph, Entra Connect. On first use in a document use the full name; after that "Entra ID" and "Exchange Online" are fine.
- Do not use Microsoft logos, product icons, the Fluent UI look, or Microsoft brand blue (#0078D4) in Rollcall's own UI, docs, or marketing.
- Use "for Microsoft 365" or "works with Microsoft 365", never "Microsoft Rollcall" or anything that suggests endorsement.
- The README and the docs site footer carry this line: *Rollcall is an independent open-source project and is not affiliated with or endorsed by Microsoft. Microsoft 365, Microsoft Entra, and Exchange Online are trademarks of the Microsoft group of companies.*

## 3. Logo

![Rollcall logo](brand/rollcall-logo.svg)

**Concept: the register.** The mark is a roster on a rounded teal tile: three rows, each with a marker and a name line. The top row is checked off in marigold. It reads as "a list of people, worked through one at a time", which is what Rollcall does for joiners and leavers. The wordmark is "rollcall" in Atkinson Hyperlegible Next Bold, converted to outlines so it renders without the font installed.

### Files

| File | Use |
| --- | --- |
| [`rollcall-logo.svg`](brand/rollcall-logo.svg) | Primary lockup (mark plus wordmark) on light backgrounds. README header, docs site header, login page. |
| [`rollcall-logo-dark.svg`](brand/rollcall-logo-dark.svg) | Lockup for dark backgrounds. Docs site and portal in dark mode. |
| [`rollcall-mark.svg`](brand/rollcall-mark.svg) | Mark alone, 24px and up. Portal sidebar, GitHub social preview, app icons. |
| [`rollcall-mark-mono.svg`](brand/rollcall-mark-mono.svg) | Single-ink mark for monochrome contexts such as printed day-1 checklists. |
| [`favicon.svg`](brand/favicon.svg) | Simplified mark (tile and check only) for 16px to 23px: browser tab, small badges. |
| [`rollcall-wordmark.svg`](brand/rollcall-wordmark.svg) | Wordmark alone, only where the mark already appears nearby. |

Raster exports (PNG at 16, 32, 180, 192, 512px for favicons and the PWA manifest, and a 1280x640 GitHub social preview) are generated from these SVGs at build time. Do not hand-edit rasters.

### Rules

- **Clear space:** keep empty space around the logo at least equal to the tile's corner radius (11 units on the 48-unit tile, so about a quarter of the mark's height).
- **Minimum size:** the lockup at 96px wide; the full mark at 24px; below 24px use `favicon.svg`.
- **Do not** recolor the tile, swap the marigold check for another color, add shadows, outlines, or gradients, rotate or stretch the logo, set the wordmark in a different font, or place the light lockup on a busy photo.
- The mark's colors stay the same in light and dark mode; only the wordmark color changes.

## 4. Color

The palette is a teal-tinted neutral scale, one brand color (Roster teal), one accent (Marigold), and four status colors. All text pairings meet WCAG 2.2 AA (4.5:1 for normal text). Control outlines meet 3:1.

### Brand colors

| Name | Light | Dark | Use |
| --- | --- | --- | --- |
| Roster teal (`--rc-primary`) | `#0E6A73` | `#5CC4CB` | Primary buttons, links, selected nav item, focus ring, logo tile |
| Teal hover (`--rc-primary-hover`) | `#0A5359` | `#7DD3D9` | Hover and pressed state of primary elements |
| Teal subtle (`--rc-primary-subtle`) | `#E3F2F3` | `#0F3236` | Selected table row, active filter chip background |
| Marigold (`--rc-accent`) | `#E8A33D` | `#F0B357` | Logo check, small highlights, illustration accents, the "new" dot. Never for text on a light background, never for status. |

The logo tile always uses light-mode teal `#0E6A73` and a marigold check (`#F0AE48`), in both themes.

### Neutrals

| Token | Light | Dark | Use |
| --- | --- | --- | --- |
| `--rc-bg` | `#F6F8F8` | `#0C1618` | Page background |
| `--rc-surface` | `#FFFFFF` | `#132226` | Cards, tables, dialogs, sidebar |
| `--rc-sunken` | `#EDF2F2` | `#091113` | Code blocks, read-only fields, table header |
| `--rc-border` | `#D3DEDF` | `#28403F` | Dividers, card edges |
| `--rc-border-strong` | `#7A8F92` | `#5E7A7D` | Input, checkbox, and select outlines |
| `--rc-text` | `#13252A` | `#E4EDEE` | Body text, headings |
| `--rc-text-muted` | `#4C6166` | `#9CB1B4` | Secondary text, labels, timestamps |
| `--rc-text-on-primary` | `#FFFFFF` | `#06282C` | Text on a Roster teal fill |

### Status colors

Status color is always paired with an icon and a word. Each status has a strong color (text, icons, borders) and a subtle color (badge and banner backgrounds).

| Status | Light strong / subtle | Dark strong / subtle | Means |
| --- | --- | --- | --- |
| Success | `#1D7A3F` / `#E6F4EA` | `#5BC585` / `#12301F` | Step or job succeeded, agent connected, license seat available |
| Warning | `#93590A` / `#FDF1DC` | `#E8A84A` / `#33260F` | Needs attention soon: certificate expires in 30 days, license on a disabled account, sync pending |
| Danger | `#B42318` / `#FDE8E6` | `#F47C70` / `#3A1714` | Failed step, destructive action, agent offline, removal in a preview |
| Info | `#2558A8` / `#E7EFFB` | `#83AEF2` / `#15233D` | Scheduled, awaiting approval, neutral notices |

### Measured contrast

| Pairing | Light | Dark |
| --- | --- | --- |
| Text on page background | 14.9:1 | 15.4:1 |
| Muted text on page background | 6.1:1 | 8.2:1 |
| Primary on surface (links) | 6.3:1 | 8.0:1 |
| Text on primary button | 6.3:1 | 7.6:1 |
| Success / Warning / Danger / Info strong on its subtle | 4.7 / 5.1 / 5.6 / 6.0 :1 | 6.7 / 7.1 / 6.1 / 6.9 :1 |
| Strong border on surface | 3.4:1 | 3.6:1 |

If you change a value, rerun the contrast check and update this table.

### Proportion

A typical portal screen is about 90% neutrals, 5 to 8% Roster teal (buttons, links, the selected nav item), and the rest status color. Marigold appears only in the logo and the occasional small highlight. If a screen looks colorful, something is wrong.

### Dark mode

Dark mode is a first-class theme, not an inversion. The portal follows the operating system setting by default and offers a Light / Dark / System choice in the user menu. Dark surfaces are teal-tinted near-black, never pure black. Primary and status colors are lighter in dark mode so they keep contrast.

## 5. Typography

| Role | Typeface | License |
| --- | --- | --- |
| Interface and docs | **Atkinson Hyperlegible Next** (weights 400, 500, 700) | SIL Open Font License 1.1 |
| Identifiers, codes, commands | **Atkinson Hyperlegible Mono** (weights 400, 500) | SIL Open Font License 1.1 |

**Why this family.** Atkinson Hyperlegible was designed by the Braille Institute so that easily confused characters look different: capital I, lowercase l, and the digit 1; capital O and zero; B and 8. Rollcall's users read Temporary Access Pass codes, one-time passwords, UPNs, and object IDs aloud to new staff over the phone. A misread character there is a failed first login. The Mono companion keeps the same shapes for codes and commands.

**Self-host the fonts.** Bundle the font files with the web app (for example from the `@fontsource/atkinson-hyperlegible-next` and `@fontsource/atkinson-hyperlegible-mono` packages). Do not load them from Google Fonts or any CDN: Rollcall is a self-hosted security tool, it should work on networks that block third-party hosts, and it should not leak its users' IPs to anyone. The fallback stacks in `tokens.css` keep the UI usable if the files fail to load.

### Type scale

| Token | Size / line height | Weight | Use |
| --- | --- | --- | --- |
| `--rc-text-2xl` | 32 / 40 | 700 | Dashboard figures, docs page H1 |
| `--rc-text-xl` | 24 / 32 | 700 | Page title |
| `--rc-text-lg` | 20 / 28 | 700 | Section heading, dialog title |
| `--rc-text-md` | 16 / 24 | 400 | Body text, form inputs, buttons (500) |
| `--rc-text-sm` | 14 / 20 | 400 | Table cells, secondary text, labels (500) |
| `--rc-text-xs` | 12 / 16 | 500 | Badges, timestamps, captions. Not for anything a user must act on. |

### Rules

- Use **Mono** for anything a technician might copy or read out: UPNs, email addresses in tables, passwords, TAP codes, object IDs, group IDs, certificate thumbprints, CLI commands, file paths, and log lines.
- Show TAP codes and temporary passwords at `--rc-text-lg` in Mono with a Copy button, grouped in fours where the format allows (`Kx7m Qp2R 9tVw`).
- Use tabular numbers (`font-variant-numeric: tabular-nums`) in tables, counts, and dates.
- Sentence case for every heading, button, menu item, and label ("Run offboarding", not "Run Offboarding"). Uppercase only for small eyebrow labels, at `--rc-text-xs` with 0.06em letter-spacing.
- Keep docs body text at about 70 characters per line.

## 6. Layout, spacing, and shape

**Grid and spacing.** Everything sits on a 4px grid. Use the spacing tokens (`4, 8, 12, 16, 24, 32, 48, 64`) and lay out siblings with `gap`, not ad hoc margins.

**App shell.**

- Left sidebar, 240px wide, collapsible to a 64px icon rail. Logo at the top, then navigation: Dashboard, Users, Onboarding, Offboarding, Groups, Licenses, Conditional Access, Approvals, Audit log. Admins also see Templates and Settings. Help (linking to the docs) and the user menu sit at the bottom.
- Top bar, 56px, with global user search (shortcut `/`), the job activity indicator, and the tenant name in Mono so technicians always know which tenant they are in.
- Content area with 32px padding, max width 1280px for forms and detail pages; tables may use the full width.

**Breakpoints.** Rollcall is a desktop-first tool, but it must stay usable on a tablet and readable on a phone for approvals on the go. Breakpoints at 640px (sidebar becomes a drawer), 1024px (sidebar collapses to the rail), and 1280px.

**Density.** Default table row height is 44px; a Compact setting gives 36px. Touch targets are at least 32px on desktop and 44px below 1024px.

**Shape.** Radius 4px for badges and checkboxes, 6px for buttons and inputs, 10px for cards and dialogs. Cards use a 1px `--rc-border` edge and no shadow. Shadows (`--rc-shadow-overlay`) are only for things that float: menus, popovers, dialogs, and toasts.

## 7. Icons

Rollcall uses **[Lucide](https://lucide.dev)** (ISC license, compatible with GPLv3). Use the `lucide-static` SVGs or the `lucide` package for the chosen frontend framework.

- Outline style, 24-unit grid, 2px stroke, round caps and joins. Never mix in filled or duotone icons.
- Render at 20px in buttons and navigation and 16px inline with `--rc-text-sm` text. At 16px set the stroke width to 1.75.
- Icons inherit `currentColor`. Color an icon only when it carries a status.
- An icon next to a text label is decorative (`aria-hidden="true"`). An icon-only button needs an `aria-label` and a tooltip.
- If Lucide lacks an icon, draw a new one on the same 24-unit grid with a 2px stroke and add it to `docs/brand/icons/`. Do not pull from another icon set.

### Icon map

Use these icons for these concepts everywhere: navigation, buttons, audit log entries, and docs.

| Concept | Lucide icon | Concept | Lucide icon |
| --- | --- | --- | --- |
| Dashboard | `layout-dashboard` | Onboarding | `user-plus` |
| Users | `users` | Offboarding | `user-minus` |
| Groups | `folder-tree` | Licenses | `ticket` |
| Conditional Access | `shield-check` | MFA and TAP | `key-round` |
| User attributes | `id-card` | Role templates | `clipboard-list` |
| Approvals | `stamp` | Audit log | `scroll-text` |
| Hybrid AD and agent | `server` | Entra Connect sync | `refresh-cw` |
| Mailbox | `mail` | Scheduled | `calendar-clock` |
| Settings | `settings` | Search | `search` |
| Undo or reverse | `rotate-ccw` | Block sign-in | `ban` |

### Status icons

| State | Icon | Color |
| --- | --- | --- |
| Succeeded | `circle-check` | Success |
| Failed | `circle-x` | Danger |
| Needs attention | `triangle-alert` | Warning |
| Running | `loader-circle` (spins, stops under reduced motion) | Primary |
| Queued | `circle-dashed` | Muted text |
| Scheduled | `calendar-clock` | Info |
| Awaiting approval | `hourglass` | Info |
| Paused or skipped | `circle-pause` | Muted text |

## 8. Components

These are the building blocks the portal needs. Build them once, from the tokens, and reuse them. Keyboard focus is always visible: a 2px `--rc-focus` outline with a 2px offset.

### Buttons

| Variant | Look | Use |
| --- | --- | --- |
| Primary | Teal fill, `--rc-text-on-primary` text | The one main action on a screen: "Review changes", "Run onboarding" |
| Secondary | Surface fill, 1px strong border, text color | Other actions: "Save draft", "Export CSV" |
| Ghost | No fill or border, teal text | Low-emphasis actions in tables and toolbars |
| Danger | Danger fill, white text (dark mode: dark text) | The final confirm of a destructive action only |

Height 36px (32px compact), 16px horizontal padding, 6px radius, label weight 500. One primary button per view. Buttons name the action ("Remove 3 licenses"), never "OK", "Submit", or "Yes".

### Form fields

Label above the field (weight 500, `--rc-text-sm`), helper text below in muted text, error text below in danger with a `circle-x` icon. Fields use the strong border, 6px radius, 36px height. Required fields are the default; mark optional ones with "(optional)" rather than marking required ones with an asterisk. Pickers for users, groups, licenses, and OUs are searchable comboboxes that show the display name plus the identifier in Mono.

### Status badge

A pill with a subtle background, strong text, the status icon at 14px, and the status word, for example `circle-check` + "Succeeded" or `hourglass` + "Awaiting approval". Never a colored dot alone.

### Change preview (signature component)

The preview is shown before every action and is the heart of Rollcall. It lists every change grouped by target system (Entra ID, Exchange Online, on-prem AD) and then by area (account, groups, licenses, policies, MFA).

- Each line starts with an operation marker in Mono: `+` add (success color), `−` remove (danger color), `~` change (info color), followed by a plain-language description and the exact object name. Example: `+ Add to group  Sales – All Staff (sg-sales-all)`.
- Attribute changes show old and new values side by side: `~ Department  Sales → Finance`.
- Lines that need approval carry an "Approval required" badge. Lines that will be skipped (for example, already a member) are listed in muted text with the reason.
- A summary bar sits on top: "12 changes across 3 systems · 2 need approval". The primary button at the bottom repeats the count: "Run 12 changes".
- The preview is also what the audit log stores and shows, so the before and after views look identical.

### Job and step progress

A vertical list of steps, each with its status icon, name, target system, and duration. Failed steps expand to show the error message in plain language, the raw error from Microsoft or AD in a collapsible Mono block, and a "Retry step" button. A job never shows a single spinner for many steps.

### Approval banner

A full-width info banner at the top of a pending job: who requested it, when, what it changes (link to the preview), and Approve / Reject buttons for approvers. Requesters cannot approve their own request; for them the buttons are replaced by "Waiting for an approver".

### Destructive confirmation

Offboarding, removing a CA exclusion, and bulk removals use a confirmation dialog that restates the impact in one sentence and asks the technician to type the user's UPN (or the number of affected users for bulk actions) before the danger button enables.

### Tables

Sticky header on a sunken background, `--rc-text-sm` rows, tabular numbers, row hover in `--rc-sunken`, selected rows in `--rc-primary-subtle`. Identifiers in Mono. Bulk actions appear in a toolbar that replaces the filters when rows are selected. Every table supports column sorting and a CSV export where the spec calls for one.

### Toasts

Bottom-right, surface background with an overlay shadow, a status icon, one sentence, and an optional action ("View job"). Success toasts dismiss after 5 seconds; error toasts stay until dismissed.

### Empty states

A 48px Lucide icon in muted text, one sentence saying what will appear here, and the action that fills it. No illustrations, mascots, or jokes. Example: "No role templates yet. Templates define what a job gets: groups, licenses, and policies. [Create template]".

## 9. Motion

Motion explains change; it is never decoration.

- Transitions use `--rc-duration` (150ms) and `--rc-ease`. Dialogs and drawers may take 200ms.
- Animate opacity and transform only. No bouncing, no parallax, no confetti on success.
- The only looping animation is the Running spinner.
- Under `prefers-reduced-motion: reduce`, remove transitions and stop the spinner (the icon stays, static).

## 10. Accessibility

Rollcall targets **WCAG 2.2 AA** in the portal and the docs site. In practice:

- All text contrast at least 4.5:1, control outlines and focus rings at least 3:1 (the palette above already meets this).
- State is never shown by color alone: always icon plus word.
- Every interactive element works by keyboard in a logical order, with a visible focus ring. Dialogs trap focus and return it on close.
- Forms have real `<label>` elements; errors are linked with `aria-describedby` and announced.
- Job progress updates are announced through a polite live region.
- Text resizes to 200% without loss of content; the layout works at 320px wide for approvals.

## 11. Voice and tone

Rollcall talks like a senior technician writing a clear runbook: direct, specific, and calm. Users are IT professionals, so use the real Microsoft 365 terms, but explain consequences in plain language.

### Principles

1. **Say exactly what will happen.** Name the object, the count, and the system. "Remove Jordan Lee from 7 groups in Entra ID" beats "Update group memberships".
2. **Lead with the outcome, then the detail.** "Offboarding finished with 1 failed step." Then the step.
3. **Errors explain and fix.** What went wrong, why if known, and what to do next. No "Oops", no "Something went wrong", no apologies.
4. **Calm, never alarming.** Even when something failed, describe it plainly. Reserve exclamation marks for never.
5. **Active voice, second person, present tense.** "You don't have permission to approve this", not "Permission was denied".
6. **Short.** Buttons are 1 to 4 words. Messages are one or two sentences.

### Tone by situation

| Situation | Tone | Example |
| --- | --- | --- |
| Routine success | Brief, factual | "Onboarding finished. Jordan Lee can sign in on Nov 3." |
| Preview | Precise, complete | "12 changes across 3 systems. 2 need approval before they run." |
| Destructive action | Serious, explicit | "This blocks sign-in for Jordan Lee immediately and signs them out of every session. Type their UPN to confirm." |
| Failure | Calm, actionable | "Couldn't convert the mailbox to shared. Exchange Online reported that the mailbox is on litigation hold. Remove the hold or skip this step, then retry." |
| Warning | Helpful, early | "The app certificate expires in 21 days. Rotate it now to avoid an outage." |
| Empty state | Inviting, useful | "No one is starting this week." |
| Docs | Instructional, step-by-step | "Run the install command. When it finishes, it prints your portal URL." |

### Do and don't

| Do | Don't |
| --- | --- |
| "Run offboarding" | "Submit" |
| "Remove 3 licenses" | "Are you sure?" |
| "Couldn't reach the AD agent on DC-01. It last checked in 14 minutes ago." | "Error 500: agent unavailable" |
| "Jordan Lee was added to Sales – All Staff." | "Success!" |
| "You need the Approver role to approve this request." | "Access denied." |
| "Sync started. Licenses will be assigned once Entra Connect finishes, usually within 5 minutes." | "Please wait..." |
| "Rollcall can't store passwords. Sign in with your Microsoft work account." | "For your security, we've made the decision not to..." |

### Word list

| Use | Avoid | Notes |
| --- | --- | --- |
| user | employee, staff member, resource | "User" matches Entra ID. In welcome packs addressed to the new hire, use their name. |
| onboarding, offboarding (nouns); onboard, offboard (verbs) | on-boarding, joiner/leaver process, termination | |
| role template | profile, preset, blueprint | |
| technician, approver, admin | operator, agent (except the AD agent), superuser | Match the portal role names. |
| sign in, sign out | log in, login (as a verb) | "Sign-in" as a noun is fine. |
| block sign-in | disable account | Except on-prem AD, where "disable" is correct. |
| Temporary Access Pass (TAP after first use) | temp code | |
| Conditional Access policy | CA rule | "CA" is fine after first use. |
| AD agent | connector, bridge | |
| failed | crashed, broke, errored | |
| remove | delete | Unless the object is actually deleted. |

### Formatting conventions

- Dates: "Nov 3, 2026" in the UI; ISO 8601 (`2026-11-03T09:00:00Z`) in exports and logs. Times in the technician's local time zone with the zone shown on hover.
- Numbers: digits for all counts ("3 groups"); thousands separators in the UI.
- Identifiers: exact, in Mono, never truncated without a tooltip and a copy button.
- Lists of 3 or more objects in messages become "7 groups" with a link to the full list.

## 12. Documentation style

Docs are a v1 requirement, so they share this guide.

- The docs site uses the same tokens, fonts, logo, and dark mode as the portal.
- Write in the voice above. Each day-1 guide step has: what you're doing (one sentence), the exact command or screen, the expected result, and "Check it worked".
- Commands go in Mono code blocks with a copy button and no shell prompt character, so they paste cleanly.
- Screenshots are taken at 1440x900 in light mode with the sample tenant "Contoso" and fictional users, cropped to the relevant panel, with a 1px `--rc-border` edge. Highlight a target with a 2px Marigold rectangle, never with arrows or freehand marks. Retake screenshots when the UI changes.
- Callouts use the status colors: Info for tips, Warning for "check this first", Danger for "this cannot be undone".

## 13. Install script and CLI output

The install script is the first thing an admin sees, so it follows the same voice.

- Print the step being run, then `✓` or `✕` with a one-line result. Use the terminal's green and red only for those marks, and stay readable with `NO_COLOR` set.
- On failure, print what failed, the likely cause, and the next command to run.
- Finish with the portal URL on its own line and the next step: "Open https://rollcall.example.com to finish setup."

## 14. Asset checklist for v1

- [x] Logo lockups, mark, mono mark, favicon (SVG): `docs/brand/`
- [x] Design tokens: `docs/brand/tokens.css`
- [ ] Raster exports and PWA manifest icons, generated at build time
- [ ] GitHub social preview image (1280x640)
- [ ] Bundled font files in the web app
- [ ] Component library implementing section 8, with a reference page in the dev build
