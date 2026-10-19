'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const core = require('../Outlook_Mail_Cleaner.user.js');

test('XML 특수문자와 한글을 안전하게 이스케이프한다', () => {
  assert.equal(core.xmlEscape(`일보 & <뉴스> "A" 'B'`), '일보 &amp; &lt;뉴스&gt; &quot;A&quot; &apos;B&apos;');
});

test('실제 달력에 없는 날짜를 거부한다', () => {
  assert.deepEqual(core.parseDateParts('2024-02-29'), { year: 2024, month: 2, day: 29 });
  assert.equal(core.parseDateParts('2025-02-29'), null);
  assert.equal(core.parseDateParts('2026-13-01'), null);
});

test('KST 날짜 양 끝을 UTC 반개방 구간으로 변환한다', () => {
  assert.equal(core.kstBoundaryUtc('2026-08-01'), '2026-07-31T15:00:00.000Z');
  assert.equal(core.kstBoundaryUtc('2026-08-31', 1), '2026-08-31T15:00:00.000Z');
});

test('KST 기본 날짜가 오늘을 포함한 30일 범위다', () => {
  const utcNow = Date.parse('2026-08-27T02:30:00.000Z');
  assert.equal(core.kstDateInput(0, utcNow), '2026-08-27');
  assert.equal(core.kstDateInput(-29, utcNow), '2026-07-29');
});

test('검색 조건을 검증한다', () => {
  const valid = { folderId: 'folder', keyword: '일보', startDate: '2026-08-01', endDate: '2026-08-27', mode: 'markRead' };
  assert.equal(core.validateCriteria(valid), '');
  assert.match(core.validateCriteria({ ...valid, keyword: ' ' }), /키워드/);
  assert.match(core.validateCriteria({ ...valid, folderId: '' }), /폴더/);
  assert.match(core.validateCriteria({ ...valid, startDate: '2026-08-28' }), /시작일/);
  assert.match(core.validateCriteria({ ...valid, mode: 'hardDelete' }), /처리 방식/);
});

test('20건 단위로 항목을 분할한다', () => {
  const groups = core.chunk(Array.from({ length: 45 }, (_, index) => index));
  assert.deepEqual(groups.map(group => group.length), [20, 20, 5]);
});

test('인라인 이미지는 일반 첨부에서 제외한다', () => {
  assert.equal(core.isNormalAttachment({ id: 'a', isInline: false }), true);
  assert.equal(core.isNormalAttachment({ id: 'b', isInline: true }), false);
  assert.equal(core.isNormalAttachment({ id: '', isInline: false }), false);
});

test('폴더 부모 관계를 표시 경로로 조립한다', () => {
  const folders = core.buildFolderPaths([
    { id: 'child', parentId: 'inbox', name: '프로젝트' },
    { id: 'inbox', parentId: 'root', name: '받은 편지함' },
    { id: 'root', parentId: '', name: '메일함' },
  ]);
  assert.equal(folders.find(folder => folder.id === 'child').path, '메일함 / 받은 편지함 / 프로젝트');
});

test('Exchange 오류 코드를 사용자용 메시지로 바꾼다', () => {
  assert.match(core.ewsErrorMessage('ErrorInvalidChangeKey'), /변경/);
  assert.match(core.ewsErrorMessage('ErrorAccessDenied'), /권한/);
  assert.equal(core.ewsErrorMessage('CustomCode', '서버 상세'), '서버 상세');
  assert.equal(core.isStaleCode('ErrorInvalidAttachmentId'), true);
});

test('메일 검색 SOAP에 날짜, 폴더, 한글 키워드, 모드 조건을 넣는다', () => {
  const body = core.buildFindItemsBody({
    folderId: 'folder<&', keyword: '일보 & <뉴스>', startDate: '2026-08-01', endDate: '2026-08-31', mode: 'removeAttachments',
  }, 100);
  assert.match(body, /Offset="100"/);
  assert.match(body, /folder&lt;&amp;/);
  assert.match(body, /일보 &amp; &lt;뉴스&gt;/);
  assert.match(body, /item:HasAttachments/);
  assert.match(body, /2026-07-31T15:00:00\.000Z/);
  assert.match(body, /2026-08-31T15:00:00\.000Z/);
});

test('읽음 처리와 휴지통 이동 SOAP이 영구 삭제를 사용하지 않는다', () => {
  const item = { id: 'id<&', changeKey: 'change"key' };
  const update = core.buildUpdateReadBody([item]);
  const move = core.buildMoveItemsBody([item]);
  assert.match(update, /message:IsRead/);
  assert.match(update, /SuppressReadReceipts="true"/);
  assert.match(move, /DistinguishedFolderId Id="deleteditems"/);
  assert.doesNotMatch(move, /DeleteItem/);
});

test('첨부 삭제 SOAP에는 선택한 첨부 ID만 들어간다', () => {
  const body = core.buildDeleteAttachmentsBody([{ id: 'normal-1' }, { id: 'normal-2' }]);
  assert.match(body, /normal-1/);
  assert.match(body, /normal-2/);
  assert.equal((body.match(/AttachmentId Id=/g) || []).length, 2);
});

test('SOAP 봉투가 호환 서버 버전과 네임스페이스를 선언한다', () => {
  const envelope = core.soapEnvelope(core.buildGetFolderBody());
  assert.match(envelope, /Exchange2013_SP1/);
  assert.match(envelope, /schemas\.xmlsoap\.org\/soap\/envelope/);
  assert.match(envelope, /DistinguishedFolderId Id="inbox"/);
});
