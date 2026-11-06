// Optional artwork regeneration; normal builds embed the checked-in PNG.
// Usage: node scripts/render-plugin-thumbnail.cjs [path-to-playwright-core]
// Set CHROME_PATH to override the local Chrome executable.
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require(path.resolve(process.argv[2] || 'artifacts/ui-check/node_modules/playwright-core'));
const root = path.resolve(__dirname, '..');
const artwork = path.join(root, 'src/EmbyBlurayHelper.EmbyPlugin/Artwork');
let source = fs.readFileSync(path.join(artwork, 'thumbnail.svg'), 'utf8');
source = source.replace('../../EmbyBlurayHelper.App/Web/infuse-icon.jpg',
  'data:image/jpeg;base64,' + fs.readFileSync(path.join(root, 'src/EmbyBlurayHelper.App/Web/infuse-icon.jpg')).toString('base64'));
(async () => {
  const browser = await chromium.launch({
    executablePath: process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe',
    headless: true
  });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 720 }, deviceScaleFactor: 1 });
    await page.setContent('<html><head><meta charset="utf-8"></head><body style="margin:0;overflow:hidden">' + source + '</body></html>');
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({ path: path.join(artwork, 'thumbnail.png'), animations: 'disabled' });
    const preview = path.join(root, 'artifacts/ui');
    fs.mkdirSync(preview, { recursive: true });
    await page.setViewportSize({ width: 544, height: 306 });
    await page.addStyleTag({ content: 'svg { width: 544px; height: 306px; }' });
    await page.screenshot({ path: path.join(preview, 'plugin-thumbnail-card.png'), animations: 'disabled' });
    console.log('Rendered plugin thumbnail: 1280 × 720, with a 544 × 306 card preview.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
