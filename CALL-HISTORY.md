# Shared call history

The authenticated client reads `/v1/cloud/call-history/read` for the active company. It checks schema, company/user identity, canonical call IDs and revision ordering. The PBX is authoritative; the app does not upload local missed-call guesses from forked SIP legs.

The client refreshes every 15 seconds, when opening the history and two seconds after a call. Unchanged revisions preserve list rows/scroll position. The All/Missed filters and number search are local. Shared whole-list or single-entry deletion requires confirmation and a validated server response; it affects this user's history for this company, not other participants' lists.

The current shared service retains calls for 90 days and returns at most the latest 500. The Windows offline snapshot is encrypted with DPAPI for the current Windows user/computer and scoped by Fonoo account/company. Loading, saving and active polling prune calls older than 90 days. A closed app/inactive account has no independent cache-erasure background service.

Old pre-synchronization local `history-*.json` files are limited to 500 entries without an age cutoff. Server deletion markers, backups and PBX logs have independent retention policies; a 90-day shared-list policy does not erase those other copies. These distinctions must remain reflected in release privacy information.

`tests/HistorySync.Tests` covers scope/revision validation, ID/outcome checks, offline expiry, protected cache migration, stale reads and authenticated shared deletion. Its crypto fixture is portable; the native Debug preview separately verifies actual Windows DPAPI with synthetic data. Real cross-device call propagation/deletion requires coordinated acceptance.
