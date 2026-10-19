'use strict';
const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const path=require('node:path');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try {
 const page=await browser.newPage({viewport:{width:1200,height:900}});
 const errors=[];page.on('pageerror',e=>errors.push(e.message));
 let mails=[{id:'a',size:1024,date:'2026-09-02'},{id:'b',size:2097152,date:'2026-09-01'}];let deleted=[];let failSearch=false;
 const folder=id=>`<t:Folder><t:FolderId Id="${id}"/><t:DisplayName>${id==='trash'?'지운 편지함':'받은 편지함'}</t:DisplayName><t:FolderClass>IPF.Note</t:FolderClass></t:Folder>`;
 const mail=m=>`<t:Message><t:ItemId Id="${m.id}"/><t:ParentFolderId Id="trash"/><t:ItemClass>IPM.Note</t:ItemClass><t:Subject>테스트 ${m.id}</t:Subject><t:DateTimeReceived>${m.date}T00:00:00Z</t:DateTimeReceived><t:Size>${m.size}</t:Size></t:Message>`;
 const ok=(op,body)=>`<m:${op}ResponseMessage ResponseClass="Success"><m:ResponseCode>NoError</m:ResponseCode>${body}</m:${op}ResponseMessage>`;
 await page.route('https://mail.apsfamily.com/**',async route=>{
  const req=route.request(); if(req.method()!=='POST')return route.fulfill({contentType:'text/html; charset=utf-8',body:'<html><head></head><body>OWA 테스트</body></html>'});
  const body=req.postData();let xml;
  if(body.includes('<m:GetFolder>'))xml=ok('GetFolder',`<m:Folders>${folder(body.includes('deleteditems')?'trash':'inbox')}</m:Folders>`);
  else if(body.includes('<m:FindFolder'))xml=ok('FindFolder',`<m:RootFolder IncludesLastItemInRange="true"><t:Folders>${folder('inbox')}${folder('trash')}</t:Folders></m:RootFolder>`);
  else if(body.includes('<m:FindItem')){
    if(failSearch)return route.fulfill({status:500,body:'test failure'});
    xml=ok('FindItem',`<m:RootFolder IncludesLastItemInRange="true" TotalItemsInView="${mails.length}"><t:Items>${mails.map(mail).join('')}</t:Items></m:RootFolder>`);
  }else if(body.includes('<m:GetItem>'))xml=[...body.matchAll(/<t:ItemId Id="([^"]+)"/g)].map(x=>ok('GetItem',`<m:Items>${mail(mails.find(m=>m.id===x[1]))}</m:Items>`)).join('');
  else if(body.includes('<m:DeleteItem')){const ids=[...body.matchAll(/<t:ItemId Id="([^"]+)"/g)].map(x=>x[1]);deleted.push(...ids);mails=mails.filter(m=>!ids.includes(m.id));xml=ids.map(()=>ok('DeleteItem','')).join('');}
  else throw Error('Unexpected request');
  await route.fulfill({contentType:'text/xml',body:`<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages" xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"><s:Body>${xml}</s:Body></s:Envelope>`});
 });
 await page.goto('https://mail.apsfamily.com/owa/');
 await page.addScriptTag({path:path.resolve('Outlook_Mail_Cleaner.user.js')});
 await page.click('.omc-launch');await page.waitForFunction(()=>!document.querySelector('input[value="emptyDeleted"]').disabled);
 await page.check('input[value="emptyDeleted"]');
 assert.equal(await page.inputValue('#omc-folder'),'trash'); assert.equal(await page.isDisabled('#omc-folder'),true);
 assert.equal(await page.inputValue('#omc-keyword'),'');assert.notEqual(await page.inputValue('#omc-start'),'');assert.notEqual(await page.inputValue('#omc-end'),'');
 await page.click('#omc-search');await page.waitForFunction(()=>!document.querySelector('#omc-search').disabled);
 assert.equal(await page.locator('.omc-select-row:checked').count(),0);
 assert.equal(await page.locator('#omc-tbody tr').first().getAttribute('data-id'),'b');
 await page.locator('tr[data-id="a"] input').check();await page.selectOption('#omc-sort','sizeDesc');
 assert.equal(await page.locator('tr[data-id="a"] input').isChecked(),true);
 await page.screenshot({path:'ui-v1.1.png',fullPage:true});
 await page.fill('#omc-keyword','변경'); assert.equal(await page.isDisabled('#omc-run'),true);assert.equal(await page.isDisabled('#omc-empty-all'),true);
 await page.fill('#omc-keyword','');await page.click('#omc-search');await page.waitForFunction(()=>!document.querySelector('#omc-search').disabled);
 await page.locator('tr[data-id="a"] input').check();page.on('dialog',d=>d.accept());
 await page.click('#omc-run');await page.waitForFunction(()=>!document.querySelector('#omc-search').disabled);
 assert.deepEqual(deleted,['a']);assert.equal(await page.locator('#omc-tbody tr[data-id]').count(),1);
 await page.click('#omc-empty-all');await page.waitForFunction(()=>!document.querySelector('#omc-search').disabled);
 assert.deepEqual(deleted,['a','b']);assert.equal(await page.isDisabled('#omc-empty-all'),true);
 failSearch=true;await page.click('#omc-search');await page.waitForFunction(()=>!document.querySelector('#omc-search').disabled);
 assert.equal(await page.isDisabled('#omc-empty-all'),true);
 await page.check('input[value="markRead"]');assert.equal(await page.inputValue('#omc-keyword'),'일보');assert.equal(await page.isDisabled('#omc-folder'),false);
 assert.deepEqual(errors,[]);console.log('PASS: browser UI, sorting, selection, invalidation, selected/all deletion, failed search, mode restoration');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
