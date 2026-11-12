// Offline contract tests. Parsed XML is represented by DOM fixtures; no live server or XML parser is used.
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '..', 'probe.js'), 'utf8');
function el(name, attrs = {}, children = [], text = '') {
  return { children, textContent: text, getAttribute: key => attrs[key] ?? null,
    getElementsByTagNameNS: (_, wanted) => children.flatMap(c => [ ...(c.name === wanted ? [c] : []), ...c.getElementsByTagNameNS('*', wanted) ]), name };
}
const field = (name, text) => el(name, {}, [], text);
const doc = (...children) => el('Document', {}, [field('ResponseCode', 'NoError'), ...children]);
const folder = (id, name='Inbox') => el('Folder', {}, [el('FolderId', {Id:id}), field('DisplayName',name), field('FolderClass','IPF.Note')]);
const page = (last, offset, children) => el('RootFolder', {IncludesLastItemInRange:String(last), IndexedPagingOffset:String(offset)}, children);
const ok = (body='fixture', parsed=doc()) => ({status:200,ok:true,type:'basic',text:async()=>body,parsed});
async function run({mode='folders', id='', origin='https://mail.apsfamily.com', pathname='/owa/', password=false, responses=[]}={}) {
  const requests=[]; const window={}; let active;
  const context={window, location:{origin,pathname}, document:{querySelector:()=>password?{}:null},
    performance:{now:()=>0}, AbortSignal:{timeout:ms=>({milliseconds:ms})},
    DOMParser:class {parseFromString(){return active.parsed ?? el('Document',{},[el('parsererror')]);}},
    fetch:async(url,options)=>{requests.push({url,options}); active=responses.shift(); if(active instanceof Error) throw active; if(!active) throw Error('Unexpected fetch'); return active;}};
  vm.runInNewContext(source.replace('__KEY__',JSON.stringify('result')).replace('__MODE__',JSON.stringify(mode)).replace('__ID__',JSON.stringify(id)),context);
  for(let i=0;i<10 && !window.result;i++) await new Promise(setImmediate);
  assert.ok(window.result,'probe must settle');
  return {result:JSON.parse(JSON.stringify(window.result)),requests};
}
for(const status of [401,403,440,500]) test('HTTP '+status+' is not success',async()=>{
  const {result}=await run({responses:[{status,ok:false,text:async()=>''}]});
  assert.equal(result.ok,false); assert.match(result.error,new RegExp('HTTP_'+status));
});
test('manual redirect is authentication-related failure',async()=>{
  const {result}=await run({responses:[{status:0,type:'opaqueredirect'}]}); assert.match(result.error,/LOGIN_REDIRECT/);
});
test('HTML login page without doctype is detected',async()=>{
  const {result}=await run({responses:[ok('<html lang="ko"><body>Login</body></html>')]}); assert.equal(result.error,'LOGIN_PAGE_RETURNED');
});
for(const [name,expected] of [['TypeError','NETWORK_ERROR'],['TimeoutError','TIMEOUT']]) test(name+' has distinct classification',async()=>{
  const error=new Error('private diagnostic must not escape'); error.name=name;
  const {result}=await run({responses:[error]}); assert.equal(result.error,expected); assert.ok(!JSON.stringify(result).includes('private diagnostic'));
});
for(const options of [{origin:'https://evil.example'}, {pathname:'/owa/auth/logon.aspx'}, {password:true}]) test('untrusted or logged-out document sends no request '+JSON.stringify(options),async()=>{
  const {result,requests}=await run(options); assert.equal(result.error,'LOGIN_REQUIRED'); assert.equal(requests.length,0);
});
test('invalid XML is rejected',async()=>{
  const {result}=await run({responses:[ok('invalid',el('Document',{},[el('parsererror')]))]}); assert.equal(result.error,'INVALID_XML');
});
test('SOAP access denied is rejected even with HTTP 200',async()=>{
  const {result}=await run({responses:[ok('fault',el('Document',{},[field('ResponseCode','ErrorAccessDenied')]))]}); assert.equal(result.error,'EWS_ErrorAccessDenied');
});
test('folder paging is complete and duplicates are removed',async()=>{
  const {result,requests}=await run({responses:[ok('inbox',doc(el('Folders',{},[folder('inbox')]))),ok('p1',doc(page(false,100,[el('Folders',{},[folder('inbox'),folder('a')])]))),ok('p2',doc(page(true,0,[el('Folders',{},[folder('b')])])))]});
  assert.equal(result.ok,true); assert.equal(result.folders.length,3); assert.equal(requests.length,3);
  assert.match(requests[2].options.body,/Offset="100"/);
  for(const r of requests){assert.equal(r.url,'/EWS/Exchange.asmx');assert.equal(r.options.credentials,'include');assert.equal(r.options.redirect,'manual');assert.doesNotMatch(r.options.body,/<m:(Update|Delete|Move|Send)/);}
});
test('repeated folder offset cannot be reported complete',async()=>{
  const {result}=await run({responses:[ok('inbox',doc(el('Folders',{},[folder('inbox')]))),ok('p',doc(page(false,0,[el('Folders')])))]}); assert.equal(result.error,'PAGING_ERROR');
});
test('message query requests at most five metadata records and escapes folder ID',async()=>{
  const message=el('Message',{},[field('Subject','sample'),field('IsRead','false')]);
  const {result,requests}=await run({mode:'items',id:'a"<&',responses:[ok('items',doc(page(true,0,[el('Items',{},[message])])))]});
  assert.equal(result.ok,true);assert.equal(result.items.length,1);assert.equal(result.items[0].read,'false');
  assert.match(requests[0].options.body,/MaxEntriesReturned="5"/);assert.match(requests[0].options.body,/a&quot;&lt;&amp;/);
  assert.doesNotMatch(requests[0].options.body,/item:Body|GetItem|SetItemField|SendItem|UpdateItem/);
});
test('valid empty folder returns zero items',async()=>{
  const {result}=await run({mode:'items',responses:[ok('empty',doc(page(true,0,[el('Items')])))]});assert.equal(result.ok,true);assert.equal(result.items.length,0);
});
test('missing FindItem result structure is not an empty-folder success',async()=>{
  const {result}=await run({mode:'items',responses:[ok('missing',doc())]});assert.equal(result.ok,false);assert.equal(result.error,'INVALID_PAGE');
});
