// Regenerates the program icon (Windows .ico) and the bookmark icon (PNG) from the browser icon, so all three match.
// Development tool only; the results are committed. Needs Node.js and Playwright with Chromium:
//   node tools/icons/make-icons.cjs
// Inputs:  src/DicomLogAnalyser.Web/Ui/favicon.svg
// Outputs: src/DicomLogAnalyser.Web/Assets/app.ico (16 to 256 px, PNG-compressed entries)
//          src/DicomLogAnalyser.Web/Ui/apple-touch-icon.png (180 px, for Safari and iOS bookmarks)
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..', '..');
const web = path.join(root, 'src', 'DicomLogAnalyser.Web');
const svg = fs.readFileSync(path.join(web, 'Ui', 'favicon.svg'), 'utf8');
const icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

/** Renders the SVG at one size with a transparent background. */
async function render(page, size) {
  await page.setViewportSize({ width: size, height: size });
  await page.setContent(`<html><body style="margin:0;background:transparent">${svg.replace('<svg ', `<svg width="${size}" height="${size}" `)}</body></html>`);
  return page.screenshot({ omitBackground: true });
}

/** An .ico file whose entries are PNG images (supported by Windows Vista and later). */
function ico(images) {
  const header = Buffer.alloc(6 + 16 * images.length);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);
  let offset = header.length;
  images.forEach(({ size, png }, i) => {
    const e = 6 + 16 * i;
    header.writeUInt8(size >= 256 ? 0 : size, e);
    header.writeUInt8(size >= 256 ? 0 : size, e + 1);
    header.writeUInt8(0, e + 2);
    header.writeUInt8(0, e + 3);
    header.writeUInt16LE(1, e + 4);
    header.writeUInt16LE(32, e + 6);
    header.writeUInt32LE(png.length, e + 8);
    header.writeUInt32LE(offset, e + 12);
    offset += png.length;
  });
  return Buffer.concat([header, ...images.map((im) => im.png)]);
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();
  const images = [];
  for (const size of icoSizes) images.push({ size, png: await render(page, size) });
  fs.mkdirSync(path.join(web, 'Assets'), { recursive: true });
  fs.writeFileSync(path.join(web, 'Assets', 'app.ico'), ico(images));
  fs.writeFileSync(path.join(web, 'Ui', 'apple-touch-icon.png'), await render(page, 180));
  await browser.close();
  console.log('Wrote Assets/app.ico and Ui/apple-touch-icon.png');
})();
