# Steam bridge attribution

Bridge/*.cs derives from [Muurrcc/AHK-asyncronus-hollow-knight](https://github.com/Muurrcc/AHK-asyncronus-hollow-knight),
commit `622735cb788dc718c8de0ec5c0ab4522366bcf2f` (2026 snapshot).
Copyright remains with its authors and the original HKMP contributors.
Licensed under GNU LGPL 2.1; the complete upstream license is in Bridge/LICENSE.
HollowConnect changes: SDK 1.48 adapter, bounded MTU validation, strict protocol
parsing, per-peer loopback addresses, pinned local client endpoint, queue/dispose
serialization, Steam lobby/invitation lifecycle and native save-menu integration.

The game supplies Steamworks.NET and steam_api64; this module does not bundle or
replace either. Valve Steamworks services remain subject to their own terms.
The upstream README reports local tests and does not establish real two-account
relay verification. This port requires its own integration and account tests.
