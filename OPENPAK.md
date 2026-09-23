# OpenPak integration

This fork is upstream [Ryujinx](https://git.ryujinx.app/ryubing/ryujinx) (MIT) with one addition:
a client for the **OpenPak** network. The emulation core is untouched; everything OpenPak adds
lives in the networking layer and the surrounding UI.

## Why this base

`NextendoNetwork/Ryujinx-Nextendo` is licensed **PolyForm Shield 1.0.0**, whose Noncompete
clause bars using the software to provide a product competing with the licensor's — which is
what OpenPak is. Its Ryujinx core is still MIT and could be taken, but that part is upstream
anyway. So: upstream base, OpenPak layer written here. Nextendo builds are a source of
*observed behavior* only (what a title asks for, which host, which error code), never code.

Upstream is `upstream` (`git.ryujinx.app`); `origin` is `openpak/ryujinx`.

## What upstream already does

- **DNS redirection is free.** `DnsMitmResolver` reads an Atmosphère hosts file from the virtual
  SD card at `/atmosphere/hosts/default.txt` and supports the AMS `*` wildcard and `%`
  environment substitution. `0.0.0.0 *.nintendo.net` style entries need no emulator code.
  Since every OpenPak console-facing service sits behind Traefik SNI passthrough on **443**
  (`../../ports.md`), a name→IP map is the whole redirect. No port rewriting is required.
- `Sfdnsres`, `nsd`/`FqdnResolver`, `bsd`, `ssl` and `nifm` are implemented well enough that a
  title reaches a server and completes a TLS handshake.

## What is missing, in the order it blocks a title

1. ~~**Trusting the OpenPak CA.**~~ **Done.** A validation callback accepts a chain rooted at the
   OpenPak CA for the guest's TLS, and refuses everything else exactly as before. The CA is
   fetched from the website over ordinary public TLS by Settings -> OpenPak -> Fetch, rather than
   being dropped into a data directory by hand.
2. ~~**Device auth.**~~ **Done.** The emulator walks dauth/aauth and asks for a device token.
3. ~~**The BAAS access token.**~~ **Done.** `acc:u0` hands the guest a real id_token carrying
   OpenPak's `nnex` claim.
4. ~~**Getting an identity into the emulator.**~~ **Done.** Two halves, both from OpenPak and
   neither minted locally: the person signs in to the website (`POST /api/v1/token`, bearer in
   the OS password store), and the emulated console links its device account to that account
   as part of the same sign-in (a failed link shows *Try again* on the Account page).
5. ~~**The friend graph.**~~ **Done.** `friend:u` serves the account's real list, ids, counts and
   presence from a cache kept warm on a timer. What is still honestly stubbed, and why:
   `GetFriendRequestList` (20201), because `FriendRequestImpl`'s layout is not established and
   zeros would be read as data; the favourites-only filter, because the core has no per-viewer
   favourite flag yet; and the newly-arrived request count, because nothing tracks what the
   console has already been shown.
6. ~~**NAT check**~~ **Done.** A game's own nncs probes reach nn-nncs through the redirect (nncs2
   on its own address, from the profile's overrides), and Status runs the console's NAT type
   test from this machine (see "Four gaps closed" below). Per-title patches as titles need them.
7. ~~**Native News delivery.**~~ **Done for BCAT data:** a title's dataset is written into its
   delivery cache at launch and read through `bcat:u`.

## Configuration surface

Nothing to configure. The first launch sets up the open profile — *Sign in with OpenPak*,
*Create an account* (the website's register page, then sign in), or *Play offline* — and never
asks again (the OpenPak menu still has Sign in). Everything else happens on its own:

- the network profile is fetched at launch and pins the console-facing CA it names;
- the console's DNS is pointed at the profile's address in the Atmosphère hosts file before a
  game starts;
- signing in also links the emulated console to the account: the website mints the token the
  console's link page would have (`POST /api/v1/me/switch/link`), so a title gets a real id_token
  straight away and the password is never typed twice — and an install whose device account was
  lost relinks on the next launch by itself;
- a title's cloud save comes down before it starts and goes up when it exits (see below).

Settings -> OpenPak keeps, in the UX spec's order (`emulators/prds/openpak-ux-spec.md` §3.13): the
on/off toggle (off is stock Ryujinx), "{profile} — Signed in as {name}" with *Sign in...* /
*Sign out...*, *Open OpenPak...*, *Account at startup*, *Sync cloud saves automatically*, crash
reports, then *Show notifications* and *Notification corner*, the DNS redirect toggle, and an
*Advanced* expander with the website address and a network refresh (its result on a line beside
it), for anyone running their own deployment. `OPENPAK_SERVER`, `OPENPAK_CA` and
`OPENPAK_WEBSITE` still override everything, so the shared launchers keep working with no GUI
in the loop.

The account token lives in the OS password store — Keychain, Credential Manager, or libsecret —
and there is deliberately no file fallback: without a store, sign-in refuses and says why.

## Profiles

Each Ryujinx profile is its own OpenPak account, and one is active at a time — a console with
one user signed in. The design is `emulators/prds/emulator-integration-prd.md` §3.1; here:

- the bearer is kept per site and profile (`{site}/{profileId}` in the password store), the
  device account per server and profile (`openpak/device-{server}-{profileId}.json`), and
  `openpak/profiles.json` records which account each profile is linked to — for the badges on the
  profile tiles and for refusing a second profile on the same account;
- switching profile takes the old account offline at once, drops everything cached for it, and
  signs the new one in; deleting a profile revokes its bearer and forgets its device account;
- at launch, more than one profile follows *Account at startup*: last used (default), ask, or a
  named profile. The picker's *Add account* runs the same setup as the first launch, for a new
  profile. A game started from a launcher or `--profile` never sees a picker;
- the guest only ever gets the active profile's identity: `acc:u0` and `friend:u` answer any other
  profile as offline, `TrySelectUserWithoutInteraction` picks the active profile rather than the
  first, and a title's own profile picker is skipped by default (System -> *Skip user profiles
  manager*, which is how to get it back);
- a linked profile goes by the account's nickname: every linked sign-in renames it (cut to 32
  characters), so a local rename lasts only until the next one. Signing in from the setup also
  copies the account's avatar into the profile, once;
