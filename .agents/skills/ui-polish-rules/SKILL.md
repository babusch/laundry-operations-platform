---
name: ui-polish-rules
description: Personal rule set for polished, stable web interfaces - no page jumps, smooth transitions, symmetric spacing, consistent alert colours, honest hover states. Use this whenever building, styling, restyling or reviewing any web UI (HTML/CSS/JS, Django or other templates, React, HTMX, dashboards, forms, lists, cards, tabs, filters, dropdowns, charts, toasts, popups), and whenever the user reports something that "jumps", "doesn't line up", "looks off", "isn't smooth" or "doesn't match" - even if they don't mention design rules. Also use it as a checklist before calling any UI change done.
---

# UI polish rules

These rules come from a real project where each one was a bug the user found and asked to fix. They are general: they apply to any web app, any framework. Treat them as the user's standing preferences, so the user doesn't have to point the same problems out again.

Read the rules before starting UI work, apply them while building, and go through the checklist at the end before saying a UI change is done. `references/techniques.md` has ready-made code patterns for the trickier ones (gliding instead of jumping, in-place swaps, count-up without a flash, custom dropdown arrows, alert frames); open it when you need to implement one of them.

## 1. Motion and stability: the page never jumps

The user's strongest preference. A jump is anything the eye did not ask for: the page snapping to the top, content shifting sideways, a panel changing height under the cursor, a value flashing before it animates.

1. **Keep the user's place.** Saving a form that returns to the same page, switching a day/tab/car/filter, or reloading a view returns to the same scroll position, not the top.
2. **Shorter content glides, it doesn't snap.** When new content is shorter than the current scroll position (an empty tab, an empty day, fewer results), the browser clamps the scroll instantly. Instead, hold the old height with a temporary spacer, glide to the new end, then remove the spacer.
3. **Prefer in-place updates to full reloads for view switches.** A full reload paints at the top before any script can restore the position, so even a perfect restore shows a flash. Swap the content in place (fetch + replace, HTMX boost, client routing) and keep the URL/history updated.
4. **Containers keep their size when their contents toggle.** If a switch adds columns or numbers, design both states to the same height: reserve the space, use the same layout in both states (e.g. always two rows on phones), or use narrower boxes so a title doesn't wrap in one state only.
5. **Changes are shown with motion.** Things that appear, disappear or rearrange fade, grow or glide, briefly (about 0.2-0.4 s, ease-out such as `cubic-bezier(.2, .8, .2, 1)`). Toggling a display preference can be a view transition so elements glide to their new places.
6. **Motion never blocks input.** A text field stays live during an animation (whole-page snapshot transitions freeze it). Updates that arrive mid-animation wait until it lands instead of cutting it short; a new user action may finish the old animation at once and start the next.
7. **Big view changes are choreographed, and nothing stretches.** Old texts fade out, the shared elements move, new texts fade in, then secondary content (a list) rises in. Scale things evenly (circles, icons) or resize real boxes; never scale a rectangle non-uniformly. Card chrome (strips, headers) stays pinned to the top while a card resizes.
8. **Load-in animation without a flash of final values.** Bars grow from zero with pure CSS (starts on first paint). Counting numbers are hidden or zero before the first paint and revealed when counting starts, with a CSS fallback that shows them if the script never runs. Numbers keep their final width while counting (tabular figures, min-width) so nothing beside them shifts.
9. **High-frequency changes stay instant.** Results refreshing while typing, repeated saves, counters updated on every edit: no animation, it reads as lag. Animation marks meaningful transitions (entering/leaving a mode), not every refresh.
10. **Respect reduced motion.** With `prefers-reduced-motion: reduce`, everything changes instantly. Keep a timer fallback for anything that waits on `scrollend` or animation frames (hidden tabs, headless browsers).

## 2. Alignment and spacing

11. **Symmetric inner spacing.** An icon inside a control (dropdown arrow, search icon, close key) sits as far from its edge as the text sits from the opposite edge. Don't accept a browser default that hugs the edge - draw your own (see techniques).
12. **Same component, same layout.** Cards in a grid line up even when some have less content. Reserve the space, or move optional text to a spot that doesn't push the rest (e.g. "2 skipped" at the right end of the line above a progress bar instead of on its own line).
13. **Labels and values on shared rows.** In number panels, every label (or tag) sits above its number on a row of one fixed height, so labels line up and numbers line up. Don't mix "label above" and "label below" in one panel, and don't make one number bigger than its neighbours if it breaks the row.
14. **Filled and empty states are the same size.** A cell with a tag and an empty placeholder cell have the same width and height, at every breakpoint.
15. **Check the narrowest screen.** Long labels get a shorter phone variant ("Stopp/vecka" becomes "Totalt"), or the layout wraps into even rows instead of overflowing its box.

