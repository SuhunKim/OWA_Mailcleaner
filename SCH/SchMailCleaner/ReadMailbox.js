// Read the visible mailbox list DOM only; never open individual messages.
(() => {
  if (location.origin !== 'https://hg.sch.ac.kr') throw Error('허용된 사이트가 아닙니다.');
  const doc = document.querySelector('frame#Start')?.contentDocument;
  if (!doc) throw Error('메일 화면이 아직 열리지 않았습니다.');
  // Stable user id; the menu text changes with unread counts and must not be used to identify the account.
  const account = doc.querySelector('#UID')?.value || '';
  if (!account) throw Error('계정 식별값(#UID)을 찾을 수 없습니다.');
  const folder = (doc.querySelector('.list_content h2') || doc.querySelector('h2'))?.textContent.trim() || '';
  const count = Number(doc.querySelector('#msgTotalCount')?.value);
  if (!Number.isFinite(count)) throw Error('총 메일 건수를 확인할 수 없습니다.');
  const target = doc.querySelector('#targetPage');
  if (!target) {
    // An empty folder has no pager.
    if (count === 0 && folder) return { Account: account, Folder: folder, Page: 1, Total: 1, Count: 0, Rows: [] };
    throw Error('메일 목록 화면이 아닙니다.');
  }
  const page = Number(target.value);
  const total = Number(target.parentElement.textContent.match(/\/\s*(\d+)/)?.[1]);
  if (!page || !total || total > 10000) throw Error('페이지 정보를 확인할 수 없습니다.');
  const rows = Array.from(doc.querySelectorAll('tr[id^="mail_list-"]')).map(row => {
    const cells = Array.from(row.cells);
    if (cells.length !== 5) throw Error('메일 목록 형식이 변경되었습니다.');
    const sender = cells[1].textContent.trim();
    const subject = cells[2].querySelector('a[title]')?.title || cells[2].textContent.trim();
    // Status icons (read/unread, attachment) live in the first cell next to the checkbox; kept raw so C# can interpret them.
    // Row ids (mail_list-N) are list positions that shift when mail is removed or arrives; the checkbox's data-mid is the mail's own number.
    const mid = cells[0].querySelector('input[type="checkbox"]')?.dataset.mid || '';
    if (!mid) throw Error('메일 고유 번호(data-mid)를 찾지 못했습니다.');
    const status = (cells[0].innerHTML || '').replace(/\s+/g, ' ').slice(0, 600);
    return { Id: mid, Icon: (row.querySelector('img[id^="wma-icon-"]')?.getAttribute('src') || '').split('/').pop().toUpperCase(), Sender: sender, Subject: subject, Cls: row.className || '', Status: status,
      ReceivedText: cells[3].textContent.trim(), SizeText: cells[4].textContent.trim() };
  });
  return { Account: account, Folder: folder, Page: page, Total: total, Count: count, Rows: rows };
})()
