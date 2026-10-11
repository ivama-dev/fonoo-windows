# Automatically synchronized personal favorites

Favorites belong to the signed-in fonoo account, independently of the selected company. Windows, iOS and macOS use the same ordered library and API. There is no synchronization switch or manual merge button. Android can use this same version-1 contract when its client is implemented.

Create, rename or delete custom groups on any platform. Empty groups remain visible. Group names are unique ignoring case, normalized to NFC and contain 1–80 Unicode scalar values. The permanent **Allgemein** group has ID `ungrouped`; it can be reordered but cannot be renamed or deleted. Removing a custom group keeps its favorites in Allgemein. Up to 50 groups including Allgemein and 500 favorites are supported.

Drag groups/favorites or use the keyboard-accessible move actions to change their order. The array order is authoritative on every device. Group edits do not change SIP registration, presence, profiles or call routing. Favorite fields include ID, name, role and number; roles from Apple clients survive Windows edits.

## Automatic synchronization

Authenticated `GET /v1/cloud/favorites` returns `{schema_version: 1, user_id, revision, library: {groups: [{id, name, contacts: [{id, name, role, number}]}]}}`. POST accepts only `{expected_revision, library}`. The server derives the owner from the account session; never send a company/user selector. Canonical group IDs are lowercase UUIDs, apart from `ungrouped`. The compact UTF-8 library budget is 60,000 bytes and the complete request budget is 65,536 bytes.

Edits save locally first, including while offline. The active app refreshes every five seconds and on activation. A revision conflict triggers read/three-way-merge/write, at most three times per refresh. Independent field changes and additions survive; unchanged local fields follow the server. Pending local edits win a same-field race on the next write. Deletions beat stale copies. Persisted deletion markers also prevent resurrection when a successful upload reply was lost. Further races remain queued for the next refresh. Do not describe pending offline changes as synchronized.

Logging out cancels the old worker. Both account generation and cache-owner guards reject late old-account replies. The server encrypts the personal library and does not expose it to other users or company administrators. Only selected favorites are transmitted, not the full address book.

## Cache and migration

Personal cache: `%LOCALAPPDATA%\Fonoo\Windows\personal-favorites-v1\<SHA256-of-account-id>.json`. It retains the library, confirmed base/revision, pending upload, deletion markers and imported legacy sources. Writes validate the complete state and atomically replace the file. Corrupt files are preserved and left read-only. Signing out preserves the personal cache for a later login to the same account.

Old Windows `favorites-<SHA256-of-account-and-company>.json` documents and array-only files are imported once per authenticated legacy company source. Old files are not rewritten or deleted. Arbitrary old group IDs become deterministic UUIDs; existing canonical IDs, order and roles survive. A repeated migration cannot reintroduce a favorite deleted from the new personal library. The design preview remains isolated in its synthetic QA cache.

## Validation

`FavoriteSync.Tests` uses the same expected merge fixtures as the Apple checks. It covers canonical migration, role preservation, UTF-8 size limits, account isolation, two devices, offline restart, bounded conflicts and cancelled old-session replies. `AccountFlow.Tests` also verifies GET/POST authentication, exact wire names, owner restrictions and HTTP 409 handling. `PhoneData.Tests` retains the group/editor/local-storage checks. `Test.ps1` runs these along with existing suites. The GitHub Windows workflow additionally compiles the native app; hardware and real-device acceptance remain separate.
