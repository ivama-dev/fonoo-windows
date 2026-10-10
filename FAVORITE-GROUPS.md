# Personal favorite groups

Each signed-in user can create up to 50 groups, rename them and put them in their preferred vertical order. Names are unique within the user's company, ignore case when checking duplicates and contain 1–60 characters. Empty groups remain visible.

Drag a group by its header/grip above or below another header to reorder groups, or use its up/down buttons. Drag a favorite by its grip above/below another favorite to reorder it, including between groups. Dropping a favorite on a header places it at the end of that group. A violet insertion marker highlights the destination. Native WinUI Thumb controls keep reordering within this window; no contact data is put on the clipboard or an external drag payload. Releasing a favorite's grip cannot initiate a call. The favorite's **···** menu also offers **Move to group**, **Move up** and **Move down** for keyboard users. The add/edit dialog includes a group picker. Removing a group asks for confirmation and keeps all of its favorites under **Without a group**.

Outlook, Windows/vCard and team favorites share this organization. Newly starred contacts initially appear without a group. Renaming a favorite or upgrading an existing favorite to a scoped team identity retains its group. Team presence updates existing row objects without rebuilding/reordering sections; group edits do not change SIP registration, profiles or call routing.

## Storage and migration

Favorites and groups are saved together as one versioned JSON document in `%LOCALAPPDATA%\Fonoo\Windows\favorites-<SHA256>.json`. The hash combines Fonoo user ID and company ID. Separate users/companies have separate libraries. `Version: 1` contains `Favorites` (each with optional `GroupId`) and `Groups` (ordered stable IDs and names). Array order defines group order and order within each group's favorites.

Existing array-only files load without modification and retain their favorite IDs, names, numbers and order. On the next successful edit the app writes the versioned document. Writes validate the entire library, write a temporary file in the same directory and atomically replace the destination. An invalid file, unknown version or missing group reference is preserved and left read-only; the app reports the load failure. A failed write keeps the previous in-memory library. Group dialogs and pointer drags are tied to the currently loaded user/company library.

**Favorites and groups currently remain local to this Windows installation.** They have no expiry; signing out preserves the library for the same account. They are not part of the account's synchronized call-history API or device-profile API. Synchronizing favorite groups across Windows and Apple clients requires a shared backend contract and matching client migration.

`PhoneData.Tests` covers array migration, restart persistence, upward/downward order changes, moving between groups, retaining team identities, keeping favorites when deleting groups, invalid edits, user/company isolation and preservation of damaged files. The explicit Debug design preview uses synthetic favorites in a separate `qa/favorite-groups` directory.
