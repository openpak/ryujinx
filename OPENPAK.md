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
   through the console's own QR/code screen.
5. ~~**The friend graph.**~~ **Done.** `friend:u` serves the account's real list, ids, counts and
   presence from a cache kept warm on a timer. What is still honestly stubbed, and why:
   `GetFriendRequestList` (20201), because `FriendRequestImpl`'s layout is not established and
   zeros would be read as data; the favourites-only filter, because the core has no per-viewer
   favourite flag yet; and the newly-arrived request count, because nothing tracks what the
   console has already been shown.
6. **NAT check** (`nncs1`/`nncs2`, UDP) and per-title patches, as titles need them.
7. **Native News delivery.** `bcat:*` is not implemented, so the News page shows and saves the
   dataset a title would receive rather than delivering it to the guest.

## Configuration surface

Settings -> OpenPak, and nothing has to be typed that is already known:

- **Connect this emulator to OpenPak** — off is upstream behaviour, offline, with the made-up
  id_token Ryujinx has always produced.
- **Website** — where the account lives and sign-in happens. Ordinary public TLS.
- **Console server** — `host[:port]` of the console-facing edge, which everything a *game* asks
  for reaches under Nintendo's own hostnames, routed by SNI. Empty means the website's host, which
  is right for any deployment serving both from one machine.
- **Certificate** — Fetch pulls the OpenPak CA from the website and pins it. Until there is one,
  no title can complete a handshake, and the page says so rather than leaving it to be discovered
  inside a game.
- **Point the emulated console's DNS at OpenPak** — writes a fenced block in the Atmosphere hosts
  file on the virtual SD card. Entries outside the block are left alone, and turning it off
  removes the block and nothing else.

`OPENPAK_SERVER`, `OPENPAK_CA` and `OPENPAK_WEBSITE` still override the settings, so the shared
launchers keep working with no GUI in the loop.

## What the OpenPak menu opens

One window, seven pages, in the order every OpenPak emulator build uses:

| Page | What it does |
| --- | --- |
| Account | Who is signed in, the friend code, the Switch identity, and the console link |
| Friends | The list with presence, requests both ways, add by friend code, accept, decline, remove, block |
| Invitations | What is waiting, and launching the title it is for |
| Cloud saves | The allowance, and a title's savedata up and down (zipped; a download backs up the local copy first) |
| Mods | The title's catalogue, installed into the folder Manage Mods already reads, each package checked against its published hash |
| News | The BCAT dataset a title would receive, and a copy of it on disk |
| Status | Who is online, per title and per network. Public, so it still answers when sign-in is the broken part |

The account token lives in the OS password store — Keychain, Credential Manager, or libsecret —
and there is deliberately no file fallback: without a store, sign-in refuses and says why.

## Linking

The console's own link screen, shown in the emulator instead of a browser. Both halves of it,
because a console only lacks one of them for want of a keyboard:

- a **QR and a six-digit code**, for whoever would rather sign in on their phone
- an **e-mail and password form**, for whoever is already sitting at a keyboard

The server renders both — `POST /connect/1.0.0/qr/new` returns the code, the page a phone should
open, the QR image and the time it has left — so every OpenPak client draws the same screen and
none of them carries a QR encoder or invents its own wording. The phone path still ends with a
person approving in the emulator, as it ends with a person approving on the console: whoever
holds the code cannot take that step for you.

A browser was tried and removed. It cannot reach these hostnames from the host in the first
place (the DNS redirect is the guest's), and what it rendered was the television page — telling
someone with a keyboard in front of them to go and find their phone.

For the QR half to be scannable, the server's `NX_LINK_BASE_URL` has to name an address a phone
can reach. The form half needs nothing.

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
