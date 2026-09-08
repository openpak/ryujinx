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

1. ~~**Trusting the OpenPak CA.**~~ **Done.** `SslManagedSocketConnection` calls `AuthenticateAsClient` with
   default validation, so the game's TLS peer is checked against the *host machine's* trust
   store. OpenPak serves `*.nintendo.net` names from its own CA, which is not there and cannot
   be publicly issued. Fix: pin the OpenPak CA in the emulator — a validation callback that
   accepts a chain rooted at `openpak-ca.pem` in the Ryujinx data dir, for redirected hosts only.
   Not a blanket "accept any certificate" bypass: the whole point of the redirect is that
   whoever holds that name holds the session.
2. ~~**Device auth.**~~ **Done.** `dauth:0` (`Services/Account/Dauth/IService.cs`) is an empty stub. OpenPak's
   `nx-baas` serves the dauth/aauth/dcert surface; the emulator has to actually ask for a device
   token instead of never asking.
3. ~~**The BAAS access token.**~~ **Done.** `acc:aa` (`IBaasAccessTokenAccessor`) is an empty stub and
   `ManagerServer`'s id-token commands are `PrintStub`. A title online needs an id_token carrying
   OpenPak's `nnex` claim, signed by the key `nx-baas` publishes as JWKS, so NPLN/NEX/Photon
   servers resolve one identity per account.
4. **Getting an identity into the emulator.** The token must come *from* OpenPak, not be minted
   locally: the emulator signs in against the account core and receives a token, the same
   identity a linked console gets. A signing key sitting in the user's data directory would let
   any user mint any identity; that is a rig shortcut, not a design.
5. **NAT check** (`nncs1`/`nncs2`, UDP) and per-title patches, as titles need them.

## Configuration surface

One setting decides which server the emulator talks to, because that server receives the
account token. Default: OpenPak production. An override is kept for local stacks and is
restricted to loopback or an `openpak.org` host over TLS — anything else is refused and logged.

## How it is configured, for now

`OPENPAK_SERVER=host[:port]` and `OPENPAK_CA=/path/to/ca.pem` (default
`<data dir>/openpak/ca.pem`). Unset, or no CA file, and the emulator behaves exactly as upstream
does: offline, with the made-up id_token it has always produced. A GUI setting replaces this once
the account link exists to put in it.

The device account and the client certificate are kept per server under `<data dir>/openpak/`.

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

Signing in and linking both work. The emulator completes the console's own chain against a live OpenPak
(`dotnet test --filter OpenPakSessionTests`, with a server configured) and a game asking acc:u0
for an id_token now gets a real one.

What is left before a title is actually online:

- **NAT check** (`nncs1`/`nncs2`, UDP), and whatever a first title turns out to want.
