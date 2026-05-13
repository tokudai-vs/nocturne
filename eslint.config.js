// ESLint v9+ flat config — minimal, scoped to plain JavaScript files only.
//
// Scope:
//   - scripts/*.js (build-time helpers run by `node`)
//   - this file itself
//
// Not in scope:
//   - src/**/*.ts and src/**/*.tsx — TS linting would require
//     @typescript-eslint, which we don't want to add as a dep right now
//     (the project's native binaries are Windows-prebuilt and a stray
//     `npm install` could clobber them). The relative-require ban that
//     would otherwise live here is enforced by
//     scripts/check-relative-requires.js, which is wired into prebuild +
//     the npm `lint` script.
//   - The postbuild script (scripts/check-bundling.js) catches the actual
//     ship-stopper: src/main/* modules that no one statically imports
//     get silently omitted from out/main/index.js.
//
// Together those two scripts cover the failure modes that bit us in v3.

'use strict';

module.exports = [
  {
    ignores: [
      'out/**',
      'dist/**',
      'build/**',
      'node_modules/**',
      'resources/**',
      '**/*.d.ts',
      'src/**',
    ],
  },
  {
    files: ['scripts/**/*.js', 'eslint.config.js'],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'commonjs',
      globals: {
        process: 'readonly',
        console: 'readonly',
        Buffer: 'readonly',
        __dirname: 'readonly',
        __filename: 'readonly',
        require: 'readonly',
        module: 'writable',
        exports: 'writable',
      },
    },
    rules: {
      // Stock ESLint rules only — no plugin deps.
      'no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
      'no-undef': 'error',
    },
  },
];
