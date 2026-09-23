# UI design

## Required design guidance

Before creating, changing, prototyping, or reviewing a user-facing interface, read and apply:

1. `.agents/skills/apple-design/SKILL.md`
2. This document

Claude-compatible tooling may discover the identical mirror at `.claude/skills/apple-design/SKILL.md`. The `.agents` copy is the cross-agent canonical source; keep both copies identical while both integration folders are retained.

The skill provides interaction principles, not a requirement to make the product look like macOS or iOS. Apply response, direct manipulation, interruptibility, spatial consistency, restraint, typography, and accessibility in a way that fits an industrial laundry environment.

## Product design priorities

In descending order:

1. Prevent incorrect physical actions.
2. Make the current plant, station, workflow, item, and batch context unmistakable.
3. Give immediate, unambiguous feedback for every scan and action.
4. Keep common operator paths fast with minimal typing.
5. Work with gloves, touchscreens, keyboard-wedge scanners, and noisy surroundings.
6. Remain usable with reduced motion, increased contrast, zoomed text, and color-vision differences.
7. Add polish and delight only when it reinforces confidence and comprehension.

## Shop-floor interface rules

- Always show whether work is `Local`, `Queued`, `Synchronizing`, `Synchronized`, `Rejected`, or `Needs attention`.
- Never communicate success, failure, contamination, or synchronization state through color alone. Pair color with text, shape, iconography, and appropriate sound or haptics.
- A scan receives immediate feedback, but success is shown only after the event reaches the durability level described in `docs/OFFLINE_AND_SYNC.md`.
- Keep the active scan target and expected identifier obvious. Prevent background fields from accidentally receiving scanner input.
- Use large, separated touch targets and layouts that remain usable with gloves and imperfect aim.
- Keep destructive or physically consequential actions specific and reversible where possible. Confirm only genuinely costly or irreversible actions.
- Preserve entered or scanned work across navigation, reconnects, application restarts, and recoverable errors.
- Use plain, direct labels based on plant vocabulary. Avoid vague navigation names and unexplained technical synchronization terms.
- Do not use blur, translucency, low contrast, parallax, or decorative motion where dust, glare, older displays, or operator urgency could reduce readability.

## Language and localization

- English and Swedish are the initial supported station languages. English is the fallback when a translation is unavailable.
- Keep operator-facing text in localization resources rather than embedding it directly in components. Internal event names, status codes, identifiers, and API contracts remain language-neutral.
- Choose a saved browser/station preference first, then a supported browser language. A plant default and operator preference may supersede this once those configuration models exist.
- Bundle essential station translations with the PWA so changing language and understanding offline states never depend on cloud availability.
- Use locale-aware formatting for dates, times, numbers, quantities, weights, and currencies. Do not concatenate translated fragments where word order or plural forms may differ.
- Update the document language for assistive technology and test layouts with every supported language, including narrow screens and enlarged text.
- Prefer a familiar native language selector with a large touch target. A language change is immediate and does not interrupt or reinterpret in-progress operational work.

### Production scan workspace direction

- Design the primary capture flow for high-throughput RFID use, while keeping barcode and manual paths clearly available where the workflow permits them.
- Keep the active order and laundry customer unmistakable throughout capture.
- Show session and active customer/order totals plus an article-type breakdown without obscuring immediate scan feedback.
- Treat manual additions, deviations, and problem reporting as explicit labelled actions. Require the appropriate reason/context and show that the action is attributed to the signed-in operator; never style a manual addition as if a scanner observed it.
- Progressive disclosure may move detailed composition and exception history behind secondary views, but the operator must see the context and totals needed to prevent work against the wrong order.

### Synchronization audit presentation

- Keep station-facing audit information read-only unless an authenticated, authorized administrative workflow is explicitly designed.
- Show plain-language delivery states and times; do not expose tag values, barcode values, internal event IDs, retry errors, leases, or other support metadata unless the user's task requires them.
- Separate locally durable acceptance from cloud confirmation. A temporarily unavailable audit snapshot must not imply that locally stored observations were lost.
- Use an explicit manual refresh for this Development view. Do not introduce attention-grabbing polling or motion for a passive diagnostic summary.

## Shop-floor dark theme

The station PWA uses a dark, laundry-specific visual system. Deep blue-green surfaces evoke water and industrial wash equipment without becoming decorative; softly muted light text provides high legibility without excessive glare, and restrained operational colors remain reserved for meaning. This is the default station theme, not an automatic reflection of the operating-system theme.

| Token | Name | Value | Use |
|---|---|---|---|
| Canvas | Midnight wash | `#071A21` | Application background and dark text on bright controls |
| Panel | Deep basin | `#0C242C` | Primary application surfaces |
| Raised surface | Raised surface | `#102F38` | Status cards and elevated content |
| Border | Steel border | `#244A53` | Structural separation |
| Primary text | Clean linen | `#DCE9E7` | Headings and essential text |
| Secondary text | Mist text | `#9FB8B6` | Supporting text that remains clearly readable |
| Accent | Aqua rinse | `#42B8AD` | Primary actions, focus, and development context |
| Success | Fresh success | `#48B97B` | Confirmed success states |
| Warning | Caution amber | `#D4A84F` | Warnings and setup-required states |
| Error | Stop coral | `#DD6C65` | Failures and unavailable states |

- Pair every operational color with explicit text and a symbol or shape. Color alone never communicates status.
- Keep large surfaces within the blue-green neutral range. Accent and status colors are signals, not decoration.
- Keep normal-mode text and signals slightly muted to reduce glare; reserve pure white and maximum contrast for the increased-contrast preference.
- Prefer solid surfaces over translucency on shop-floor screens to preserve contrast and performance.
- Interaction variants may be derived from these tokens, but must preserve clear contrast and recognizable status meaning.
- Increased-contrast mode remains dark and strengthens borders and text rather than switching the station to a bright theme.

## Motion and feedback

- Respond visually on pointer-down and continuously during direct manipulation.
- Gesture-driven motion must be interruptible and start from the current presented state.
- Use restrained, critically damped motion by default. Bounce is reserved for interactions whose physical gesture carries momentum.
- Entry and exit should follow the same spatial path and remain anchored to the initiating control.
- Coordinate visual, audio, and haptic feedback at the causal event. Reserve multimodal feedback for meaningful success, warning, and error states.
- Honor `prefers-reduced-motion`, `prefers-reduced-transparency`, and `prefers-contrast`. Reduced motion retains useful feedback through static changes or short cross-fades.

## Management and customer interfaces

Administrative interfaces may use denser layouts than operator stations, but hierarchy, typography, keyboard operation, predictable placement, and visible system status remain mandatory. Translucent materials or depth effects are acceptable only when they preserve contrast, performance, and information density.

## UI completion checklist

- The common task is obvious without explanation.
- The screen answers: Where am I? What is selected? What happens next? How do I leave or recover?
- Loading, empty, offline, queued, synchronized, rejected, unauthorized, and unexpected-error states are designed.
- Pointer, touch, keyboard, scanner, and assistive-technology paths are considered where applicable.
- Text resizing and narrow/wide layouts do not hide essential actions.
- Motion is interruptible where interactive and has a reduced-motion equivalent.
- Feedback timing matches the actual durability and business result.
- The implementation has been reviewed against the `apple-design` skill rather than merely referencing it.
- Station PWA changes use the documented dark-theme tokens and preserve their semantic roles.
