const seen = new Set<string>();

export function warnOnce(key: string, ...args: unknown[]): void {
  if (seen.has(key)) return;
  seen.add(key);
  console.warn('[zeus]', ...args);
}
