// Renders design/brand/mark.svg into the app icon (.ico), favicons and the Open Graph image.
import sharp from "sharp";
import pngToIco from "png-to-ico";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const svg = readFileSync(join(root, "design/brand/mark.svg"));
const tokens = JSON.parse(readFileSync(join(root, "design/tokens.json"), "utf8")).color.dark;
const out = join(root, "design/generated/icons");
mkdirSync(out, { recursive: true });
mkdirSync(join(root, "site"), { recursive: true });

const png = (size) => sharp(svg, { density: 384 }).resize(size, size).png().toBuffer();
const sizes = [16, 24, 32, 48, 64, 128, 256];
const bufs = await Promise.all(sizes.map(png));
writeFileSync(join(out, "passthestick.ico"), await pngToIco(bufs));
writeFileSync(join(root, "site/favicon.ico"), await pngToIco(bufs.slice(0, 3)));
for (const s of [180, 192, 512]) writeFileSync(join(root, `site/icon-${s}.png`), await png(s));
writeFileSync(join(root, "site/favicon.svg"), svg);

// Open Graph card 1200x630: mark left, name and one line right. Colors come from tokens.
const og = `<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="630">
  <rect width="1200" height="630" fill="${tokens.bg}"/>
  <image href="data:image/svg+xml;base64,${svg.toString("base64")}" x="90" y="165" width="300" height="300"/>
  <text x="450" y="300" font-family="Segoe UI, Arial, sans-serif" font-weight="700" font-size="92" fill="${tokens.text}">PassTheStick</text>
  <text x="450" y="375" font-family="Segoe UI, Arial, sans-serif" font-size="38" fill="${tokens.textMuted}">One copy of the game.</text>
  <text x="450" y="425" font-family="Segoe UI, Arial, sans-serif" font-size="38" fill="${tokens.textMuted}">Everyone plays. Free, no account.</text>
  <rect x="450" y="470" width="260" height="10" rx="5" fill="${tokens.accent}"/>
</svg>`;
await sharp(Buffer.from(og)).png().toFile(join(root, "site/og-image.png"));
console.log("icons written: ico, favicon.ico/svg, icon-180/192/512, og-image.png");
