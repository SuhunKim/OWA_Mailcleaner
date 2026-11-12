'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const core = require('../cleaner.js');

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

const trash = { folderId: 'trash', keyword: '', startDate: '', endDate: '', mode: 'emptyDeleted' };
function element(name, attrs = {}, children = [], text = '') {
  return { localName: name, children, textContent: text,
    getAttribute: key => attrs[key] ?? null, hasAttribute: key => key in attrs,
    getElementsByTagName: () => children.flatMap(child => [child, ...child.getElementsByTagName('*')]) };
}
function message(id, parent = 'trash', size = '1024') {
  return element('Message', {}, [element('ItemId', { Id: id }), element('ParentFolderId', { Id: parent }),
    element('ItemClass', {}, [], 'IPM.Note'), element('Subject', {}, [], '테스트'),
    element('DateTimeReceived', {}, [], '2026-09-01T00:00:00Z'), element('Size', {}, [], size), element('HasAttachments', {}, [], 'true')]);
}
function response(code = 'NoError', items = []) {
  return element('GetItemResponseMessage', { ResponseClass: code === 'NoError' ? 'Success' : 'Error' },
    [element('ResponseCode', {}, [], code), element('Items', {}, items)]);
}
function page(items, done, offset = 100) {
  return element('Document', {}, [response(), element('RootFolder', { IncludesLastItemInRange: String(done), IndexedPagingOffset: String(offset), TotalItemsInView: '200' }, [element('Items', {}, items)])]);
}
function clientWith(call) {
  global.location = { origin: 'https://mail.apsfamily.com' };
  const client = new core.EwsClient(); client.call = call; return client;
}

test('휴지통은 제목과 기간 없이 조회하며 단일 조건 SOAP도 유효하다', () => {
  assert.equal(core.validateCriteria(trash), '');
  assert.doesNotMatch(core.buildFindItemsBody(trash), /<m:Restriction>/);
  const body = core.buildFindItemsBody({ ...trash, keyword: '보고' });
  assert.match(body, /<m:Restriction><t:Contains/);
  assert.doesNotMatch(body, /<t:And>/);
  assert.match(body, /item:Size/);
  assert.match(core.validateCriteria({ ...trash, startDate: '2026-09-01' }), /종료일/);
  assert.match(core.validateCriteria({ ...trash, startDate: '2026-09-02', endDate: '2026-09-01' }), /늦을/);
});
test('용량 미확인과 KB MB 경계를 구분한다', () => {
  assert.equal(core.parseSize(''), null); assert.equal(core.parseSize('-1'), null);
  assert.equal(core.formatSize(null), '미확인'); assert.equal(core.formatSize(0), '0.0 KB');
  assert.equal(core.formatSize(1024), '1.0 KB'); assert.equal(core.formatSize(1048576), '1.0 MB');
  assert.match(core.totalSize([{ size: null }, { size: 1024 }]), /1.0 KB.*미확인 1건/);
  assert.equal(core.parseMessageElement(message('x', 'trash', '')).size, null);
});
test('정렬은 숫자와 날짜 기준이고 원본 및 ID 선택은 유지된다', () => {
  const items = [{ id:'a', size: 2048, receivedDateTime:'2026-09-02' }, { id:'b', size:10000, receivedDateTime:'2026-09-01' }, {id:'c', size:null, receivedDateTime:''}];
  const selected = new Set(['a']);
  assert.deepEqual(core.sortMessages(items,'sizeDesc').map(x=>x.id), ['b','a','c']);
  assert.deepEqual(core.sortMessages(items,'sizeAsc').map(x=>x.id), ['a','b','c']);
  assert.deepEqual(core.sortMessages(items,'dateAsc').map(x=>x.id), ['b','a','c']);
  assert.deepEqual(items.map(x=>x.id), ['a','b','c']);
  assert.equal(core.sortMessages(items,'dateDesc').filter(x=>selected.has(x.id))[0].id,'a');
});
test('휴지통 조회는 전체 페이지 중복을 제거하고 첨부 상세를 요청하지 않는다', async () => {
  let calls=0;
  const client=clientWith(async op=>{assert.equal(op,'FindItem'); return ++calls===1 ? page([message('a')],false) : page([message('a'),message('b')],true);});
  assert.deepEqual((await client.findMessages(trash)).map(x=>x.id),['a','b']);
  assert.equal(calls,2);
});
test('페이지 누락, 조회 상한, 반복 페이지, 중단은 완료 결과를 반환하지 않는다', async () => {
  await assert.rejects(clientWith(async()=>element('Document')).findMessages(trash), /페이지/);
  await assert.rejects(clientWith(async()=>page([],false,100000)).findMessages(trash), /상한/);
  await assert.rejects(clientWith(async()=>page([],false,100)).findMessages(trash), /반복/);
  const error=new Error('중단'); error.name='AbortError';
  await assert.rejects(clientWith(async()=>{throw error;}).findMessages(trash), {name:'AbortError'});
});
test('비우기는 확인된 휴지통 메일만 삭제하고 이동 및 누락은 건너뛴다', async () => {
  const calls=[];
  const client=clientWith(async(op,body)=>{calls.push([op,body]);
    return op==='GetItem' ? element('Document',{},[response('NoError',[message('a')]),response('NoError',[message('b','inbox')]),response('ErrorItemNotFound')]) : element('Document',{},[response()]);
  });
  const statuses=await client.emptyDeleted([{id:'a'},{id:'b'},{id:'c'}],'trash');
  assert.equal(statuses[0].ok,true); assert.equal(statuses[1].skipped,true); assert.equal(statuses[2].skipped,true);
  assert.match(calls[1][1], /DeleteType="SoftDelete"/);
  assert.match(calls[1][1], /Id="a"/); assert.doesNotMatch(calls[1][1], /Id="[bc]"/);
});
test('권한 실패와 삭제 부분 실패를 구분하고 확인 후 중단하면 삭제하지 않는다', async () => {
  let deleted=0;
  const client=clientWith(async op=> op==='GetItem' ? element('Document',{},[response('NoError',[message('a')]),response('ErrorAccessDenied')]) : (deleted++,element('Document',{},[response('ErrorAccessDenied')])));
  const statuses=await client.emptyDeleted([{id:'a'},{id:'b'}],'trash');
  assert.equal(statuses.every(x=>!x.ok),true); assert.equal(deleted,1);
  assert.equal(await client.emptyDeleted([{id:'a'},{id:'b'}],'trash',()=>true),null);
  assert.equal(deleted,1);
});
test('개별 삭제 응답 누락을 성공으로 간주하지 않는다', () => {
  const statuses=core.statusesFor(element('Document',{},[response()]),2);
  assert.equal(statuses[0].ok,true); assert.equal(statuses[1].ok,false);
});

