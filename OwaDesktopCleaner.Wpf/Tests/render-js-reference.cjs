// Renders the existing JS version's actual CSS/HTML without running its EWS code.
// Usage: node render-js-reference.cjs <output-directory> (Playwright + Edge required).
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const source = fs.readFileSync(path.resolve(__dirname, '../../OwaDesktopCleaner/cleaner.js'), 'utf8');
const css = source.match(/style\.textContent = `([\s\S]*?)`;/)[1];
let html = source.match(/overlay\.innerHTML = `([\s\S]*?)`;/)[1];
html = html.replaceAll('${APP_NAME}', '메일 클리너').replaceAll('${VERSION}', '2.0.0-desktop')
  .replace(/\$\{kstDateInput\((-?\d+)\)\}/g, (_, days) => {
    const date = new Date('2026-10-20T00:00:00Z'); date.setUTCDate(date.getUTCDate() + Number(days)); return date.toISOString().slice(0, 10);
  });
const output = path.resolve(process.argv[2]); fs.mkdirSync(output, { recursive: true });
(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1140, height: 850 }, deviceScaleFactor: 1 });
    await page.route('**/*', route => route.abort());
    await page.setContent(`<html lang="ko"><head><meta charset="utf-8"><style>body{margin:0;background:#fff}${css}</style></head><body><div class="omc-overlay">${html}</div></body></html>`);
    await page.screenshot({ path: path.join(output, 'javascript-initial.png') });
    await page.evaluate(() => {
      document.querySelector('#omc-folder').innerHTML = '<option>받은 편지함</option>';
      document.querySelector('#omc-folder').disabled = false;
      document.querySelector('#omc-search').disabled = false;
      document.querySelector('input[value="inspect"]').checked = false;
      document.querySelector('input[value="removeAttachments"]').checked = true;
      const status = document.querySelector('#omc-status'); status.className = 'omc-status success'; status.textContent = '검색 완료 · 3건 · 본인 사서함과 폴더를 확인하세요';
      document.querySelector('#omc-sort').value = 'sizeDesc';
      document.querySelector('#omc-count').textContent = '검색 결과: 3건 · 3.7 MB';
      document.querySelector('#omc-tbody').innerHTML = [
        ['2026. 10. 20. 09:30', '업무지원팀', '[예시] 주간 업무 보고 및 첨부 자료', '안 읽음', '3.4 MB', '주간보고서.xlsx (2.5 MB)'],
        ['2026. 10. 19. 17:20', '개발팀', '[예시] 개발팀 일보', '읽음', '150.0 KB', '업무일보.pdf (100.0 KB)'],
        ['2026. 10. 19. 17:20', '개발팀', '[예시] 프로젝트 일정 안내', '안 읽음', '150.0 KB', '업무일보.pdf (100.0 KB)']
      ].map((r, i) => `<tr><td><input type="checkbox" ${i < 2 ? 'checked' : ''}></td><td>${r[0]}</td><td>${r[1]}<div class="omc-muted">sample@example.invalid</div></td><td class="omc-subject">${r[2]}</td><td>${r[3]}</td><td>${r[4]}</td><td class="omc-attachments"><b>1개</b><br>${r[5]}</td></tr>`).join('');
      document.querySelector('#omc-all').disabled = false; document.querySelector('#omc-none').disabled = false; document.querySelector('#omc-run').disabled = false;
      document.querySelector('#omc-summary').textContent = '선택: 2건 · 3.5 MB'; document.querySelector('#omc-run-hint').textContent = '';
    });
    await page.screenshot({ path: path.join(output, 'javascript-results.png') });
    console.log('Rendered original JS CSS/markup; fake data only; all network requests blocked.');
  } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
