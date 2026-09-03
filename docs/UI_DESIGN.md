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
