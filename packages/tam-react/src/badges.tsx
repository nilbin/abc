// Framework registry states only; the app registers its DOMAIN enum colors (docs/13: the
// framework owns semantics, the app owns pixels).
const badgeColors = new Map<string, string>([
  ['active', 'green'], ['retired', 'gray'], ['deprecated', 'yellow'],
]);

/** App-owned pixels: map enum wire values (lowercase) to Mantine badge colors. */
export function registerBadgeColors(colors: Record<string, string>): void {
  for (const [value, color] of Object.entries(colors)) badgeColors.set(value.toLowerCase(), color);
}

export function badgeColor(value: string): string {
  return badgeColors.get(value.toLowerCase()) ?? 'gray';
}
