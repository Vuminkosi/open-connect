# OpenConnect.LiveUI

OpenConnect.LiveUI lets a .NET host expose its compiled application routes and UI targets to an AI agent through host-authorized actions. The Blazor provider executes those actions in the connected browser and returns a receipt describing the observed result. Browser execution does not replace host authorization.

## Highlighting a specific list item

`liveui_highlight` accepts a compiled `targetId` and an optional `matchText`. Without `matchText`, it highlights the authorized target. With `matchText`, the target must be a compiled `List` or `Table`; the browser searches visible descendant rows inside that target and highlights the unique row whose normalized rendered text contains the supplied fragment.

The match text must contain 1–256 non-whitespace characters. Matching is scoped to the already authorized compiled target and does not accept selectors or executable code. If more than one visible row matches, the action returns `Ambiguous` with `text_match_ambiguous` and applies no highlight. If no row matches, it returns `MissingTarget` with `text_match_missing`. Other compiled target kinds return `Failed` with `text_match_unsupported`.

Callers should pass the exact visible title when identifying a particular item. A completed receipt is required before describing the item as highlighted. The sample Todo agent obtains recommendation data through the read-only `todo.list` tool, selects an eligible task using its record data, and then uses the visible title as `matchText`; it does not change Todo records as part of recommending or highlighting.
