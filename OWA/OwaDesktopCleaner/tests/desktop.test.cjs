const test = require('node:test');
const assert = require('node:assert/strict');
const { JSDOM } = require('jsdom');
const fs = require('node:fs');
const path = require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../cleaner.js'),'utf8');
const writes=['UpdateItem','DeleteItem','MoveItem','DeleteAttachment'];
const esc=s=>String(s).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('"','&quot;');
const folder=id=>'<t:Folder><t:FolderId Id="'+id+'"/><t:DisplayName>'+id+'</t:DisplayName><t:FolderClass>IPF.Note</t:FolderClass><t:TotalCount>2</t:TotalCount></t:Folder>';
const envelope=(op,content,code='NoError')=>'<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages" xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"><s:Body><m:'+op+'Response><m:ResponseMessages><m:'+op+'ResponseMessage ResponseClass="'+(code==='NoError'?'Success':'Error')+'"><m:ResponseCode>'+code+'</m:ResponseCode>'+content+'</m:'+op+'ResponseMessage></m:ResponseMessages></m:'+op+'Response></s:Body></s:Envelope>';
async function until(fn){for(let i=0;i<100;i++){if(fn())return;await new Promise(setImmediate);}throw Error('condition not reached');}
function fixture(options={}) {
 const dom=new JSDOM('<!doctype html><html><head></head><body><div id="owa">OWA</div></body></html>',{url:'https://mail.apsfamily.com/owa/',runScripts:'outside-only',pretendToBeVisual:true});
 const w=dom.window,calls=[];
 let messages=[{id:'m1',parent:options.parent || 'inbox',subject:'일보 A',read:false},{id:'m2',parent:options.parent || 'inbox',subject:'일보 B',read:false}];
 w.confirm=()=>options.confirm!==false;
 w.AbortSignal=AbortSignal;w.AbortController=AbortController;
 const message=m=>'<t:Message><t:ItemId Id="'+m.id+'" ChangeKey="fresh"/><t:ParentFolderId Id="'+m.parent+'"/><t:ItemClass>IPM.Note</t:ItemClass><t:Subject>'+esc(m.subject)+'</t:Subject><t:DateTimeReceived>'+new Date().toISOString()+'</t:DateTimeReceived><t:IsRead>'+m.read+'</t:IsRead><t:Size>2048</t:Size><t:HasAttachments>'+(options.attachments?'true':'false')+'</t:HasAttachments><t:From><t:Mailbox><t:Name>Fixture</t:Name></t:Mailbox></t:From>'+(options.attachments?'<t:Attachments><t:FileAttachment><t:AttachmentId Id="file-'+m.id+'"/><t:Name>a.txt</t:Name><t:IsInline>false</t:IsInline></t:FileAttachment><t:FileAttachment><t:AttachmentId Id="cid-'+m.id+'"/><t:Name>image.png</t:Name><t:IsInline>false</t:IsInline><t:ContentId>inline-image</t:ContentId></t:FileAttachment></t:Attachments>':'')+'</t:Message>';
 w.fetch=async(url,request)=>{
  const op=request.headers.SOAPAction.split('/').pop();calls.push({op,body:request.body,request,url});
  if(options.malformed===op)return {status:200,ok:true,type:'basic',url,text:async()=>'<root><ResponseCode>NoError</ResponseCode></root>'};
  if(options.fail===op)return {status:401,ok:false,type:'basic',url,text:async()=>''};
  if(options.throwOn===op)throw new TypeError('offline');
  let content='';
  if(op==='GetFolder')content='<m:Folders>'+folder(request.body.includes('Id="deleteditems"')?'trash':'inbox')+'</m:Folders>';
  if(op==='FindFolder')content='<m:RootFolder IncludesLastItemInRange="true"><t:Folders>'+folder('inbox')+folder('trash')+'</t:Folders></m:RootFolder>';
  if(op==='FindItem')content='<m:RootFolder IncludesLastItemInRange="true" TotalItemsInView="2"><t:Items>'+messages.map(message).join('')+'</t:Items></m:RootFolder>';
  if(op==='GetItem'){
    let selected=messages.filter(m=>request.body.includes('Id="'+m.id+'"'));
    if(options.moved)selected=selected.map(m=>({...m,parent:'elsewhere'}));
    const responses=selected.map(m=>envelope(op,'<m:Items>'+message(m)+'</m:Items>'));
    const xml=responses.length?responses[0].replace('</m:ResponseMessages>',responses.slice(1).map(r=>r.match(/<m:GetItemResponseMessage .*?<\/m:GetItemResponseMessage>/s)[0]).join('')+'</m:ResponseMessages>'):envelope(op,'<m:Items/>','ErrorItemNotFound');
    return {status:200,ok:true,type:'basic',url,text:async()=>xml};
  }
  if(options.stopOn===op)w.owaDesktop.stop();
  return {status:200,ok:true,type:'basic',url,text:async()=>envelope(op,content)};
 };
 w.eval(source);
 return {w,dom,calls,options,ready:()=>until(()=>!w.owaDesktop.status().busy),close:()=>dom.window.close()};
}
async function search(f,mode='inspect') {
 const radio=f.w.document.querySelector('input[value="'+mode+'"]');radio.checked=true;radio.dispatchEvent(new f.w.Event('change',{bubbles:true}));
 f.w.document.querySelector('#omc-search').click();await until(()=>!f.w.owaDesktop.status().busy);
}
async function execute(f){f.w.document.querySelector('#omc-all').click();f.w.document.querySelector('#omc-run').click();await until(()=>!f.w.owaDesktop.status().busy);}