- an install from before profiles hands its bearer and device account to the profile open at the
  first launch since, so nobody is signed out by the upgrade.

## What the OpenPak menu opens

One window, seven pages, in the order every OpenPak emulator build uses:

| Page | What it does |
| --- | --- |
| Account | The identity card (picture and name, both changeable; friend code with Copy; Sign out...), then the friend code, the Switch identity, linked consoles, and the console link with *Try again* |
| Friends | The list with presence, requests both ways, add by friend code, accept, decline, remove, block |
| Invitations | What is waiting, with *Join* (hands it to the running game, or starts the title) and *Ignore* |
| Cloud saves | The allowance, and a title's savedata up and down (zipped; a download backs up the local copy first); a conflicted row offers *Resolve...* |
| Mods | The title's catalogue, installed into the folder Manage Mods already reads, each package checked against its published hash |
| News | The BCAT dataset a title would receive, and a copy of it on disk |
| Status | The verdict, "Players online: {n}", then services, this session (with NAT type and Ping from *Test connection*), and players per title and network. Public, so it still answers when sign-in is the broken part |

## Linking

There is no separate link screen. Signing in links the emulated console in the same step (the
website mints the token the console's link page would have), and if that part fails the Account
page's *Console link* row says so with **Try again**. The QR/code dialog that used to be the
fallback was removed by the UX spec (§2): it was the one place with a second way to sign in.

## Status

Signing in, linking, and the friend graph all work: the emulator completes the console's own chain
against a live OpenPak, a game asking `acc:u0` for an id_token gets a real one, and a title asking
`friend:u` for its friend list gets the account's actual friends rather than an empty buffer.

`dotnet test --filter OpenPak` covers the certificate guard, the save archive round trip and its
traversal refusal, and the hosts-file block. The session tests need a configured server and skip
without one.

What is left before every title is online: **NAT check** (`nncs1`/`nncs2`, UDP), native **News**
delivery (`bcat:*`), and whatever a first title turns out to want.

### Online at launch, and invitations a console sent (2026-09-13)

Being online was something a person had to ask for: nothing signed in until the OpenPak window
was opened and refreshed, so an emulator sitting on the game list was offline, invisible to
friends, and heard about nothing. The main window now signs in at launch, next to the network
profile fetch it already did, and starts the account cache — the same two things that Refresh did.

Invitations sent from a console land in the native inbox on `app.lp1.five.nintendo.net`, which is
a different store from the core's `/api/v1/me/invitations` the Invitations page was reading. So
one never appeared here. `OpenPakSession` now polls that inbox on the presence heartbeat (every
third beat, 30s), the page shows those rows beside the core's, and an arrival is announced the
same way a friend coming online is.

No NPNS client. A console is *pushed* its invitations over Penne and this is not; the server
queues that push, finds no connection, and drops it ten minutes later, while the invitation
itself sits in the inbox for a day — so asking is what makes it visible, and it is what the
server's own store-and-forward design expects. Push would be a FlatBuffers frontline connection
(`nx-baas/docs/penne-protocol.md`) for one toast arriving sooner.

Two things this does not do: read state is shared with every device on the account, so a console
signed in as the same person marks these read from over there (they are still shown here —
read is not gone); and the guest title is not handed the invitation, so accepting still means
launching the title from the page rather than from inside a game.

### Stardew system certificate support (2026-09-13)

At game startup, enabling OpenPak supplies the configured CA through system
certificate ID 1033, which Stardew requests. Disabling OpenPak retains the normal
system certificate. No manually installed DER file or automatic Stardew executable
patch is required. The earlier built-in patch application was removed after native
Switch testing demonstrated that providing the CA through SSL works with the
unmodified game. Restart the game after changing OpenPak settings or updating the
CA. Invalid/non-CA files retain stock trust and produce a warning in the SSL log.

### Pia sessions, and the Diablo II: Resurrected groundwork (2026-09-14 – 2026-09-15)

Online titles stay connected: the game's UDP receives no longer come back ETIMEDOUT from
deferred-poll re-checks (the IClient port from Ryujinx-Nextendo) — the NPLN/Pia stack read
that as a fatal error and abandoned the session at the co-op screen. Live-verified: auth,
ActivateUser, SubscribeFriendUsers and QueryGameSessions complete, and the farm list renders
stably.

D2R's Battle.net link was the next wall, and the groundwork is in: `IManagerForApplication.
CreateAuthorizationRequest` (150) returns a real IAuthorizationRequest instead of a stub —
invoking it completes locally (the user is already signed in), IsAuthorized is yes, and
GetAuthorizationCode/GetIdToken hand over the OpenPak session id_token. The SSL layer around
it hardened: a failed handshake returns ConnectionReset/ConnectionAbort to the guest instead
of tearing down the process, Dispose honours DoNotCloseSocket so a title can dial again on its
descriptor, and the network profile blocks `.battle.net` like the other third-party hosts. Two
opt-in envs serve the local gateway experiment loop
(`servers/diablo-ii-resurrected/next-session.md`): `RYU_BNET_SSL_TRACE=1` logs guest-side
plaintext for battle.net connections, and `RYU_BNET_DEV_TLS=1` accepts a locally-terminated
battle.net TLS with a self-signed cert — every other certificate validates exactly as before.

### Nothing to set up (2026-09-16)

Getting online used to be six steps across two windows — enable, fetch a certificate, sign in,
open the OpenPak window, link the console, sign in again. Now it is one: the first launch shows
the sign-in dialog, and a sign-in does the console link with the same credentials in the same
call. The profile fetch was already pinning the CA and writing the hosts file, so the Fetch
button and the console-server box were asking for things the emulator already knew; they are
gone, and the website address lives under *Advanced*. OpenPak is on by default (migration 75);
*Not now* leaves it on but signed out, which is a console with no account: Nintendo-hostname
titles still reach OpenPak anonymously, and signing in later needs no restart.

Cloud saves stopped being a page you had to visit: the newest cloud copy is downloaded before a
title starts and the local copy uploaded when it exits, toasting either way. An `openpak-version`
marker beside the save's `0` slot records which cloud version the local copy last matched, which
is what tells "the cloud moved on from another machine" (take it, keep the old local copy as
`0.openpak-backup`) from "both sides have a save and no shared history" (touch nothing, say so —
the Cloud saves page is where that choice is made). No per-title toggle yet.

### Dialog pass (2026-09-16, later)

Opening the OpenPak window at any page loaded nothing: the refresh ran after the modal `ShowAsync`
returned, on a disposed view model. It runs on `Opened` now. The Account page never showed the
player count because `Guarded` dropped a second call while one ran; it queues. Enter submits the
sign-in form, Play on an invitation closes the window before launching, the console panel redraws
after sign-in/out, the clipboard says when it is not there, and the menu's account entry opens
the sign-in dialog when signed out instead of a page with one button on it.

Cloud saves page: every row now shows both sides — the cloud version with its device and date, and
the local copy's last write and which version it was last in step with — and offers *Take cloud*
/ *Keep local* per row. The top bar keeps only the first-upload path for a title the cloud has
never seen.

Native invitations reach the guest as far as their shape is known: `GetReceivedFriendInvitationCountCache`
(22010) answers the unread count from the `five` inbox, and `ReadFriendInvitation` /
`ReadAllFriendInvitations` (30910/30911) mark read through the same PATCH the page uses. The list
and detail (22000/22001) stay stubbed on purpose: `FriendInvitationForViewerImpl` and
`FriendInvitationGroupImpl` have no established layout (switchbrew documents only the 8-byte ids
and the 0xC00 game-mode description), and a struct of zeros a title reads as data is worse than
an empty list. The inbox is asked with `read=false`, so a dismissal survives a restart without a
local list.


### Four gaps closed (2026-09-23)

- **In-game block/unblock** (friend 30400–30403): POST/DELETE `/1.0.0/users/<me>/blocks` with the
  module's body (reason, plus the route keys for the in-app variants); the cache takes the block at
  once and blocks, friends and request boxes re-sync after it. 2031→2213, 2061→2701, and an
  unblock the server does not know is 2121-2711.
- **Push notifications:** the session registers a Penne id (kept per server and profile), takes a
  login ticket and holds the frontline POST open, reading the length-prefixed frames. A delivered
  `friend_request_*`, `friend_deleted`, `friend_invitation_received` or `presence_updated`
  re-reads the list it concerns at once; a fresh connection catches up. Downlink only, as the
  console's own frontline body is empty — presence stays on the REST PATCH, not DAPresence, which
  would need the chunked uplink and the record sync. The 30 s poll stays as the fallback;
  `OPENPAK_NO_PUSH=1` turns push off.
- **BCAT delivery:** at launch, a title whose NACP asks for a delivery cache gets
  `/api/emulator/v1/bcat/titles/<tid>` from the website written into its BCAT save
  (`directories/<dir>/files/<file>`, `files.meta`, `directories.meta`, MD5 digests), sha256-checked,
  whole or not at all, cached for offline launches within its window, and removed when the service
  stops publishing it.
- **NAT type:** nothing on a console but qlaunch's netdiag names a NAT type, and games run the
  nncs exchange themselves through their sockets. Status runs the same test from the host's UDP
  stack against nncs1/nncs2 and shows the letter with mapping and filtering.

### UX spec pass (2026-09-23)

The OpenPak UI now follows `emulators/prds/openpak-ux-spec.md`, the contract every OpenPak
emulator shares:

- **Menu:** sentence case (*Cloud saves*, *OpenPak settings...*, *OpenPak website*, *Sign out...*),
  header *Sign in to OpenPak...*; signing in or out waits for the running game to stop; with
  OpenPak off the header opens the settings tab; *OpenPak settings...* opens the OpenPak tab.
- **Sign-out** asks first, the same dialog from the menu, the Account page and Settings.
- **Cloud-save conflicts** never block a launch: the game starts on its local save, an
  `openpak-conflict` marker beside the save pauses automatic sync for that title, and the toast or
  *Resolve...* opens *Choose a save* (*Keep this machine's* / *Take the cloud's* / *Decide later*).
- **Times** are absolute in the locale's short date and time everywhere (no "3 minutes ago").
- **Toasts** have a category line, stay 6 s (hovering holds them), at most four, in the corner the
  settings name, and a click opens what they are about; results inside the window go to its
  status line instead.
- **Strings:** the core's error sentences come from the locale file by the spec's keys
  (`OpenPakText`), exception text and HTTP codes go to the log only, and the last hard-coded
  English (the device-name template, "A friend", console names, "bytes") is in the table. The
  shared `openpak-client/strings/en.json` does not exist yet, so `Dialog_OpenPak.json` and
  `MenuBar_OpenPak.json` stay the carrier, holding the spec's English.
