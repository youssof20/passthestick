# Brand

## What it is

PassTheStick is a tool someone made for their friends. It should feel like a console and a couch on game night: matte putty plastic, a bright lit button, plain labels. Not an esports dashboard.

## The one memorable thing

The stick. A round-topped arcade stick, drawn flat. Whoever holds it is lit **handoff yellow**. Everything else stays quiet. The signature moment is the handoff: the lit ball leaves one player's row and lands in the next in 280 ms (`motion.handoff`), with the previous holder's row dimming as it goes. With reduced motion on, the yellow marker simply switches rows with no movement.

## Color

Source: `design/tokens.json`. Generated into `design/generated/Tokens.*.xaml` and `site/css/tokens.css`. `node scripts/gen-design.mjs` fails the build if any pair drops below WCAG AA, in both themes.

- **Putty** (`bg`, `surface`): warm grey plastic in light, near-black charcoal in dark.
- **Ink** (`text`): near-black on light, warm off-white on dark.
- **Handoff yellow** (`accent`): the stick holder and the primary action. Always with `onAccent` text. Yellow is never used as text color on light; use `accentText` there.
- **Live green** (`live`): connected, input arriving.
- **Danger red** (`danger`): disconnects, errors.

No gradients. No purple-to-blue. No glass.

## Type

- **Familjen Grotesk**: headings and the wordmark.
- **Atkinson Hyperlegible Next**: all UI text. Chosen for legibility at small sizes on a TV-distance screenshare.
- **JetBrains Mono**: the room code only, 72 px, tabular, letter-spaced. The alphabet has no 0/O/1/I/L so it cannot be misread.

All three are open fonts (OFL). The app and site bundle them; Segoe UI is the fallback. Font files are added in phase 5 and phase 6 and must ship with their license text.

## Shape and space

Radii are deliberately different by role: `control` 4 px, `panel` 12 px, `pill` for status chips only. Spacing uses the 4-based scale in the tokens. Focus ring is 3 px in `focus`, 2 px offset, on every interactive element.

## Logo and icon

`design/brand/mark.svg`: the stick on an ink rounded square, with a faded earlier stick behind it to show the pass. `node scripts/gen-icons.mjs` renders the Windows `.ico` (16 to 256 px), favicons, touch icons and the Open Graph card. The OG card uses Segoe UI because Familjen Grotesk is not installed on the build machine; re-render once the font is bundled.

## Voice

Plain and short. Say what happens, then stop. No exclamation marks, no emoji, no hype lines. Example error: "Your friend's code is wrong or has expired. Ask them for a new one." Full rules are in `CLAUDE.md`.