test('desktop connects, searches without changing mail and renders unselected rows',async()=>{
 const f=fixture();try{await f.ready();assert.equal(f.w.owaDesktop.status().connected,true);await search(f);
 assert.equal(f.w.owaDesktop.status().writeEnabled,true);
 assert.equal(f.w.document.querySelectorAll('.omc-select-row').length,2);
 assert.equal(f.w.document.querySelectorAll('.omc-select-row:checked').length,0);
 assert.equal(f.w.document.querySelector('#omc-run').disabled,true);
 assert.equal(f.calls.some(c=>writes.includes(c.op)),false);
 for(const c of f.calls){assert.equal(c.request.credentials,'include');assert.equal(c.request.redirect,'manual');}
 }finally{f.close();}
});
test('write permission required and changed criteria invalidate selection',async()=>{
 const f=fixture();try{await f.ready();f.w.owaDesktop.setWriteEnabled(false);await search(f,'markRead');f.w.document.querySelector('#omc-all').click();assert.equal(f.w.document.querySelector('#omc-run').disabled,true);
 assert.equal(f.w.owaDesktop.setWriteEnabled(true),true);assert.equal(f.w.document.querySelector('#omc-run').disabled,false);
 f.w.document.querySelector('#omc-keyword').value='changed';f.w.document.querySelector('#omc-keyword').dispatchEvent(new f.w.Event('input'));
 assert.equal(f.w.document.querySelector('#omc-run').disabled,true);assert.equal(f.calls.some(c=>writes.includes(c.op)),false);
 }finally{f.close();}
});

test('execution guidance follows permission, mode, search and selection without sending writes',async()=>{
 const f=fixture();try{
 await f.ready();await search(f);
 const d=f.w.document,run=d.querySelector('#omc-run'),hint=()=>d.querySelector('#omc-run-hint').textContent;
 assert.match(hint(),/조회만/);assert.equal(run.disabled,true);
 f.w.owaDesktop.setWriteEnabled(false);assert.match(hint(),/변경 작업 허용/);assert.equal(run.disabled,true);
 f.w.owaDesktop.setWriteEnabled(true);assert.match(hint(),/조회만/);assert.equal(run.disabled,true);
 const mode=d.querySelector('input[value="markRead"]');mode.checked=true;mode.dispatchEvent(new f.w.Event('change',{bubbles:true}));
 assert.match(hint(),/다시 검색/);assert.equal(run.disabled,true);
 await search(f,'markRead');assert.match(hint(),/체크박스/);assert.equal(run.disabled,true);
 d.querySelector('#omc-all').click();assert.equal(run.disabled,false);assert.match(hint(),/실행할 수 있습니다/);
 f.w.owaDesktop.setWriteEnabled(false);assert.equal(run.disabled,true);assert.match(hint(),/변경 작업 허용/);
 assert.equal(f.calls.some(c=>writes.includes(c.op)),false);
 }finally{f.close();}
});
test('mark-read revalidates each item and suppresses receipts; report is retained',async()=>{
 const f=fixture();try{await f.ready();await search(f,'markRead');await execute(f);
 const changes=f.calls.filter(c=>c.op==='UpdateItem');assert.equal(changes.length,2);
 for(const c of changes){assert.match(c.body,/SuppressReadReceipts="true"/);assert.match(c.body,/ChangeKey="fresh"/);}
 assert.match(f.w.document.querySelector('#omc-report').textContent,/성공/);
 assert.equal(f.w.document.querySelectorAll('.omc-select-row').length,0);
 }finally{f.close();}
});
test('moved mail is skipped after fresh parent check',async()=>{
 const f=fixture({moved:true});try{await f.ready();await search(f,'markRead');f.w.owaDesktop.setWriteEnabled(true);await execute(f);assert.equal(f.calls.some(c=>writes.includes(c.op)),false);assert.match(f.w.document.querySelector('#omc-report').textContent,/건너뜀/);}finally{f.close();}
});

