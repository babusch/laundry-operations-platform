# Techniques for the UI polish rules

Framework-neutral patterns (plain CSS and JavaScript). Adapt names and tokens to the project.

## Contents
1. Glide to a shorter end instead of jumping (spacer)
2. Keep scroll across a reload, and glide if the new page is shorter
3. In-place view switches (HTMX boost / fetch-and-replace)
4. Hold an update that arrives mid-animation
5. Display preferences: remember, apply before paint, animate the toggle
6. Same size in both states of a toggle
7. Load-in: bars grow, numbers count up without a flash
8. Moving a shared element between two layouts (FLIP, no stretching)
9. Dropdown arrow with symmetric spacing
10. Alert frame + wash
11. Pointing at a required field after a warning
12. Visible "white" category colours

---

## 1. Glide to a shorter end instead of jumping

When content gets shorter than the current scroll position, the browser clamps `scrollY` instantly. Hold the old height with a spacer, make the change, then glide to the new end and remove the spacer.

```js
// Size the spacer so the page can scroll exactly to y. Measure after inserting: a page shorter than
// the window already has empty room the spacer partly fills.
function fitSpacer(spacer, y, natural) {
  spacer.style.height = Math.max(0, y + innerHeight - natural) + "px";
  const missing = y - (document.documentElement.scrollHeight - innerHeight);
  if (missing > 0) spacer.style.height = spacer.offsetHeight + missing + "px";
}

function glideToEnd(spacer, y, natural) {
  const end = Math.max(0, natural - innerHeight);
  fitSpacer(spacer, y, natural);
  let done = false;
  const t0 = performance.now();
  function release() {
    if (done) return;
    done = true;
    removeEventListener("scrollend", release);
    spacer.remove();
  }
  addEventListener("scrollend", release);
  scrollTo({ top: end, behavior: "smooth" });
  // Fallback with timers, not animation frames: frames stop in hidden tabs and headless browsers.
  (function wait() {
    if (done) return;
    if (Math.abs(scrollY - end) <= 1 || performance.now() - t0 > 2000) return release();
    setTimeout(wait, 50);
  })();
}

// Wrap any change that may shorten the page (switching tabs, filtering).
function smoothShrink(change) {
  const root = document.documentElement, y = scrollY;
  const spacer = Object.assign(document.createElement("div"), { ariaHidden: "true" });
  spacer.style.height = root.scrollHeight + "px";  // plenty while the change is made
  document.body.appendChild(spacer);
  change();
  const natural = root.scrollHeight - spacer.offsetHeight;
  if (y <= Math.max(0, natural - innerHeight) || matchMedia("(prefers-reduced-motion: reduce)").matches) {
    spacer.remove();
    return;
  }
  glideToEnd(spacer, y, natural);
}
```

## 2. Keep scroll across a reload

Save `{path, y}` in `sessionStorage` on submit of a form that posts back to the same page, and on clicks of links that only change the view. On load, if the saved path matches, set `history.scrollRestoration = "manual"` and restore. If the new page is shorter than the saved position, insert a spacer, jump to the saved `y`, and glide to the end (section 1) after a short delay so the restored position is drawn first.

A reload always paints once before scripts run. If the flash at the top is visible, switch to an in-place update (section 3).

## 3. In-place view switches

With HTMX, boost the navigation links and swap only the main content, keeping the scroll:

```html
<nav hx-boost="true" hx-target="main" hx-select="main" hx-swap="outerHTML show:none" data-smooth-swap> … </nav>
```

- Hold the height in `htmx:beforeSwap` (spacer as in section 1) for requests from `[data-smooth-swap]`. In `htmx:afterSettle`, restore `scrollY` and either remove the spacer or glide.
- After an `outerHTML` swap the event target is the old element. Run page-wide re-initialisation (switch states, count-ups) on `document`, not on `e.target`.
- Skip sessionStorage scroll-restore for these links, since there is no reload.

Without HTMX: `fetch` the URL, parse it, replace `main`, `history.pushState`, and handle `popstate`.

## 4. Hold an update that arrives mid-animation

Track when the current animation ends (`busyUntil = performance.now() + total`). For HTMX, delay the incoming swap:

```js
el.addEventListener("htmx:beforeSwap", e => {
  const wait = busyUntil - performance.now();
  if (wait > 0) e.detail.swapOverride = `innerHTML swap:${Math.ceil(wait)}ms`;
});
```

If the user starts a new big transition instead, finish the running animations immediately (`el.getAnimations({subtree: true}).forEach(a => a.finish())`), clean up any stand-ins, then start the new one from the final positions.

## 5. Display preferences

```html
<head><script>
  try { if (localStorage.getItem("pref:show-weekend") === "1") document.documentElement.dataset.showWeekend = ""; } catch (e) {}
</script></head>
```

```css
:root { --week-days: 5; }
html[data-show-weekend] { --week-days: 7; }
html:not([data-show-weekend]) .is-weekend { display: none !important; }
.week-row { grid-template-columns: minmax(0, 1fr) repeat(var(--week-days), 92px); }
```

Toggle with one delegated `change` listener on `input[data-pref]`, so switches added later by swaps work too. Wrap the attribute change in a view transition, unless reduced motion is on:

