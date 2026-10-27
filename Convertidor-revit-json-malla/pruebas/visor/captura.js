// Abre el visor local con Chrome (Playwright) y guarda una captura. Uso: node captura.js <url> <png>
const { chromium } = require("playwright-core");
(async () => {
  const [url, png] = process.argv.slice(2);
  const browser = await chromium.launch({ executablePath: "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", headless: true,
    args: ["--use-angle=swiftshader", "--enable-unsafe-swiftshader"] });
  try {
    const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
    const errors = [];
    page.on("pageerror", (e) => errors.push(e.message));
    page.on("console", (m) => { if (m.type() === "error") errors.push(m.text()); });
    await page.goto(url);
    await page.waitForFunction(() => document.body.dataset.listo, null, { timeout: 240000 });
    await page.waitForTimeout(1500);
    await page.screenshot({ path: png });
    console.log(JSON.stringify({ estado: await page.locator("#estado").innerText(), errores: errors.slice(0, 5) }));
  } finally {
    // Chrome sin ventana consume recursos: se cierra siempre, también si la carga falla.
    await browser.close();
  }
})();