## 3. Colour and meaning

16. **One colour per meaning, never borrowed.** Danger/delete/deviation has its own strong red, not a category colour that happens to be red. Warning amber marks the unusual, including rare events (a skipped step should usually not happen, so it gets the warning colour).
17. **One look for every alert.** Errors red; warnings, "fill this in first" and plain information amber (unless the project decides otherwise - ask once). Each alert: a 2px frame in the colour, a light wash of the same colour inside, an icon. A field error frames the field (or its group of inputs) a few pixels out with the gap washed too, and the message sits inside the frame. In a group of inputs only the group is framed - don't turn every input red.
18. **Every category colour works in both themes.** A white or silver category gets an outline so it stays visible on light surfaces, and a "current" state that can't be confused with "done" (e.g. dark fill with a white frame vs solid white).
19. **Theme-specific assets are verified.** The dark logo on the light theme and the light logo on the dark theme - check it, they are easy to swap.

## 4. Interaction

20. **Only clickable things look clickable.** Hover effects only on real buttons and links. Hover is a border or ring, not a glow; no underline on non-text items like icon circles. Inactive options in a switch get a hover background.
21. **Hover and focus never change size.** Text size, weight, letter-spacing, padding and border width stay exactly the same on hover and focus; only colours, border colours, rings/shadows and backgrounds change. A size change on hover makes text jump and nudges the layout. A common cause is a "reset" rule that lists `:hover`/`:focus` selectors (for higher specificity) and also sets a size - it then overrides smaller sizes set elsewhere, but only while hovered. Keep sizes in their own plain rule.
22. **A selected item keeps its look on hover.** The chosen tab, card, day or option shows its selected state unchanged while hovered; the hover effect is only for items that can still be picked. General hover rules (e.g. "every button gets an accent border on hover") easily out-rank the selected style and paint over it - scope hover to `:not([aria-selected="true"])` / `:not(.is-on)` or restate the selected border in a selected-and-hovered rule.
23. **Hover keeps the colour family.** A coloured button brightens or darkens on hover; it never switches to the app's generic accent colour. White text stays white.
24. **Choosers always show every option.** Where the user picks something (days, cars, categories), all options are always available; dim the empty ones (with a tooltip) rather than hiding them. Display toggles ("show weekend", "show customer numbers") only control dense overview tables.
25. **Display preferences are remembered** per browser and applied before the first paint (a tiny inline script in `<head>` setting a data attribute on `<html>`), so nothing flickers on load.
26. **After a blocking warning, point at the problem.** When a popup says "fill in X first", closing it frames field X in the warning colour, shows a short note in place, scrolls it into view and puts the cursor there. The frame goes away as soon as the user types.

## 5. Before calling it done

27. **Verify visually, in every state.** Screenshot (or look at) light and dark theme, desktop and phone width, and each state the change touches: switch on and off, empty and full, mid-animation, after a reload that keeps scroll. Logic checks and tests are not enough for UI. If an animation can't be observed in the tool you have (headless browsers often don't advance animations or smooth scrolling), say so plainly and ask the user to try it rather than claiming it works.

## Checklist

Run through this before reporting a UI change as finished:

- [ ] Does anything jump - scroll to top, clamp at a shorter end, shift sideways, change height when toggled?
- [ ] Do appearing/disappearing things move briefly, and do frequent refreshes stay instant?
- [ ] Is reduced motion respected, with timer fallbacks?
- [ ] Do icons in controls have the same edge spacing as the text?
- [ ] Do cards, cells and number panels line up with missing or empty content?
- [ ] Does it fit at phone width without overflow?
- [ ] Are colours used for one meaning each, and do alerts use the frame + wash rule?
- [ ] Does every colour hold up in both themes?
- [ ] Do only clickable things react to hover, keeping their colour family and their size (no text growing on hover or focus), and do selected items keep their selected look when hovered?
- [ ] Have I actually looked at it in both themes and both widths?