test('recent report starts collapsed, scrolls separately and hides immediately on a new search',async()=>{
 const f=fixture();try{
 await f.ready();await search(f,'markRead');await execute(f);
 const d=f.w.document,report=d.querySelector('#omc-report'),details=report.querySelector('details');
 assert.equal(report.classList.contains('omc-body'),false);
 assert.equal(report.classList.contains('omc-hidden'),false);assert.equal(details.open,false);
 assert.match(d.querySelector('#omc-report-count').textContent,/2건/);
 details.open=true;assert.match(report.textContent,/성공/);
 d.querySelector('#omc-search').click();
 assert.equal(report.classList.contains('omc-hidden'),true);assert.equal(details.open,false);
 await until(()=>!f.w.owaDesktop.status().busy);
 assert.equal(report.classList.contains('omc-hidden'),true);
 assert.equal(d.querySelectorAll('.omc-select-row').length,2);
 }finally{f.close();}
});
test('mutation network error stops and never retries or continues',async()=>{
 const f=fixture({throwOn:'UpdateItem'});try{await f.ready();await search(f,'markRead');f.w.owaDesktop.setWriteEnabled(true);await execute(f);
 assert.equal(f.calls.filter(c=>c.op==='UpdateItem').length,1);const report=f.w.document.querySelector('#omc-report').textContent;assert.match(report,/결과 미확인/);assert.match(report,/미처리/);
 }finally{f.close();}
});
test('authentication expiry revokes write permission and stops work',async()=>{
 const f=fixture({fail:'UpdateItem'});try{await f.ready();await search(f,'markRead');f.w.owaDesktop.setWriteEnabled(true);await execute(f);
 assert.equal(f.w.owaDesktop.status().authRequired,true);assert.equal(f.w.owaDesktop.status().writeEnabled,false);assert.equal(f.w.owaDesktop.setWriteEnabled(true),false);assert.equal(f.calls.filter(c=>c.op==='UpdateItem').length,1);
 }finally{f.close();}
});
test('cancelled confirmation issues no mutation',async()=>{
 const f=fixture({confirm:false});try{await f.ready();await search(f,'markRead');f.w.owaDesktop.setWriteEnabled(true);await execute(f);assert.equal(f.calls.some(c=>writes.includes(c.op)),false);}finally{f.close();}
});
test('stop after current operation leaves remaining mail untouched',async()=>{
 const f=fixture({stopOn:'UpdateItem'});try{await f.ready();await search(f,'markRead');f.w.owaDesktop.setWriteEnabled(true);await execute(f);assert.equal(f.calls.filter(c=>c.op==='UpdateItem').length,1);assert.match(f.w.document.querySelector('#omc-report').textContent,/미처리/);}finally{f.close();}
});
test('CID inline image is preserved even when IsInline is false',async()=>{
 const f=fixture({attachments:true});try{await f.ready();await search(f,'removeAttachments');f.w.owaDesktop.setWriteEnabled(true);await execute(f);const changes=f.calls.filter(c=>c.op==='DeleteAttachment');assert.equal(changes.length,2);for(const c of changes){assert.match(c.body,/file-m/);assert.doesNotMatch(c.body,/cid-m/);}}finally{f.close();}
});


test('move operation targets deleteditems only',async()=>{
 const f=fixture();try{await f.ready();await search(f,'moveToDeleted');f.w.owaDesktop.setWriteEnabled(true);await execute(f);const changes=f.calls.filter(c=>c.op==='MoveItem');assert.equal(changes.length,2);for(const c of changes)assert.match(c.body,/<t:DistinguishedFolderId Id="deleteditems"\/>/);}finally{f.close();}
});
test('trash cleanup uses SoftDelete on refreshed trash messages only',async()=>{
 const f=fixture({parent:'trash'});try{await f.ready();await search(f,'emptyDeleted');f.w.owaDesktop.setWriteEnabled(true);await execute(f);const changes=f.calls.filter(c=>c.op==='DeleteItem');assert.equal(changes.length,2);for(const c of changes){assert.match(c.body,/DeleteType="SoftDelete"/);assert.match(c.body,/SendMeetingCancellations="SendToNone"/);assert.doesNotMatch(c.body,/HardDelete/);}}finally{f.close();}
});
test('failed initial authentication never enables cleaner writes',async()=>{
 const f=fixture({fail:'GetFolder'});try{await f.ready();assert.equal(f.w.owaDesktop.status().authRequired,true);assert.equal(f.w.owaDesktop.status().connected,false);assert.equal(f.w.owaDesktop.setWriteEnabled(true),false);}finally{f.close();}
});

test('malformed success document cannot connect the app',async()=>{
 const f=fixture({malformed:'GetFolder'});try{await f.ready();assert.equal(f.w.owaDesktop.status().connected,false);assert.equal(f.w.document.querySelector('#omc-search').disabled,true);}finally{f.close();}
});
test('inspection permits empty title but mutation search remains constrained',()=>{
 const core=require('../cleaner.js');const c={folderId:'inbox',keyword:'',startDate:'2026-10-01',endDate:'2026-10-19',mode:'inspect'};
 assert.equal(core.validateCriteria(c),'');assert.notEqual(core.validateCriteria({...c,mode:'markRead'}),'');
 assert.throws(()=>core.requireOperation('UpdateItem'),/허용/);assert.throws(()=>core.requireOperation('SendItem'),/지원/);
});
