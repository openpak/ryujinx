# Next session — ryujinx

Updated 2026-09-24.

Upstream Ryujinx (MIT) plus a C# OpenPak client: console-chain sign-in and linking, the
friend graph, invitations, cloud saves, mods, news, status (OPENPAK.md). Latest tag `v0.2.2`
(2026-09-24); tags moved from `openpak-v0.1.x` (last `openpak-v0.1.5`) to `v*.*.*` on 09-23,
and CI builds only on those tags. The Switch reference for Citron and Eden (emulators/prds
§2a).

Current status 2026-09-24: `v0.2.2`. Landed since 09-16: zero-setup sign-in, one account per
profile, friends against the 22.5.0 firmware contract (requests, blocks, invitation list and
detail, presence), BCAT delivery (a909bfd3b), the NAT type test against nncs (2c418dc17),
Penne push hold, crash reports, UI to the OpenPak UX spec, the signed redirect ceiling
(403aab3a5), the CTR Demonware key IPS, compatibility list from the site catalogue, Bsd
select/poll and P2P tap fixes, Npns for ACNH, LibHac vendored.

## Where things stand

- Sign-in at launch and native-inbox invitation polling (2026-09-13, OPENPAK.md).
- Pia/NPLN sessions stay up live: deferred-poll IClient port (3bab55478) — auth,
  ActivateUser, SubscribeFriendUsers, QueryGameSessions complete; the farm list renders.
- D2R groundwork (5ce4ea9f0, 0df1f35a7, 8c86aeaa6): a real IAuthorizationRequest (cmd 150)
  hands the OpenPak session id_token to D2R's Battle.net link; SSL hardening around it;
  `RYU_BNET_SSL_TRACE`, `RYU_BNET_DEV_TLS`. This fork is the client side of the
  servers/diablo-ii-resurrected local loop.

## Next steps

- Zero-setup pass landed 2026-09-16 (first-run sign-in, sign-in = link, CA/DNS from the profile
  only, cloud saves on launch/exit). Untested against a live account from a fresh config —
  do that first: delete `openpak/` and the config, launch, sign in, start a title, check the
  hosts block, the 1033 cert, and the save toast on exit.
- Next release is a `v*.*.*` tag (local Linux build first — PRD rule).
- NAT check and BCAT delivery landed 09-23; verify both per title before calling them done.
- Every change here is ported to Citron and Eden through `openpak-client` (PRD §2a).
- D2R: follow the game-server stage in servers/diablo-ii-resurrected/next-session.md — fork
  changes only as the gateway experiments demand.

## Pointers

- `../../servers/diablo-ii-resurrected/next-session.md` — the D2R frontier and the local
  loop that drives `RYU_BNET_*`.
- [`../prds/`](../prds/README.md) — emulator-wide PRDs (`emulators/prds/` in the workspace):
  emulator-integration-prd.md (E2), emulator-network-profile-prd.md.
- `OPENPAK.md` — this fork's own readme.

## Scratch (research and throwaway work)

Decompiles, Ghidra projects, dumps, exefs/romfs extracts, packet captures,
strace and emulator logs, probe harnesses: put them in
`~/REPOS/Openpak/scratch/<topic>`. That folder is a local mount of the media pool,
outside every repository, so nothing in it is committed. Never use `/tmp` (a
shared 15 GB RAM disk) or elsewhere on `/home` for this. Keys and signing
material never go there. Rule: `docs/playbooks/conventions.md` in the workspace.
