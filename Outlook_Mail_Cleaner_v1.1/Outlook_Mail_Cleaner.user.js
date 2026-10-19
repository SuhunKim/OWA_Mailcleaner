// ==UserScript==
// @name         OWA 메일 클리너
// @namespace    apsystems-owa-mail-cleaner
// @version      1.1.0
// @description  사내 Outlook Web App에서 메일을 미리 확인한 뒤 첨부 삭제, 읽음 처리, 휴지통 이동 및 휴지통 정리를 수행합니다.
// @match        https://mail.apsfamily.com/owa/*
// @match        https://mail.apsfamily.com/OWA/*
// @grant        none
// @run-at       document-idle
// @noframes
// ==/UserScript==

(() => {
  'use strict';

  const VERSION = '1.1.0';
  const APP_NAME = '메일 클리너';
  const EWS_PATH = '/EWS/Exchange.asmx';
  const PAGE_SIZE = 100;
  const BATCH_SIZE = 20;
  const KST_OFFSET_MS = 9 * 60 * 60 * 1000;
  const POSITION_KEY = 'owa-mail-cleaner-position:v1';
  const NS = {
    soap: 'http://schemas.xmlsoap.org/soap/envelope/',
    m: 'http://schemas.microsoft.com/exchange/services/2006/messages',
    t: 'http://schemas.microsoft.com/exchange/services/2006/types',
  };

  class OwaMailCleanerError extends Error {
    constructor(message, code = 'UNKNOWN', details = '') {
      super(message);
      this.name = 'OwaMailCleanerError';
      this.code = code;
      this.details = details;
    }
  }

  function xmlEscape(value) {
    return String(value ?? '').replace(/[&<>"']/g, character => ({
      '&': '&amp;',
      '<': '&lt;',
      '>': '&gt;',
      '"': '&quot;',
      "'": '&apos;',
    })[character]);
  }

  function htmlEscape(value) {
    return String(value ?? '').replace(/[&<>"']/g, character => ({
      '&': '&amp;',
      '<': '&lt;',
      '>': '&gt;',
      '"': '&quot;',
      "'": '&#39;',
    })[character]);
  }

  function chunk(items, size = BATCH_SIZE) {
    const result = [];
    for (let index = 0; index < items.length; index += size) {
      result.push(items.slice(index, index + size));
    }
    return result;
  }

  function parseDateParts(dateText) {
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(dateText || ''));
    if (!match) return null;
    const year = Number(match[1]);
    const month = Number(match[2]);
    const day = Number(match[3]);
    const check = new Date(Date.UTC(year, month - 1, day));
    if (check.getUTCFullYear() !== year || check.getUTCMonth() !== month - 1 || check.getUTCDate() !== day) return null;
    return { year, month, day };
  }

  function kstBoundaryUtc(dateText, addDays = 0) {
    const parts = parseDateParts(dateText);
    if (!parts) throw new OwaMailCleanerError('날짜 형식이 올바르지 않습니다.', 'INVALID_DATE');
    return new Date(Date.UTC(parts.year, parts.month - 1, parts.day + addDays) - KST_OFFSET_MS).toISOString();
  }

  function kstDateInput(offsetDays = 0, now = Date.now()) {
    const shifted = new Date(now + KST_OFFSET_MS);
    return new Date(Date.UTC(
      shifted.getUTCFullYear(),
      shifted.getUTCMonth(),
      shifted.getUTCDate() + offsetDays,
    )).toISOString().slice(0, 10);
  }

  function validateCriteria(criteria) {
    if (criteria.mode !== 'emptyDeleted' && !String(criteria.keyword || '').trim()) return '제목 키워드를 입력해 주세요.';
    if (!criteria.folderId) return '검색할 폴더를 선택해 주세요.';
    const start = parseDateParts(criteria.startDate);
    const end = parseDateParts(criteria.endDate);
    if (!(criteria.mode === 'emptyDeleted' && !criteria.startDate && !criteria.endDate) && (!start || !end)) return '시작일과 종료일을 확인해 주세요.';
    if (criteria.startDate > criteria.endDate) return '시작일은 종료일보다 늦을 수 없습니다.';
    if (!['removeAttachments', 'markRead', 'moveToDeleted', 'emptyDeleted'].includes(criteria.mode)) return '처리 방식을 선택해 주세요.';
    return '';
  }

  function criteriaSignature(criteria) {
    return JSON.stringify({
      folderId: criteria.folderId,
      keyword: String(criteria.keyword || '').trim(),
      startDate: criteria.startDate,
      endDate: criteria.endDate,
      mode: criteria.mode,
    });
  }

  function isNormalAttachment(attachment) {
    return Boolean(attachment?.id) && attachment.isInline !== true;
  }

  function buildFolderPaths(folders) {
    const byId = new Map(folders.map(folder => [folder.id, folder]));
    const pathFor = folder => {
      const names = [];
      const visited = new Set();
      let current = folder;
      while (current && !visited.has(current.id)) {
        visited.add(current.id);
        if (current.name) names.unshift(current.name);
        current = byId.get(current.parentId);
      }
      return names.join(' / ') || folder.name || '(이름 없는 폴더)';
    };
    return folders
      .map(folder => ({ ...folder, path: pathFor(folder) }))
      .sort((left, right) => left.path.localeCompare(right.path, 'ko'));
  }

  function ewsErrorMessage(code, text = '') {
    const messages = {
      ErrorAccessDenied: '이 작업을 수행할 Exchange 권한이 없습니다.',
      ErrorInvalidChangeKey: '메일이 다른 곳에서 변경되었습니다.',
      ErrorChangeKeyRequired: '메일의 최신 변경 정보가 필요합니다.',
      ErrorInvalidAttachmentId: '첨부파일 정보가 변경되었거나 이미 삭제되었습니다.',
      ErrorItemNotFound: '메일을 찾을 수 없습니다. 이동 또는 삭제되었을 수 있습니다.',
      ErrorFolderNotFound: '선택한 폴더를 찾을 수 없습니다.',
      ErrorServerBusy: 'Exchange 서버가 사용 중입니다. 잠시 후 다시 시도해 주세요.',
      ErrorTimeoutExpired: 'Exchange 서버의 처리 시간이 초과되었습니다.',
      ErrorInvalidIdMalformed: '메일 또는 폴더 식별자가 올바르지 않습니다.',
    };
    return messages[code] || text || code || 'Exchange 요청에 실패했습니다.';
  }

  function isStaleCode(code) {
    return ['ErrorInvalidChangeKey', 'ErrorChangeKeyRequired', 'ErrorInvalidAttachmentId'].includes(code);
  }

  function soapEnvelope(body) {
    return `<?xml version="1.0" encoding="utf-8"?>
<soap:Envelope xmlns:soap="${NS.soap}" xmlns:m="${NS.m}" xmlns:t="${NS.t}">
  <soap:Header><t:RequestServerVersion Version="Exchange2013_SP1"/></soap:Header>
  <soap:Body>${body}</soap:Body>
</soap:Envelope>`;
  }

  function folderIdXml(folderId) {
    return `<t:FolderId Id="${xmlEscape(folderId)}"/>`;
  }

  function itemIdXml(item) {
    const changeKey = item.changeKey ? ` ChangeKey="${xmlEscape(item.changeKey)}"` : '';
    return `<t:ItemId Id="${xmlEscape(item.id)}"${changeKey}/>`;
  }

  function buildGetFolderBody(folder = 'inbox', distinguished = true) {
    const id = distinguished
      ? `<t:DistinguishedFolderId Id="${xmlEscape(folder)}"/>`
      : folderIdXml(folder);
    return `<m:GetFolder>
  <m:FolderShape>
    <t:BaseShape>Default</t:BaseShape>
    <t:AdditionalProperties>
      <t:FieldURI FieldURI="folder:ParentFolderId"/>
      <t:FieldURI FieldURI="folder:FolderClass"/>
    </t:AdditionalProperties>
  </m:FolderShape>
  <m:FolderIds>${id}</m:FolderIds>
</m:GetFolder>`;
  }

  function buildFindFoldersBody(offset = 0) {
    return `<m:FindFolder Traversal="Deep">
  <m:FolderShape>
    <t:BaseShape>Default</t:BaseShape>
    <t:AdditionalProperties>
      <t:FieldURI FieldURI="folder:ParentFolderId"/>
      <t:FieldURI FieldURI="folder:FolderClass"/>
      <t:FieldURI FieldURI="folder:ChildFolderCount"/>
    </t:AdditionalProperties>
  </m:FolderShape>
  <m:IndexedPageFolderView MaxEntriesReturned="500" Offset="${Number(offset)}" BasePoint="Beginning"/>
  <m:ParentFolderIds><t:DistinguishedFolderId Id="msgfolderroot"/></m:ParentFolderIds>
</m:FindFolder>`;
  }

  function buildFindItemsBody(criteria, offset = 0) {
    const filters = [
      criteria.startDate && `<t:IsGreaterThanOrEqualTo><t:FieldURI FieldURI="item:DateTimeReceived"/><t:FieldURIOrConstant><t:Constant Value="${xmlEscape(kstBoundaryUtc(criteria.startDate))}"/></t:FieldURIOrConstant></t:IsGreaterThanOrEqualTo>`,
      criteria.endDate && `<t:IsLessThan><t:FieldURI FieldURI="item:DateTimeReceived"/><t:FieldURIOrConstant><t:Constant Value="${xmlEscape(kstBoundaryUtc(criteria.endDate, 1))}"/></t:FieldURIOrConstant></t:IsLessThan>`,
      String(criteria.keyword || '').trim() && `<t:Contains ContainmentMode="Substring" ContainmentComparison="IgnoreCase"><t:FieldURI FieldURI="item:Subject"/><t:Constant Value="${xmlEscape(String(criteria.keyword || '').trim())}"/></t:Contains>`,
    ].filter(Boolean);
    if (criteria.mode === 'removeAttachments') {
      filters.push('<t:IsEqualTo><t:FieldURI FieldURI="item:HasAttachments"/><t:FieldURIOrConstant><t:Constant Value="true"/></t:FieldURIOrConstant></t:IsEqualTo>');
    }
    if (criteria.mode === 'markRead') {
      filters.push('<t:IsEqualTo><t:FieldURI FieldURI="message:IsRead"/><t:FieldURIOrConstant><t:Constant Value="false"/></t:FieldURIOrConstant></t:IsEqualTo>');
    }
    return `<m:FindItem Traversal="Shallow">
  <m:ItemShape>
    <t:BaseShape>IdOnly</t:BaseShape>
    <t:AdditionalProperties>
      <t:FieldURI FieldURI="item:ItemClass"/>
      <t:FieldURI FieldURI="item:Subject"/>
      <t:FieldURI FieldURI="item:DateTimeReceived"/>
      <t:FieldURI FieldURI="item:Size"/>
      <t:FieldURI FieldURI="item:ParentFolderId"/>
      <t:FieldURI FieldURI="item:HasAttachments"/>
      <t:FieldURI FieldURI="message:IsRead"/>
      <t:FieldURI FieldURI="message:From"/>
    </t:AdditionalProperties>
  </m:ItemShape>
  <m:IndexedPageItemView MaxEntriesReturned="${PAGE_SIZE}" Offset="${Number(offset)}" BasePoint="Beginning"/>
  ${filters.length ? `<m:Restriction>${filters.length === 1 ? filters[0] : `<t:And>${filters.join('')}</t:And>`}</m:Restriction>` : ''}
  <m:SortOrder><t:FieldOrder Order="Descending"><t:FieldURI FieldURI="item:DateTimeReceived"/></t:FieldOrder></m:SortOrder>
  <m:ParentFolderIds>${folderIdXml(criteria.folderId)}</m:ParentFolderIds>
</m:FindItem>`;
  }

  function buildGetItemsBody(items, includeAttachments = true) {
    const attachmentField = includeAttachments ? '<t:FieldURI FieldURI="item:Attachments"/>' : '';
    return `<m:GetItem>
  <m:ItemShape>
    <t:BaseShape>IdOnly</t:BaseShape>
    <t:AdditionalProperties>
      <t:FieldURI FieldURI="item:ItemClass"/>
      <t:FieldURI FieldURI="item:Subject"/>
      <t:FieldURI FieldURI="item:DateTimeReceived"/>
      <t:FieldURI FieldURI="item:Size"/>
      <t:FieldURI FieldURI="item:ParentFolderId"/>
      <t:FieldURI FieldURI="item:HasAttachments"/>
      <t:FieldURI FieldURI="message:IsRead"/>
      <t:FieldURI FieldURI="message:From"/>
      ${attachmentField}
    </t:AdditionalProperties>
  </m:ItemShape>
  <m:ItemIds>${items.map(itemIdXml).join('')}</m:ItemIds>
</m:GetItem>`;
  }

  function buildUpdateReadBody(items) {
    const changes = items.map(item => `<t:ItemChange>
  ${itemIdXml(item)}
  <t:Updates>
    <t:SetItemField>
      <t:FieldURI FieldURI="message:IsRead"/>
      <t:Message><t:IsRead>true</t:IsRead></t:Message>
    </t:SetItemField>
  </t:Updates>
</t:ItemChange>`).join('');
    return `<m:UpdateItem ConflictResolution="AutoResolve" MessageDisposition="SaveOnly" SuppressReadReceipts="true">
  <m:ItemChanges>${changes}</m:ItemChanges>
</m:UpdateItem>`;
  }

  function buildMoveItemsBody(items) {
    return `<m:MoveItem>
  <m:ToFolderId><t:DistinguishedFolderId Id="deleteditems"/></m:ToFolderId>
  <m:ItemIds>${items.map(item => `<t:ItemId Id="${xmlEscape(item.id)}"/>`).join('')}</m:ItemIds>
</m:MoveItem>`;
  }

  function buildDeleteAttachmentsBody(attachments) {
    return `<m:DeleteAttachment>
  <m:AttachmentIds>${attachments.map(attachment => `<t:AttachmentId Id="${xmlEscape(attachment.id)}"/>`).join('')}</m:AttachmentIds>
</m:DeleteAttachment>`;
  }

  function parseSize(value) {
    return value !== '' && Number.isFinite(Number(value)) && Number(value) >= 0 ? Number(value) : null;
  }

  function sortMessages(items, order) {
    const size = order.startsWith('size');
    const descending = order.endsWith('Desc');
    return [...items].sort((a,b) => {
      const av = size ? a.size : Date.parse(a.receivedDateTime);
      const bv = size ? b.size : Date.parse(b.receivedDateTime);
      if (!Number.isFinite(av)) return Number.isFinite(bv) ? 1 : 0;
      if (!Number.isFinite(bv)) return -1;
      return (av - bv) * (descending ? -1 : 1);
    });
  }

  function totalSize(items) {
    const unknown = items.filter(item => !Number.isFinite(item.size)).length;
    return formatSize(items.reduce((sum,item) => sum + (Number.isFinite(item.size) ? item.size : 0), 0))
      + (unknown ? ' (용량 미확인 ' + unknown + '건 제외)' : '');
  }

  function buildDeleteItemsBody(items) {
    return '<m:DeleteItem DeleteType="SoftDelete" SendMeetingCancellations="SendToNone"><m:ItemIds>'
      + items.map(itemIdXml).join('') + '</m:ItemIds></m:DeleteItem>';
  }

  const testApi = {
    parseSize, sortMessages, formatSize, totalSize, buildDeleteItemsBody,
    VERSION,
    xmlEscape,
    chunk,
    parseDateParts,
    kstBoundaryUtc,
    kstDateInput,
    validateCriteria,
    criteriaSignature,
    isNormalAttachment,
    buildFolderPaths,
    ewsErrorMessage,
    isStaleCode,
    soapEnvelope,
    buildGetFolderBody,
    buildFindFoldersBody,
    buildFindItemsBody,
    buildGetItemsBody,
    buildUpdateReadBody,
    buildMoveItemsBody,
    buildDeleteAttachmentsBody,
  };

  const runningInNode = typeof process === 'object' && Boolean(process?.versions?.node) && typeof module === 'object';
  function allElements(node, localName) {
    return Array.from(node?.getElementsByTagName('*') || []).filter(element => element.localName === localName);
  }

  function directChild(node, localName) {
    return Array.from(node?.children || []).find(element => element.localName === localName) || null;
  }

  function nestedText(node, path, fallback = '') {
    let current = node;
    for (const name of path) {
      current = directChild(current, name);
      if (!current) return fallback;
    }
    return current.textContent || fallback;
  }

  function boolText(node, path) {
    return nestedText(node, path, 'false').trim().toLowerCase() === 'true';
  }

  function parseXml(text) {
    const documentNode = new DOMParser().parseFromString(text, 'text/xml');
    const parserError = documentNode.getElementsByTagName('parsererror')[0];
    if (parserError) throw new OwaMailCleanerError('Exchange 응답을 해석하지 못했습니다.', 'INVALID_XML', parserError.textContent || '');
    return documentNode;
  }

  function responseInfos(documentNode) {
    return Array.from(documentNode.getElementsByTagName('*'))
      .filter(element => element.hasAttribute('ResponseClass') && /ResponseMessage$/.test(element.localName))
      .map(element => {
        const code = nestedText(element, ['ResponseCode'], 'NoError');
        const messageText = nestedText(element, ['MessageText'], '');
        const responseClass = element.getAttribute('ResponseClass') || '';
        return {
          ok: responseClass === 'Success' && code === 'NoError',
          responseClass,
          code,
          messageText,
          message: ewsErrorMessage(code, messageText),
          node: element,
        };
      });
  }

  function assertSuccess(documentNode, operation) {
    const failed = responseInfos(documentNode).find(info => !info.ok);
    if (failed) throw new OwaMailCleanerError(`${operation}: ${failed.message}`, failed.code, failed.messageText);
    return documentNode;
  }

  function statusesFor(documentNode, count) {
    const infos = responseInfos(documentNode);
    if (!infos.length) return Array.from({ length: count }, () => ({ ok: false, code: 'EMPTY_RESPONSE', message: 'Exchange 응답 결과가 없습니다.' }));
    if (infos.length === 1 && count > 1 && !infos[0].ok) return Array.from({ length: count }, () => ({ ...infos[0], node: undefined }));
    return Array.from({ length: count }, (_, index) => {
      const info = infos[index] || { ok: false, code: 'MISSING_RESPONSE', message: '개별 처리 응답이 없습니다.' };
      return { ...info, node: undefined };
    });
  }

  function parseFolderElement(element) {
    const idNode = directChild(element, 'FolderId');
    const parentNode = directChild(element, 'ParentFolderId');
    return {
      id: idNode?.getAttribute('Id') || '',
      changeKey: idNode?.getAttribute('ChangeKey') || '',
      parentId: parentNode?.getAttribute('Id') || '',
      name: nestedText(element, ['DisplayName'], '(이름 없는 폴더)'),
      folderClass: nestedText(element, ['FolderClass'], ''),
      totalCount: Number(nestedText(element, ['TotalCount'], '0')) || 0,
    };
  }

  function parseFolders(documentNode) {
    const allowed = new Set(['Folder', 'SearchFolder']);
    return Array.from(documentNode.getElementsByTagName('*'))
      .filter(element => allowed.has(element.localName) && directChild(element, 'FolderId'))
      .map(parseFolderElement)
      .filter(folder => folder.id && (!folder.folderClass || folder.folderClass === 'IPF.Note'));
  }

  function parseMessageElement(element) {
    const idNode = directChild(element, 'ItemId');
    const senderName = nestedText(element, ['From', 'Mailbox', 'Name'], '');
    const senderAddress = nestedText(element, ['From', 'Mailbox', 'EmailAddress'], '');
    return {
      id: idNode?.getAttribute('Id') || '',
      changeKey: idNode?.getAttribute('ChangeKey') || '',
      itemClass: nestedText(element, ['ItemClass'], ''),
      subject: nestedText(element, ['Subject'], '(제목 없음)'),
      size: parseSize(nestedText(element, ['Size'], '')),
      parentId: directChild(element, 'ParentFolderId')?.getAttribute('Id') || '',
      receivedDateTime: nestedText(element, ['DateTimeReceived'], ''),
      hasAttachments: boolText(element, ['HasAttachments']),
      isRead: boolText(element, ['IsRead']),
      sender: senderName || senderAddress || '(발신자 없음)',
      senderAddress,
      attachments: [],
      normalAttachments: [],
    };
  }

  function parseMessages(documentNode) {
    const messages = [];
    for (const itemsNode of allElements(documentNode, 'Items')) {
      for (const element of Array.from(itemsNode.children || [])) {
        if (element.localName === 'Message' && directChild(element, 'ItemId')) messages.push(parseMessageElement(element));
      }
    }
    return messages;
  }

  function parseAttachments(messageElement) {
    const attachmentsNode = directChild(messageElement, 'Attachments');
    if (!attachmentsNode) return [];
    return Array.from(attachmentsNode.children || []).map(element => {
      const idNode = directChild(element, 'AttachmentId');
      return {
        id: idNode?.getAttribute('Id') || '',
        kind: element.localName,
        name: nestedText(element, ['Name'], '(이름 없는 첨부)'),
        size: Number(nestedText(element, ['Size'], '0')) || 0,
        isInline: boolText(element, ['IsInline']),
      };
    }).filter(attachment => attachment.id);
  }

  function parseDetailedMessages(documentNode) {
    const result = [];
    for (const info of responseInfos(documentNode)) {
      if (!info.ok) continue;
      const itemsNode = directChild(info.node, 'Items');
      const messageElement = Array.from(itemsNode?.children || []).find(element => element.localName === 'Message');
      if (!messageElement) continue;
      const message = parseMessageElement(messageElement);
      message.attachments = parseAttachments(messageElement);
      message.normalAttachments = message.attachments.filter(isNormalAttachment);
      result.push(message);
    }
    if (result.length) return result;
    for (const messageElement of allElements(documentNode, 'Message')) {
      if (!directChild(messageElement, 'ItemId')) continue;
      const message = parseMessageElement(messageElement);
      message.attachments = parseAttachments(messageElement);
      message.normalAttachments = message.attachments.filter(isNormalAttachment);
      result.push(message);
    }
    return result;
  }

  function pagingInfo(documentNode) {
    const root = allElements(documentNode, 'RootFolder')[0];
    if (!root) throw new OwaMailCleanerError('조회 페이지 정보가 없습니다.', 'INVALID_PAGE');
    const done = String(root.getAttribute('IncludesLastItemInRange')).toLowerCase() === 'true';
    return {
      done,
      nextOffset: Number(root.getAttribute('IndexedPagingOffset')) || 0,
      total: Number(root.getAttribute('TotalItemsInView')) || 0,
    };
  }

  class EwsClient {
    constructor() {
      this.endpoint = new URL(EWS_PATH, location.origin).href;
    }

    async call(operation, body, signal) {
      let response;
      try {
        response = await fetch(this.endpoint, {
          method: 'POST',
          credentials: 'include',
          cache: 'no-store',
          headers: {
            'Content-Type': 'text/xml; charset=utf-8',
            SOAPAction: `${NS.m}/${operation}`,
          },
          body: soapEnvelope(body),
          signal,
        });
      } catch (error) {
        if (error?.name === 'AbortError') throw error;
        throw new OwaMailCleanerError('EWS 서버에 연결하지 못했습니다. 사내망과 인증 상태를 확인해 주세요.', 'NETWORK_ERROR', error?.message || '');
      }
      if ([401, 403, 440].includes(response.status)) {
        throw new OwaMailCleanerError('EWS 인증이 거부되었습니다. OWA에 다시 로그인하거나 회사 EWS 정책을 확인해 주세요.', `HTTP_${response.status}`);
      }
      if (!response.ok) {
        throw new OwaMailCleanerError(`Exchange 서버가 HTTP ${response.status} 오류를 반환했습니다.`, `HTTP_${response.status}`);
      }
      const text = await response.text();
      if (/<!doctype html|<html[\s>]/i.test(text) || /\/owa\/auth/i.test(response.url)) {
        throw new OwaMailCleanerError('OWA 로그인 화면이 반환되었습니다. 다시 로그인한 뒤 시도해 주세요.', 'LOGIN_REQUIRED');
      }
      return parseXml(text);
    }

    async probe(signal) {
      const documentNode = assertSuccess(await this.call('GetFolder', buildGetFolderBody('inbox', true), signal), '받은편지함 확인 실패');
      const inbox = parseFolders(documentNode)[0];
      if (!inbox) throw new OwaMailCleanerError('받은편지함 정보를 읽지 못했습니다.', 'INBOX_NOT_FOUND');
      return inbox;
    }

    async deletedFolder(signal) {
      const doc = assertSuccess(await this.call('GetFolder', buildGetFolderBody('deleteditems'), signal), '지운 편지함 확인 실패');
      const folder = parseFolders(doc)[0];
      if (!folder) throw new OwaMailCleanerError('지운 편지함을 확인하지 못했습니다.', 'FOLDER_NOT_FOUND');
      return folder;
    }

    async emptyDeleted(items, folderId, shouldStop = () => false) {
      const doc = await this.call('GetItem', buildGetItemsBody(items, false));
      const infos = responseInfos(doc);
      const eligible = [];
      const statuses = items.map((item,index) => {
        const info = infos[index];
        if (!info) return { ok: false, message: '메일 확인 응답이 없습니다.' };
        if (!info.ok) return info.code === 'ErrorItemNotFound'
          ? { ok: true, skipped: true } : { ok: false, message: info.message };
        const current = parseMessages(info.node)[0];
        if (!current || current.id !== item.id || !current.parentId) return { ok: false, message: '메일 위치를 확인하지 못했습니다.' };
        if (current.parentId !== folderId || !current.itemClass.startsWith('IPM.Note')) return { ok: true, skipped: true };
        eligible.push({ ...current, index });
        return null;
      });
      if (shouldStop()) return null;
      if (eligible.length) {
        const deleted = statusesFor(await this.call('DeleteItem', buildDeleteItemsBody(eligible)), eligible.length);
        eligible.forEach((item,index) => { statuses[item.index] = deleted[index]; });
      }
      return statuses;
    }

    async findFolders(inbox, signal) {
      const folders = [];
      let offset = 0;
      const seenOffsets = new Set();
      do {
        if (seenOffsets.has(offset)) throw new OwaMailCleanerError('폴더 페이지 정보가 반복되었습니다.', 'PAGING_LOOP');
        seenOffsets.add(offset);
        const documentNode = assertSuccess(await this.call('FindFolder', buildFindFoldersBody(offset), signal), '폴더 조회 실패');
        folders.push(...parseFolders(documentNode));
        const page = pagingInfo(documentNode);
        if (page.done) break;
        offset = page.nextOffset || (offset + 500);
      } while (offset < 10000);
      if (!folders.some(folder => folder.id === inbox.id)) folders.push(inbox);
      return buildFolderPaths(Array.from(new Map(folders.map(folder => [folder.id, folder])).values()));
    }

    async findMessages(criteria, onProgress = () => {}, signal) {
      const messages = [];
      let offset = 0;
      const seenOffsets = new Set();
      do {
        if (seenOffsets.has(offset)) throw new OwaMailCleanerError('메일 페이지 정보가 반복되었습니다.', 'PAGING_LOOP');
        seenOffsets.add(offset);
        const documentNode = assertSuccess(await this.call('FindItem', buildFindItemsBody(criteria, offset), signal), '메일 검색 실패');
        messages.push(...parseMessages(documentNode));
        const page = pagingInfo(documentNode);
        onProgress({ phase: 'messages', loaded: messages.length, total: page.total });
        if (page.done) break;
        offset = page.nextOffset || (offset + PAGE_SIZE);
        if (offset >= 100000) throw new OwaMailCleanerError('조회 상한에 도달했습니다. 기간을 좁혀 다시 검색해 주세요.', 'SEARCH_LIMIT');
      } while (true);

      let filtered = [...new Map(messages.map(message => [message.id, message])).values()].filter(message => message.id && (!message.itemClass || message.itemClass.startsWith('IPM.Note')));
      const start = criteria.startDate ? Date.parse(kstBoundaryUtc(criteria.startDate)) : -Infinity;
      const end = criteria.endDate ? Date.parse(kstBoundaryUtc(criteria.endDate, 1)) : Infinity;
      const keyword = String(criteria.keyword).trim().toLocaleLowerCase('ko');
      filtered = filtered.filter(message => {
        const received = Date.parse(message.receivedDateTime);
        return message.subject.toLocaleLowerCase('ko').includes(keyword)
          && ((!criteria.startDate && !criteria.endDate) || (Number.isFinite(received) && received >= start && received < end))
          && (criteria.mode !== 'markRead' || !message.isRead)
          && (criteria.mode !== 'removeAttachments' || message.hasAttachments);
      });

      const messagesWithAttachments = filtered.filter(message => message.hasAttachments);
      if (criteria.mode !== 'emptyDeleted' && messagesWithAttachments.length) {
        const detailedById = new Map();
        let loaded = 0;
        for (const group of chunk(messagesWithAttachments, BATCH_SIZE)) {
          const items = await this.getItems(group, true, signal);
          items.forEach(item => detailedById.set(item.id, item));
          loaded += group.length;
          onProgress({ phase: 'attachments', loaded, total: messagesWithAttachments.length });
        }
        filtered = filtered.map(message => detailedById.get(message.id) || message);
      }
      if (criteria.mode === 'removeAttachments') filtered = filtered.filter(message => message.normalAttachments.length > 0);
      return filtered;
    }

    async getItems(items, includeAttachments = true, signal) {
      if (!items.length) return [];
      const documentNode = await this.call('GetItem', buildGetItemsBody(items, includeAttachments), signal);
      const infos = responseInfos(documentNode);
      const failed = infos.find(info => !info.ok);
      if (failed && infos.every(info => !info.ok)) throw new OwaMailCleanerError(`메일 새로고침 실패: ${failed.message}`, failed.code, failed.messageText);
      return parseDetailedMessages(documentNode);
    }

    async updateRead(items, signal) {
      if (!items.length) return [];
      const documentNode = await this.call('UpdateItem', buildUpdateReadBody(items), signal);
      return statusesFor(documentNode, items.length);
    }

    async moveToDeleted(items, signal) {
      if (!items.length) return [];
      const documentNode = await this.call('MoveItem', buildMoveItemsBody(items), signal);
      return statusesFor(documentNode, items.length);
    }

    async deleteAttachments(attachments, signal) {
      if (!attachments.length) return { ok: true, code: 'NoError', message: '' };
      const documentNode = await this.call('DeleteAttachment', buildDeleteAttachmentsBody(attachments), signal);
      const info = responseInfos(documentNode)[0];
      return info ? { ...info, node: undefined } : { ok: false, code: 'EMPTY_RESPONSE', message: 'Exchange 응답 결과가 없습니다.' };
    }
  }

  if (runningInNode) {
    module.exports = { ...testApi, EwsClient, parseMessageElement, pagingInfo, statusesFor };
    return;
  }
  if (location.hostname.toLowerCase() !== 'mail.apsfamily.com' || window.top !== window.self) return;
  if (document.documentElement.dataset.owaMailCleanerInstalled) return;
  document.documentElement.dataset.owaMailCleanerInstalled = VERSION;

  function installStyles() {
    if (document.getElementById('omc-style')) return;
    const style = document.createElement('style');
    style.id = 'omc-style';
    style.textContent = `
.omc-launch{position:fixed;right:24px;bottom:24px;z-index:2147483000;border:0;border-radius:999px;padding:12px 17px;background:#0f6cbd;color:#fff;font:700 14px/1.2 "Segoe UI","Malgun Gothic",sans-serif;box-shadow:0 5px 18px #0005;cursor:grab;touch-action:none;user-select:none}.omc-launch:hover{background:#115ea3}.omc-launch.is-dragging{cursor:grabbing}.omc-overlay{position:fixed;inset:0;z-index:2147483001;display:grid;place-items:center;padding:18px;background:#10182899;color:#172033;font:14px/1.45 "Segoe UI","Malgun Gothic",sans-serif}.omc-modal{display:flex;flex-direction:column;width:min(1040px,100%);max-height:94vh;border-radius:16px;background:#fff;box-shadow:0 22px 70px #0007;overflow:hidden}.omc-header,.omc-footer{display:flex;align-items:center;gap:12px;padding:16px 20px}.omc-header{border-bottom:1px solid #e5e7eb}.omc-header h2{margin:0;font-size:20px}.omc-version{margin-right:auto;color:#667085;font-size:12px}.omc-close{border:0;background:transparent;color:#344054;font-size:28px;line-height:1;cursor:pointer}.omc-body{overflow:auto;padding:18px 20px}.omc-form{display:grid;grid-template-columns:minmax(190px,1.3fr) minmax(150px,1fr) 145px 145px;gap:12px}.omc-field{display:grid;gap:5px;color:#344054;font-weight:700}.omc-field select,.omc-field input{min-width:0;border:1px solid #b8c2d0;border-radius:8px;padding:9px 10px;background:#fff;color:#172033;font:inherit}.omc-modes{display:flex;flex-wrap:wrap;gap:8px;margin-top:14px}.omc-mode{display:flex;align-items:center;gap:6px;border:1px solid #ccd5e0;border-radius:999px;padding:8px 12px;background:#f8fafc;cursor:pointer}.omc-mode:has(input:checked){border-color:#0f6cbd;background:#e7f3ff;color:#0b5cab;font-weight:700}.omc-actions,.omc-result-actions{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin-top:14px}.omc-button{border:1px solid #91afd0;border-radius:8px;padding:9px 13px;background:#fff;color:#0b5cab;font:700 13px inherit;cursor:pointer}.omc-button.primary{border-color:#0f6cbd;background:#0f6cbd;color:#fff}.omc-button.danger{border-color:#d92d20;background:#d92d20;color:#fff}.omc-button:disabled{opacity:.48;cursor:not-allowed}.omc-status{margin-top:14px;border-radius:9px;padding:10px 12px;background:#eef5fc;color:#24547f}.omc-status.success{background:#ecfdf3;color:#067647}.omc-status.warning{background:#fffaeb;color:#93370d}.omc-status.error{background:#fef3f2;color:#b42318}.omc-results{margin-top:16px}.omc-results-head{flex-wrap:wrap;display:flex;align-items:center;gap:10px}.omc-results-head strong{margin-right:auto}.omc-table-wrap{margin-top:9px;border:1px solid #dfe4ea;border-radius:10px;overflow:auto;max-height:430px}.omc-table{width:100%;border-collapse:collapse;font-size:13px}.omc-table th,.omc-table td{padding:9px 10px;border-bottom:1px solid #edf0f3;text-align:left;vertical-align:top}.omc-table th{position:sticky;top:0;z-index:1;background:#f7f9fc;color:#475467;white-space:nowrap}.omc-table tr:last-child td{border-bottom:0}.omc-table tr.is-failed{background:#fff3f2}.omc-subject{min-width:230px;font-weight:600}.omc-muted{color:#667085;font-size:12px}.omc-attachments{max-width:240px;word-break:break-all}.omc-empty{padding:28px!important;text-align:center!important;color:#667085}.omc-footer{flex-wrap:wrap;justify-content:flex-end;border-top:1px solid #e5e7eb}.omc-summary{margin-right:auto;color:#475467}.omc-progress{height:6px;margin-top:9px;border-radius:999px;background:#dbe7f3;overflow:hidden}.omc-progress>span{display:block;height:100%;width:0;background:#0f6cbd;transition:width .18s ease}.omc-hidden{display:none!important}@media(max-width:760px){.omc-overlay{padding:6px}.omc-modal{max-height:98vh;border-radius:10px}.omc-form{grid-template-columns:1fr 1fr}.omc-field:first-child,.omc-field:nth-child(2){grid-column:1/-1}.omc-body{padding:14px}.omc-table-wrap{max-height:360px}.omc-header,.omc-footer{padding:13px 14px}}`;
    document.head.append(style);
  }

  function installDrag(button) {
    let dragState;
    let ignoreClick = false;
    const savePosition = () => {
      const rect = button.getBoundingClientRect();
      localStorage.setItem(POSITION_KEY, JSON.stringify({ left: Math.round(rect.left), top: Math.round(rect.top) }));
    };
    try {
      const saved = JSON.parse(localStorage.getItem(POSITION_KEY) || 'null');
      if (Number.isFinite(saved?.left) && Number.isFinite(saved?.top)) {
        button.style.left = `${Math.min(Math.max(0, saved.left), Math.max(0, innerWidth - 100))}px`;
        button.style.top = `${Math.min(Math.max(0, saved.top), Math.max(0, innerHeight - 45))}px`;
        button.style.right = 'auto';
        button.style.bottom = 'auto';
      }
    } catch { localStorage.removeItem(POSITION_KEY); }
    button.addEventListener('pointerdown', event => {
      if (event.button !== 0) return;
      const rect = button.getBoundingClientRect();
      dragState = { dx: event.clientX - rect.left, dy: event.clientY - rect.top, startX: event.clientX, startY: event.clientY, moved: false };
      button.setPointerCapture(event.pointerId);
    });
    button.addEventListener('pointermove', event => {
      if (!dragState) return;
      if (Math.abs(event.clientX - dragState.startX) > 4 || Math.abs(event.clientY - dragState.startY) > 4) dragState.moved = true;
      if (!dragState.moved) return;
      button.style.left = `${Math.min(Math.max(0, event.clientX - dragState.dx), Math.max(0, innerWidth - button.offsetWidth))}px`;
      button.style.top = `${Math.min(Math.max(0, event.clientY - dragState.dy), Math.max(0, innerHeight - button.offsetHeight))}px`;
      button.style.right = 'auto';
      button.style.bottom = 'auto';
      button.classList.add('is-dragging');
    });
    const finish = () => {
      if (!dragState) return;
      if (dragState.moved) { ignoreClick = true; savePosition(); }
      dragState = undefined;
      button.classList.remove('is-dragging');
    };
    button.addEventListener('pointerup', finish);
    button.addEventListener('pointercancel', finish);
    button.addEventListener('click', event => {
      if (!ignoreClick) return;
      event.preventDefault();
      event.stopImmediatePropagation();
      ignoreClick = false;
    }, true);
  }

  function formatReceived(value) {
    const date = new Date(value);
    if (!Number.isFinite(date.getTime())) return value || '-';
    return new Intl.DateTimeFormat('ko-KR', {
      timeZone: 'Asia/Seoul', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false,
    }).format(date);
  }

  function formatSize(bytes) {
    if (!Number.isFinite(bytes) || bytes < 0) return '미확인';
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  }

  function actionLabel(mode) {
    return ({ removeAttachments: '일반 첨부파일 삭제', markRead: '읽음 처리', moveToDeleted: '휴지통 이동', emptyDeleted: '휴지통 비우기' })[mode] || '처리';
  }

  function resultRow(message, selected, trash, busy) {
    const attachmentText = message.normalAttachments?.length
      ? `<b>${message.normalAttachments.length}개</b><br>${message.normalAttachments.map(attachment => `${htmlEscape(attachment.name)}${attachment.size ? ` (${formatSize(attachment.size)})` : ''}`).join('<br>')}`
      : `<span class="omc-muted">${trash && message.hasAttachments ? '있음 (상세 생략)' : '없음'}</span>`;
    return `<tr data-id="${htmlEscape(message.id)}">
  <td><input class="omc-select-row" type="checkbox" aria-label="메일 선택" ${busy ? 'disabled' : ''} ${selected ? 'checked' : ''}></td>
  <td>${htmlEscape(formatReceived(message.receivedDateTime))}</td>
  <td>${htmlEscape(message.sender)}${message.senderAddress && message.senderAddress !== message.sender ? `<div class="omc-muted">${htmlEscape(message.senderAddress)}</div>` : ''}</td>
  <td class="omc-subject">${htmlEscape(message.subject)}</td>
  <td>${message.isRead ? '읽음' : '<b>안 읽음</b>'}</td>
  <td>${htmlEscape(formatSize(message.size))}</td>
  <td class="omc-attachments">${attachmentText}</td>
</tr>`;
  }

  async function mapWithConcurrency(items, limit, worker) {
    const results = new Array(items.length);
    let next = 0;
    async function run() {
      while (next < items.length) {
        const index = next++;
        try { results[index] = await worker(items[index], index); }
        catch (error) { results[index] = { ok: false, code: error?.code || 'UNKNOWN', message: error?.message || '처리 실패' }; }
      }
    }
    await Promise.all(Array.from({ length: Math.min(limit, items.length) }, run));
    return results;
  }

  function createModal() {
    const client = new EwsClient();
    const state = {
      connected: false,
      inbox: null,
      deleted: null,
      previousCriteria: null,
      folders: [],
      results: [],
      selected: new Set(),
      lastSignature: '',
      busy: false,
      cancelRequested: false,
      controller: null,
    };
    const overlay = document.createElement('div');
    overlay.className = 'omc-overlay';
    overlay.innerHTML = `<section class="omc-modal" role="dialog" aria-modal="true" aria-labelledby="omc-title">
  <header class="omc-header"><h2 id="omc-title">${APP_NAME}</h2><span class="omc-version">v${VERSION} · 사내 Exchange</span><button class="omc-close" type="button" aria-label="닫기">×</button></header>
  <div class="omc-body">
    <div class="omc-form">
      <label class="omc-field">폴더<select id="omc-folder" disabled><option>폴더를 불러오는 중...</option></select></label>
      <label class="omc-field">제목 키워드<input id="omc-keyword" type="text" value="일보" maxlength="120"></label>
      <label class="omc-field">시작일<input id="omc-start" type="date" value="${kstDateInput(-29)}"></label>
      <label class="omc-field">종료일<input id="omc-end" type="date" value="${kstDateInput(0)}"></label>
    </div>
    <div class="omc-modes" role="radiogroup" aria-label="처리 방식">
      <label class="omc-mode"><input type="radio" name="omc-mode" value="removeAttachments"> 일반 첨부파일 삭제</label>
      <label class="omc-mode"><input type="radio" name="omc-mode" value="markRead" checked> 읽음 처리</label>
      <label class="omc-mode"><input type="radio" name="omc-mode" value="moveToDeleted"> 휴지통 이동</label>
      <label class="omc-mode"><input type="radio" name="omc-mode" value="emptyDeleted" disabled> 휴지통 정리</label>
    </div>
    <div class="omc-actions">
      <button class="omc-button primary" id="omc-search" type="button" disabled>대상 메일 검색</button>
      <button class="omc-button omc-hidden" id="omc-stop" type="button">중단 요청</button>
      <span class="omc-muted" id="omc-hint">선택한 한 폴더만 검색합니다. 종료일의 23:59:59까지 포함됩니다.</span>
    </div>
    <div class="omc-status" id="omc-status" role="status">EWS 연결과 받은편지함을 확인하는 중입니다.</div>
    <div class="omc-progress omc-hidden" id="omc-progress"><span></span></div>
    <section class="omc-results">
      <div class="omc-results-head"><label>정렬 <select id="omc-sort"><option value="dateDesc">수신일 최신순</option><option value="dateAsc">수신일 오래된 순</option><option value="sizeDesc">용량 큰 순</option><option value="sizeAsc">용량 작은 순</option></select></label><strong id="omc-count">검색 결과: -</strong><button class="omc-button" id="omc-all" type="button" disabled>전체 선택</button><button class="omc-button" id="omc-none" type="button" disabled>선택 해제</button></div>
      <div class="omc-table-wrap"><table class="omc-table"><thead><tr><th>선택</th><th>수신일</th><th>발신자</th><th>제목</th><th>상태</th><th>메일 용량</th><th>일반 첨부</th></tr></thead><tbody id="omc-tbody"><tr><td class="omc-empty" colspan="7">검색 조건을 입력해 주세요.</td></tr></tbody></table></div>
    </section>
  </div>
  <footer class="omc-footer"><span class="omc-summary" id="omc-summary">선택: 0건</span><button class="omc-button omc-cancel" type="button">닫기</button><button class="omc-button danger omc-hidden" id="omc-empty-all" type="button" disabled>검색 결과 전체 비우기</button><button class="omc-button primary" id="omc-run" type="button" disabled>선택 항목 실행</button></footer>
</section>`;
    document.body.append(overlay);

    const $ = selector => overlay.querySelector(selector);
    const elements = {
      sort: $('#omc-sort'), emptyAll: $('#omc-empty-all'), hint: $('#omc-hint'),
      folder: $('#omc-folder'), keyword: $('#omc-keyword'), start: $('#omc-start'), end: $('#omc-end'),
      search: $('#omc-search'), stop: $('#omc-stop'), status: $('#omc-status'), progress: $('#omc-progress'),
      progressBar: $('#omc-progress span'), count: $('#omc-count'), tbody: $('#omc-tbody'),
      all: $('#omc-all'), none: $('#omc-none'), summary: $('#omc-summary'), run: $('#omc-run'),
    };

    function setStatus(message, type = '') {
      elements.status.className = `omc-status${type ? ` ${type}` : ''}`;
      elements.status.textContent = message;
    }

    function setProgress(current, total) {
      elements.progress.classList.toggle('omc-hidden', !total);
      elements.progressBar.style.width = total ? `${Math.min(100, Math.round(current / total * 100))}%` : '0%';
    }

    function criteria() {
      return {
        folderId: elements.folder.value,
        keyword: elements.keyword.value.trim(),
        startDate: elements.start.value,
        endDate: elements.end.value,
        mode: overlay.querySelector('input[name="omc-mode"]:checked')?.value || '',
      };
    }

    function renderSelection() {
      elements.summary.textContent = `선택: ${state.selected.size}건 · ${totalSize(state.results.filter(item => state.selected.has(item.id)))}`;
      const trash = criteria().mode === 'emptyDeleted';
      elements.run.textContent = trash ? '선택 항목 비우기' : '선택 항목 실행';
      elements.run.classList.toggle('danger', trash);
      elements.emptyAll.classList.toggle('omc-hidden', !trash);
      const currentSignature = criteriaSignature(criteria());
      elements.emptyAll.disabled = state.busy || !state.connected || !state.results.length || !state.lastSignature || currentSignature !== state.lastSignature;
      elements.run.disabled = state.busy || !state.connected || !state.selected.size || !state.lastSignature || currentSignature !== state.lastSignature;
    }

    function renderResults() {
      elements.count.textContent = `검색 결과: ${state.results.length}건 · ${totalSize(state.results)}`;
      elements.tbody.innerHTML = state.results.length
        ? sortMessages(state.results, elements.sort.value).map(message => resultRow(message, state.selected.has(message.id), criteria().mode === 'emptyDeleted', state.busy)).join('')
        : '<tr><td class="omc-empty" colspan="7">조건에 맞는 메일이 없습니다.</td></tr>';
      elements.all.disabled = state.busy || !state.results.length;
      elements.none.disabled = state.busy || !state.results.length;
      renderSelection();
    }

    function invalidateResults() {
      if (!state.lastSignature) return;
      state.lastSignature = '';
      setStatus('검색 조건이 변경되었습니다. 다시 검색해 주세요.', 'warning');
      renderSelection();
    }

    function setBusy(busy) {
      state.busy = busy;
      elements.search.disabled = busy || !state.connected;
      elements.folder.disabled = busy || !state.connected || criteria().mode === 'emptyDeleted';
      elements.sort.disabled = busy;
      elements.keyword.disabled = busy;
      elements.start.disabled = busy;
      elements.end.disabled = busy;
      overlay.querySelectorAll('input[name="omc-mode"]').forEach(input => { input.disabled = busy || (input.value === 'emptyDeleted' && !state.deleted); });
      elements.stop.classList.toggle('omc-hidden', !busy);
      $('.omc-close').disabled = busy;
      $('.omc-cancel').disabled = busy;
      renderResults();
    }

    async function connect() {
      try {
        state.controller = new AbortController();
        state.inbox = await client.probe(state.controller.signal);
        setStatus('EWS 연결 성공. 메일 폴더를 불러오는 중입니다.');
        state.folders = await client.findFolders(state.inbox, state.controller.signal);
        elements.folder.innerHTML = state.folders.map(folder => `<option value="${htmlEscape(folder.id)}"${folder.id === state.inbox.id ? ' selected' : ''}>${htmlEscape(folder.path)} (${folder.totalCount})</option>`).join('');
        try {
          state.deleted = await client.deletedFolder(state.controller.signal);
          if (!state.folders.some(folder => folder.id === state.deleted.id)) {
            const option = document.createElement('option');
            option.value = state.deleted.id; option.textContent = state.deleted.name;
            elements.folder.append(option);
          }
          $('input[value="emptyDeleted"]').disabled = false;
        } catch (error) { state.deletedError = error.message; }
        state.connected = true;
        elements.folder.disabled = false;
        elements.search.disabled = false;
        setStatus(`연결되었습니다. ${state.folders.length}개 메일 폴더를 찾았습니다.${state.deletedError ? " 휴지통 정리 사용 불가: " + state.deletedError : ""}`, 'success');
      } catch (error) {
        state.connected = false;
        elements.folder.innerHTML = '<option value="">EWS 연결 불가</option>';
        setStatus(error?.message || 'EWS 연결에 실패했습니다.', 'error');
      } finally {
        state.controller = null;
      }
    }

    async function search({ finalMessage = '' } = {}) {
      const searchCriteria = criteria();
      const validation = validateCriteria(searchCriteria);
      if (validation) { setStatus(validation, 'error'); return; }
      state.lastSignature = '';
      state.results = [];
      state.selected.clear();
      state.cancelRequested = false;
      state.controller = new AbortController();
      setBusy(true);
      setProgress(0, 1);
      setStatus('대상 메일을 검색하는 중입니다.');
      try {
        const results = await client.findMessages(searchCriteria, progress => {
          if (progress.phase === 'attachments') setStatus(`첨부파일을 확인하는 중입니다. ${progress.loaded}/${progress.total}`);
          else setStatus(`메일을 검색하는 중입니다. ${progress.loaded}${progress.total ? `/${progress.total}` : ''}`);
          setProgress(progress.loaded, Math.max(progress.total, progress.loaded, 1));
        }, state.controller.signal);
        state.results = results;
        state.selected = new Set(searchCriteria.mode === 'emptyDeleted' ? [] : results.map(message => message.id));
        state.lastSignature = criteriaSignature(searchCriteria);
        renderResults();
        setStatus(finalMessage || `${results.length}건을 찾았습니다. 처리할 메일과 검색 조건을 확인해 주세요.`, results.length ? 'success' : 'warning');
      } catch (error) {
        if (error?.name === 'AbortError') setStatus('검색을 중단했습니다.', 'warning');
        else setStatus(`${finalMessage ? finalMessage + ' 목록 새로고침 실패: ' : ''}${error?.message || '메일 검색에 실패했습니다.'}`, 'error');
      } finally {
        state.controller = null;
        setProgress(0, 0);
        setBusy(false);
      }
    }

    async function retryRead(item, firstStatus) {
      if (firstStatus.ok || !isStaleCode(firstStatus.code)) return firstStatus;
      const refreshed = (await client.getItems([item], false))[0];
      if (!refreshed) return firstStatus;
      return (await client.updateRead([refreshed]))[0];
    }

    async function deleteNormalAttachments(item) {
      if (!item.normalAttachments?.length) return { ok: true, skipped: true, code: 'NO_NORMAL_ATTACHMENTS', message: '삭제할 일반 첨부가 없습니다.' };
      let status = await client.deleteAttachments(item.normalAttachments);
      if (status.ok || !isStaleCode(status.code)) return status;
      const refreshed = (await client.getItems([item], true))[0];
      if (!refreshed?.normalAttachments?.length) return { ok: true, skipped: true, code: 'ALREADY_REMOVED', message: '일반 첨부가 이미 삭제되었습니다.' };
      status = await client.deleteAttachments(refreshed.normalAttachments);
      return status;
    }

    async function execute(allResults = false) {
      if (state.busy) return;
      const runCriteria = criteria();
      if (criteriaSignature(runCriteria) !== state.lastSignature) { setStatus('검색 조건이 변경되었습니다. 다시 검색해 주세요.', 'error'); return; }
      const selectedItems = state.results.filter(message => (allResults && runCriteria.mode === 'emptyDeleted') || state.selected.has(message.id));
      if (runCriteria.mode === 'emptyDeleted' && runCriteria.folderId !== state.deleted?.id) return;
      if (!selectedItems.length) { setStatus('실행할 메일을 선택해 주세요.', 'error'); return; }
      const label = actionLabel(runCriteria.mode);
      if (!confirm(`${selectedItems.length}건 · ${totalSize(selectedItems)}에 '${label}' 작업을 실행할까요?\n제목: ${runCriteria.keyword || '전체'}\n수신일: ${runCriteria.startDate || '전체'} ~ ${runCriteria.endDate || '전체'}\n${runCriteria.mode === 'emptyDeleted' ? '검색 결과 중 지정한 메일만 지운 편지함에서 제거합니다. 복구 가능 여부는 서버 보존 정책을 따릅니다.' : '메일 내용과 대상을 다시 확인해 주세요.'}`)) return;
      state.lastSignature = '';

      state.cancelRequested = false;
      setBusy(true);
      let completed = 0;
      let succeeded = 0;
      let failed = 0;
      let skipped = 0;
      const failures = [];
      try {
        for (const group of chunk(selectedItems, BATCH_SIZE)) {
          if (state.cancelRequested) break;
          let statuses;
          if (runCriteria.mode === 'markRead') {
            const initial = await client.updateRead(group);
            statuses = [];
            for (let index = 0; index < group.length; index += 1) {
              statuses.push(await retryRead(group[index], initial[index]));
            }
          } else if (runCriteria.mode === 'emptyDeleted') {
            statuses = await client.emptyDeleted(group, state.deleted.id, () => state.cancelRequested);
            if (!statuses) break;
          } else if (runCriteria.mode === 'moveToDeleted') {
            statuses = await client.moveToDeleted(group);
          } else {
            statuses = await mapWithConcurrency(group, 4, deleteNormalAttachments);
          }

          statuses.forEach((status, index) => {
            completed += 1;
            if (status?.ok && status?.skipped) skipped += 1;
            else if (status?.ok) succeeded += 1;
            else {
              failed += 1;
              failures.push(`${group[index]?.subject || '(제목 없음)'}: ${status?.message || '처리 실패'}`);
            }
          });
          setProgress(completed, selectedItems.length);
          setStatus(`${label} 처리 중: ${completed}/${selectedItems.length} · 성공 ${succeeded} · 실패 ${failed}`);
        }
        const remaining = selectedItems.length - completed;
        const resultMessage = `${label} 완료: 성공 ${succeeded}건, 실패 ${failed}건, 건너뜀 ${skipped}건${remaining ? `, 미처리 ${remaining}건` : ''}.${failures.length ? ` 첫 오류: ${failures[0]}` : ''}`;
        await search({ finalMessage: resultMessage });
      } catch (error) {
        const remaining = selectedItems.length - completed;
        await search({ finalMessage: `${label} 중단: 성공 ${succeeded}건, 실패 ${failed}건, 건너뜀 ${skipped}건, 결과 미확인/미처리 ${remaining}건. ${error?.message || ''}` });
      } finally {
        state.cancelRequested = false;
        setProgress(0, 0);
        setBusy(false);
      }
    }

    function close() {
      if (state.busy) return;
      state.controller?.abort();
      overlay.remove();
    }

    $('.omc-close').addEventListener('click', close);
    $('.omc-cancel').addEventListener('click', close);
    overlay.addEventListener('click', event => { if (event.target === overlay) close(); });
    elements.search.addEventListener('click', () => search());
    elements.stop.addEventListener('click', () => {
      state.cancelRequested = true;
      state.controller?.abort();
      setStatus('현재 요청이 끝난 뒤 중단합니다.', 'warning');
    });
    elements.run.addEventListener('click', () => execute(false));
    elements.emptyAll.addEventListener('click', () => execute(true));
    elements.sort.addEventListener('change', renderResults);
    elements.all.addEventListener('click', () => { state.selected = new Set(state.results.map(message => message.id)); renderResults(); });
    elements.none.addEventListener('click', () => { state.selected.clear(); renderResults(); });
    elements.tbody.addEventListener('change', event => {
      const checkbox = event.target.closest('.omc-select-row');
      if (!checkbox) return;
      const id = checkbox.closest('tr')?.dataset.id;
      if (!id) return;
      if (checkbox.checked) state.selected.add(id); else state.selected.delete(id);
      renderSelection();
    });
    [elements.folder, elements.keyword, elements.start, elements.end].forEach(element => element.addEventListener('input', invalidateResults));
    overlay.querySelectorAll('input[name="omc-mode"]').forEach(element => element.addEventListener('change', () => {
      const trash = criteria().mode === 'emptyDeleted';
      if (trash) {
        state.previousCriteria = { folder: elements.folder.value, keyword: elements.keyword.value, start: elements.start.value, end: elements.end.value };
        elements.folder.value = state.deleted.id;
        elements.keyword.value = '';
        elements.sort.value = 'dateAsc';
      } else if (state.previousCriteria) {
        for (const [key,value] of Object.entries(state.previousCriteria)) elements[key].value = value;
        state.previousCriteria = null;
        elements.sort.value = 'dateDesc';
      }
      elements.keyword.placeholder = trash ? '선택 입력 · 비우면 모든 제목' : '';
      elements.hint.textContent = trash ? '지운 편지함 바로 아래의 일반 메일만 조회합니다. 시작일·종료일은 최근 1개월로 기본 설정되며 직접 변경할 수 있습니다. 용량은 메일 크기 합계입니다.' : '선택한 한 폴더만 검색합니다. 종료일의 23:59:59까지 포함됩니다.';
      invalidateResults();
      setBusy(false);
    }));
    overlay.addEventListener('keydown', event => { if (event.key === 'Escape') close(); });
    connect();
  }

  function installLauncher() {
    installStyles();
    if (document.querySelector('.omc-launch')) return;
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'omc-launch';
    button.textContent = APP_NAME;
    button.title = '메일 정리 도구 열기';
    document.body.append(button);
    installDrag(button);
    button.addEventListener('click', () => { if (!document.querySelector('.omc-overlay')) createModal(); });
  }

  if (document.body) installLauncher();
  else document.addEventListener('DOMContentLoaded', installLauncher, { once: true });

  const observer = new MutationObserver(() => {
    if (document.body && !document.querySelector('.omc-launch')) installLauncher();
  });
  observer.observe(document.body, { childList: true });
})();
