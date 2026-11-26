// Message tags that show the processing state in Thunderbird, and their mapping to job statuses.

export const TAGS = Object.freeze({
  sorban: Object.freeze({ key: 'efai_sorban', tag: 'AI: sorban', color: '#1E88E5' }),
  kesz: Object.freeze({ key: 'efai_kesz', tag: 'AI: kész', color: '#2E7D32' }),
  ellenorzendo: Object.freeze({ key: 'efai_ellenorzendo', tag: 'AI: ellenőrzendő', color: '#F9A825' }),
  hiba: Object.freeze({ key: 'efai_hiba', tag: 'AI: hiba', color: '#C62828' }),
});

export const TERMINAL_STATUSES = Object.freeze(['done', 'needs_review', 'failed']);

export function isTerminal(status) {
  return TERMINAL_STATUSES.includes(status);
}

/** Job status of the exe -> tag name (key of TAGS). */
export function tagForStatus(status) {
  switch (status) {
    case 'done':
      return 'kesz';
    case 'needs_review':
      return 'ellenorzendo';
    case 'failed':
      return 'hiba';
    default:
      return 'sorban';
  }
}

/** The message's tag list with every AI tag replaced by `newKey` (other tags are kept, order preserved). */
export function replaceAiTag(currentTags, aiKeys, newKey) {
  const others = (currentTags ?? []).filter((t) => !aiKeys.includes(t));
  return newKey ? [...others, newKey] : others;
}

export function hasAiTag(tags, aiKeys) {
  return (tags ?? []).some((t) => aiKeys.includes(t));
}
