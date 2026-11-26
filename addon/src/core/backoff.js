// Retry delays for submissions that could not reach the exe (outbox).

export const OUTBOX_DELAYS_MINUTES = [1, 2, 5, 10, 30, 60];

/** Delay before the next try after `attempts` failed tries (1-based), capped at 60 minutes. */
export function backoffMs(attempts) {
  const index = Math.min(Math.max(attempts, 1), OUTBOX_DELAYS_MINUTES.length) - 1;
  return OUTBOX_DELAYS_MINUTES[index] * 60_000;
}
