// Fallback HTML to plain text conversion, used only when messengerUtilities.convertToPlainText is not available
// (Thunderbird before 140). Mirrors the exe's Email/HtmlToText.cs closely enough for extraction purposes.

const ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };

function decodeEntities(text) {
  return text.replace(/&(#x[0-9a-f]+|#[0-9]+|[a-z]+);/gi, (all, code) => {
    if (code[0] === '#') {
      const value = code[1] === 'x' || code[1] === 'X' ? parseInt(code.slice(2), 16) : parseInt(code.slice(1), 10);
      return Number.isFinite(value) ? String.fromCodePoint(value) : all;
    }
    return ENTITIES[code.toLowerCase()] ?? all;
  });
}

export function htmlToText(html) {
  if (!html || !html.trim()) return '';
  let text = html.replace(/\r\n?/g, '\n');
  text = text.replace(/<(script|style|head|title)\b[^>]*>[\s\S]*?<\/\1\s*>/gi, '');
  text = text.replace(/<!--[\s\S]*?-->/g, '');
  text = text.replace(/\n/g, ' ');
  text = text.replace(/<br\s*\/?>/gi, '\n');
  text = text.replace(/<li\b[^>]*>/gi, '\u0001- ');
  text = text.replace(/<\/t[dh]\s*>/gi, '\t');
  text = text.replace(/<\/?p\b[^>]*>/gi, '\u0002');
  text = text.replace(/<\/?(div|tr|li|ul|ol|table|h[1-6]|blockquote|pre|section|article|header|footer)\b[^>]*>/gi, '\u0001');
  text = text.replace(/[\u0001\u0002][\u0001\u0002 \t]*/g, (run) => {
    const marker = run.replace(/[ \t]+$/, '');
    return (marker.includes('\u0002') ? '\n\n' : '\n') + run.slice(marker.length);
  });
  text = text.replace(/<[^>]+>/g, '');
  text = decodeEntities(text);
  text = text.split('\n').map((line) => line.replace(/[ \t\u00A0]+/g, ' ').trim()).join('\n');
  return text.replace(/\n{3,}/g, '\n\n').trim();
}
