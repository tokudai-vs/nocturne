export const LANGUAGE_MAP: Record<string, string> = {
  eng: 'English',
  hin: 'Hindi',
  jpn: 'Japanese',
  kor: 'Korean',
  zho: 'Chinese',
  cmn: 'Chinese (Mandarin)',
  yue: 'Chinese (Cantonese)',
  chi: 'Chinese',
  spa: 'Spanish',
  fra: 'French',
  fre: 'French',
  deu: 'German',
  ger: 'German',
  ita: 'Italian',
  por: 'Portuguese',
  rus: 'Russian',
  ara: 'Arabic',
  tha: 'Thai',
  vie: 'Vietnamese',
  ind: 'Indonesian',
  msa: 'Malay',
  tam: 'Tamil',
  tel: 'Telugu',
  ben: 'Bengali',
  mar: 'Marathi',
  guj: 'Gujarati',
  kan: 'Kannada',
  mal: 'Malayalam',
  pan: 'Punjabi',
  urd: 'Urdu',
  nld: 'Dutch',
  dut: 'Dutch',
  pol: 'Polish',
  swe: 'Swedish',
  nor: 'Norwegian',
  dan: 'Danish',
  fin: 'Finnish',
  tur: 'Turkish',
  ell: 'Greek',
  gre: 'Greek',
  heb: 'Hebrew',
  ces: 'Czech',
  cze: 'Czech',
  hun: 'Hungarian',
  ron: 'Romanian',
  rum: 'Romanian',
  ukr: 'Ukrainian',
  und: 'Unknown',
  mis: 'Other',
  mul: 'Multiple',
  zxx: 'No Language',
};

export function getLanguageName(code: string): string {
  if (!code) return 'Unknown';
  return LANGUAGE_MAP[code.toLowerCase()] || code.toUpperCase();
}

/** Options for subtitle language dropdown (includes 'none') */
export const SUBTITLE_LANGUAGE_OPTIONS = [
  { value: 'none', label: 'None' },
  { value: 'eng', label: 'English' },
  { value: 'hin', label: 'Hindi' },
  { value: 'spa', label: 'Spanish' },
  { value: 'fra', label: 'French' },
  { value: 'deu', label: 'German' },
  { value: 'jpn', label: 'Japanese' },
  { value: 'kor', label: 'Korean' },
  { value: 'zho', label: 'Chinese' },
  { value: 'por', label: 'Portuguese' },
  { value: 'ita', label: 'Italian' },
  { value: 'rus', label: 'Russian' },
  { value: 'ara', label: 'Arabic' },
  { value: 'tha', label: 'Thai' },
  { value: 'tam', label: 'Tamil' },
  { value: 'tel', label: 'Telugu' },
  { value: 'ben', label: 'Bengali' },
  { value: 'urd', label: 'Urdu' },
  { value: 'tur', label: 'Turkish' },
  { value: 'pol', label: 'Polish' },
  { value: 'nld', label: 'Dutch' },
  { value: 'swe', label: 'Swedish' },
];

/** Options for audio language dropdown (no 'none') */
export const AUDIO_LANGUAGE_OPTIONS = SUBTITLE_LANGUAGE_OPTIONS.filter((o) => o.value !== 'none');

/**
 * Map ISO 639-2/B (used throughout Nocturne UI) → ISO 639-1 (required by
 * OpenSubtitles v1 API). Only covers the subset surfaced in
 * SUBTITLE_LANGUAGE_OPTIONS; anything else returns null.
 */
const ISO_639_1_MAP: Record<string, string> = {
  eng: 'en', hin: 'hi', spa: 'es',
  fra: 'fr', fre: 'fr',
  deu: 'de', ger: 'de',
  ita: 'it', jpn: 'ja', kor: 'ko',
  zho: 'zh', chi: 'zh',
  por: 'pt', rus: 'ru', ara: 'ar',
  tha: 'th', tam: 'ta', tel: 'te',
  ben: 'bn', urd: 'ur', tur: 'tr',
  pol: 'pl',
  nld: 'nl', dut: 'nl',
  swe: 'sv',
};

export function toIso6391(code: string): string | null {
  if (!code) return null;
  const lower = code.toLowerCase();
  if (lower.length === 2) return lower; // already 639-1
  return ISO_639_1_MAP[lower] || null;
}
