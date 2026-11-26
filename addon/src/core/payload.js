// Builds the POST /api/jobs body (CLAUDE.md section 7) from Thunderbird message data. Pure functions only.

/** Canonical Message-ID: wrapped in angle brackets (Thunderbird's headerMessageId has none). */
export function canonicalMessageId(headerMessageId) {
  const id = String(headerMessageId ?? '').trim().replace(/^<|>$/g, '').trim();
  return id ? `<${id}>` : '';
}

/** Picks the plain text and HTML bodies from messages.listInlineTextParts() output (first of each type). */
export function pickBodies(inlineTextParts) {
  const parts = inlineTextParts ?? [];
  const lower = (p) => String(p.contentType ?? '').toLowerCase();
  const text = parts.find((p) => lower(p).startsWith('text/plain'))?.content ?? null;
  const html = parts.find((p) => lower(p).startsWith('text/html'))?.content ?? null;
  return { text, html };
}

/** Walks a messages.getFull() MessagePart tree (fallback when listInlineTextParts is unavailable). */
export function collectInlineTextParts(part, result = []) {
  if (!part) return result;
  const type = String(part.contentType ?? '').toLowerCase();
  const isAttachment = Boolean(part.name) && !type.startsWith('multipart/');
  if (!isAttachment && (type.startsWith('text/plain') || type.startsWith('text/html')) && typeof part.body === 'string') {
    result.push({ contentType: part.contentType, content: part.body });
  }
  for (const child of part.parts ?? []) collectInlineTextParts(child, result);
  return result;
}

/**
 * @param {object} args
 * @param {object} args.header       MessageHeader (headerMessageId, date, author, recipients, subject)
 * @param {object} args.folder       MailFolder (accountId, path)
 * @param {string|null} args.bodyText  plain text body, or the converted HTML body when there is no plain part
 * @param {string|null} args.bodyHtml  HTML body; sent only when there is no plain text part
 * @param {string[]} args.attachmentNames
 * @param {'auto'|'manual'} args.source
 * @param {string} [args.ruleId]     explicit rule (manual submission)
 */
export function buildPayload({ header, folder, bodyText, bodyHtml, attachmentNames, source, ruleId }) {
  const date = header.date instanceof Date ? header.date : header.date ? new Date(header.date) : null;
  const payload = {
    messageId: canonicalMessageId(header.headerMessageId),
    account: folder?.accountId ?? header.folder?.accountId ?? '',
    folder: folder?.path ?? header.folder?.path ?? '',
    receivedAt: date && !Number.isNaN(date.getTime()) ? date.toISOString() : null,
    from: header.author ?? '',
    to: Array.isArray(header.recipients) ? [...header.recipients] : [],
    subject: header.subject ?? '',
    bodyText: bodyText ?? '',
  };
  if (bodyHtml) payload.bodyHtml = bodyHtml;
  payload.attachmentNames = [...(attachmentNames ?? [])];
  payload.source = source;
  if (ruleId) payload.ruleId = ruleId;
  return payload;
}