```js
if (document.startViewTransition && !matchMedia("(prefers-reduced-motion: reduce)").matches) document.startViewTransition(apply);
else apply();
```

Give elements that should glide (not just crossfade) unique names: `style="view-transition-name: stat-3"`.

## 6. Same size in both states of a toggle

- Use the same layout family in both states. For example, on phones a 6-item panel is `repeat(3, 1fr)` (two rows) and the 8-item version is `repeat(4, 1fr)` (still two rows).
- On wide screens, use the narrower box size in both states, so a neighbouring title doesn't wrap in one state only.
- Use `:has()` to scope rules to the panel that actually contains toggled items.

## 7. Load-in animation

Bars, in pure CSS. This starts on first paint, so there's no flash. Stagger with an index variable:

```css
@keyframes grow-x { from { transform: scaleX(0); } }
@media (prefers-reduced-motion: no-preference) {
  [data-grow="x"] { transform-origin: left center; animation: grow-x 700ms cubic-bezier(.2,.8,.2,1) both;
                    animation-delay: calc(var(--i, 0) * 45ms); }
}
```

For numbers, hide them before paint, count up, and reveal:

```css
[data-count] { font-variant-numeric: tabular-nums; }
@keyframes count-fallback { to { visibility: visible; } }
@media (prefers-reduced-motion: no-preference) {
  .can-count [data-count]:not(.is-counting) { visibility: hidden; animation: count-fallback 0s 1.5s forwards; }
}
```

- Add `can-count` to `<html>` in a head script, when motion is allowed.
- In JS, for each `[data-count]`:
  1. Store its text.
  2. Set `minWidth` to its current width.
  3. Add `is-counting`.
  4. Replace every `\d+` with `0`.
  5. Animate with `requestAnimationFrame` and an ease-out curve, writing `Math.round(n * eased)` into each number.
  6. Restore the exact text at the end, and clear `minWidth`.

## 8. Moving a shared element between layouts (FLIP)

1. Before the change, record `getBoundingClientRect()` of each shared element (key it by a stable class or id).
2. Make the change.
3. After it, compute `dx`, `dy` and `scale = oldWidth / newWidth` for each element, and animate `transform: translate(dx, dy) scale(s)` back to `none`.

Rules:
- Only scale things that look right scaled evenly (circles, icons).
- For a box that changes aspect ratio (a card), don't scale. Animate a stand-in element's `left/top/width/height` from the old rect to the new one, while the real card waits with its background hidden. Remove the stand-in when its animation finishes; don't use a timer.
- Give the stand-in `display: block`. A copied grid/flex class can centre its `::before` strip and move it away from the top.
- Animate the strip/header size with `el.animate(keyframes, { pseudoElement: "::before" })` between the two sizes.
- Keep the real card's content above the stand-in (`position: relative; z-index: 1`).

## 9. Dropdown arrow with symmetric spacing

```css
:root { --select-arrow: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='%231d1f20' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'%3E%3Cpath d='m6 9 6 6 6-6'/%3E%3C/svg%3E"); }
/* plus a dark-theme value with a light stroke */
select:not([multiple]):not([size]) {
  appearance: none; padding-right: 42px;
  background-image: var(--select-arrow); background-repeat: no-repeat;
  background-position: right 14px center; background-size: 16px 16px;
}
@media (forced-colors: active) { select { appearance: auto; background-image: none; } }
```

Selects whose background is set with the `background:` shorthand (for example coloured pickers) lose the arrow. Exclude them, or set only `background-color`.

## 10. Alert frame + wash

```css
:root { --error-line: #c8102e; --warn-line: #b5761f; }        /* plus dark-theme values */
.alert-error, .has-error { --alert-line: var(--error-line); }
.alert-warning, .needs-input { --alert-line: var(--warn-line); }
.alert-error, .alert-warning {                                  /* messages */
  border: 2px solid var(--alert-line);
  background: color-mix(in srgb, var(--alert-line) 10%, var(--panel-bg));
  padding: 12px 16px 12px 44px; position: relative;              /* icon at left: 14px */
}
.has-error, .needs-input {                                      /* a field or a group of fields */
  --wash: color-mix(in srgb, var(--alert-line) 10%, var(--panel-bg));
  background: var(--wash); box-shadow: 0 0 0 6px var(--wash), 0 0 0 8px var(--alert-line);
}
```

- Use `box-shadow` rings, not padding or border, so framing a field doesn't move anything.
- Leave a margin below a framed group so the ring doesn't touch the next field.
- When an error message follows a group of inputs instead of sitting inside a wrapper, wrap the group and its messages together (in JS on load), so the frame goes around both.

## 11. Pointing at a required field

After the "fill this in first" popup closes:
1. Add the warning frame (section 10) to the field's wrapper.
2. Insert a short note with an icon under the field, and link it with `aria-describedby`.
3. Set `aria-invalid="true"`.
4. `scrollIntoView({block: "center"})`, smooth unless reduced motion.
5. Focus the field with `{preventScroll: true}`.
6. On the first `input` with a non-empty value, remove the frame, the note and the aria attributes.

## 12. Visible "white" category colours

On light surfaces, give white or silver fills a thin dark outline: `box-shadow: inset 0 0 0 1px rgba(15, 23, 42, .55)`. Mark "current" differently from "done", for example a dark fill with a 2px white inset frame.
