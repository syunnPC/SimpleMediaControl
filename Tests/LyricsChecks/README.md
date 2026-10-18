# Lyrics checks

Run deterministic checks for LRC parsing, time selection, metadata matching,
cache behavior, partial search/match selection, request cancellation, response
limits, and HTTP retry/rate-limit handling:

```powershell
dotnet run --project Tests/LyricsChecks/LyricsChecks.csproj -c Release
```

Add `-- --live` to query LRCLIB using the example track from its API documentation
and the reported track #38517550 with different punctuation and album metadata,
plus Galaxy Anthem, which has both plain and synced editions in LRCLIB.
The optional check reports the result type and cue count without storing or
printing lyrics. The deterministic checks use synthetic lyrics and an in-memory
HTTP handler. No additional packages are required.

The client searches LRCLIB by title and artist, preferring a matching synced
edition. Album names are never sent as search constraints or used to rank matches.
If no synced lyrics are found (including a plain-only response), it removes trailing parenthetical/suffix metadata
and progressively shortens the title at word or Unicode text-element boundaries.
At most four distinct searches run. Candidates must match the title, artist and
known duration (within five seconds); near-equal competing recordings are rejected.
Results are cached using normalized playback metadata. Unicode NFKC handles
full/half-width characters, including punctuation and kana. Search terms and
candidate comparison tolerate punctuation differences. An album embedded in the
source's artist field after an em dash is removed. A plain-only result is retained
if no suitable synced edition is found. Add `-- --live-galaxy` for
request/status and candidate metadata diagnostics; these never print lyric text.

Each GET endpoint retries 5xx responses twice, with exponential delays and jitter.
`Retry-After` takes precedence when longer. Requests remain serialized and at
least 500 ms apart; cancellation stops pending searches and retries. Other 4xx
responses are not retried. A 429 response sets a shared cooldown and surfaces the
rate-limit message. Failed requests are never cached as missing lyrics.

Display-follow checks cover small backwards timeline corrections at cue
boundaries, explicit/external seeks, and scroll targets that settle without
repeated recentering. Synced rows reserve their largest text layout; active text
scales to 20 px, other lines to 16 px, without changing wrapping or row heights.
Scale changes use a 240 ms WinUI transition, including retargeting if cues change
quickly. Windows' animation preference is respected. Highlight timestamps lead
the source by one second and clamp at zero; source timestamps remain intact.
Boundary, sub-second, offset and backwards-seek checks cover this display lead.

Timed lyric rows are buttons when the connected source supports seeking. Mouse,
touch and keyboard activation seek to the same one-second-early timestamp (clamped
at zero). Capability/timeline/connection changes update availability, while plain
or disabled rows remain readable. Failed seeks show a brief status without changing
another source or a newly loaded track. Clicking a row defers auto-follow while
the native timeline catches up.

Empty timed cues display three dots. An intro placeholder is added if needed;
no gaps are guessed from the distance between nonempty lyric lines. The gap lasts
from its advanced start to the next advanced cue, or track end for a final empty
cue. The three dots change color in order at thirds of that interval and follow
pause/seek operations. Tests cover thirds, boundaries, intro/outro, backwards
seeks and absent timelines.
