// Tarayıcı akış testi (Playwright). Örnek verili bir ortama karşı çalışır:
//   docker compose -f deploy/docker-compose.yml up -d
//   NE_URL=http://localhost:8080 node tests/e2e/akis.cjs
// Kapsam: giriş, özel alan ekleme, sütun tercihinin kalıcılığı, zimmet, PDF indirme, imzalı evrak yükleme,
// stokta bakiyeden fazla çıkışın reddi.
const { chromium } = require('playwright');
const fs = require('fs');
const os = require('os');
const path = require('path');

const base = (process.env.NE_URL || 'http://localhost:8080').replace(/\/$/, '') + '/';
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ne-e2e-'));
let step = '';
const check = (cond, msg) => { if (!cond) throw new Error(`${step}: ${msg}`); console.log(`  ✓ ${msg}`); };

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1400, height: 900 }, acceptDownloads: true });
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  try {
    step = 'Giriş';
    console.log(step);
    await page.goto(base);
    await page.waitForURL(/giris/);
    await page.fill('#lu', 'selin.aydin');
    await page.fill('#lp', 'yanlis-parola');
    await page.click('button[type=submit]');
    await page.waitForSelector('.note.error');
    check(/hatalı/.test(await page.textContent('.note.error')), 'hatalı parola anlaşılır mesajla reddedildi');
    await page.fill('#lp', 'Gelistirme123');
    await page.click('button[type=submit]');
    await page.waitForSelector('.kpis');
    check(true, 'şirket yöneticisi giriş yaptı, genel bakış açıldı');

    step = 'Özel alan';
    console.log(step);
    await page.goto(base + 'kategoriler');
    await page.click('.cat-list button:has-text("Monitör")');
    await page.click('button:has-text("+ Alan ekle")');
    const label = 'Bağlantı ' + Date.now().toString().slice(-5);
    await page.fill('#ff-label', label);
    await page.selectOption('#ff-type', 'Select');
    await page.fill('#ff-opts', 'HDMI\nDisplayPort\nUSB-C');
    await page.click('.modal button:has-text("Kaydet")');
    await page.waitForSelector(`.fl-row:has-text("${label}")`);
    check(true, `"${label}" seçim listesi alanı eklendi`);

    step = 'Sütun tercihi';
    console.log(step);
    await page.goto(base + 'envanter');
    await page.waitForSelector('tbody tr.click');
    await page.click('button:has-text("Sütunlar")');
    await page.locator('.pop label', { hasText: 'Maliyet' }).locator('input').check();
    await page.click('.pop button:has-text("Kapat")');
    check(await page.locator('th', { hasText: 'Maliyet' }).count() === 1, 'maliyet sütunu açıldı');
    await page.reload();
    await page.waitForSelector('tbody tr.click');
    check(await page.locator('th', { hasText: 'Maliyet' }).count() === 1, 'sütun tercihi sayfa yenilemesinden sonra korundu');

    step = 'Zimmet';
    console.log(step);
    await page.goto(base + 'zimmetler/yeni');
    await page.fill('input[placeholder="Ad, sicil no veya birim"]', 'Burak');
    await page.locator('.pick label').first().click();
    await page.selectOption('.card:has(h2:text("2. Cihazlar")) select', { label: 'Ankara Merkez Depo' });
    await page.waitForSelector('.card:has(h2:text("2. Cihazlar")) .pick label');
    await page.locator('.card:has(h2:text("2. Cihazlar")) .pick label').first().click();
    await page.click('button:has-text("cihazı zimmetle")');
    await page.waitForURL(/zimmetler\/[0-9a-f-]{36}$/);
    await page.waitForSelector('h1');
    const no = (await page.textContent('h1')).trim();
    check(/^ZMT-\d{4}-\d{6}$/.test(no), `${no} tutanağı oluşturuldu`);

    step = 'PDF';
    console.log(step);
    const [dl] = await Promise.all([page.waitForEvent('download'), page.click('a:has-text("PDF indir")')]);
    const pdfPath = path.join(tmp, 'tutanak.pdf');
    await dl.saveAs(pdfPath);
    check(fs.readFileSync(pdfPath).subarray(0, 4).toString() === '%PDF', 'tutanak PDF olarak indirildi');

    step = 'İmzalı evrak';
    console.log(step);
    const scan = path.join(tmp, 'imzali.pdf');
    fs.writeFileSync(scan, '%PDF-1.4\n% imzali tarama\n');
    await page.setInputFiles('#upl', scan);
    await page.waitForSelector('text=Sürüm 1');
    check(await page.locator('.pill.p-ok', { hasText: 'İmzalı yüklendi' }).count() > 0, 'imzalı kopya sürüm 1 olarak yüklendi');
    const bad = path.join(tmp, 'sahte.pdf');
    fs.writeFileSync(bad, 'bu bir pdf degil');
    await page.setInputFiles('#upl', bad);
    await page.waitForSelector('.note.error');
    check(/PDF, JPEG veya PNG/.test(await page.textContent('.note.error')), 'içeriği PDF olmayan dosya reddedildi');

    step = 'Stok';
    console.log(step);
    await page.goto(base + 'stok');
    await page.waitForSelector('tbody tr');
    await page.click('button:has-text("Sarf çıkışı")');
    await page.selectOption('#mv-item', { label: 'HP 151A toner (adet)' });
    await page.selectOption('#mv-wh', { index: 1 });
    await page.fill('#mv-qty', '9999');
    await page.fill('#mv-prj', 'E2E');
    await page.click('.modal button:has-text("Kaydet")');
    await page.waitForSelector('.modal .err-msg');
    check(/En fazla bu kadar/.test(await page.textContent('.modal .err-msg')), 'bakiyeden fazla çıkış reddedildi');

    check(errors.length === 0, 'tarayıcıda yakalanmamış hata yok' + (errors.length ? ': ' + errors.join(' | ') : ''));
    console.log('\nTüm adımlar başarılı.');
  } catch (e) {
    await page.screenshot({ path: path.join(tmp, 'hata.png') }).catch(() => {});
    console.error('\nBAŞARISIZ:', e.message, '\nEkran görüntüsü:', path.join(tmp, 'hata.png'));
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
