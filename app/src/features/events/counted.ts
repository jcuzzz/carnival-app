/** "1 division" / "4 divisions" — the count and its noun, pluralised once. */
export function counted(n: number, noun: string, plural = `${noun}s`): string {
  return `${n} ${n === 1 ? noun : plural}`;
}
