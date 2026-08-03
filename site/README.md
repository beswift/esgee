# esgee landing page

Static landing page served at https://beswift.github.io/esgee/ via GitHub
Pages (`.github/workflows/pages.yml` deploys this directory on pushes to
`main` that touch `site/`).

## Stack: one HTML file, hand-rolled CSS

No framework, no build step. The page has exactly one interactive element (a
copy button), so shipping a React runtime — or even a Tailwind build — buys
nothing here. The "shadcn look" (neutral grays, hairline borders, glass
panels, calm motion) is a visual language, and ~500 lines of modern CSS
reproduce it with a faster load, zero layout shift, and no toolchain to
maintain. Fonts (Bricolage Grotesque + IBM Plex Mono) come from Google Fonts
with `display=swap`; everything else is inline.

The "app screenshots" are deliberately stylized CSS/SVG mockups, not real
captures.

`assets/esgee-256.png` is the 256px frame extracted from
`src/Esgee/esgee.ico`.

## Editing

Edit `index.html`, open it in a browser to check, push to `main`. The
workflow uploads `site/` as-is — what you see locally is what deploys.
