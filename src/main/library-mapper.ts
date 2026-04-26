import type { LibraryMapping } from './settings';

interface LibraryInfo {
  id: string;
  name: string;
  type?: string;
}

const QUALITY_SUFFIXES = /\s+(4K|HD|UHD|HDR|DV|Dolby\s?Vision|Remux)\s*/gi;
const AUDIENCE_SUFFIXES = /\s+(Kids|Anime|Bollywood|Non-English|Foreign|International|Classic|Classics|Family)\s*/gi;
const PAREN_CONTENT = /\s*\(.*?\)\s*/g;

const ICON_GUESSES: Record<string, string> = {
  movie: 'Film',
  film: 'Film',
  tv: 'Tv',
  show: 'Tv',
  series: 'Tv',
  music: 'Music',
  book: 'BookOpen',
  audiobook: 'BookOpen',
  collection: 'Archive',
  boxset: 'Archive',
  playlist: 'Folder',
  sport: 'Folder',
};

function guessIcon(name: string): string {
  const lower = name.toLowerCase();
  for (const [keyword, icon] of Object.entries(ICON_GUESSES)) {
    if (lower.includes(keyword)) return icon;
  }
  return 'Folder';
}

export function suggestLibraryMapping(
  libraries: LibraryInfo[],
): Record<string, LibraryMapping> {
  const groupMap = new Map<string, string[]>();

  for (const lib of libraries) {
    // Extract base name: strip parenthetical content, quality/audience suffixes
    let baseName = lib.name.replace(PAREN_CONTENT, ' ').trim();
    baseName = baseName.replace(QUALITY_SUFFIXES, ' ').trim();
    baseName = baseName.replace(AUDIENCE_SUFFIXES, ' ').trim();

    // Normalize: "TV Shows" and "TV Show" → same group
    const normalized = baseName.replace(/s$/i, '').trim();
    const groupKey = normalized || baseName || lib.name;

    if (!groupMap.has(groupKey)) {
      groupMap.set(groupKey, []);
    }
    groupMap.get(groupKey)!.push(lib.id);
  }

  // Build result with proper display names
  const result: Record<string, LibraryMapping> = {};

  for (const [key, ids] of groupMap) {
    // Use the shortest library name in this group as the display name
    const matchingLibs = libraries.filter((l) => ids.includes(l.id));
    const displayName =
      matchingLibs.length === 1
        ? matchingLibs[0].name
        : key.endsWith('s') ? key : key + 's'; // Re-pluralize

    // Capitalize first letter
    const name = displayName.charAt(0).toUpperCase() + displayName.slice(1);

    const groupId = `group_${key.toLowerCase().replace(/\s+/g, '_')}`;
    result[groupId] = {
      name,
      icon: guessIcon(name),
      libraryIds: ids,
    };
  }

  return result;
}
