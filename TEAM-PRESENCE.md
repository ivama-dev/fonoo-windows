# Shared telephone presence

Windows implements the PBX-authoritative contract from `fonoo-windows-praesenz-20261008.zip`. Its four source checksums were verified before implementation. The live Web server's `call_presence.py` was inspected read-only on 2026-10-09 and confirms schema 1 and a 15-second maximum lifetime.

The authenticated client posts only `{ "tenant_id": "selected-company" }` to `/v1/cloud/team/presence`. It validates schema, company, signed-in user, unique participant IDs, peer references, direction and timestamps. It does not infer colleagues' calls from the Windows SIP engine and uploads no address book.

## Display and lifecycle

- Team and internal favorites share one in-memory snapshot and color/text presentation: Frei (green), Telefoniert (red), Klingelt / Ruft an (orange), Telefonstatus unbekannt (neutral).
- A single busy conversation shows the counterpart only when a number is disclosed. Multiple conversations show a count. Unanswered calls and suppressed numbers show no inferred counterpart.
- The foreground window refreshes about every three seconds with at most one presence request in flight. A separate one-second timer removes expired data even while HTTP is pending. A monotonic 15-second deadline also bounds lifetime if the local wall clock moves backwards.
- Backgrounding, minimizing, hiding, logout, shutdown and account/company transitions cancel pending reads and clear displayed status/peers. Late replies are rejected by the lifetime, account, company and session generation. Older `observed_at` cannot roll back an accepted snapshot or resurrect it after expiry.
- Unavailable collector, authorization failure, malformed response, timeout and network failure mean Unknown. This status describes Fonoo calls only; neither SIP registration nor calls outside Fonoo establish personal availability.

## Identities and contact resolution

New team favorites use `team:<tenant_id>:<user_id>`, with the active company's exact extension. Old/manual favorites correlate only when exactly one member has the same internal extension. Foreign company favorites, suffix matches and name similarity never correlate. Editing an internal favorite to a different number detaches its team identity.

Counterparts resolve first by `peer_user_id` within the active team, then by an exact normalized number from already authorized local contacts/favorites. The AT dial region equates national `0…`, `+43…` and `0043…` numbers; short extensions remain exact. Multiple different matching names leave the original number visible. Resolution is recomputed from current contacts, with no persistent name cache. Microsoft disconnect or detected authorization revocation clears names immediately through its existing contact lifecycle. Windows contact capability changes discard Windows-derived contacts; imported vCards retain their independent user authorization. No automatic contact permission request was added.

Telephone presence is never written to disk. Existing favorite files and protected Outlook contact caches keep their existing storage policies; this feature does not change call-history retention.

## Compatibility and verification

Windows uses liblinphone exclusively and requests `sip_engine: "liblinphone"` during device provisioning. The server returns that device's SDK declaration without changing the account's preferences for other devices. An incompatible returned declaration is rejected. Call routing profiles are separate from the device's SIP library.

`Presence.Tests` verifies API authentication/Content-Length, contract validation, context/session isolation, out-of-order replies, expiry while a request is pending, suppressed/ambiguous peers, foreign-company favorites and AT numbers. Existing account, history, favorite and desktop suites also pass. Native synthetic preview shows the actual colors, text, shared favorite status and expiry. Real calls 500↔505, external counterpart resolution, network loss and packaged Windows permission revocation still require coordinated acceptance; the agent placed no real calls and changed no real contact permission.

The handoff contains telephone presence only. It has neither the new macOS call-profile UI nor its routing profile contract; matching that new UI remains a separate handoff item.
