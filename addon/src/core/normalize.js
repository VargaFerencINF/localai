// Subject normalization and rule matching (CLAUDE.md section 6).
// JavaScript twin of src/EfMailAgent/Matching/SubjectNormalizer.cs and SubjectMatcher.cs; both are verified by
// /shared/normalization-cases.json and must agree 100%. Character classes are spelled out explicitly because .NET
// and JavaScript define \s and \d differently.

// Removed outright: soft hyphen, zero-width space/joiners, LRM/RLM, word joiner, BOM.
const INVISIBLE = /[\u00AD\u200B\u200C\u200D\u200E\u200F\u2060\uFEFF]/g;

// Every whitespace character that either .NET or JavaScript treats as whitespace.
const WHITESPACE = /[\u0009\u000A\u000B\u000C\u000D \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+/g;

// Applied after accent removal and lowercasing, so plain ASCII forms suffice.
const PREFIX = /^(?:(?:re|fw|fwd|tov|tovabbitas|va|valasz|aw|wg) ?(?:\[[0-9]+\]|\([0-9]+\))? ?:|\[[^\[\]]{1,30}\]) ?/;

const COMBINING_MARKS = /\p{M}/gu;

function trimSpaces(text) {
  return text.replace(/^ +| +$/g, '');
}

/** Accent removal (NFD + drop combining marks), invisible characters removed, lowercase, whitespace collapsed. */
export function normalizeText(text) {
  if (!text) return '';
  const stripped = String(text).normalize('NFD').replace(COMBINING_MARKS, '').replace(INVISIBLE, '');
  return trimSpaces(stripped.toLowerCase().replace(WHITESPACE, ' '));
}

/** Full subject normalization: text normalization, then leading reply/forward prefixes and [tags] removed repeatedly. */
export function normalizeSubject(subject) {
  let text = normalizeText(subject);
  for (;;) {
    const match = PREFIX.exec(text);
    if (!match || match[0].length === 0) break;
    text = text.slice(match[0].length);
  }
  return trimSpaces(text);
}

/** Precomputes normalized keywords and the regex of a matcher ({ ruleId, priority, enabled, subjectContains, subjectRegex }). */
export function compileMatcher(matcher) {
  const keywords = [...new Set((matcher.subjectContains ?? []).map(normalizeText).filter((k) => k.length > 0))];
  let regex = null;
  if (matcher.subjectRegex) {
    try {
      regex = new RegExp(matcher.subjectRegex);
    } catch {
      regex = null; // the exe validates rules; an invalid pattern simply never matches here
    }
  }
  return {
    ruleId: matcher.ruleId,
    name: matcher.name ?? matcher.ruleId,
    priority: Number.isFinite(matcher.priority) ? matcher.priority : 100,
    enabled: matcher.enabled !== false,
    keywords,
    regex,
  };
}

export function isMatch(compiled, normalizedSubject) {
  if (!compiled.enabled) return false;
  if (compiled.keywords.some((k) => normalizedSubject.includes(k))) return true;
  return compiled.regex ? compiled.regex.test(normalizedSubject) : false;
}

function ordinalCompare(a, b) {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Best matching rule: lowest priority wins, ties broken by rule id (ordinal). Returns the matcher or null. */
export function findBestMatch(subject, matchers) {
  const normalized = normalizeSubject(subject);
  const compiled = matchers.map((m) => (m.keywords ? m : compileMatcher(m)));
  const hits = compiled.filter((m) => isMatch(m, normalized));
  hits.sort((a, b) => a.priority - b.priority || ordinalCompare(a.ruleId, b.ruleId));
  return hits[0] ?? null;
}
