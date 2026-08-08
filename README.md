# PF Explorer

A Dalamud plugin that captures Party Finder listings as you browse them in-game and contributes them to [pfexplorer.com](https://pfexplorer.com), a cross-client Party Finder search site. In return, it can alert you (chat/toast/sound) when a listing matching your job/item level/data center filters appears, even while you're not looking at the in-game Party Finder window.

## Features

- **Capture & contribute** — every PF listing you see in-game (via normal browsing, or an opt-in background scanner that cycles categories for you) is uploaded to pfexplorer.com so other players can search it from the website or their own plugin.
- **Alerts** — poll pfexplorer.com's aggregated listings and notify you when one matches your configured job, item level range, data center(s), and category.
- **Results window** — a full or compact in-plugin view of matching listings, with freshness indicators and one-click "open in-game" via the game's own listing popup.
- **Privacy-conscious by default** — private listings (friends/FC only) are never uploaded. Uploads are keyed to a random per-install ID, not your character name or account.

## Usage

- `/pfexplorer` or `/pf` — toggle the results window.
- `/pfconfig` — open plugin options (opens results too, if closed).

## How it works

`Plugin.cs` subscribes to Dalamud's `IPartyFinderGui.ReceiveListing` event, which fires for every listing the game receives while you have Party Finder open (or while the optional background scraper is cycling categories). Each listing is mapped to a plain DTO (`ListingMapper.cs`) and queued for a periodic, batched, HMAC-signed upload to the server (`ListingUploader.cs`). Separately, `AlertPoller.cs` polls the server's search endpoint on a timer and raises notifications for anything matching your configured filters.

## Data sent to the server

Per listing: the listing/recruiter's public Party Finder info (duty, world, job slots, description, etc. — the same data the game shows to any player who opens PF) and a locally-generated random contributor ID used only to count distinct installs. No account credentials, character name beyond what PF already shows, or fixed hardware/account identifiers are collected.

## License

MIT — see [LICENSE](LICENSE).
