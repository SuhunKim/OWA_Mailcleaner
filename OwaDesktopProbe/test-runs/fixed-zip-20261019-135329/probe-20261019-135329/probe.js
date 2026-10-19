(() => {
const key=__KEY__, mode=__MODE__, id=__ID__, evidence=[], deadline=Date.now()+240000;
(async()=>{
const ns='http://schemas.microsoft.com/exchange/services/2006/', t=ns+'types', m=ns+'messages';
const esc=s=>s.replace(/[<>&"']/g,c=>({'<':'&lt;','>':'&gt;','&':'&amp;','"':'&quot;',"'":'&apos;'}[c]));
const all=(n,name)=>Array.from(n.getElementsByTagNameNS('*',name));
const val=(n,name)=>all(n,name)[0]?.textContent || '';
if(location.origin!=='https://mail.apsfamily.com' || !location.pathname.toLowerCase().startsWith('/owa') || location.pathname.toLowerCase().includes('/auth/') || document.querySelector('input[type=password]')) throw Error('LOGIN_REQUIRED');
async function call(op,body) {
 if(Date.now()>=deadline) throw Error('TIMEOUT'); const start=performance.now();
 let r;
 try { r=await fetch('/EWS/Exchange.asmx',{method:'POST',credentials:'include',cache:'no-store',redirect:'manual',headers:{'Content-Type':'text/xml; charset=utf-8',SOAPAction:m+'/'+op},body:'<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" xmlns:t="'+t+'" xmlns:m="'+m+'"><s:Header><t:RequestServerVersion Version="Exchange2013_SP1"/></s:Header><s:Body>'+body+'</s:Body></s:Envelope>',signal:AbortSignal.timeout(Math.max(1,Math.min(180000,deadline-Date.now())))}); }
 catch(e) { throw Error(e.name==='TimeoutError'?'TIMEOUT':'NETWORK_ERROR'); }
 evidence.push(op+' HTTP '+r.status+' · '+Math.round(performance.now()-start)+'ms');
 if(r.type==='opaqueredirect') throw Error('LOGIN_REDIRECT: 재로그인 필요');
 if([401,403,440].includes(r.status)) throw Error('HTTP_'+r.status+(r.status===440?' SESSION_EXPIRED':' 인증/접근 거부'));
 const text=await r.text();
 if(/<!doctype html|<html[\s>]/i.test(text)) throw Error('LOGIN_PAGE_RETURNED');
 if(!r.ok) throw Error('HTTP_'+r.status);
 const doc=new DOMParser().parseFromString(text,'text/xml');
 if(all(doc,'parsererror').length) throw Error('INVALID_XML');
 const codes=all(doc,'ResponseCode').map(n=>n.textContent);
 if(!codes.length || codes.some(c=>c!=='NoError')) throw Error('EWS_'+(codes.filter(c=>/^[A-Za-z0-9_]+$/.test(c)).join(',')||'INVALID_RESPONSE'));
 evidence.push(op+' SOAP '+codes.join(','));
 return doc;
}
const shape='<m:FolderShape><t:BaseShape>Default</t:BaseShape><t:AdditionalProperties><t:FieldURI FieldURI="folder:FolderClass"/></t:AdditionalProperties></m:FolderShape>';
if(mode==='folders'){
 const inbox=await call('GetFolder','<m:GetFolder>'+shape+'<m:FolderIds><t:DistinguishedFolderId Id="inbox"/></m:FolderIds></m:GetFolder>');
 const folders=new Map();
 function collect(doc) { for(const f of all(doc,'Folders').flatMap(n=>Array.from(n.children))) { const id=all(f,'FolderId')[0]?.getAttribute('Id'); if(id && (val(f,'FolderClass').startsWith('IPF.Note') || !val(f,'FolderClass'))) folders.set(id,{id,name:val(f,'DisplayName')}); } }
 collect(inbox); if(!folders.size) throw Error('INBOX_NOT_FOUND');
 let offset=0,done=false;
 for(let page=0;page<100;page++){
 const doc=await call('FindFolder','<m:FindFolder Traversal="Deep">'+shape+'<m:IndexedPageFolderView MaxEntriesReturned="100" Offset="'+offset+'" BasePoint="Beginning"/><m:ParentFolderIds><t:DistinguishedFolderId Id="msgfolderroot"/></m:ParentFolderIds></m:FindFolder>');
 collect(doc);
 const root=all(doc,'RootFolder')[0]; if(!root) throw Error('INVALID_PAGE');
 if(root.getAttribute('IncludesLastItemInRange')==='true'){done=true;break;}
 const next=Number(root.getAttribute('IndexedPagingOffset')); if(next<=offset) throw Error('PAGING_ERROR'); offset=next;
 }
 if(!done) throw Error('FOLDER_LIMIT: 전체 폴더 미완료');
 evidence.push('실제 메일 폴더 '+folders.size+'개 · 전체 페이지 완료');
 return {ok:true,folders:[...folders.values()],evidence};
}
if(mode==='items'){
 const props=['item:Subject','item:DateTimeReceived','item:Size','message:From','message:IsRead'].map(p=>'<t:FieldURI FieldURI="'+p+'"/>').join('');
 const doc=await call('FindItem','<m:FindItem Traversal="Shallow"><m:ItemShape><t:BaseShape>IdOnly</t:BaseShape><t:AdditionalProperties>'+props+'</t:AdditionalProperties></m:ItemShape><m:IndexedPageItemView MaxEntriesReturned="5" Offset="0" BasePoint="Beginning"/><m:SortOrder><t:FieldOrder Order="Descending"><t:FieldURI FieldURI="item:DateTimeReceived"/></t:FieldOrder></m:SortOrder><m:ParentFolderIds><t:FolderId Id="'+esc(id)+'"/></m:ParentFolderIds></m:FindItem>');
 const root=all(doc,'RootFolder')[0];
 if(!root || all(root,'Items').length!==1) throw Error('INVALID_PAGE');
 const items=all(root,'Items').flatMap(n=>Array.from(n.children)).map(n=>({subject:val(n,'Subject'),from:val(n,'Name')||val(n,'EmailAddress'),received:val(n,'DateTimeReceived'),size:val(n,'Size'),read:val(n,'IsRead')}));
 evidence.push('FindItem 실제 메타데이터 '+items.length+'건 (최대 5)');
 return {ok:true,items,evidence};
}
throw Error('INVALID_OPERATION');
})().then(r=>window[key]=r).catch(e=>window[key]={ok:false,error:e.message,evidence});
})();


