/**
 * Bundled Trakt application credentials.
 *
 * Trakt's docs permit baking these into desktop clients (public OAuth pattern).
 * Users can override via Settings → Trakt → Advanced if they want to point at
 * their own app registration.
 *
 * To populate for a build:
 *   1. Visit https://trakt.tv/oauth/applications
 *   2. Create an app:
 *        Name:         Nocturne (or your fork name)
 *        Redirect URI: urn:ietf:wg:oauth:2.0:oob
 *        Permissions:  /scrobble (Phase 1) — add /sync for Phase 2+
 *   3. Paste client_id + client_secret below.
 *
 * Empty strings disable Trakt entirely; the Advanced override path still works.
 */
export const TRAKT_BUNDLED_CLIENT_ID = '';
export const TRAKT_BUNDLED_CLIENT_SECRET = '';

export const TRAKT_API_BASE = 'https://api.trakt.tv';
export const TRAKT_API_VERSION = '2';
export const TRAKT_OOB_REDIRECT = 'urn:ietf:wg:oauth:2.0:oob';
